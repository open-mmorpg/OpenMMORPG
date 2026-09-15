# Open MMORPG Demo

A small island with a village, three enemy families, a rideable horse and a dungeon,
built to show the kit running end to end: the MMO flow (`Scenes/00Init` -> `01Home` ->
`DemoMap`), character creation with a choice of body, hairstyle, hair colour and beard,
three classes, looted armour sets, harvesting, quests, an inn and a bank, and a second
map - the Cultist Crypt (`Scenes/DemoDungeon`), reached through the crypt door in the
hills and left the same way, with a boss at the end of it.

Everything here except the scripts under `Scripts/` and `Editor/` is **generated** by the
editor tools in the separate
[open-mmorpg-demo-builder](https://github.com/open-mmorpg/open-mmorpg-demo-builder)
repository, from CC0 art libraries that are not part of the kit. Edit the generators,
not the generated assets: rebuilding the scene replaces `DemoMap.unity`.

`Scripts/` holds the demo's own gameplay components (doors, torches, the sky cycle,
patrols, the ladder exit, the mount animator, the crypt gate that sends a character
through once and a frame after the trigger fires, and the two small fixes that let the
character screens show a model that hangs off a child). `Editor/` holds only the editor
support for those components.

**Controls** are the ones most MMO players already know. WASD moves relative to the camera; either mouse
button dragged orbits the camera, and a right drag also turns the character; the wheel
zooms from first person out to fourteen metres. A left click on an enemy targets it, a
right click targets and attacks, Tab cycles nearby enemies, T attacks the target (or the
nearest enemy) and Space jumps. There is no click-to-move: NPCs, loot and harvestables are
reached by walking up and pressing the activate key. The controller is
`Scripts/DemoPlayerController` over the kit's default one; the keys are the key settings on
`Prefabs/GameInstance`, both written by `Build Player Controller`. The over-the-shoulder
shooter controller is still built by `Build Player Controller (Shooter)`.

**The sea is swimmable, on the surface only.** Walk in off any beach: the character wades
until the bottom drops away, then swims at eight tenths of run speed, held at the surface.
There is no diving. The water is a trigger volume on the Water layer under the sea plane
(`Rebuild Sea` puts it there), the player entities and the horse have the kit's
autoSwimToSurface on, and `Scripts/DemoSurfaceSwimmer` lifts the model to the surface
while swimming, because the kit holds the capsule deep enough for treading water and the
swim clips lie flat.

**Audio** lives in `Audio/` and is wired by name: a family is a prefix plus a number
(`Footstep1.wav`, `SwordSwing3.wav`, `WomanHit2.wav`), and `Open MMORPG > Demo > Wire
Audio` hooks every family the kit has a slot for and logs the ones still empty. Footsteps
go on every character through the kit's footstep component; sword and axe swings on the
attack animations; bow shots and staff casts on the weapon item, at the launch; hurt
grunts through `Scripts/DemoHurtSoundComponent` on the synced HP (the kit has no
client-side hit event); the island's nature and shore loops are built with the sea, the
shore fading with height through `Scripts/DemoAmbientLoop`.

Art credits are in `CREDITS.md`.
