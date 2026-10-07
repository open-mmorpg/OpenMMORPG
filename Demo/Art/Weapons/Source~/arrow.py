# Arrow: arrow.glb -> Arrow.fbx + Arrow_{BaseColor,Mask,Normal}.png
#
#   blender --background --factory-startup --python arrow.py
#
# Final frame: 0.72 m long, the shaft on the Z axis, origin at the middle (an arrow is carried and
# nocked at its balance point), broadhead toward +Z, fletching toward -Z.
import sys, os, time, math
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
sys.path.insert(0, HERE)
import wp, bpy
from mathutils import Vector, Matrix

t0 = time.time()
OUT = os.environ.get("WEAPON_OUT", os.path.dirname(HERE)).replace("\\", "/")
WORK = os.environ.get("WEAPON_WORK")

LENGTH = 0.72
TRIS = int(os.environ.get("TRIS", "600"))      # the quiver shows a dozen of them
TEXTURE = 512

wp.reset()
high = wp.import_glb(HERE + "/arrow.glb")
high.name = "high"

# the glb arrow runs along Blender Y with its head at -Y; stand it up with the head at +Z
R = Matrix.Rotation(math.radians(-90), 4, 'X')
mn = Vector((min(v.co[i] for v in high.data.vertices) for i in range(3)))
mx = Vector((max(v.co[i] for v in high.data.vertices) for i in range(3)))
s = LENGTH / (mx.y - mn.y)
mid = (mx.y + mn.y) / 2
shaft = [v.co for v in high.data.vertices if abs(v.co.y - mid) < 0.01]
cx = (min(p.x for p in shaft) + max(p.x for p in shaft)) / 2
cz = (min(p.z for p in shaft) + max(p.z for p in shaft)) / 2
wp.to_frame(high, R @ Matrix.Scale(s, 4) @ Matrix.Translation(Vector((-cx, -mid, -cz))))

low = wp.weld_copy(high, "low", dist=2e-5, drop_loose_below=20)
wp.decimate_to(low, TRIS)
wp.tidy(low, 40)
wp.unwrap(low, margin=0.008)

baked = wp.bake_all(high, low, "arrow", extrusion=0.006, max_dist=0.02)
wp.pack_and_save(baked, OUT, "Arrow", final=TEXTURE)
if WORK:
    os.makedirs(WORK, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=WORK + "/arrow_work.blend")
wp.finish(low, "Arrow", OUT)
print("TIME", time.time() - t0)
