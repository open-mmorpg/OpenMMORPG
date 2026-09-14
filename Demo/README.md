# Open MMORPG Demo

A small island with a village, three enemy families and a rideable horse, built to show
the kit running end to end: the MMO flow (`Scenes/00Init` -> `01Home` -> `DemoMap`),
character creation with a choice of body, hairstyle, hair colour and beard, three
classes, looted armour sets, harvesting, quests, an inn and a bank.

Everything here except the scripts under `Scripts/` and `Editor/` is **generated** by the
editor tools in the separate
[open-mmorpg-demo-builder](https://github.com/open-mmorpg/open-mmorpg-demo-builder)
repository, from CC0 art libraries that are not part of the kit. Edit the generators,
not the generated assets: rebuilding the scene replaces `DemoMap.unity`.

`Scripts/` holds the demo's own gameplay components (doors, torches, the sky cycle,
patrols, the ladder exit, the mount animator, and the two small fixes that let the
character screens show a model that hangs off a child). `Editor/` holds only the editor
support for those components.

Art credits are in `CREDITS.md`.
