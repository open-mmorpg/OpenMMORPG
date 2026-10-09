# Saddle: saddle.glb + the fit -> Saddle.fbx + Saddle_{BaseColor,Mask,Normal}.png
#
#   python fit_saddle.py
#   blender --background --factory-startup --python saddle.py
#
# saddle_fit.npy is the high-poly saddle already fitted to the horse and rider by fit_saddle.py (scale, pitch, drop on
# the spine, fender/stirrup swing, lateral drape), one row per glb vertex, in the Unity entity frame of DemoHorse
# (X lateral, Y up, Z forward). This script re-centres it on the rider's seat point, builds the low-poly, bakes the glb's
# textures onto it and exports an FBX whose origin is that seat point.
#
# The glb cannot be trusted for topology or winding (see README.md), so the low-poly is made to survive that:
#   * stitch beads and rivet heads (2 298 small bodies) stay in the high-poly for the bake but not in the low-poly;
#   * every face is turned toward the open air by probing (wp.orient_by_free_space), which is what the bake needs;
#   * and the whole thing is then doubled, each face once each way round with its own normal, so a thin plate seen from
#     either side is lit as a front face and no face that decimation or the probe got wrong can open a hole. That is
#     the same job the glb's doubleSided flag does, without a shader that flips normals on back faces (URP Lit has none).
import sys, os, time, json
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
sys.path.insert(0, HERE)
import wp, bpy, bmesh, numpy as np
from mathutils import Vector, Matrix

t0 = time.time()
OUT = os.environ.get("SADDLE_OUT", os.path.dirname(HERE)).replace("\\", "/")
WORK = os.environ.get("SADDLE_WORK")
TRIS = int(os.environ.get("TRIS", "4500"))
EXTR = float(os.environ.get("EXTR", "0.008"))
MAXD = float(os.environ.get("MAXD", "0.03"))
SHARP = float(os.environ.get("SHARP", "40"))
TWO_SIDED = os.environ.get("TWO_SIDED", "1") == "1"

fit = json.load(open(HERE + "/saddle_fit.json"))
seat = np.array(fit["seat_pt"])
V = np.load(HERE + "/saddle_fit.npy") - seat           # Unity frame, origin at the seat point

wp.reset()
high = wp.import_glb(HERE + "/saddle.glb")
high.name = "high"
assert len(high.data.vertices) == len(V), (len(high.data.vertices), len(V))
# Unity (x, y, z) -> Blender (-x, -z, y)
B = np.stack([-V[:, 0], -V[:, 2], V[:, 1]], 1).astype(np.float32)
high.data.vertices.foreach_set("co", B.reshape(-1))
high.data.update()
mn, mx = wp.bounds(high)
wp.log("high bounds", tuple(mn), tuple(mx))

small = np.load(HERE + "/small_faces.npy")
assert len(high.data.polygons) == 51282
bm = bmesh.new(); bm.from_mesh(high.data); bm.faces.ensure_lookup_table()
bmesh.ops.delete(bm, geom=[bm.faces[int(i)] for i in small], context="FACES")
loose = [v for v in bm.verts if not v.link_faces]
if loose:
    bmesh.ops.delete(bm, geom=loose, context="VERTS")
mesh = bpy.data.meshes.new("low"); bm.to_mesh(mesh); bm.free()
low0 = bpy.data.objects.new("low0", mesh); bpy.context.scene.collection.objects.link(low0)
low = wp.weld_copy(low0, "low", dist=2e-5)
bpy.data.objects.remove(low0)
wp.decimate_to(low, TRIS)
wp.orient_by_free_space(low)
wp.tidy_smooth(low, 40)
wp.unwrap_seams(low, SHARP, margin=0.004)
wp.uv_report(low)

baked = wp.bake_all(high, low, "saddle", extrusion=EXTR, max_dist=MAXD)
wp.pack_and_save(baked, OUT, "Saddle")
if TWO_SIDED:
    wp.double_sided(low)
if WORK:
    os.makedirs(WORK, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=WORK + "/saddle_work.blend")
wp.finish(low, "Saddle", OUT)
print("TIME", time.time() - t0)
