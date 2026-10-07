# Quiver: quiver.glb -> Quiver.fbx + Quiver_{BaseColor,Mask,Normal}.png
#
#   blender --background --factory-startup --python quiver.py
#
# The original is a 50k-tri sculpt of a hollow, tapered leather shell with a rolled lip, a strap
# band, a buckle strap, rivets, wire hooks and a metal foot plate. Decimating it wrecked the lip, so
# the low-poly is built by hand instead - the outline of the shell is sampled from the original at a
# series of heights (convex hull of each slice, resampled at N angles) and lofted - and everything
# else (strap, stitching, buckle, plate, lining) is baked from the original.
#
# Final frame (Blender Z-up = Unity Y-up; Unity z is Blender -y, Unity x is Blender -x):
#   * foot to mouth 0.44 m; origin on the mouth's axis at the height of the middle of the opening;
#     the flat face of the shell is the wearer's back, 0.015 m behind the origin (Unity -Z) - 1.5 cm
#     further out than the old, flat pouch's back sat on the SheathBack socket, because this one is
#     wider and its rear strap dipped 2 cm into the shoulder blades;
#   * the depth is scaled to 80% (about 11 cm at the mouth instead of 14): the bow lies over the quiver
#     on the back and, at full depth, sank 7 cm into its collar;
#   * the rounded face with the strap and buckle looks outward (Unity +Z); the rim is cut on a slant,
#     higher against the back and lower on the outside, as in the original;
#   * the loops either side of the strap are modelled as two small wire hooks.
import sys, os, time, math
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
sys.path.insert(0, HERE)
import wp, bpy, bmesh
from mathutils import Vector, Matrix

t0 = time.time()
OUT = os.environ.get("WEAPON_OUT", os.path.dirname(HERE)).replace("\\", "/")
WORK = os.environ.get("WEAPON_WORK")

N = 24                      # sides around the shell
wp.reset()
high = wp.import_glb(HERE + "/quiver.glb")
high.name = "high"
H = [v.co.copy() for v in high.data.vertices]      # glb frame, before anything moves

# ---------------- outlines sampled from the original ----------------
def hull(pts):
    pts = sorted(set((round(p[0], 5), round(p[1], 5)) for p in pts))
    if len(pts) < 3: return pts
    def cross(o, a, b): return (a[0]-o[0])*(b[1]-o[1]) - (a[1]-o[1])*(b[0]-o[0])
    lo = []
    for p in pts:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0: lo.pop()
        lo.append(p)
    up = []
    for p in reversed(pts):
        while len(up) >= 2 and cross(up[-2], up[-1], p) <= 0: up.pop()
        up.append(p)
    return lo[:-1] + up[:-1]

def ray(poly, c, ang):
    d = (math.cos(ang), math.sin(ang)); best = 0.0
    for i in range(len(poly)):
        a = poly[i]; b = poly[(i+1) % len(poly)]
        ex, ey = b[0]-a[0], b[1]-a[1]
        den = d[0]*ey - d[1]*ex
        if abs(den) < 1e-12: continue
        t = ((a[0]-c[0])*ey - (a[1]-c[1])*ex) / den
        u = ((a[0]-c[0])*d[1] - (a[1]-c[1])*d[0]) / den
        if t > 0 and -1e-9 <= u <= 1+1e-9: best = max(best, t)
    return best

def outline(zs, xcap=None, half=0.006):
    pts = [(p.x, p.y) for p in H if abs(p.z - zs) < half and (xcap is None or abs(p.x) < xcap)]
    poly = hull(pts)
    xs = [p[0] for p in poly]; ys = [p[1] for p in poly]
    c = ((min(xs)+max(xs))/2, (min(ys)+max(ys))/2)
    ring = []
    for k in range(N):
        a = 2*math.pi*k/N
        r = ray(poly, c, a)
        ring.append((c[0]+r*math.cos(a), c[1]+r*math.sin(a)))
    return ring

def lerp_ring(a, b, t):
    return [(a[k][0]*(1-t)+b[k][0]*t, a[k][1]*(1-t)+b[k][1]*t) for k in range(N)]

def grow(ring, d):
    cx = sum(p[0] for p in ring)/N; cy = sum(p[1] for p in ring)/N
    out = []
    for p in ring:
        vx, vy = p[0]-cx, p[1]-cy; L = math.hypot(vx, vy) or 1
        out.append((p[0]+vx/L*d, p[1]+vy/L*d))
    return out

def ztop(y):                       # the slanted rim cut, from the original's rim heights
    return min(0.501, 0.4465 - 0.41*y)

S_foot = outline(-0.47)
S = {z: outline(z) for z in (-0.42, -0.37, -0.30, -0.20, -0.10, 0.0, 0.10, 0.16, 0.20, 0.33, 0.37, 0.40)}
S[-0.47] = S_foot
band = outline(0.28, xcap=0.136)
body_rings = [(-0.47, S[-0.47]), (-0.42, S[-0.42]), (-0.37, S[-0.37]), (-0.30, S[-0.30]), (-0.20, S[-0.20]),
              (-0.10, S[-0.10]), (0.0, S[0.0]), (0.10, S[0.10]), (0.17, S[0.16]),
              (0.215, grow(S[0.20], 0.002)), (0.235, band), (0.31, band), (0.322, S[0.33])]
collar = S[0.40]
print("rings outlined; band y span", min(p[1] for p in band), max(p[1] for p in band))

# ---------------- build the shell in the glb frame ----------------
bm = bmesh.new()
def vert(x, y, z): return bm.verts.new((x, y, z))
rows = []
# tip: tiny ring, then up
tipc = (sum(p[0] for p in S_foot)/N, sum(p[1] for p in S_foot)/N)
tip = [vert(tipc[0] + 0.012*math.cos(2*math.pi*k/N), tipc[1] + 0.012*math.sin(2*math.pi*k/N), -0.501) for k in range(N)]
rows.append(tip)
for z, ring in body_rings:
    rows.append([vert(p[0], p[1], z) for p in ring])
# collar: lower edge parallel to the cut, then the cut itself
lipL = [vert(p[0], p[1], max(ztop(p[1]) - 0.075, 0.335)) for p in collar]
lipT = [vert(p[0], p[1], ztop(p[1])) for p in collar]
rows.append(lipL); rows.append(lipT)
for a, b in zip(rows[:-1], rows[1:]):
    for k in range(N):
        bm.faces.new((a[k], a[(k+1) % N], b[(k+1) % N], b[k]))
# foot cap
fc = vert(tipc[0], tipc[1], -0.505)
for k in range(N):
    bm.faces.new((fc, tip[(k+1) % N], tip[k]))
# rim annulus + inner wall + floor
WALL = 0.014
inner_top = grow(collar, -WALL)
I_top = [vert(p[0], p[1], ztop(p[1]) - 0.008) for p in inner_top]
for k in range(N):
    bm.faces.new((lipT[k], lipT[(k+1) % N], I_top[(k+1) % N], I_top[k]))
zfloor = 0.25
inner_deep = grow(collar, -WALL - 0.012)
I_deep = [vert(p[0], p[1], zfloor) for p in inner_deep]
for k in range(N):
    bm.faces.new((I_deep[k], I_top[k], I_top[(k+1) % N], I_deep[(k+1) % N]))
cx = sum(p[0] for p in inner_deep)/N; cy = sum(p[1] for p in inner_deep)/N
ic = vert(cx, cy, zfloor - 0.005)
for k in range(N):
    bm.faces.new((ic, I_deep[k], I_deep[(k+1) % N]))

# ---------------- the two wire hooks that stand off the strap ----------------
def hook(sign, y=-0.060, r=0.0048, sides=6, per=3):
    ctrl = [(0.134, 0.292), (0.165, 0.302), (0.179, 0.276), (0.171, 0.246), (0.149, 0.231), (0.134, 0.236)]
    # Catmull-Rom through the control points
    pts = []
    P = [ctrl[0]] + ctrl + [ctrl[-1]]
    for i in range(1, len(P) - 2):
        p0, p1, p2, p3 = P[i-1], P[i], P[i+1], P[i+2]
        for t in [j / per for j in range(per)]:
            t2, t3 = t*t, t*t*t
            pts.append(tuple(0.5*((2*p1[k]) + (-p0[k]+p2[k])*t + (2*p0[k]-5*p1[k]+4*p2[k]-p3[k])*t2 + (-p0[k]+3*p1[k]-3*p2[k]+p3[k])*t3) for k in range(2)))
    pts.append(ctrl[-1])
    rings = []
    for i, (px_, pz_) in enumerate(pts):
        a = pts[max(0, i-1)]; b = pts[min(len(pts)-1, i+1)]
        tx, tz = b[0]-a[0], b[1]-a[1]; L = math.hypot(tx, tz) or 1; tx, tz = tx/L, tz/L
        n2 = Vector((tz, 0, -tx))          # in-plane normal
        n1 = Vector((0, 1, 0))
        c = Vector((sign*px_, y, pz_))
        n2.x *= sign
        rings.append([bm.verts.new(c + r*(math.cos(2*math.pi*j/sides)*n1 + math.sin(2*math.pi*j/sides)*n2)) for j in range(sides)])
    for ra, rb in zip(rings[:-1], rings[1:]):
        for j in range(sides):
            f = (ra[j], ra[(j+1) % sides], rb[(j+1) % sides], rb[j])
            bm.faces.new(f if sign > 0 else f[::-1])
hook(1); hook(-1)

bmesh.ops.triangulate(bm, faces=bm.faces)
me = bpy.data.meshes.new("low")
bm.to_mesh(me); bm.free()
low = bpy.data.objects.new("low", me)
bpy.context.scene.collection.objects.link(low)

# ---------------- same final frame as the high ----------------
QL = 0.44
Sc = QL / 0.951
MOUTH_Z = 0.45
REAR = 0.015                   # the flat face sits this far behind the origin
KZ = 0.8                       # the shell's depth (Blender y) is scaled to 80%: it lies against the back
M = (Matrix.Translation(Vector((0, REAR - 0.134*KZ*Sc, -MOUTH_Z*Sc))) @
     Matrix.Diagonal(Vector((Sc, KZ*Sc, Sc, 1.0))) @ Matrix.Rotation(math.pi, 4, 'Z'))
for o in (high, low):
    wp.to_frame(o, M)
wp.tidy(low, 35)
wp.unwrap(low, margin=0.008)
print("low tris", len(low.data.polygons), "verts", len(low.data.vertices))
baked = wp.bake_all(high, low, "quiver", extrusion=0.012, max_dist=0.035)
wp.pack_and_save(baked, OUT, "Quiver")
if WORK:
    os.makedirs(WORK, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=WORK + "/quiver_work.blend")
wp.finish(low, "Quiver", OUT)
print("TIME", time.time() - t0)
