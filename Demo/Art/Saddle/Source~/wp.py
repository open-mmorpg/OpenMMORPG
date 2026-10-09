# Shared helpers for turning a generated .glb weapon into a game-ready Unity prop.
# Run inside Blender (headless): blender --background --factory-startup --python <driver>.py
#
# Pipeline: import glb -> move into the final frame (metres, +Z up, grip at origin) ->
# weld + decimate a low-poly -> unwrap -> bake normal / colour / metallic / roughness from the
# original -> pack for URP (Mask: R metallic, G white AO, A smoothness) -> export FBX.
import bpy, bmesh, math, os
import numpy as np
from mathutils import Vector, Matrix

OUT = None            # set by the driver: folder for intermediate + final files
BAKE_RES = 2048       # baked at this size, delivered at FINAL_RES
FINAL_RES = 1024


def log(*a):
    print("[wp]", *a, flush=True)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == 'MESH']


def import_glb(path):
    bpy.ops.import_scene.gltf(filepath=path)
    obs = mesh_objects()
    assert len(obs) == 1, "expected a single mesh, found %d" % len(obs)
    o = obs[0]
    o.parent = None
    for m in o.constraints:
        o.constraints.remove(m)
    # bake any object transform into the data so the object sits at the origin, identity
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def to_frame(o, matrix):
    """Move the mesh data into the final frame (matrix applies to vertices)."""
    o.data.transform(matrix)
    o.data.update()


def bounds(o):
    vs = [o.matrix_world @ v.co for v in o.data.vertices]
    mn = Vector((min(v[i] for v in vs) for i in range(3)))
    mx = Vector((max(v[i] for v in vs) for i in range(3)))
    return mn, mx


def weld_copy(high, name, dist=1e-5, drop_loose_below=0):
    """Welded duplicate of `high` (data copied), loose bits under N verts removed."""
    low = high.copy()
    low.data = high.data.copy()
    low.name = name
    low.data.name = name
    bpy.context.scene.collection.objects.link(low)
    bm = bmesh.new()
    bm.from_mesh(low.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=dist)
    bmesh.ops.dissolve_degenerate(bm, dist=dist, edges=bm.edges)
    if drop_loose_below:
        seen = set()
        kill = []
        for v in bm.verts:
            if v.index in seen:
                continue
            comp = [v]
            seen.add(v.index)
            i = 0
            while i < len(comp):
                x = comp[i]
                i += 1
                for e in x.link_edges:
                    w = e.other_vert(x)
                    if w.index not in seen:
                        seen.add(w.index)
                        comp.append(w)
            if len(comp) < drop_loose_below:
                kill.extend(comp)
        if kill:
            bmesh.ops.delete(bm, geom=kill, context='VERTS')
            log("dropped loose verts:", len(kill))
    bm.to_mesh(low.data)
    bm.free()
    # drop the glTF custom normals / uv; the low gets its own
    low.data.materials.clear()
    return low


def decimate_to(o, tris, planar=0.0):
    bpy.context.view_layer.objects.active = o
    for ob in bpy.context.selected_objects:
        ob.select_set(False)
    o.select_set(True)
    cur = len(o.data.polygons)
    # polygons here may be tris already (decimate triangulates anyway)
    bm = bmesh.new(); bm.from_mesh(o.data)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.to_mesh(o.data); bm.free()
    cur = len(o.data.polygons)
    m = o.modifiers.new("dec", 'DECIMATE')
    m.decimate_type = 'COLLAPSE'
    m.ratio = min(1.0, tris / cur)
    m.use_collapse_triangulate = True
    bpy.ops.object.modifier_apply(modifier=m.name)
    log("decimated %s: %d -> %d tris (target %d)" % (o.name, cur, len(o.data.polygons), tris))


def tidy(o, sharp_deg=35.0):
    """Recalculate normals outward, smooth shade, sharp edges by angle."""
    bpy.context.view_layer.objects.active = o
    for ob in bpy.context.selected_objects:
        ob.select_set(False)
    o.select_set(True)
    bm = bmesh.new(); bm.from_mesh(o.data)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(o.data); bm.free()
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(sharp_deg), keep_sharp_edges=False)


def tidy_smooth(o, sharp_deg=35.0):
    """Smooth shading with sharp edges by angle, leaving the winding exactly as it is (see orient_by_free_space)."""
    bpy.context.view_layer.objects.active = o
    for ob in bpy.context.selected_objects:
        ob.select_set(False)
    o.select_set(True)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(sharp_deg), keep_sharp_edges=False)


def unwrap(o, margin=0.012, angle=66.0, keep_corner=0.0):
    """Smart UV project + pack. keep_corner > 0 leaves an (empty) strip of that width at u,v > 1-keep_corner."""
    me = o.data
    while me.uv_layers:
        me.uv_layers.remove(me.uv_layers[0])
    me.uv_layers.new(name="UVMap")
    bpy.context.view_layer.objects.active = o
    for ob in bpy.context.selected_objects:
        ob.select_set(False)
    o.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(angle), island_margin=margin, area_weight=0.0,
                             correct_aspect=True, scale_to_bounds=False)
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.pack_islands(rotate=True, margin=margin)
    bpy.ops.object.mode_set(mode='OBJECT')
    if keep_corner > 0:
        s = 1.0 - keep_corner
        uv = me.uv_layers.active.data
        for l in uv:
            l.uv = (l.uv[0] * s, l.uv[1] * s)


def set_cycles(samples=8):
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.cycles.samples = samples
    sc.cycles.use_denoising = False
    b = sc.render.bake
    b.use_pass_direct = False
    b.use_pass_indirect = False
    b.use_pass_color = False
    b.margin = 16
    b.margin_type = 'EXTEND'


def new_image(name, res, srgb):
    img = bpy.data.images.new(name, res, res, alpha=False, float_buffer=False, is_data=not srgb)
    return img


def make_target_material(low, img):
    mat = bpy.data.materials.new("bake_target")
    mat.use_nodes = True
    nt = mat.node_tree
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = img
    nt.nodes.active = tex
    low.data.materials.clear()
    low.data.materials.append(mat)
    return mat


def high_tex_nodes(high):
    mat = high.data.materials[0]
    d = {}
    for n in mat.node_tree.nodes:
        if n.type == 'TEX_IMAGE':
            d[n.label or n.name] = n.image
    return d


def emit_material(name, image, channel=None, srgb=True):
    """Material that emits an image (or one channel of it, as grey) - for baking raw texture data."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    em = nt.nodes.new('ShaderNodeEmission')
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = image
    tex.interpolation = 'Linear'
    if channel is None:
        nt.links.new(tex.outputs['Color'], em.inputs['Color'])
    else:
        sep = nt.nodes.new('ShaderNodeSeparateColor')
        nt.links.new(tex.outputs['Color'], sep.inputs['Color'])
        nt.links.new(sep.outputs[channel], em.inputs['Color'])
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
    return mat


def bake_select(high, low):
    bpy.ops.object.select_all(action='DESELECT')
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low


def do_bake(high, low, kind, extrusion, max_dist, samples=8):
    b = bpy.context.scene.render.bake
    b.use_selected_to_active = True
    b.cage_extrusion = extrusion
    b.max_ray_distance = max_dist
    b.use_cage = False
    if kind == 'NORMAL':
        b.normal_space = 'TANGENT'
        b.normal_r, b.normal_g, b.normal_b = 'POS_X', 'POS_Y', 'POS_Z'
    bpy.context.scene.cycles.samples = samples
    bake_select(high, low)
    bpy.ops.object.bake(type=kind)


def bake_all(high, low, name, extrusion, max_dist, res=BAKE_RES, hide_for_low=None):
    """Bake normal + base colour + roughness + metallic from `high` onto `low`. Returns dict of images."""
    set_cycles()
    imgs = high_tex_nodes(high)
    col_img = imgs['BASE COLOR']
    mr_img = imgs['METALLIC ROUGHNESS']
    nrm_img = imgs['NORMAL MAP']
    result = {}
    high_orig_mat = high.data.materials[0]

    # --- normal: the high's own material (it has the generator's normal map)
    t = new_image(name + "_n", res, srgb=False)
    make_target_material(low, t)
    do_bake(high, low, 'NORMAL', extrusion, max_dist, samples=16)
    result['normal'] = t

    # --- raw channels through emission
    for key, (src, ch, srgb) in {
        'color': (col_img, None, True),
        'rough': (mr_img, 'Green', False),
        'metal': (mr_img, 'Blue', False),
    }.items():
        t = new_image(name + "_" + key, res, srgb=srgb)
        make_target_material(low, t)
        high.data.materials.clear()
        high.data.materials.append(emit_material("emit_" + key, src, ch))
        do_bake(high, low, 'EMIT', extrusion, max_dist, samples=4)
        result[key] = t
    high.data.materials.clear()
    high.data.materials.append(high_orig_mat)
    return result


def px(img):
    a = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(img.size[1], img.size[0], 4)


def down(a, factor):
    h, w, c = a.shape
    return a.reshape(h // factor, factor, w // factor, factor, c).mean(axis=(1, 3))


def write_png(path, arr, srgb, alpha=False):
    h, w, _ = arr.shape
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=True, float_buffer=False, is_data=not srgb)
    img.alpha_mode = 'CHANNEL_PACKED' if alpha else 'STRAIGHT'
    img.pixels.foreach_set(arr.astype(np.float32).reshape(-1))
    img.filepath_raw = path
    img.file_format = 'PNG'
    scn = bpy.context.scene
    scn.render.image_settings.color_mode = 'RGBA' if alpha else 'RGB'
    scn.render.image_settings.compression = 90
    img.save()
    bpy.data.images.remove(img)


def pack_and_save(baked, outdir, name, final=FINAL_RES, paint_patch=None):
    """paint_patch: dict(u0,v0,u1,v1, color(rgb linear-ish sRGB-encoded), rough, metal) painted after the bake.
    Writes <name>_BaseColor.png / _Mask.png / _Normal.png into outdir."""
    f = BAKE_RES // final
    color = px(baked['color'])      # sRGB byte image: pixels come back sRGB-encoded
    normal = px(baked['normal'])
    rough = px(baked['rough'])
    metal = px(baked['metal'])
    col = down(color, f)[..., :3]
    nor = down(normal, f)[..., :3]
    r = down(rough, f)[..., 0]
    m = down(metal, f)[..., 0]
    mask = np.zeros((final, final, 4), dtype=np.float32)
    mask[..., 0] = m
    mask[..., 1] = 1.0
    mask[..., 2] = 0.0
    mask[..., 3] = 1.0 - r            # URP smoothness
    colA = np.ones((final, final, 4), dtype=np.float32)
    colA[..., :3] = col
    norA = np.ones((final, final, 4), dtype=np.float32)
    norA[..., :3] = nor
    if paint_patch:
        p = paint_patch
        x0, x1 = int(p['u0'] * final), int(p['u1'] * final)
        y0, y1 = int(p['v0'] * final), int(p['v1'] * final)
        colA[y0:y1, x0:x1, :3] = p['color']
        mask[y0:y1, x0:x1, 0] = p['metal']
        mask[y0:y1, x0:x1, 3] = 1.0 - p['rough']
        norA[y0:y1, x0:x1, :3] = (0.5, 0.5, 1.0)
    os.makedirs(outdir, exist_ok=True)
    write_png(os.path.join(outdir, name + "_BaseColor.png"), colA, True)
    write_png(os.path.join(outdir, name + "_Mask.png"), mask, False, alpha=True)
    write_png(os.path.join(outdir, name + "_Normal.png"), norA, False)
    log("textures written for", name)
    return colA, mask, norA


def qa_material(name, outdir, tex_name):
    """Material that reads the delivered textures the way URP will."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')

    def load(suffix, srgb):
        im = bpy.data.images.load(os.path.join(outdir, tex_name + suffix), check_existing=False)
        im.colorspace_settings.name = 'sRGB' if srgb else 'Non-Color'
        return im
    t_c = nt.nodes.new('ShaderNodeTexImage'); t_c.image = load("_BaseColor.png", True)
    t_m = nt.nodes.new('ShaderNodeTexImage'); t_m.image = load("_Mask.png", False)
    t_m.image.alpha_mode = 'CHANNEL_PACKED'
    t_n = nt.nodes.new('ShaderNodeTexImage'); t_n.image = load("_Normal.png", False)
    sep = nt.nodes.new('ShaderNodeSeparateColor')
    inv = nt.nodes.new('ShaderNodeMath'); inv.operation = 'SUBTRACT'; inv.inputs[0].default_value = 1.0
    nm = nt.nodes.new('ShaderNodeNormalMap')
    nt.links.new(t_c.outputs['Color'], bsdf.inputs['Base Color'])
    nt.links.new(t_m.outputs['Color'], sep.inputs['Color'])
    nt.links.new(sep.outputs['Red'], bsdf.inputs['Metallic'])
    nt.links.new(t_m.outputs['Alpha'], inv.inputs[1])
    nt.links.new(inv.outputs['Value'], bsdf.inputs['Roughness'])
    nt.links.new(t_n.outputs['Color'], nm.inputs['Color'])
    nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    return mat


def render_qa(objs, path_prefix, views, size=512, engine='CYCLES', samples=24, ortho=None):
    """Render each object set from the given view directions; returns list of png paths."""
    sc = bpy.context.scene
    sc.render.engine = engine
    if engine == 'CYCLES':
        sc.cycles.device = 'CPU'
        sc.cycles.samples = samples
        sc.cycles.use_denoising = False
    sc.render.resolution_x = size
    sc.render.resolution_y = size
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGB'
    if sc.world is None:
        sc.world = bpy.data.worlds.new("w")
    sc.world.use_nodes = True
    bg = sc.world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.55, 0.57, 0.62, 1)
    bg.inputs['Strength'].default_value = 0.9
    for n in [o for o in sc.objects if o.type in ('CAMERA', 'LIGHT')]:
        bpy.data.objects.remove(n)
    cam = bpy.data.objects.new("qa_cam", bpy.data.cameras.new("qa_cam"))
    cam.data.type = 'ORTHO'
    sc.collection.objects.link(cam)
    sc.camera = cam
    sun = bpy.data.objects.new("qa_sun", bpy.data.lights.new("qa_sun", 'SUN'))
    sun.data.energy = 3.0
    sun.rotation_euler = (math.radians(50), 0, math.radians(35))
    sc.collection.objects.link(sun)
    vs = []
    for o in objs:
        vs += [o.matrix_world @ Vector(c) for c in o.bound_box]
    mn = Vector((min(v[i] for v in vs) for i in range(3)))
    mx = Vector((max(v[i] for v in vs) for i in range(3)))
    center = (mn + mx) / 2
    scale = ortho or max((mx - mn)) * 1.12
    paths = []
    hidden = {o.name: o.hide_render for o in sc.objects}
    for i, d in enumerate(views):
        d = Vector(d).normalized()
        cam.location = center + d * 6
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        cam.data.ortho_scale = scale
        cam.data.clip_end = 50
        p = "%s_%d.png" % (path_prefix, i)
        sc.render.filepath = p
        bpy.ops.render.render(write_still=True)
        paths.append(p)
    for k, v in hidden.items():
        if k in bpy.data.objects:
            bpy.data.objects[k].hide_render = v
    return paths


def sheet(paths, out):
    imgs = [bpy.data.images.load(p, check_existing=False) for p in paths]
    arrs = [px(i) for i in imgs]
    row = np.concatenate(arrs, axis=1)
    h, w, _ = row.shape
    im = bpy.data.images.new("sheet", w, h, alpha=True)
    im.pixels.foreach_set(row.reshape(-1))
    im.filepath_raw = out
    im.file_format = 'PNG'
    im.save()
    for i in imgs:
        bpy.data.images.remove(i)


def export_fbx(obj, path):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, global_scale=1.0, apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_ALL', bake_space_transform=True,
        axis_forward='-Z', axis_up='Y', object_types={'MESH'}, use_mesh_modifiers=True,
        mesh_smooth_type='EDGE', use_tspace=True, add_leaf_bones=False,
        path_mode='STRIP', embed_textures=False, bake_anim=False)
    log("exported", path)


def finish(obj, name, outdir):
    """Name the object, mesh and material after the weapon (Unity binds <name>.mat by it) and export the FBX."""
    obj.name = name
    obj.data.name = name
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    os.makedirs(outdir, exist_ok=True)
    export_fbx(obj, os.path.join(outdir, name + ".fbx"))


def drop_small_bodies(o, min_faces):
    """Delete every edge-connected body of fewer than `min_faces` faces (stitch beads, studs) from a low-poly.
    Vertex-welded bodies still count as separate when they share no edge, which is what decimation cares about:
    each one is an island it can never collapse below a few faces."""
    bm = bmesh.new(); bm.from_mesh(o.data)
    bm.faces.ensure_lookup_table()
    seen = set(); kill = []; bodies = 0
    for f in bm.faces:
        if f.index in seen:
            continue
        comp = [f]; seen.add(f.index); i = 0
        while i < len(comp):
            x = comp[i]; i += 1
            for e in x.edges:
                for g in e.link_faces:
                    if g.index not in seen:
                        seen.add(g.index); comp.append(g)
        bodies += 1
        if len(comp) < min_faces:
            kill.extend(comp)
    if kill:
        bmesh.ops.delete(bm, geom=kill, context='FACES')
    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context='VERTS')
    bm.to_mesh(o.data); bm.free()
    log("dropped %d faces of %d small bodies; %d faces left" % (len(kill), bodies, len(o.data.polygons)))


def unwrap_seams(o, sharp_deg=55.0, margin=0.004, method='ANGLE_BASED'):
    """Unwrap along seams cut at sharp edges (dihedral over `sharp_deg`) and open boundaries, then pack.
    smart_project fragments a decimated, irregular mesh into thousands of tiny islands; cutting only where the
    surface really bends keeps the islands large and the texel density even. Returns the island count."""
    import math
    me = o.data
    while me.uv_layers:
        me.uv_layers.remove(me.uv_layers[0])
    me.uv_layers.new(name="UVMap")
    bm = bmesh.new(); bm.from_mesh(me)
    lim = math.radians(sharp_deg)
    for e in bm.edges:
        e.seam = (not e.is_manifold) or (len(e.link_faces) == 2 and e.calc_face_angle(0.0) > lim)
    # island count = face regions not crossing a seam
    bm.faces.ensure_lookup_table()
    seen = set(); islands = 0
    for f in bm.faces:
        if f.index in seen:
            continue
        islands += 1; stack = [f]; seen.add(f.index)
        while stack:
            x = stack.pop()
            for e in x.edges:
                if e.seam:
                    continue
                for g in e.link_faces:
                    if g.index not in seen:
                        seen.add(g.index); stack.append(g)
    bm.to_mesh(me); bm.free()
    bpy.context.view_layer.objects.active = o
    for ob in bpy.context.selected_objects:
        ob.select_set(False)
    o.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.unwrap(method=method, margin=margin)
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.pack_islands(rotate=True, margin=margin)
    bpy.ops.object.mode_set(mode='OBJECT')
    log("unwrap_seams: %d islands (sharp %.0f deg)" % (islands, sharp_deg))
    return islands


def uv_report(o):
    """Texel-density spread, flipped faces and UV coverage of the active UV map."""
    me = o.data; uv = me.uv_layers.active.data
    ratios = []; flipped = 0; used = 0.0
    for p in me.polygons:
        if len(p.loop_indices) != 3:
            continue
        a, b, c = [uv[i].uv for i in p.loop_indices]
        sa = ((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) / 2
        used += abs(sa)
        if sa < 0:
            flipped += 1
        if p.area > 1e-9:
            ratios.append(abs(sa) / p.area)
    r = np.array(ratios); m = np.median(r)
    q = np.percentile(r / m, [5, 25, 75, 95])
    log("uv: flipped %d of %d, coverage %.2f, density vs median p5 %.2f p25 %.2f p75 %.2f p95 %.2f" % (flipped, len(r), used, q[0], q[1], q[2], q[3]))


def orient_by_free_space(o, probe=0.002, reach=0.5):
    """Point every face of `o` at the open air, not at the leather behind it.

    A generated mesh cannot be trusted for winding (this saddle's is inconsistent: 73% of faces point away from their
    neighbours, and its small closed bodies are inside-out about as often as not, which is why the glb is marked
    double-sided), and `recalc_face_normals` only decides per connected piece, so one piece fused to the rest by a
    non-manifold edge flips whole plates. So decide face by face: probe a ray `probe` off each side of the face and
    keep the normal on the side whose ray travels further. A skin of a 1.5 cm plate sees the other skin 1.5 cm away
    on its inner side and the room on its outer side; a face with open air both ways (a loose sheet) is left alone.
    Returns (flipped, ambiguous)."""
    from mathutils.bvhtree import BVHTree
    bm = bmesh.new(); bm.from_mesh(o.data); bm.faces.ensure_lookup_table()
    bvh = BVHTree.FromBMesh(bm)
    flip = []; ambiguous = 0
    for f in bm.faces:
        c = f.calc_center_median(); n = f.normal
        far = []
        for sign in (1.0, -1.0):
            hit = bvh.ray_cast(c + n * (sign * probe), n * sign, reach)
            far.append(reach if hit[0] is None else hit[3])
        if far[0] >= reach * 0.99 and far[1] >= reach * 0.99:
            ambiguous += 1
        elif far[1] > far[0]:
            flip.append(f)
    if flip:
        bmesh.ops.reverse_faces(bm, faces=flip)
    bm.to_mesh(o.data); bm.free()
    log("orient: flipped %d of %d faces, %d open on both sides" % (len(flip), len(o.data.polygons), ambiguous))
    return len(flip), ambiguous


def double_sided(o):
    """Add a second copy of every face, wound the other way, on vertices of its own, so each carries its own normal.
    The UVs are shared. Seen from either side a face is a front face, with a normal that points at the viewer; where the
    decimated mesh has a hole, the other side of the plate shows through instead of the room behind it."""
    bm = bmesh.new(); bm.from_mesh(o.data)
    n = len(bm.faces)
    dup = bmesh.ops.duplicate(bm, geom=bm.faces[:])
    new_faces = [g for g in dup["geom"] if isinstance(g, bmesh.types.BMFace)]
    bmesh.ops.reverse_faces(bm, faces=new_faces)
    bm.to_mesh(o.data); bm.free()
    bpy.context.view_layer.objects.active = o
    for ob in bpy.context.selected_objects:
        ob.select_set(False)
    o.select_set(True)
    # the copies were shaded from their own (reversed) winding when the object was re-meshed; keep them smooth
    for p in o.data.polygons:
        p.use_smooth = True
    log("double sided: %d -> %d faces" % (n, len(o.data.polygons)))
