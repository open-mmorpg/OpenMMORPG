# Fit saddle.glb to the demo horse and its seated rider.
#
#   python fit_saddle.py            (needs numpy, scipy, trimesh; ~1 minute)
#
# Inputs (Open MMORPG > Demo > Dump Saddle Fit Inputs writes them): dump_horse_*.bin and dump_rider_*.bin, the horse's
# skinned mesh in its bind pose and the male rider in the riding pose, plus dump_rider.json (hips, leg spread).
# Outputs: saddle_fit.npy (one row per glb vertex: the fitted high-poly, in the horse's entity space),
# saddle_fit.json (where everything ended up) and small_faces.npy (faces left out of the low-poly), which saddle.py
# turns into the game-ready model.
#
# Frames. glb: gx along the saddle (horn at +gx), gy up, gz across. Unity entity space: X across, Y up, Z the horse's
# heading. The horn goes to +Z, so a glb point (gx, gy, gz) lands on (gz, gy, gx) before scaling.
#
# The fit, in the order it is applied:
#   1 place   : non-uniform scale (the horse is far broader and shorter-backed than the saddle was generated for),
#               nose-down pitch, then dropped onto the spine until the underside of the tree is 4 mm clear.
#   2 seat    : the lowest point of the dish is where the rider's pelvis rests; the vehicle's Seat anchor follows.
#   3 swing   : the fender and stirrup assembly rotates aft about a lateral hinge (the rider's legs are carried
#               behind the saddle's own stirrups) until the stirrup treads sit under the rider's feet.
#   4 drape   : every vertex is pushed sideways, by a smooth field of (y, z), until the leather's inner skin is just
#               outside the horse's barrel; the stirrups move rigidly out to the feet.
import numpy as np, trimesh, json, sys, os, struct, time
import scipy.sparse as sp
from scipy.sparse.csgraph import connected_components
from scipy.interpolate import RegularGridInterpolator
from scipy.spatial import cKDTree

HERE = os.path.dirname(os.path.abspath(__file__))
os.chdir(HERE)

P = dict(sx=0.76, sy=0.82, sz=1.30,            # length, height and breadth scale of the glb
         pitch=4.0, tz=-0.26,                  # nose-down degrees; where the glb origin lands along the horse
         pivot_gx=0.16, pivot_gy=0.12,         # the fender hinge, in glb units
         gap=0.018, apex_clear=0.010,          # leather outside the hide; tree above the spine (see README: the walk)
         x_trim=-0.03, pct=15.0, r1=0.035,     # stirrup centre inboard of the foot; drape: percentile / radius
         ex0=-0.22, ex1=-0.30)                 # glb gy range over which straps blend into the rigid stirrup
for a in sys.argv[1:]:
    k, v = a.split('='); P[k] = float(v)
GY = 0.234                                      # glb height of the underside of the tree at the spine


def rd(fn, kind):
    b = open(fn, 'rb').read(); n = struct.unpack('<i', b[:4])[0]
    if kind == 'v':
        return np.frombuffer(b[4:4 + n * 12], dtype='<f4').reshape(n, 3).astype(float)
    return np.frombuffer(b[4:4 + n * 4], dtype='<i4')


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t)


# ---- horse signed distance (inside positive), sampled on a 1 cm grid --------------------------------
hv = rd('dump_horse_verts.bin', 'v'); ht = rd('dump_horse_tris.bin', 'i').reshape(-1, 3)
horse = trimesh.Trimesh(hv, ht, process=False); horse.merge_vertices(digits_vertex=4)
SDF_CACHE = os.path.join(os.environ.get('TEMP', HERE), 'saddle_horse_sdf.npz')
if os.path.exists(SDF_CACHE):
    Z = np.load(SDF_CACHE)
else:
    t0 = time.time()
    tri = horse.vertices[horse.faces]
    n = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]); area = np.linalg.norm(n, axis=1) / 2; n /= np.linalg.norm(n, axis=1)[:, None]
    rng = np.random.default_rng(1); N = 1_500_000
    fi = rng.choice(len(horse.faces), size=N, p=area / area.sum())
    r1 = np.sqrt(rng.random(N)); r2 = rng.random(N)
    S = (1 - r1)[:, None] * tri[fi, 0] + (r1 * (1 - r2))[:, None] * tri[fi, 1] + (r1 * r2)[:, None] * tri[fi, 2]
    NN = n[fi]; tree = cKDTree(S)
    def sdf_pts(p):
        d, i = tree.query(p, workers=-1)
        s = np.einsum('ij,ij->i', p - S[i], NN[i])
        return np.where(s < 0, d, -d)
    if sdf_pts(np.array([[0, 1.9, -0.1]]))[0] > 0:        # winding came out inward: flip the sample normals
        NN = -NN
    xs = np.arange(-0.5, 0.5001, 0.01); ys = np.arange(0.80, 1.901, 0.01); zs = np.arange(-0.85, 0.551, 0.01)
    G = np.stack(np.meshgrid(xs, ys, zs, indexing='ij'), -1).reshape(-1, 3)
    np.savez(SDF_CACHE, xs=xs, ys=ys, zs=zs, sdf=sdf_pts(G).reshape(len(xs), len(ys), len(zs)))
    Z = np.load(SDF_CACHE); print("horse sdf %.0fs" % (time.time() - t0))
xs, ys, zs, SDF = Z['xs'], Z['ys'], Z['zs'], Z['sdf']
sd = RegularGridInterpolator((xs, ys, zs), SDF, bounds_error=False, fill_value=-1.0)
ix0 = np.argmin(np.abs(xs)); HW = np.zeros((len(ys), len(zs)))        # half-width of the barrel at (y, z)
for j in range(len(ys)):
    for k in range(len(zs)):
        col = SDF[ix0:, j, k]
        if col[0] <= 0:
            continue
        neg = np.where(col <= 0)[0]
        if len(neg) == 0:
            HW[j, k] = xs[-1]; continue
        i = neg[0]; a, b = col[i - 1], col[i]; HW[j, k] = xs[ix0 + i - 1] + (xs[ix0 + i] - xs[ix0 + i - 1]) * a / (a - b)
hw_i = RegularGridInterpolator((ys, zs), HW, bounds_error=False, fill_value=0.0)

# ---- the saddle ---------------------------------------------------------------------------------------
sc = trimesh.load('saddle.glb'); g = list(sc.geometry.values())[0]
V0 = np.array(g.vertices); F0 = np.array(g.faces); assert len(V0) == 47075 and len(F0) == 51282
m = trimesh.Trimesh(V0, F0, process=False); FN = m.face_normals; fc = V0[F0].mean(1)
m.merge_vertices(digits_vertex=5)
lab = connected_components(sp.coo_matrix((np.ones(len(m.face_adjacency)), (m.face_adjacency[:, 0], m.face_adjacency[:, 1])),
                                         shape=(len(F0), len(F0))), directed=False)[1]
small = np.where(np.bincount(lab)[lab] < 100)[0]
np.save('small_faces.npy', small.astype(np.int32))
print("faces left out of the low-poly (bodies under 100 faces):", len(small))

# ---- rider --------------------------------------------------------------------------------------------
RV = rd('dump_rider_verts.bin', 'v'); RJ = json.load(open('dump_rider.json')); HIPS = np.array(RJ['hips'])


def place(V):
    gx, gy, gz = V[:, 0], V[:, 1] - GY, V[:, 2]
    X = gz * P['sz']; Y = gy * P['sy']; Zc = gx * P['sx']
    p = np.radians(P['pitch']); c, s = np.cos(p), np.sin(p)
    return np.stack([X, Y * c - Zc * s, Zc * c + Y * s], 1)


# 1. drop on the spine
apex_v = np.unique(F0[(FN[:, 1] < -0.4) & (np.abs(fc[:, 2]) < 0.07) & (fc[:, 1] > 0.2) & (fc[:, 1] < 0.31)].ravel())
U = place(V0)
lo, hi = 1.2, 1.9
for _ in range(24):
    mid = (lo + hi) / 2
    if sd(U[apex_v] + np.array([0, mid, P['tz']])).max() > -P['apex_clear']: lo = mid
    else: hi = mid
ty = hi; off = np.array([0, ty, P['tz']])

# 2. seat: the dish, and the Seat anchor that puts the rider's pelvis on it
sv = (np.abs(V0[:, 2]) < 0.03) & (np.abs(V0[:, 0]) < 0.02) & (V0[:, 1] < 0.4)
dish = U[np.where(sv)[0][np.argmax(V0[sv, 1])]] + off
mk = (RV[:, 1] < HIPS[1]) & (np.hypot(RV[:, 0] - HIPS[0], RV[:, 2] - HIPS[2]) <= 0.22)
contact = np.array([RV[mk, 0].mean(), RV[mk, 1].min(), RV[mk, 2].mean()])      # = DemoMountBuilder.MeasureRiderContact
seat_pt = np.array([0.0, dish[1] - 0.004, dish[2]])
SEAT = seat_pt - contact
RVE = RV + SEAT
print("dish", dish.round(3), "rider contact", contact.round(3), "-> Seat anchor", SEAT.round(3))
foot = {}
for sg in (-1, 1):
    f = RVE[(RV[:, 1] < 0.12) & (np.sign(RV[:, 0]) == sg) & (np.abs(RV[:, 0]) > 0.25)]
    foot[sg] = dict(low=f[f[:, 1] < f[:, 1].min() + 0.08].mean(0), ymin=f[:, 1].min(), xmid=np.abs(f[:, 0]).mean())
tgt_y = np.mean([foot[s]['ymin'] for s in foot]) - 0.012          # tread bottom just under the sole
tgt_z = np.mean([foot[s]['low'][2] for s in foot]); tgt_x = np.mean([foot[s]['xmid'] for s in foot])

# 3. swing the fender and stirrup assembly aft until the treads are under the feet
B = np.where(V0[:, 1] < -0.44)[0]                                  # the stirrup treads
piv = place(np.array([[P['pivot_gx'], P['pivot_gy'], 0.0]]))[0]
wgt = smoothstep(P['pivot_gy'], P['pivot_gy'] - 0.22, V0[:, 1]) * smoothstep(-0.06, 0.06, V0[:, 0])


def swung(phi, dyleg=0.0):
    ph = np.radians(phi); c, s = np.cos(ph), np.sin(ph); d = U - piv
    R = np.stack([d[:, 0], d[:, 1] * c - d[:, 2] * s, d[:, 2] * c + d[:, 1] * s], 1) + piv
    S = U + (R - U) * wgt[:, None]; S[:, 1] += dyleg * wgt
    return S + off


lo, hi = 0.0, 60.0
for _ in range(30):
    mid = (lo + hi) / 2
    if swung(mid)[B, 2].mean() > tgt_z: lo = mid
    else: hi = mid
phi = (lo + hi) / 2; S = swung(phi); dyleg = tgt_y - S[B, 1].mean(); S = swung(phi, dyleg)
print("swing %.1f deg, leather lengthened %.3f m" % (phi, dyleg))

# 4. lateral drape
absx = np.abs(S[:, 0]); yz = S[:, [1, 2]]; tree = cKDTree(yz); cand = absx > 0.04
nb = tree.query_ball_point(yz, P['r1'])
xin = np.array([np.percentile(absx[[i for i in n if cand[i]]], P['pct']) if any(cand[i] for i in n) else np.nan for n in nb])
delta = np.nan_to_num(np.where(hw_i(yz) > 0, np.clip(hw_i(yz) + P['gap'] - xin, 0, None), 0.0))
nb2 = tree.query_ball_point(yz, 0.035)
ds = np.array([delta[n].mean() for n in nb2])
ramp = np.clip(absx / 0.06, 0, 1); sgn = np.sign(S[:, 0])
for _ in range(5):
    Wd = S.copy(); Wd[:, 0] = S[:, 0] + sgn * ds * ramp
    pen = sd(Wd); bad = pen > P['gap']
    if bad.sum() < 5: break
    ds = ds + np.array([np.where(bad, pen, 0.0)[n].max() for n in nb2]) * 0.9
wb = smoothstep(P['ex0'], P['ex1'], V0[:, 1])
D_rigid = float(max(tgt_x + P['x_trim'] - np.abs(S[B, 0]).mean(), 0.0))
Wd = S.copy(); Wd[:, 0] = S[:, 0] + sgn * ((1 - wb) * ds + wb * D_rigid) * ramp
pen = sd(Wd)
print("inside the horse by more than the %.0f mm gap + 4 mm: %d vertices (deepest %.1f mm)" % (P['gap'] * 1000, (pen > P['gap'] + 0.004).sum(), pen.max() * 1000))
print("tread centre |x| %.3f, foot centre %.3f" % (np.abs(Wd[B, 0]).mean(), tgt_x))
rpen = sd(RVE)
print("rider inside the horse by more than 5 mm: %d vertices" % (rpen > 0.005).sum())

np.save('saddle_fit.npy', Wd.astype(np.float32))
json.dump(dict(P={k: float(v) for k, v in P.items()}, ty=float(ty), dish=dish.tolist(), seat_pt=seat_pt.tolist(),
               SEAT=SEAT.tolist(), contact=contact.tolist(), swing_deg=float(phi), leather_lengthened=float(dyleg),
               stirrup_offset=D_rigid, foot_target=[float(tgt_x), float(tgt_y), float(tgt_z)]),
          open('saddle_fit.json', 'w'), indent=1)
print("wrote saddle_fit.npy, saddle_fit.json, small_faces.npy")
