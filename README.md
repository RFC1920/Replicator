# Replicator
This is a very silly plugin, indeed.
Replicate any item which will fit into a loot container.

However, the item must be in the list of ReplicateableTypes (item categories) and cannot be in the blacklist (prefab name match).
See the configuration section below.

## Requires

	[Economics](https://umod.org/plugins/economics)

## Permissions

- `replicator.use` -- Allows player to open the personal replicator
- `replicator.nocooldown`  --- Allows player to bypass the cooldown until next use

## Configuration

```json
{
  "Settings": {
    "blacklist": [
      "keycard",
      "explosive"
    ],
    "box": "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab",
    "bypass": 5,
    "cost": 10,
    "cooldownMinutes": 5,
    "NPCIDs": [],
    "NPCOnly": false,
    "radiationMax": 1,
    "replicateableTypes": [
      "Attire",
      "Common",
      "Component",
      "Construction",
      "Items",
      "Medical",
      "Misc"
    ],
    "sessionLimit": 3
  },
  "VERSION": "1.0.0"
}

```

- blacklist: A list of matches for items you want to exclude, regardless of the replicateableTypes (categories)
- sessionLimit: Limits the number of replications that can be done per session.
- cooldownMinutes: Limits how often replicator can be used
- bypass: Cost, using Economics, to bypass the cooldown between use
- replicateableTypes: List of categories allowed for replication
- NPCOnly - Only use in association with a HumanNPC
- NPCIDs - Id(s) of HumanNPC to use as a replicator bot

## Chat Commands

- `/rep` -- Opens a small loot container for replication
