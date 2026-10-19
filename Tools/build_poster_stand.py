"""Builds the double-sided poster stand and writes it as an ASCII FBX for Unity.

    python Tools/build_poster_stand.py                 # writes Assets/Models/PosterStand.fbx
    python Tools/build_poster_stand.py --preview DIR   # also renders front/back/angle PNGs

Pure Python, no dependencies. Material slots, in order:
    0 Frame         black frame, neck and base
    1 Poster_Front  front panel (faces Unity -Z, towards a default camera)
    2 Poster_Back   back panel
Both poster faces have 0..1 UVs sized to the served poster aspect (857 x 2048),
oriented so each reads correctly from its own side.

Geometry is authored in FBX space (right-handed, Y up, metres). Unity converts to
its left-handed space by negating X, so "viewer's right" on the front is FBX -X.
"""
import math
import os
import struct
import sys
import zlib

# ---------- dimensions (metres) ----------
POSTER_ASPECT = 857 / 2048          # width / height of the served posters
PANEL_H = 1.70                      # visible poster height
PANEL_W = PANEL_H * POSTER_ASPECT   # visible poster width (~0.711)
BORDER = 0.05                       # frame border around the poster
FRAME_DEPTH = 0.12
FRAME_RADIUS = 0.05                 # rounded outer corners
CHAMFER = 0.008                     # softened front/back outer edges
PANEL_THICKNESS = 0.05              # panel is recessed inside the frame on both sides
CORNER_SEGMENTS = 8

BASE_W, BASE_D, BASE_H, BASE_RADIUS = 0.75, 0.36, 0.04, 0.03
NECK_W, NECK_D, NECK_H = 0.40, 0.08, 0.12

FRAME_BOTTOM = BASE_H + NECK_H - 0.01
FRAME_W = PANEL_W + 2 * BORDER
FRAME_H = PANEL_H + 2 * BORDER
PANEL_BOTTOM = FRAME_BOTTOM + BORDER

SMOOTH_ANGLE = 35.0

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT_FBX = os.path.join(REPO, "Assets", "Models", "PosterStand.fbx")

MAT_FRAME, MAT_FRONT, MAT_BACK = 0, 1, 2
MATERIALS = [("Frame", (0.03, 0.03, 0.03)), ("Poster_Front", (1, 1, 1)), ("Poster_Back", (1, 1, 1))]


# ---------- vector helpers ----------
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a):
    l = math.sqrt(dot(a, a))
    return (a[0] / l, a[1] / l, a[2] / l) if l > 1e-12 else (0.0, 0.0, 0.0)


class Mesh:
    def __init__(self):
        self.verts = []
        self.faces = []   # dicts: idx, mat, uv (per corner or None)

    def v(self, p):
        self.verts.append(p)
        return len(self.verts) - 1

    def face_normal(self, idx):
        # Newell's method — robust for any planar polygon.
        n = [0.0, 0.0, 0.0]
        for i in range(len(idx)):
            a, b = self.verts[idx[i]], self.verts[idx[(i + 1) % len(idx)]]
            n[0] += (a[1] - b[1]) * (a[2] + b[2])
            n[1] += (a[2] - b[2]) * (a[0] + b[0])
            n[2] += (a[0] - b[0]) * (a[1] + b[1])
        return norm(tuple(n))

    def f(self, idx, outward, mat=MAT_FRAME, uv=None):
        """Add a face; winding is flipped if needed so its normal agrees with `outward`."""
        idx = list(dict.fromkeys(idx))          # drop collapsed corners
        if len(idx) < 3:
            return
        if dot(self.face_normal(idx), outward) < 0:
            idx.reverse()
            if uv:
                uv = list(reversed(uv))
        self.faces.append({"idx": idx, "mat": mat, "uv": uv})


def rounded_rect(w, h, r, seg):
    """CCW outline (in its own a/b plane) of a w x h rectangle centred on 0 with corner radius r.
    Returns (points, corner_of_point)."""
    pts, corner = [], []
    centres = [(w / 2 - r, -h / 2 + r), (w / 2 - r, h / 2 - r), (-w / 2 + r, h / 2 - r), (-w / 2 + r, -h / 2 + r)]
    for k, (cx, cy) in enumerate(centres):
        start = -90 + 90 * k
        for s in range(seg + 1):
            a = math.radians(start + 90 * s / seg)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
            corner.append(k)
    return pts, corner


def extrude_outline(mesh, rings, to3d, centre3d, cap_first=None, cap_last=None):
    """rings: list of (points2d, height). Consecutive rings are joined with quads; walls face
    away from the outline centre. Optional convex caps close the first/last ring."""
    ids = [[mesh.v(to3d(p, h)) for p in pts] for pts, h in rings]
    for r in range(len(ids) - 1):
        a, b = ids[r], ids[r + 1]
        n = len(a)
        for i in range(n):
            j = (i + 1) % n
            mid = mesh.verts[a[i]]
            mesh.f([a[i], a[j], b[j], b[i]], sub(mid, centre3d(mid)))
    if cap_first is not None:
        mesh.f(ids[0], cap_first)
    if cap_last is not None:
        mesh.f(ids[-1], cap_last)
    return ids


# ---------- parts ----------
def build_frame(mesh):
    zc_y = FRAME_BOTTOM + FRAME_H / 2
    d = FRAME_DEPTH / 2
    to3d = lambda p, z: (p[0], p[1] + zc_y, z)
    centre = lambda p: (0.0, zc_y, p[2])

    def outline(offset):
        return rounded_rect(FRAME_W - 2 * offset, FRAME_H - 2 * offset, FRAME_RADIUS - offset, CORNER_SEGMENTS)

    outer_c, corner_of = outline(CHAMFER)
    outer_0, _ = outline(0)
    rings = extrude_outline(mesh, [(outer_c, -d), (outer_0, -d + CHAMFER), (outer_0, d - CHAMFER), (outer_c, d)],
                            to3d, centre)

    # Opening corners in the same corner order as rounded_rect: BR, TR, TL, BL.
    hw, hh = PANEL_W / 2, PANEL_H / 2
    inner2d = [(hw, -hh), (hw, hh), (-hw, hh), (-hw, -hh)]
    inner_front = [mesh.v(to3d(p, -d)) for p in inner2d]
    inner_back = [mesh.v(to3d(p, d)) for p in inner2d]

    # Front/back ring faces between the rounded outline and the rectangular opening.
    for ring, inner, facing in ((rings[0], inner_front, (0, 0, -1)), (rings[-1], inner_back, (0, 0, 1))):
        n = len(ring)
        for i in range(n):
            j = (i + 1) % n
            mesh.f([ring[i], ring[j], inner[corner_of[j]], inner[corner_of[i]]], facing)

    # Opening walls face inwards, towards the panel.
    for k in range(4):
        m = (k + 1) % 4
        mid = mesh.verts[inner_front[k]]
        mesh.f([inner_front[k], inner_front[m], inner_back[m], inner_back[k]], sub(centre(mid), mid))


def build_base(mesh):
    to3d = lambda p, y: (p[0], y, p[1])
    centre = lambda p: (0.0, p[1], 0.0)
    top_c, _ = rounded_rect(BASE_W - 2 * CHAMFER, BASE_D - 2 * CHAMFER, BASE_RADIUS - CHAMFER, 6)
    full, _ = rounded_rect(BASE_W, BASE_D, BASE_RADIUS, 6)
    extrude_outline(mesh, [(full, 0.0), (full, BASE_H - CHAMFER), (top_c, BASE_H)], to3d, centre,
                    cap_first=(0, -1, 0), cap_last=(0, 1, 0))


def box(mesh, x0, x1, y0, y1, z0, z1, mat=MAT_FRAME, face_uv=None):
    """Axis-aligned box. face_uv(axis, sign, corners) may return per-corner UVs."""
    c = {(i, j, k): mesh.v((x1 if i else x0, y1 if j else y0, z1 if k else z0))
         for i in (0, 1) for j in (0, 1) for k in (0, 1)}
    quads = {
        (0, -1): [(0, 0, 0), (0, 0, 1), (0, 1, 1), (0, 1, 0)], (0, 1): [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)],
        (1, -1): [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)], (1, 1): [(0, 1, 0), (0, 1, 1), (1, 1, 1), (1, 1, 0)],
        (2, -1): [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)], (2, 1): [(0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)],
    }
    for (axis, sign), keys in quads.items():
        outward = tuple(sign if a == axis else 0 for a in range(3))
        m, uv = mat, None
        if face_uv:
            res = face_uv(axis, sign, [mesh.verts[c[k]] for k in keys])
            if res:
                m, uv = res
        mesh.f([c[k] for k in keys], outward, m, uv)


def panel_uv(axis, sign, corners):
    if axis != 2:
        return None
    hw = PANEL_W / 2
    uv = []
    for x, y, _ in corners:
        v = (y - PANEL_BOTTOM) / PANEL_H
        # Front (-Z) viewer's right is FBX -X; back (+Z) viewer's right is FBX +X.
        u = (hw - x) / PANEL_W if sign < 0 else (x + hw) / PANEL_W
        uv.append((u, v))
    return (MAT_FRONT if sign < 0 else MAT_BACK), uv


def build():
    mesh = Mesh()
    build_frame(mesh)
    build_base(mesh)
    box(mesh, -NECK_W / 2, NECK_W / 2, BASE_H - 0.005, FRAME_BOTTOM + 0.01, -NECK_D / 2, NECK_D / 2)
    hw, ht = PANEL_W / 2, PANEL_THICKNESS / 2
    box(mesh, -hw, hw, PANEL_BOTTOM, PANEL_BOTTOM + PANEL_H, -ht, ht, face_uv=panel_uv)
    return mesh


# ---------- normals / uvs ----------
def corner_normals(mesh):
    fn = [mesh.face_normal(f["idx"]) for f in mesh.faces]
    by_vert = {}
    for fi, f in enumerate(mesh.faces):
        for vi in f["idx"]:
            by_vert.setdefault(vi, []).append(fi)
    cos_t = math.cos(math.radians(SMOOTH_ANGLE))
    out = []
    for fi, f in enumerate(mesh.faces):
        for vi in f["idx"]:
            acc = (0.0, 0.0, 0.0)
            for other in by_vert[vi]:
                if dot(fn[fi], fn[other]) >= cos_t:
                    acc = add(acc, fn[other])
            out.append(norm(acc))
    return fn, out


def corner_uvs(mesh, fn):
    out = []
    for fi, f in enumerate(mesh.faces):
        if f["uv"]:
            out.extend(f["uv"])
            continue
        n = fn[fi]
        ax = max(range(3), key=lambda a: abs(n[a]))
        for vi in f["idx"]:
            p = mesh.verts[vi]
            out.append({0: (p[2], p[1]), 1: (p[0], p[2]), 2: (p[0], p[1])}[ax])
    return out


# ---------- FBX (ASCII 7.4) ----------
def fmt(values):
    return ",".join(f"{v:.6g}" if isinstance(v, float) else str(v) for v in values)


def write_fbx(mesh, path):
    fn, normals = corner_normals(mesh)
    uvs = corner_uvs(mesh, fn)

    verts = [c for p in mesh.verts for c in p]
    poly = []
    for f in mesh.faces:
        poly.extend(f["idx"][:-1])
        poly.append(-f["idx"][-1] - 1)
    nrm = [c for n in normals for c in n]
    uv_flat = [c for uv in uvs for c in uv]
    mats = [f["mat"] for f in mesh.faces]

    def arr(name, values, indent):
        return f"{indent}{name}: *{len(values)} {{\n{indent}\ta: {fmt(values)}\n{indent}}}\n"

    t = "\t"
    s = []
    s.append("; FBX 7.4.0 project file\n; Generated by Tools/build_poster_stand.py\n\n")
    s.append("FBXHeaderExtension:  {\n\tFBXHeaderVersion: 1003\n\tFBXVersion: 7400\n"
             "\tCreator: \"ClubElementum build_poster_stand.py\"\n}\n")
    s.append("GlobalSettings:  {\n\tVersion: 1000\n\tProperties70:  {\n"
             "\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1\n"
             "\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1\n"
             "\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2\n"
             "\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1\n"
             "\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0\n"
             "\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1\n"
             "\t\tP: \"OriginalUpAxis\", \"int\", \"Integer\", \"\",1\n"
             "\t\tP: \"OriginalUpAxisSign\", \"int\", \"Integer\", \"\",1\n"
             "\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",100\n"
             "\t\tP: \"OriginalUnitScaleFactor\", \"double\", \"Number\", \"\",100\n"
             "\t}\n}\n")
    s.append("Definitions:  {\n\tVersion: 100\n\tCount: 6\n"
             "\tObjectType: \"GlobalSettings\" {\n\t\tCount: 1\n\t}\n"
             "\tObjectType: \"Model\" {\n\t\tCount: 1\n\t}\n"
             "\tObjectType: \"Geometry\" {\n\t\tCount: 1\n\t}\n"
             f"\tObjectType: \"Material\" {{\n\t\tCount: {len(MATERIALS)}\n\t}}\n}}\n")

    s.append("Objects:  {\n")
    s.append("\tGeometry: 1000000, \"Geometry::PosterStand\", \"Mesh\" {\n")
    s.append(arr("Vertices", verts, t * 2))
    s.append(arr("PolygonVertexIndex", poly, t * 2))
    s.append("\t\tGeometryVersion: 124\n")
    s.append("\t\tLayerElementNormal: 0 {\n\t\t\tVersion: 102\n\t\t\tName: \"\"\n"
             "\t\t\tMappingInformationType: \"ByPolygonVertex\"\n\t\t\tReferenceInformationType: \"Direct\"\n")
    s.append(arr("Normals", nrm, t * 3))
    s.append("\t\t}\n")
    s.append("\t\tLayerElementUV: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"UVMap\"\n"
             "\t\t\tMappingInformationType: \"ByPolygonVertex\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n")
    s.append(arr("UV", uv_flat, t * 3))
    s.append(arr("UVIndex", list(range(len(uvs))), t * 3))
    s.append("\t\t}\n")
    s.append("\t\tLayerElementMaterial: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"\"\n"
             "\t\t\tMappingInformationType: \"ByPolygon\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n")
    s.append(arr("Materials", mats, t * 3))
    s.append("\t\t}\n")
    s.append("\t\tLayer: 0 {\n\t\t\tVersion: 100\n"
             "\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementNormal\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n"
             "\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementMaterial\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n"
             "\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementUV\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n"
             "\t\t}\n\t}\n")
    s.append("\tModel: 2000000, \"Model::PosterStand\", \"Mesh\" {\n\t\tVersion: 232\n\t\tProperties70:  {\n"
             "\t\t\tP: \"DefaultAttributeIndex\", \"int\", \"Integer\", \"\",0\n\t\t}\n"
             "\t\tShading: T\n\t\tCulling: \"CullingOff\"\n\t}\n")
    for i, (name, col) in enumerate(MATERIALS):
        s.append(f"\tMaterial: {3000000 + i}, \"Material::{name}\", \"\" {{\n\t\tVersion: 102\n"
                 "\t\tShadingModel: \"lambert\"\n\t\tMultiLayer: 0\n\t\tProperties70:  {\n"
                 f"\t\t\tP: \"DiffuseColor\", \"Color\", \"\", \"A\",{fmt(col)}\n\t\t}}\n\t}}\n")
    s.append("}\n")

    s.append("Connections:  {\n\tC: \"OO\",2000000,0\n\tC: \"OO\",1000000,2000000\n")
    for i in range(len(MATERIALS)):
        s.append(f"\tC: \"OO\",{3000000 + i},2000000\n")
    s.append("}\n")

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as fh:
        fh.write("".join(s))
    return fn


# ---------- preview renderer (verification only) ----------
def read_png(path):
    with open(path, "rb") as fh:
        data = fh.read()
    pos, idat, plte = 8, b"", None
    while pos < len(data):
        ln, typ = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + ln]
        if typ == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", chunk[:10])
        elif typ == b"PLTE":
            plte = chunk
        elif typ == b"IDAT":
            idat += chunk
        pos += 12 + ln
    if depth != 8 or ctype not in (0, 2, 3, 4, 6):
        return None
    bpp = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    raw = zlib.decompress(idat)
    stride = w * bpp
    rows, prev = [], bytearray(stride)
    p = 0
    for _ in range(h):
        ft = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        if ft == 1:
            for i in range(bpp, stride): line[i] = (line[i] + line[i - bpp]) & 255
        elif ft == 2:
            for i in range(stride): line[i] = (line[i] + prev[i]) & 255
        elif ft == 3:
            for i in range(stride):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        rows.append(line)
        prev = line

    def sample(u, v):
        x = min(w - 1, max(0, int(u * w)))
        y = min(h - 1, max(0, int((1 - v) * h)))
        px = rows[y]
        if ctype == 3:
            k = px[x] * 3
            return tuple(plte[k:k + 3])
        if ctype in (0, 4):
            g = px[x * bpp]
            return (g, g, g)
        return tuple(px[x * bpp:x * bpp + 3])
    return sample


def write_png(path, w, h, pix):
    raw = b"".join(b"\x00" + bytes(pix[y * w * 3:(y + 1) * w * 3]) for y in range(h))
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


def render(mesh, fn, textures, forward, path, size=(700, 900)):
    """Orthographic render in *Unity* space (X negated), with back-face culling."""
    W, H = size
    to_u = lambda p: (-p[0], p[1], p[2])
    fwd = norm(forward)
    right = norm(cross((0, 1, 0), fwd))        # Unity: Cross(up, forward) = right
    up = cross(fwd, right)
    light = norm((0.4, 0.8, -0.5))
    scale, cy = H / 2.3, 1.0
    pix = bytearray(b"\xe6" * (W * H * 3))
    zbuf = [1e9] * (W * H)

    def proj(p):
        q = to_u(p)
        return (W / 2 + dot(q, right) * scale, H / 2 - (dot(q, up) - cy * up[1]) * scale, dot(q, fwd))

    uvs = corner_uvs(mesh, fn)
    k = 0
    for fi, f in enumerate(mesh.faces):
        n = to_u(fn[fi])
        corner_uv = uvs[k:k + len(f["idx"])]
        k += len(f["idx"])
        if dot(n, fwd) >= 0:
            continue
        shade = 0.35 + 0.65 * max(0.0, dot(n, light))
        pts = [proj(mesh.verts[i]) for i in f["idx"]]
        for t in range(1, len(pts) - 1):
            tri, tuv = (pts[0], pts[t], pts[t + 1]), (corner_uv[0], corner_uv[t], corner_uv[t + 1])
            (x0, y0, z0), (x1, y1, z1), (x2, y2, z2) = tri
            den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
            if abs(den) < 1e-9:
                continue
            for py in range(max(0, int(min(y0, y1, y2))), min(H, int(max(y0, y1, y2)) + 1)):
                for px in range(max(0, int(min(x0, x1, x2))), min(W, int(max(x0, x1, x2)) + 1)):
                    sx, sy = px + 0.5, py + 0.5
                    a = ((y1 - y2) * (sx - x2) + (x2 - x1) * (sy - y2)) / den
                    b = ((y2 - y0) * (sx - x2) + (x0 - x2) * (sy - y2)) / den
                    c = 1 - a - b
                    if a < 0 or b < 0 or c < 0:
                        continue
                    z = a * z0 + b * z1 + c * z2
                    o = py * W + px
                    if z >= zbuf[o]:
                        continue
                    zbuf[o] = z
                    if f["mat"] == MAT_FRAME:
                        col = (40 * shade, 40 * shade, 42 * shade)
                    else:
                        u = a * tuv[0][0] + b * tuv[1][0] + c * tuv[2][0]
                        v = a * tuv[0][1] + b * tuv[1][1] + c * tuv[2][1]
                        tex = textures.get(f["mat"])
                        base = tex(u, v) if tex else (255 * u, 255 * v, 128)
                        col = tuple(ch * (0.8 + 0.2 * shade) for ch in base)
                    pix[o * 3:o * 3 + 3] = bytes(min(255, int(ch)) for ch in col)
    write_png(path, W, H, pix)
    print(f"Preview: {path}")


def main():
    mesh = build()
    fn = write_fbx(mesh, OUT_FBX)
    print(f"Wrote {OUT_FBX}")
    print(f"  {len(mesh.verts)} verts, {len(mesh.faces)} faces")
    print(f"  Poster panel {PANEL_W:.4f} x {PANEL_H:.4f} m, aspect {PANEL_W / PANEL_H:.4f} (target {POSTER_ASPECT:.4f})")
    print(f"  Overall {FRAME_W:.3f} W x {BASE_D:.2f} D x {FRAME_BOTTOM + FRAME_H:.3f} H m")

    if "--preview" in sys.argv:
        out = sys.argv[sys.argv.index("--preview") + 1]
        os.makedirs(out, exist_ok=True)
        served = os.path.join(REPO, "Posters", "served")
        textures = {}
        for mat, name in ((MAT_FRONT, "poster1.png"), (MAT_BACK, "poster2.png")):
            p = os.path.join(served, name)
            if os.path.exists(p):
                textures[mat] = read_png(p)
        render(mesh, fn, textures, (0, 0, 1), os.path.join(out, "stand_front.png"))
        render(mesh, fn, textures, (0, 0, -1), os.path.join(out, "stand_back.png"))
        render(mesh, fn, textures, (0.55, -0.25, 1), os.path.join(out, "stand_angle.png"))
        render(mesh, fn, textures, (1, 0, 0), os.path.join(out, "stand_side.png"), size=(400, 900))


if __name__ == "__main__":
    main()
