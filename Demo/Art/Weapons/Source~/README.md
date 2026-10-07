# Bow, staff, arrow, quiver and short sword - sources

The models arrived as `bow.glb`, `staff.glb`, `arrow.glb` and `quiver.glb`, then `shortsword.glb` (all 2026-10-06): single
dense meshes (11k-37k verts, thousands of UV-seam fragments) with 2048 px glTF PBR textures. The scripts here turn them
into the game-ready `Bow.fbx`, `MageStaff.fbx`, `Arrow.fbx`, `Quiver.fbx` and `ShortSword.fbx` and their textures one
folder up. The `~` keeps
Unity from importing this folder, and the package build skips it.

Run any of them with Blender 4.5 (they write straight into `Art/Weapons`):

    blender --background --factory-startup --python bow.py

`WEAPON_OUT` redirects the output, `WEAPON_WORK` also saves a `.blend` of the high and low (for checking bakes).

## What each script does

`wp.py` is the shared library: weld and decimate, smart-UV unwrap, a Cycles bake from the original
(tangent-space normal, base colour, roughness, metallic), packing for URP, FBX export.

1. The glb is moved into its final frame (real size, grip/pivot at the origin, +Z up in Blender = +Y up in Unity).
2. A low-poly is made: welded and decimated (bow body, staff, arrow, short sword), or built by hand from the original's
   cross-sections (quiver, whose thin rolled lip did not survive decimation), plus a hand-built string for the bow.
3. Everything the low-poly lost is baked from the original at 2048 and delivered at 1024 (512 for the arrow).
4. The textures are packed the way URP's Lit wants them: `_BaseColor.png` (sRGB), `_Normal.png` (tangent space, +Y up),
   `_Mask.png` (R metallic, G white - there is no AO - and A = 1 - roughness, linear). The glTF's own packing
   (roughness in G, metallic in B, empty R) would black the prop out in URP.
5. FBX: `bake_space_transform` so Unity's root is identity, `FBX_SCALE_ALL`, tangents, no leaf bones. The object, mesh
   and material are all named after the weapon, which is how Unity binds `Materials/<name>.mat`.

| Model | Tris | Frame in Unity |
|---|---|---|
| Bow | 2 592 (2 400 body + 192 string) | 1.70 m long, handle on the Y axis at x +0.058, string on -x, thickness on Z |
| MageStaff | 2 600 | 1.65 m long, grip at the origin 0.60 m above the butt, crystal at +Y |
| Arrow | 599 | 0.72 m long, origin at the middle, broadhead at +Y |
| ShortSword | 2 200 | 0.80 m long, tip at +Y, flat facing Z, crossguard along X, origin at the middle of the grip (0.12 m above the pommel's end) |
| Quiver | 1 224 | 0.44 m foot to mouth, about 13 cm wide and 11 cm deep at the mouth, origin at the mouth's axis, flat back face 0.015 m behind it (-Z), rounded face out |

## Three deliberate departures from the originals

* **The bow's depth is halved** (`KX = 0.5` in `bow.py`). At full depth its string sat 0.30 m from the handle at 1.7 m
  long. The draw code (`BowEquipmentEntity`) nocks an arrow only when the drawing hand is within 0.16 m of the string
  (`nockRadius`) and the draw clips were tuned for a string about 0.13 m out; halved, it is 0.14 m. Length, thickness and
  the shape of the limbs are unchanged in the FBX. (On the prefab, the bow's `Mesh` child is further trimmed to 70% in Z
  - its thickness - by hand; `DemoWeaponBuilder`'s `MeshScale` keeps that across rebuilds. It still nocks.)
* **The quiver's depth is 80% of the original** (`KZ` in `quiver.py`), and its flat back is 1.5 cm behind the origin. It
  hangs from the same socket pose the old, flat pouch used. At full depth the bow, which lies over it on the back, sank 7 cm
  into its collar and the rear strap dipped 2 cm into the shoulder blades (measured by ray casts on the baked male body).
* **The bow's string is a separate mesh piece.** `BowEquipmentEntity` finds it as a separate connected component and
  bends it into a V, so it must share no vertex with the limbs and must be straight, long and thin. It is a 4-sided
  tube in 25 rings, coloured from the original's string. The body copy has the string cut out before it is decimated.

## Licence / provenance

All five were generated with Ludo AI on a paid plan (stated by the user, 2026-10-06; the short sword "with the same Ludo
account"; the glb files themselves only say `generator: trimesh`). Ludo's terms grant the maker a licence to use, modify, distribute and make derivative works for
commercial purposes, which is what the CC0 dedication in `Demo/CREDITS.md` rests on. Credited there, with the shield
and the shrine.
