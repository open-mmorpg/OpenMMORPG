# Bow: bow.glb -> Bow.fbx + Bow_{BaseColor,Mask,Normal}.png
#
#   blender --background --factory-startup --python bow.py
#
# Final frame (Blender Z-up = Unity Y-up; Unity x is Blender -x):
#   * 1.70 m long, the handle's centre line on the Y axis, +0.058 m along Unity x from the origin -
#     where the previous bow's handle sat, so every tuned grip, sheath and draw value still lands.
#   * The string is on Unity -x (the archer's side), the belly on +x, thickness on Z.
#   * The model's depth (its bend, along Blender X) is halved. At full depth the string was 0.30 m
#     off the handle; the draw code only nocks an arrow when the drawing hand comes within 0.16 m
#     of the string (BowEquipmentEntity.nockRadius), and the draw clips were authored for a string
#     about 0.13 m out. Halved, it is 0.14 m.
#   * The string is a separate mesh piece on purpose: BowEquipmentEntity finds it as a separate
#     connected component and bends it, so it must not share a vertex with the limbs. It is a thin
#     4-sided tube (25 rings, so it can bend into a V); its colour is sampled from the original.
import sys, os, time, math
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
sys.path.insert(0, HERE)
import wp, bpy, bmesh
import numpy as np
from mathutils import Vector, Matrix

t0 = time.time()
OUT = os.environ.get("WEAPON_OUT", os.path.dirname(HERE)).replace("\\", "/")
WORK = os.environ.get("WEAPON_WORK")

LENGTH = 1.70
KX = 0.5                      # depth scale
HANDLE_X_UNITY = 0.058        # where the handle's centre line sits, Unity x
BODY_TRIS = 2400
STRING_RADIUS = 0.0024

wp.reset()
high = wp.import_glb(HERE + "/bow.glb")
high.name = "high"

# ---- the string, in the glb's own frame: a thin tube at x~0.0905 running |z| < 0.41
SX, SY = 0.0905, 0.003
def is_string(co, zmax):
    return (co.x - SX) ** 2 + (co.y - SY) ** 2 < 0.0075 ** 2 and abs(co.z) < zmax

# the colour, roughness and metal the string wears, sampled through the original's UVs
imgs = wp.high_tex_nodes(high)
def sample(img, uvs):
    w, h = img.size
    a = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    a = a.reshape(h, w, 4)
    return np.mean([a[min(h - 1, int(v * h)), min(w - 1, int(u * w))] for u, v in uvs], axis=0)
uvl = high.data.uv_layers.active.data
uvs = [tuple(uvl[lp.index].uv) for lp in high.data.loops
       if high.data.vertices[lp.vertex_index].co.x > 0.0875
       and is_string(high.data.vertices[lp.vertex_index].co, 0.30)]
s_col = sample(imgs['BASE COLOR'], uvs)[:3]
s_mr = sample(imgs['METALLIC ROUGHNESS'], uvs)
s_rough, s_metal = float(s_mr[1]), float(s_mr[2])

# body-only copy of the original (string removed), decimated further down
body_src = high.copy()
body_src.data = high.data.copy()
body_src.name = "body_src"
bpy.context.scene.collection.objects.link(body_src)
bm = bmesh.new()
bm.from_mesh(body_src.data)
bmesh.ops.delete(bm, geom=[v for v in bm.verts if is_string(v.co, 0.37)], context='VERTS')
bm.to_mesh(body_src.data)
bm.free()

# ---- final frame
mn = Vector((min(v.co[i] for v in high.data.vertices) for i in range(3)))
mx = Vector((max(v.co[i] for v in high.data.vertices) for i in range(3)))
s = LENGTH / (mx.z - mn.z)
HANDLE_X = -0.0765            # glb x of the handle's centre line
tx = -HANDLE_X_UNITY - HANDLE_X * KX * s
M = Matrix.Translation(Vector((tx, 0, 0))) @ Matrix.Diagonal(Vector((KX * s, s, s, 1.0)))
for o in (high, body_src):
    wp.to_frame(o, M)

body = wp.weld_copy(body_src, "body", dist=2e-5, drop_loose_below=60)
wp.decimate_to(body, BODY_TRIS)
wp.tidy(body, 40)
wp.unwrap(body, margin=0.008, keep_corner=0.04)    # leaves a corner of the sheet for the string

# ---- string tube
xs = SX * KX * s + tx
Z = 0.4096 * s
RINGS, SIDES = 25, 4
verts, faces = [], []
for i in range(RINGS):
    z = -Z + 2 * Z * i / (RINGS - 1)
    for k in range(SIDES):
        a = 2 * math.pi * k / SIDES + math.pi / 4
        verts.append((xs + STRING_RADIUS * math.cos(a), STRING_RADIUS * math.sin(a), z))
for i in range(RINGS - 1):
    for k in range(SIDES):
        a = i * SIDES + k
        b = i * SIDES + (k + 1) % SIDES
        faces.append((a, b, b + SIDES, a + SIDES))
me = bpy.data.meshes.new("string")
me.from_pydata(verts, [], faces)
me.update()
me.uv_layers.new(name="UVMap")
for l in me.uv_layers.active.data:
    l.uv = (0.986, 0.986)
string = bpy.data.objects.new("string", me)
bpy.context.scene.collection.objects.link(string)
bm = bmesh.new()
bm.from_mesh(me)
bmesh.ops.triangulate(bm, faces=bm.faces)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
bm.to_mesh(me)
bm.free()
for p in me.polygons:
    p.use_smooth = True

# ---- join
bpy.ops.object.select_all(action='DESELECT')
body.select_set(True)
string.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.join()
low = body
low.name = "low"

body_src.hide_render = True
baked = wp.bake_all(high, low, "bow", extrusion=0.012, max_dist=0.04)
patch = dict(u0=0.972, v0=0.972, u1=1.0, v1=1.0, color=tuple(float(c) for c in s_col), rough=s_rough, metal=s_metal)
wp.pack_and_save(baked, OUT, "Bow", paint_patch=patch)
bpy.data.objects.remove(body_src)
if WORK:
    os.makedirs(WORK, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=WORK + "/bow_work.blend")
wp.finish(low, "Bow", OUT)
print("TIME", time.time() - t0)
