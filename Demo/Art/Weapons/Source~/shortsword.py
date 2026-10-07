# Short sword: shortsword.glb -> ShortSword.fbx + ShortSword_{BaseColor,Mask,Normal}.png
#
#   blender --background --factory-startup --python shortsword.py
#
# Final frame (Blender Z-up = Unity Y-up; Unity x is Blender -x, Unity z is Blender -y), the same one every blade in
# the demo is built to: the blade runs along +Y with the tip up, its flat faces Z (so DemoItemBuilder's BladeFacing roll
# makes the edge lead a swing), the crossguard runs along X, and the origin is the middle of the grip, 0.12 m above the
# pommel's end. 0.80 m overall (the dagger it replaces was 0.78).
# In the glb the blade lies along X with the tip at -X, its flat facing +-Y and its edges and guard along Z.
import sys, os, time, math
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
sys.path.insert(0, HERE)
import wp, bpy
from mathutils import Vector, Matrix

t0 = time.time()
OUT = os.environ.get("WEAPON_OUT", os.path.dirname(HERE)).replace("\\", "/")
WORK = os.environ.get("WEAPON_WORK")

LENGTH = 0.80
GRIP_X = 0.35                 # glb x of the middle of the grip (the turned section between guard and pommel)
TRIS = int(os.environ.get("TRIS", "2200"))

wp.reset()
high = wp.import_glb(HERE + "/shortsword.glb")
high.name = "high"

mn, mx = wp.bounds(high)
s = LENGTH / (mx.x - mn.x)
# (x, y, z) -> (z, y, GRIP_X - x), then to metres
R = Matrix(((0, 0, 1, 0), (0, 1, 0, 0), (-1, 0, 0, GRIP_X), (0, 0, 0, 1)))
wp.to_frame(high, Matrix.Scale(s, 4) @ R)

low = wp.weld_copy(high, "low", dist=2e-5, drop_loose_below=40)
wp.decimate_to(low, TRIS)
wp.tidy(low, 40)
wp.unwrap(low, margin=0.008)

baked = wp.bake_all(high, low, "shortsword", extrusion=0.008, max_dist=0.025)
wp.pack_and_save(baked, OUT, "ShortSword")
if WORK:
    os.makedirs(WORK, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=WORK + "/shortsword_work.blend")
wp.finish(low, "ShortSword", OUT)
print("TIME", time.time() - t0)
