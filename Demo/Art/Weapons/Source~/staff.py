# Mage staff: staff.glb -> MageStaff.fbx + MageStaff_{BaseColor,Mask,Normal}.png
#
#   blender --background --factory-startup --python staff.py
#
# Final frame (Blender Z-up, which Unity reads as Y-up): 1.65 m long, the shaft's centre line on
# the Z axis, grip at the origin 0.60 m above the butt, the crystal head toward +Z.
import sys, os, time, math
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
sys.path.insert(0, HERE)
import wp, bpy
from mathutils import Vector, Matrix

t0 = time.time()
# Output goes to Art/Weapons (the folder above this one) unless WEAPON_OUT says otherwise;
# WEAPON_WORK, if set, also receives the .blend.
OUT = os.environ.get("WEAPON_OUT", os.path.dirname(HERE)).replace("\\", "/")
WORK = os.environ.get("WEAPON_WORK")

LENGTH = 1.65
GRIP_FROM_BUTT = 0.60
TRIS = 2600

wp.reset()
high = wp.import_glb(HERE + "/staff.glb")
high.name = "high"

mn, mx = wp.bounds(high)
s = LENGTH / (mx.z - mn.z)
# the shaft's centre at grip height, in the glb's own frame, before scaling
gz = mn.z + GRIP_FROM_BUTT / s
pts = [v.co for v in high.data.vertices if abs(v.co.z - gz) < 0.012]
cx = (min(p.x for p in pts) + max(p.x for p in pts)) / 2
cy = (min(p.y for p in pts) + max(p.y for p in pts)) / 2
wp.to_frame(high, Matrix.Scale(s, 4) @ Matrix.Translation(Vector((-cx, -cy, -gz))))

low = wp.weld_copy(high, "low", dist=2e-5, drop_loose_below=40)
wp.decimate_to(low, TRIS)
wp.tidy(low, 40)
wp.unwrap(low, margin=0.008)

baked = wp.bake_all(high, low, "staff", extrusion=0.012, max_dist=0.04)
wp.pack_and_save(baked, OUT, "MageStaff")
if WORK:
    os.makedirs(WORK, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=WORK + "/staff_work.blend")
wp.finish(low, "MageStaff", OUT)
print("TIME", time.time() - t0)
