//#define DEBUG
using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using System.Text;
using System.Linq;
using Oxide.Core.Plugins;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("Replicator", "RFC1920", "1.0.1")]
    [Description("Replicate items")]
    class Replicator : RustPlugin
    {
        [PluginReference]
        private Plugin Economics;

        #region Configuration
        float cooldownMinutes;
        float bypass;
        float cost;
        int sessionLimit;
        int sessionCount;

        string box;
        bool npconly;
        List<object> npcids;
        float radiationMax;
        List<object> replicateableTypes;
        List<object> blacklist;
        #endregion

        #region State
        Dictionary<string, DateTime> replicateCooldowns = new Dictionary<string, DateTime>();
        private Dictionary<int, string> idToItemName = new Dictionary<int, string>();

        class OnlinePlayer
        {
            public BasePlayer Player;
            public BasePlayer Target;
            public StorageContainer View;
            public List<BasePlayer> Matches;

            public OnlinePlayer(BasePlayer player)
            {
            }
        }

        class ItemData
        {
            public string Shortname { get; set; }
            public double Cooldown { get; set; }
            public double Buy { get; set; } = -1;
            public double Sell { get; set; } = -1;
            public bool Fixed { get; set; }
            public List<string> Cmd { get; set; }
            public string Img { get; set; }
        }

        public Dictionary<ItemContainer, ulong> containers = new Dictionary<ItemContainer, ulong>();

        [OnlinePlayers]
        Hash<BasePlayer, OnlinePlayer> onlinePlayers = new Hash<BasePlayer, OnlinePlayer>();
        #endregion

        #region Initialization
        protected override void LoadDefaultConfig()
        {
            Config["Settings", "box"] = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";
            Config["Settings", "cooldownMinutes"] = 5;
            Config["Settings", "sessionLimit"] = 3;
            Config["Settings", "radiationMax"] = 1;
            Config["Settings", "bypass"] = 5f;
            Config["Settings", "cost"] = 10f;
            Config["Settings", "NPCOnly"] = false;
            Config["Settings", "NPCIDs"] = new List<object>();
            Config["Settings", "replicateableTypes"] = GetDefaultReplicatableTypes();
            Config["Settings", "blacklist"] = GetDefaultBlackList();
            Config["VERSION"] = Version.ToString();
        }

        void Unloaded()
        {
            foreach(var player in BasePlayer.activePlayerList)
            {
                OnlinePlayer onlinePlayer;
                if(onlinePlayers.TryGetValue(player, out onlinePlayer) && onlinePlayer.View != null)
                {
                    CloseBoxView(player, onlinePlayer.View);
                }
            }
        }

        void Init()
        {
            Unsubscribe(nameof(CanNetworkTo));
            Unsubscribe(nameof(OnEntityTakeDamage));

            foreach(var itemDef in ItemManager.GetItemDefinitions())
            {
                idToItemName.Add(itemDef.itemid, itemDef.shortname);
#if DEBUG
                Puts($"{itemDef.shortname}: {itemDef.itemid.ToString()}");
#endif
            }

            permission.RegisterPermission("replicator.use", this);
            permission.RegisterPermission("replicator.nocooldown", this);
        }

        void Loaded()
        {
            LoadMessages();
            CheckConfig();

            cooldownMinutes = GetConfig("Settings", "cooldownMinutes", 5f);
            box = GetConfig("Settings", "box", "assets/prefabs/deployable/woodenbox/box_wooden.item.prefab");
            sessionLimit = GetConfig("Settings", "sessionLimit", 3);
            radiationMax = GetConfig("Settings", "radiationMax", 1f);
            replicateableTypes = GetConfig("Settings", "replicateableTypes", GetDefaultReplicatableTypes());
            blacklist =  GetConfig("Settings", "blacklist", GetDefaultBlackList());
            bypass = GetConfig("Settings", "bypass", 5f);
            cost = GetConfig("Settings", "cost", 10f);

            npconly = GetConfig("Settings", "NPCOnly", false);
            npcids = GetConfig("Settings", "NPCIDs", new List<object>());
        }

        void CheckConfig()
        {
            if(Config ["VERSION"] == null)
            {
                ReloadConfig();
            }
            else if(GetConfig("VERSION", "") != Version.ToString())
            {
                ReloadConfig();
            }
        }

        protected void ReloadConfig()
        {
            Config["VERSION"] = Version.ToString();

            replicateableTypes = GetConfig("Settings", "replicateableTypes", GetDefaultReplicatableTypes());
            blacklist = GetConfig("Settings", "blacklist", GetDefaultBlackList());
            PrintToConsole("Upgrading configuration file");
            SaveConfig();
        }

        void LoadMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Replicator: Complete", "Replicating <color=lime>{0}</color> to {1}% base materials:"},
                {"Replicator: Item", "    <color=lime>{0}</color> X <color=yellow>{1}</color>"},
                {"Replicator: Invalid", "Cannot replicate that type of item!"},
                {"Replicator: BlackList", "Cannot replicate blacklisted item!"},
                {"Replicator: Limit", "Session limit reached - no more replicating now!"},
                {"Denied: Permission", "You lack the permission to do that"},
                {"Denied: Privilege", "You lack build privileges and cannot do that"},
                {"Denied: Swimming", "You cannot do that while swimming"},
                {"Denied: Falling", "You cannot do that while falling"},
                {"Denied: Mounted", "You cannot do that while mounted"},
                {"Denied: Wounded", "You cannot do that while wounded"},
                {"Denied: Irradiated", "You cannot do that while irradiated"},
                {"Denied: Generic", "You cannot do that right now"},
                {"InventorySlots", "Your inventory needs {0} free slots."},
                {"ItemNoExist", "WARNING: The item you are trying to buy doesn't seem to exist"},
                {"Cooldown: Minutes", "You are doing that too often, try again in {0} minute(s)."},
                {"Cooldown: Seconds", "You are doing that too often, try again in {0} seconds(s)."},
                {"Cooldown: Bypass", "Replicator cooldown bypassed by paying {0}." },
                {"Cooldown: ToBypass", "You may bypass this by typing /rep bypass and paying {0}." }
            }, this);
        }

        List<object> GetDefaultReplicatableTypes()
        {
            return new List<object>()
            {
                ItemCategory.Attire.ToString(),
                ItemCategory.Common.ToString(),
                ItemCategory.Component.ToString(),
                ItemCategory.Construction.ToString(),
                ItemCategory.Items.ToString(),
                ItemCategory.Medical.ToString(),
                ItemCategory.Misc.ToString()
            };
        }

        List<object> GetDefaultBlackList()
        {
            return new List<object>()
            {
                "keycard",
                "explosive"
            };
        }

        bool IsReplicatorBox(BaseNetworkable entity)
        {
            foreach(KeyValuePair<BasePlayer, OnlinePlayer> kvp in onlinePlayers)
            {
                if(kvp.Value?.View?.net != null && entity?.net != null && kvp.Value.View.net.ID == entity.net.ID)
                {
                    return true;
                }
            }

            return false;
        }

        bool checkBlackList(string itemname)
        {
            foreach(var match in blacklist)
            {
                if(itemname.Contains(match.ToString()))
                {
                    return true;
                }
            }

            return false;
        }
        #endregion

        #region Commands
        [ChatCommand("rep"), Permission("replicator.use")]
        void cmdReplicator(BasePlayer player, string command, string[] args)
        {
            if(npconly) return;
            if(!permission.UserHasPermission(player.UserIDString, "replicator.use"))
            {
                SendReply(player, GetMsg("Denied: Permission", player));
            }
            ShowBox(player, player, args);
        }
        #endregion

        #region Oxide Hooks
        object CanNetworkTo(BaseNetworkable entity, BasePlayer target)
        {
            if(entity == null || target == null || entity == target) return null;
            if(target.IsAdmin) return null;

            OnlinePlayer onlinePlayer;
            bool IsMyBox = false;
            if(onlinePlayers.TryGetValue(target, out onlinePlayer))
            {
                if(onlinePlayer.View != null && onlinePlayer.View.net.ID == entity.net.ID)
                {
                    IsMyBox = true;
                }
            }

            if(IsReplicatorBox(entity) && !IsMyBox) return false;

            return null;
        }

        object OnEntityTakeDamage (BaseCombatEntity entity, HitInfo hitInfo)
        {
            if(hitInfo == null) return null;
            if(entity == null) return null;
            if(IsReplicatorBox(entity)) return false;

            return null;
        }

        void OnPlayerConnected(BasePlayer player)
        {
            onlinePlayers[player].View = null;
            onlinePlayers[player].Target = null;
            onlinePlayers[player].Matches = null;
        }

        void OnPlayerDisconnected(BasePlayer player)
        {
            if(onlinePlayers [player].View != null)
            {
                CloseBoxView(player, onlinePlayers [player].View);
            }
        }

        void OnPlayerLootEnd(PlayerLoot inventory)
        {
            BasePlayer player;
            if((player = inventory.GetComponent<BasePlayer>()) == null) return;

            OnlinePlayer onlinePlayer;
            if(onlinePlayers.TryGetValue(player, out onlinePlayer) && onlinePlayer.View != null)
            {
                if(onlinePlayer.View == inventory.entitySource)
                {
                    CloseBoxView(player,(StorageContainer)inventory.entitySource);
                }
            }
        }

        void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            string itemname = null;
            if(container.playerOwner is BasePlayer)
            {
                if(onlinePlayers.ContainsKey(container.playerOwner))
                {
                    BasePlayer owner = container.playerOwner as BasePlayer;
                    if(containers.ContainsKey(container))
                    {
                        if(sessionCount >= sessionLimit)
                        {
                            ShowNotification(owner, GetMsg("Replicator: Limit", owner));
                            item.MoveToContainer(owner.inventory.containerMain);
                        }
                        else if(replicateableTypes.Contains(Enum.GetName(typeof(ItemCategory), item.info.category)))
                        {
                            sessionCount++;
#if DEBUG
                            Puts($"session count: {sessionCount.ToString()}");
#endif
                            int myid = item.info.itemid;
                            idToItemName.TryGetValue(myid, out itemname);
#if DEBUG
                            Puts($"Player added {itemname}:{myid.ToString()}");
#endif
                            if(checkBlackList(itemname))
                            {
                                ShowNotification(owner, GetMsg("Replicator: BlackList", owner));
                                item.MoveToContainer(owner.inventory.containerMain);
                            }
                            else
                            {

                                Item repitem = ItemManager.CreateByItemID(myid, 1, 0);
#if DEBUG
                                Puts($"Created new {repitem.ToString()}");
#endif
                                item.MoveToContainer(owner.inventory.containerMain, -1, true);
                                repitem.MoveToContainer(owner.inventory.containerMain, -1, true);
                            }
                        }
                        else
                        {
                            ShowNotification(owner, GetMsg("Replicator: Invalid", owner));
                            item.MoveToContainer(owner.inventory.containerMain);
                        }
                    }
                }
            }
        }

        void OnUseNPC(BasePlayer npc, BasePlayer player)
        {
            if(!npcids.Contains(npc.UserIDString)) return;
            ShowBox(player, player);
        }

        void AddNpc(string id)
        {
            npcids.Add(id);
        }

        void RemoveNpc(string id)
        {
            if(npcids.Contains(id))
            {
                npcids.Remove(id);
            }
        }
        #endregion

        #region Main
        void ShowBox(BasePlayer player, BaseEntity target, string[] args = null)
        {
            string playerID = player.userID.ToString();
            string dobypass = "";
            try
            {
                dobypass = args[0];
            }
            catch {}

            if(!CanPlayerReplicate(player)) return;

            if(cooldownMinutes > 0 && !permission.UserHasPermission(player.UserIDString, "replicator.nocooldown"))
            {
                DateTime startTime;

                if(replicateCooldowns.TryGetValue(playerID, out startTime))
                {
                    DateTime endTime = DateTime.Now;

                    TimeSpan span = endTime.Subtract(startTime);
                    if(span.TotalMinutes > 0 && span.TotalMinutes < Convert.ToDouble(cooldownMinutes))
                    {
                        double timeleft = System.Math.Round(Convert.ToDouble(cooldownMinutes) - span.TotalMinutes, 2);
                        if(span.TotalSeconds < 0)
                        {
                            replicateCooldowns.Remove(playerID);
                        }

                        if(bypass > 0 && (double)(Economics?.CallHook("Balance", player.UserIDString) ?? 0) > bypass && dobypass == "bypass")
                        {
                            var w = Economics.CallHook("Withdraw", player.userID,(double)bypass);
                            //if(w == null || !(bool)w) return false;
                            SendReply(player, string.Format(GetMsg("Cooldown: Bypass", player), bypass.ToString()));
                        }
                        else
                        {
                            if(timeleft < 1)
                            {
                                double timelefts = System.Math.Round((Convert.ToDouble(cooldownMinutes) * 60) - span.TotalSeconds);
                                SendReply(player, string.Format(GetMsg("Cooldown: Seconds", player), timelefts.ToString()));
                                SendReply(player, string.Format(GetMsg("Cooldown: ToBypass", player), bypass.ToString()));
                                return;
                            }
                            else
                            {
                                SendReply(player, string.Format(GetMsg("Cooldown: Minutes", player), System.Math.Round(timeleft).ToString()));
                                SendReply(player, string.Format(GetMsg("Cooldown: ToBypass", player), bypass.ToString()));
                                return;
                            }
                        }
                    }

                    replicateCooldowns.Remove(playerID);
                }
            }

            if(!replicateCooldowns.ContainsKey(player.userID.ToString()))
            {
                replicateCooldowns.Add(player.userID.ToString(), DateTime.Now);
            }
            var ply = onlinePlayers [player];
            if(ply.View == null)
            {
                if(!OpenBoxView(player, target))
                {
                    replicateCooldowns.Remove(playerID);
                }
                return;
            }

            CloseBoxView(player, ply.View);
            timer.In(1f, delegate()
            {
                if(!OpenBoxView(player, target))
                {
                    replicateCooldowns.Remove(playerID);
                }
            });
        }

        void HideBox(BasePlayer player)
        {
            player.EndLooting();
            var ply = onlinePlayers [player];
            if(ply.View == null)
            {
                return;
            }

            CloseBoxView(player, ply.View);
        }

        bool OpenBoxView(BasePlayer player, BaseEntity targArg)
        {
            Subscribe(nameof(CanNetworkTo));
            Subscribe(nameof(OnEntityTakeDamage));

            var pos = new Vector3(player.transform.position.x, player.transform.position.y - 0.6f, player.transform.position.z);
            int slots = 1;
            var view = GameManager.server.CreateEntity(box, pos) as StorageContainer;

            if(!view) return false;

            view.GetComponent<DestroyOnGroundMissing>().enabled = false;
            view.GetComponent<GroundWatch>().enabled = false;
            view.transform.position = pos;

            player.EndLooting();
            if(targArg is BasePlayer)
            {
                BasePlayer target = targArg as BasePlayer;
                ItemContainer container = new ItemContainer();
                container.playerOwner = player;
                container.ServerInitialize((Item)null, slots);
                if((int)container.uid == 0)
                {
                    container.GiveUID();
                }

                if(!containers.ContainsKey(container))
                {
                    containers.Add(container, player.userID);
                }

                view.enableSaving = false;
                view.Spawn();
                view.inventory = container;
                view.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                onlinePlayers [player].View = view;
                onlinePlayers [player].Target = target;
                timer.Once(0.2f, delegate()
                {
                    view.PlayerOpenLoot(player);
                });

                return true;
            }

            return false;
        }

        void CloseBoxView(BasePlayer player, StorageContainer view)
        {
            sessionCount = 0;
            OnlinePlayer onlinePlayer;
            if(!onlinePlayers.TryGetValue(player, out onlinePlayer)) return;
            if(onlinePlayer.View == null) return;

            if(containers.ContainsKey(view.inventory))
            {
                containers.Remove(view.inventory);
            }

            player.inventory.loot.containers = new List<ItemContainer>();
            view.inventory = new ItemContainer();

            if(player.inventory.loot.IsLooting())
            {
                player.SendConsoleCommand("inventory.endloot", null);
            }

            onlinePlayer.View = null;
            onlinePlayer.Target = null;

            NextFrame(delegate()
            {
                view.KillMessage();

                if(onlinePlayers.Values.Count(p => p.View != null) <= 0)
                {
                    Unsubscribe(nameof(CanNetworkTo));
                    Unsubscribe(nameof(OnEntityTakeDamage));
                }
            });
        }

        bool CanPlayerReplicate(BasePlayer player)
        {
            if(!permission.UserHasPermission(player.UserIDString, "replicator.use"))
            {
                SendReply(player, GetMsg("Denied: Permission", player));
                return false;
            }

            if(!player.CanBuild())
            {
                SendReply(player, GetMsg("Denied: Privilege", player));
                return false;
            }
            if(radiationMax > 0 && player.radiationLevel > radiationMax)
            {
                SendReply(player, GetMsg("Denied: Irradiated", player));
                return false;
            }
            if(player.IsSwimming())
            {
                SendReply(player, GetMsg("Denied: Swimming", player));
                return false;
            }
            if(!player.IsOnGround() || player.IsFlying || player.isInAir)
            {
                SendReply(player, GetMsg("Denied: Falling", player));
                return false;
            }
            if(player.isMounted)
            {
                SendReply(player, GetMsg("Denied: Mounted", player));
                return false;
            }
            if(player.IsWounded())
            {
                SendReply(player, GetMsg("Denied: Wounded", player));
                return false;
            }

            var canReplicate = Interface.Call("CanReplicateCommand", player);
            if(canReplicate != null)
            {
                if(canReplicate is string)
                {
                    SendReply(player, Convert.ToString(canReplicate));
                }
                else
                {
                    SendReply(player, GetMsg("Denied: Generic", player));
                }
                return false;
            }

            return true;
        }
        #endregion

        #region GUI
        public string jsonNotify = @"[{""name"":""NotifyMsg"",""parent"":""Overlay"",""components"":[{""type"":""UnityEngine.UI.Image"",""color"":""0 0 0 0.89""},{""type"":""RectTransform"",""anchormax"":""0.99 0.94"",""anchormin"":""0.69 0.77""}]},{""name"":""MassText"",""parent"":""NotifyMsg"",""components"":[{""type"":""UnityEngine.UI.Text"",""text"":""{msg}"",""fontSize"":16,""align"":""UpperLeft""},{""type"":""RectTransform"",""anchormax"":""0.98 0.99"",""anchormin"":""0.01 0.02""}]},{""name"":""CloseButton{1}"",""parent"":""NotifyMsg"",""components"":[{""type"":""UnityEngine.UI.Button"",""color"":""0.95 0 0 0.68"",""close"":""NotifyMsg"",""imagetype"":""Tiled""},{""type"":""RectTransform"",""anchormax"":""0.99 1"",""anchormin"":""0.91 0.86""}]},{""name"":""CloseButtonLabel"",""parent"":""CloseButton{1}"",""components"":[{""type"":""UnityEngine.UI.Text"",""text"":""X"",""fontSize"":5,""align"":""MiddleCenter""},{""type"":""RectTransform"",""anchormax"":""1 1"",""anchormin"":""0 0""}]}]";

        public void ShowNotification(BasePlayer player, string msg)
        {
            this.HideNotification(player);
            string send = jsonNotify.Replace("{msg}", msg);

            CommunityEntity.ServerInstance.ClientRPCEx(new Network.SendInfo { connection = player.net.connection }, null, "AddUI", send);
            timer.Once(3f, delegate() {
                this.HideNotification(player);
            });
        }

        public void HideNotification(BasePlayer player)
        {
            CommunityEntity.ServerInstance.ClientRPCEx(new Network.SendInfo { connection = player.net.connection }, null, "DestroyUI", "NotifyMsg");
        }
        #endregion

        #region HelpText
        private void SendHelpText(BasePlayer player)
        {
            var sb = new StringBuilder()
               .Append("Replicator by <color=#ce422b>RFC1920</color>\n")
               .Append("  ").Append("<color=#ffd479>/rep</color> - Open replication box").Append("\n");
            player.ChatMessage(sb.ToString());
        }
        #endregion

        #region Helper methods
        string GetMsg(string key, BasePlayer player = null)
        {
            return lang.GetMessage(key, this, player == null ? null : player.UserIDString);
        }

        private T GetConfig<T>(string name, T defaultValue)
        {
            if(Config [name] == null)
            {
                return defaultValue;
            }

            return(T)Convert.ChangeType(Config [name], typeof(T));
        }

        private T GetConfig<T>(string name, string name2, T defaultValue)
        {
            if(Config [name, name2] == null)
            {
                return defaultValue;
            }

            return(T)Convert.ChangeType(Config [name, name2], typeof(T));
        }
        #endregion
    }
}
