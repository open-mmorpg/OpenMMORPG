# The horse's saddle - sources

`saddle.glb` is a single dense mesh from Ludo AI (47 075 vertices, 51 282 triangles, 2048 px glTF PBR textures). The scripts
here turn it into `Saddle.fbx` and its three textures one folder up, **already fitted to the demo's horse and to its seated
rider**. The `~` keeps Unity from importing this folder, and the package build skips it.

    python fit_saddle.py                                                        (numpy, scipy, trimesh; about a minute)
    blender --background --factory-startup --python saddle.py                   (Blender 4.5; writes into Art/Saddle)

`SADDLE_OUT` redirects the output, `SADDLE_WORK` also saves a `.blend`, `TRIS` (4500) and `SHARP` (40) tune the low-poly.
`Open MMORPG > Demo > Build Saddle` then makes the material, skins the model to the horse and moves the rider's seat onto
it (`DemoSaddleBuilder`; a full `Build Mounts` does the same).

## What the glb needed

It was generated for a narrow horse of no particular length, with a mesh that cannot be trusted: 2 299 edge-disconnected
bodies, 3 475 non-manifold edges, and a winding that is inconsistent (73% of faces point away from their neighbours, the
small closed bodies are inside-out about as often as not), which is why the glb is marked double-sided.

1. **Fit (`fit_saddle.py`).** Measured, not eyeballed, against the horse's skinned mesh in its bind pose and the male rider
   in the riding pose (both written by `Open MMORPG > Demo > Dump Saddle Fit Inputs` into `dump_*`):
   - *Scale* 0.76 along the back, 0.82 tall, 1.30 across, pitched 4 degrees nose-down and dropped until the underside of the
     tree is 1 cm clear of the spine. The horse's usable back is only 0.55 m (the dip between the croup and the withers), so
     the saddle is 0.57 m long and stops at the withers.
   - *Seat.* The lowest point of the dish is where the rider's pelvis rests: `(0, 1.588, -0.244)` in the horse's frame
     (`DemoSaddleBuilder.SeatPoint`, the FBX's origin). The vehicle's Seat anchor is that minus the rider's measured pelvis
     contact, which moved the rider up 12.8 cm and back 16.8 cm from where the old bare-back seat had them.
   - *Swing.* The fender and stirrup assembly rotates aft 23.7 degrees about a lateral hinge, because the riding clip carries
     the rider's feet about a quarter of a metre behind where the glb hangs its stirrups. The treads land under the soles.
   - *Drape.* Every vertex is pushed sideways by a smooth field of (height, position along the back) until the leather's
     inner skin is 1.8 cm outside the horse's barrel (the glb's fenders hang straight down, which is inside this horse's
     barrel); the stirrups then move rigidly outboard to sit under the feet instead of being sheared.
   - Result, from the script: 0 saddle vertices more than 4 mm inside the 1.8 cm gap, 0 rider vertices more than 5 mm inside
     the horse, stirrup centre 3 cm inboard of the foot centre.
2. **Low-poly (`saddle.py`).**
   - 2 298 of the glb's bodies are 17-38 face stitch beads and rivet heads that share vertices with the skin once welded;
     each is an island decimation can never collapse (they held it at 9 500 triangles), so they stay in the high-poly for the
     bake and are left out of the low-poly (`small_faces.npy`).
   - Then weld, collapse-decimate to 4 499 triangles, and **turn every face toward the open air** (`wp.orient_by_free_space`:
     probe a ray 2 mm off each side of the face and keep the normal on the side whose ray travels further; a skin of a 1.5 cm
     plate sees the other skin on one side and the room on the other). `recalc_face_normals` decides per connected piece, one
     non-manifold join flips whole plates, and the low-poly then had 59% of its visible pixels showing the wrong surface;
     probing brings it to 6%.
   - Then **every face is added a second time, the other way round, on its own vertices** (`wp.double_sided`): 8 998 triangles,
     each plate lit as a front face from either side. The glb's `doubleSided` flag does this job; URP Lit has no back-face
     normal flip, so a Cull Off material lights the wrong-way faces dark and blue. With the copies, culling stays on and no
     face the decimation or the probe got wrong can open a hole.
   - Unwrapped with seams at every edge sharper than 40 degrees (`wp.unwrap_seams`; `smart_project` gave thousands of tiny
     islands, more forgiving angles gave stretched ones).
3. **Bake.** From the fitted high-poly at 2048, delivered at 1024: tangent-space normal, base colour, roughness and metallic
   (the last two through an emission material, because the Diffuse pass zeroes metals), packed for URP as
   `Saddle_BaseColor.png` (sRGB), `Saddle_Normal.png` and `Saddle_Mask.png` (R metallic, G white, A smoothness). The glb has
   no occlusion map.
4. **FBX.** `bake_space_transform`, `FBX_SCALE_ALL`, tangents, no leaf bones; the object, mesh and material are all named
   `Saddle`, which is how Unity binds `Materials/Saddle.mat`. The root is identity and the origin is the seat point.

Tried and dropped: collapse-decimating without fixing the winding; a voxel envelope (rasterise, flood-fill, marching cubes,
quadric collapse) that is watertight and consistently wound by construction but loses the stirrups and straps to the
decimator (33% of the picture missing at 3 mm closing); Blender's voxel remesh (shreds a non-watertight input).

| Model | Tris | Frame in Unity |
|---|---|---|
| Saddle | 8 998 (4 499 faces, each twice; 15 003 vertices) | 1.06 m across the stirrups, 0.76 m from the horn to the stirrup treads, 0.56 m long; the horn faces +Z and the origin is the rider's seat point, 1.588 m above and 0.244 m behind the horse's origin |

## On the horse

`Saddle` is a child of the horse's `Model`, a **SkinnedMeshRenderer with two bones**: Torso2 (the bone the rider is glued
to) from the seat forward, Torso behind the seat, a smoothstep between them over Z -0.42..-0.26. The generated mesh asset is
`SaddleSkinned.asset` (the FBX's mesh plus those weights and the bind poses); `DemoSaddleBuilder` rewrites it in place.

Why two bones: hung off Torso2 alone, the saddle is fine standing and galloping (the spine bones move as one in the
gallop clip) but the walk bends the spine at the Torso/Torso2 joint, under the middle of the saddle, and the hide behind it
moved up to 4 cm against a rigid saddle and showed through the skirts (1 159 hide vertices past the leather by more than 3 mm
over 24 walk frames, worst 4.2 cm). With the blend: 162 and 1.5 cm walking, 18 and 1.1 cm galloping (measured against the
horse's own clips, hide vertices within 6 cm of the leather, their outward motion minus the clearance they started with).
The dish is at full Torso2 weight so it stays with the rider's pelvis.

Known limit: the hide under the rear skirts follows the hip bones, which rock 5-14 cm against any spine bone in the walk and
gallop clips (measured against Torso2, Torso, Back and Body alike), so the rear skirts float above the croup, or sink a few
centimetres into it, at some phases. Skinning the saddle to every bone the hide uses (Gaussian average of the hide's weights)
tracks it to 0.3 cm but stretches edges by up to 11 cm and smears the conchos, so it was not used. A saddle blanket under the
skirts would be the physical answer.

It is not a piece of equipment and is not drawn on the rider's own model: a horse that has been called up and a horse
standing in the village wear it the same way. No collider, no network identity.

## Re-running

If the horse mesh, its seat bone, the riding clip or the rider's leg spread changes, run `Dump Saddle Fit Inputs`,
`python fit_saddle.py`, `blender ... saddle.py`, then `Build Saddle`. Changing only the saddle's own look (a new glb) means the
same without the dump. `fit_saddle.py` builds a 12 MB signed-distance grid of the horse in `%TEMP%` on first run (about a
minute); delete `saddle_horse_sdf.npz` there after changing the horse. After a new FBX, copy `SeatPoint` from `seat_pt` in
`saddle_fit.json` into `DemoSaddleBuilder`.

## Licence / provenance

Generated with Ludo AI (stated by the user, 2026-10-08, "I generated this saddle model in Ludo"; credited with the weapons,
the shield and the shrine as the same paid account - check that this one was too). Ludo's terms grant the maker a licence to
use, modify, distribute and make derivative works for commercial purposes, which is what the CC0 dedication in
`Demo/CREDITS.md` rests on.
