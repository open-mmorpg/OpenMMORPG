"""Turns the authored Shrine_Wayside.glb into the game-ready FBX beside it.

    blender --background --factory-startup --python export_fbx.py

The three decisions baked in here, and why:

  * **2.60m tall.** The demo's characters are 1.80m, so at this height the crown of the
    stele stands a head and a half over a player and the basin rim comes to 0.73m, which
    is a font you lean over rather than one you step onto. The source is a unit cube, so
    the scale is a decision and not a property of the file.
  * **Origin on the ground at the centre of the footprint**, because that is the point the
    shrine stands on, and the point the kit measures its activate range from.
  * **Face on +Z**, which is Unity-forward. The model faces -Y in Blender, and
    axis_forward='-Z', axis_up='Y' turns that into +Z.

bake_space_transform=True is the load-bearing one. Without it Blender writes the Y-up
conversion as a 270-degree turn on the exported node instead of into the vertices, and it
lands as 270.02 rather than 270 - so the Unity prefab's root carries a rotation nobody
asked for. Unity's own bakeAxisConversion does not fix this; that is for files authored
Z-up, and on an already-converted file it lays the model on its face.

It also prints the measurements DemoShrineBuilder is written from: the footprint, the
vertical profile the collider boxes come off, and the brass clusters found by reading the
metallic map through the UVs, which is where the fire anchors come from.
"""
import bpy, os, math, json
import numpy as np
from mathutils import Vector

OUT = os.path.dirname(os.path.abspath(__file__))
TARGET_H = 2.60   # metres, crown of the stele

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=os.path.join(OUT, "Shrine_Wayside.glb"))

ob = [o for o in bpy.context.scene.objects if o.type == 'MESH'][0]
bpy.ops.object.select_all(action='DESELECT')
ob.select_set(True); bpy.context.view_layer.objects.active = ob
bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
# drop the glTF wrapper empties
for o in list(bpy.context.scene.objects):
    if o.type == 'EMPTY':
        bpy.data.objects.remove(o, do_unlink=True)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

def bbox():
    v = np.array([v.co[:] for v in ob.data.vertices])
    return v.min(axis=0), v.max(axis=0)

mn, mx = bbox()
H0 = mx[2] - mn[2]
s = TARGET_H / H0
ob.scale = (s, s, s)
bpy.ops.object.transform_apply(scale=True)

# Origin: centred on the footprint in X/Y, on the ground in Z. A shrine is placed by
# where it stands, so its pivot is the point it stands on, and the kit measures an
# entity's activate range from that pivot.
mn, mx = bbox()
cx = (mn[0] + mx[0]) * 0.5
cy = (mn[1] + mx[1]) * 0.5
ob.location = (-cx, -cy, -mn[2])
bpy.ops.object.transform_apply(location=True)
mn, mx = bbox()
print("FINAL_BBOX", json.dumps({"min": [round(float(x),4) for x in mn], "max": [round(float(x),4) for x in mx],
                               "size": [round(float(x),4) for x in (mx-mn)], "scale_applied": round(s,6)}))

# names Unity will see
ob.name = "Shrine_Wayside"
ob.data.name = "Shrine_Wayside"
mat = ob.data.materials[0]
mat.name = "T_Shrine"

# --- landmarks, in the final metric Blender frame --------------------------------
# The brass is found in the metallic map rather than guessed at: the vertices whose UV
# lands on a metallic texel, clustered. The two front caps are where the shrine's fires
# belong - the model has sockets for them, so it does not need braziers stood beside it.
me = ob.data
uvl = me.uv_layers.active.data
img = None
for n in mat.node_tree.nodes:
    if n.type == 'TEX_IMAGE' and n.image and n.image.size[0]:
        # metallic/roughness is the one feeding Metallic
        for l in n.outputs[0].links:
            if l.to_socket.name in ('Metallic', 'Roughness'):
                img = n.image
if img is None:
    img = [n.image for n in mat.node_tree.nodes if n.type=='TEX_IMAGE' and n.image][-1]
W, Hh = img.size
px = np.array(img.pixels[:]).reshape(Hh, W, 4)
verts = np.array([v.co[:] for v in me.vertices])
met = np.zeros(len(verts))
for poly in me.polygons:
    for li in poly.loop_indices:
        vi = me.loops[li].vertex_index
        u, vv = uvl[li].uv
        ix = min(max(int(u * (W - 1)), 0), W - 1)
        iy = min(max(int(vv * (Hh - 1)), 0), Hh - 1)
        met[vi] = max(met[vi], px[iy, ix, 2])
sel = verts[met > 0.5]
print("metal verts", len(sel))
pts = sel.copy(); used = np.zeros(len(pts), bool); clusters = []
for i in range(len(pts)):
    if used[i]: continue
    st = [i]; used[i] = True; grp = [i]
    while st:
        k = st.pop()
        nb = np.where((np.linalg.norm(pts - pts[k], axis=1) < 0.18) & (~used))[0]
        for n in nb: used[n] = True; st.append(n); grp.append(n)
    clusters.append(np.array(grp))
clusters.sort(key=len, reverse=True)
info = []
for c in clusters[:6]:
    p = pts[c]
    info.append({"n": int(len(c)), "centre": [round(float(x),3) for x in p.mean(axis=0)],
                 "top_z": round(float(p[:,2].max()),3),
                 "min": [round(float(x),3) for x in p.min(axis=0)],
                 "max": [round(float(x),3) for x in p.max(axis=0)]})
print("BRASS", json.dumps(info))

# basin rim: the central bowl
v = verts
m = (np.abs(v[:,0]) < 0.45) & (np.abs(v[:,1]) < 0.55) & (v[:,2] > 0.45) & (v[:,2] < 0.90)
print("BASIN", json.dumps({"n": int(m.sum()),
                           "z": [round(float(v[m][:,2].min()),3), round(float(v[m][:,2].max()),3)],
                           "x": [round(float(v[m][:,0].min()),3), round(float(v[m][:,0].max()),3)],
                           "y": [round(float(v[m][:,1].min()),3), round(float(v[m][:,1].max()),3)]}))

# vertical profile of the footprint, for the collider boxes
prof = []
for z0 in np.arange(0, TARGET_H, 0.10):
    m2 = (v[:,2] >= z0) & (v[:,2] < z0 + 0.10)
    if m2.sum() < 3: continue
    p = v[m2]
    prof.append([round(float(z0),2), round(float(p[:,0].min()),3), round(float(p[:,0].max()),3),
                 round(float(p[:,1].min()),3), round(float(p[:,1].max()),3), int(m2.sum())])
print("PROFILE", json.dumps(prof))

# --- export ----------------------------------------------------------------------
bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True)
bpy.context.view_layer.objects.active = ob
path = os.path.join(OUT, "Shrine_Wayside.fbx")
bpy.ops.export_scene.fbx(
    filepath=path, use_selection=True,
    apply_scale_options='FBX_SCALE_ALL',      # conversion in the unit header: fileScale 1, identity scales
    axis_forward='-Z', axis_up='Y',           # Blender -Y ends up Unity +Z
    object_types={'MESH'}, use_mesh_modifiers=True,
    mesh_smooth_type='FACE', use_tspace=False,
    bake_anim=False, path_mode='STRIP', embed_textures=False,
    bake_space_transform=True,   # conversion into the vertices, so Unity's root node is identity
)
print("EXPORTED", path, os.path.getsize(path))
