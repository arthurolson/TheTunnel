"""Coarse Gaussian molecular surface of a PDB biological assembly, one OBJ group per chain type.

usage: gauss_surface.py <in.cif.gz> <out_dir> <name> [spacing_A] [sigma_A] [iso] [diameter_units]
"""
import sys, os, gzip
import numpy as np
import gemmi
from scipy.ndimage import gaussian_filter, map_coordinates
from skimage.measure import marching_cubes

src, out_dir, name = sys.argv[1], sys.argv[2], sys.argv[3]
spacing = float(sys.argv[4]) if len(sys.argv) > 4 else 3.0   # grid step (A)
sigma = float(sys.argv[5]) if len(sys.argv) > 5 else 4.0     # gaussian width (A)
iso = float(sys.argv[6]) if len(sys.argv) > 6 else 0.5       # fraction of per-atom peak density
diameter_units = float(sys.argv[7]) if len(sys.argv) > 7 else 0.4

st = gemmi.read_structure(src)
st.setup_entities()
st.remove_hydrogens()  # ligands/waters stay: the assembly lists their subchains; only polymer atoms are used below
asm = st.assemblies[0]
print("assembly:", asm.name, asm.oligomeric_details)
# chain type = polymer entity of the chain's subchain in the asymmetric unit
sub2ent = {}
for ent in st.entities:
    if ent.entity_type == gemmi.EntityType.Polymer:
        for sub in ent.subchains:
            sub2ent[sub] = ent
ents = sorted({e.name for e in sub2ent.values()}, key=lambda n: int(n) if n.isdigit() else n)
ent_index = {n: i for i, n in enumerate(ents)}
ent_desc = {e.name: (e.full_sequence and len(e.full_sequence), e.name) for e in sub2ent.values()}

# asymmetric-unit polymer atoms per subchain, then every assembly operator applied to them
au = {}
for ch in st[0]:
    for res in ch:
        if res.subchain in sub2ent:
            au.setdefault(res.subchain, []).extend((a.pos.x, a.pos.y, a.pos.z) for a in res)
parts, tparts, ncopies = [], [], 0
for gen in asm.generators:
    for op in gen.operators:
        R = np.array(op.transform.mat.tolist())
        T = np.array(op.transform.vec.tolist())
        for sub in gen.subchains:
            if sub in au:
                xyz = np.asarray(au[sub])
                parts.append(xyz @ R.T + T)
                tparts.append(np.full(len(xyz), ent_index[sub2ent[sub].name]))
                ncopies += 1
coords = np.concatenate(parts)
types = np.concatenate(tparts)
print("polymer chains in assembly:", ncopies)
center = (coords.min(0) + coords.max(0)) / 2
coords -= center
print(f"atoms {len(coords)}  chain types {ents}")
print("counts per type:", np.bincount(types))

pad = 3 * sigma + 2 * spacing
lo = coords.min(0) - pad
shape = np.ceil((coords.max(0) + pad - lo) / spacing).astype(int) + 1
print("grid", shape, "spacing", spacing)

# splat atoms per chain type (trilinear), then blur -> per-type gaussian density
dens = np.zeros((len(ents),) + tuple(shape), dtype=np.float32)
g = (coords - lo) / spacing
i0 = np.floor(g).astype(int)
f = g - i0
for dx in (0, 1):
    for dy in (0, 1):
        for dz in (0, 1):
            w = (np.where(dx, f[:, 0], 1 - f[:, 0]) * np.where(dy, f[:, 1], 1 - f[:, 1])
                 * np.where(dz, f[:, 2], 1 - f[:, 2])).astype(np.float32)
            np.add.at(dens, (types, i0[:, 0] + dx, i0[:, 1] + dy, i0[:, 2] + dz), w)
s = sigma / spacing
for t in range(len(ents)):
    dens[t] = gaussian_filter(dens[t], s, mode="constant")
# normalize so 1.0 = peak density of a single isolated atom
peak = 1.0 / ((2 * np.pi) ** 1.5 * s ** 3)
dens /= peak
total = dens.sum(0)

verts, faces, normals, _ = marching_cubes(total, level=iso, spacing=(spacing,) * 3, step_size=1,
                                          allow_degenerate=False)
verts += lo
# skimage normals point toward lower values (outward); keep them, faces wound consistently
print(f"surface: {len(verts)} verts {len(faces)} tris")


def sample(grid, pts):
    """Trilinear sample of a density grid at world positions (A)."""
    return map_coordinates(grid, ((pts - lo) / spacing).T, order=1, mode="nearest")


def winding_sign(v, fc, n):
    """+1 if face winding agrees with the vertex normals, -1 if opposite (majority vote)."""
    cr = np.cross(v[fc[:, 1]] - v[fc[:, 0]], v[fc[:, 2]] - v[fc[:, 0]])
    return np.sign(np.sum(np.sign(np.einsum("ij,ij->i", cr, n[fc].sum(1)))))


# optional quadric decimation of the whole surface (shape-preserving), before colouring
target_tris = int(sys.argv[8]) if len(sys.argv) > 8 else 0
if target_tris and target_tris < len(faces):
    import pyfqmr
    sign_before = winding_sign(verts, faces, normals)
    simp = pyfqmr.Simplify()
    simp.setMesh(verts.astype(np.float64), faces.astype(np.int32))
    simp.simplify_mesh(target_count=target_tris, aggressiveness=7, preserve_border=True, verbose=False)
    verts, faces, _ = simp.getMesh()
    # smooth normals from the density gradient: outward = toward lower density
    grad = np.gradient(total, spacing)
    normals = -np.stack([sample(gc, verts) for gc in grad], 1)
    normals /= np.linalg.norm(normals, axis=1, keepdims=True).clip(1e-9)
    if winding_sign(verts, faces, normals) != sign_before:
        faces = faces[:, [0, 2, 1]]  # keep skimage's winding convention for the writer below
    print(f"decimated: {len(verts)} verts {len(faces)} tris")

# chain type per triangle = dominant density at the triangle centroid (trilinear sample of each type)
cent = verts[faces].mean(1)
tri_type = np.stack([sample(dens[t], cent) for t in range(len(ents))]).argmax(0)

vr = np.linalg.norm(verts, axis=1)
tri_r = vr[faces].mean(1)
outer = tri_r > np.percentile(vr, 50)
for t in range(len(ents)):
    print(f"  type{t + 1}: outer-half tris {np.sum(outer & (tri_type == t))}, inner-half {np.sum(~outer & (tri_type == t))}")
radius = vr.max()
scale = (diameter_units / 2) / radius
print(f"max radius {radius:.1f} A -> scale {scale:.6f} units/A (diameter {diameter_units} units)")

palette = [(0.20, 0.45, 0.85), (0.30, 0.75, 0.35), (0.90, 0.35, 0.30), (0.95, 0.80, 0.25),
           (0.65, 0.40, 0.80), (0.30, 0.80, 0.80)]
os.makedirs(out_dir, exist_ok=True)
obj, mtl = os.path.join(out_dir, name + ".obj"), os.path.join(out_dir, name + ".mtl")
with open(mtl, "w") as m:
    for t, en in enumerate(ents):
        r, g_, b = palette[t % len(palette)]
        m.write(f"newmtl {name}_type{t + 1}\nKd {r} {g_} {b}\nKa 0 0 0\nKs 0.2 0.2 0.2\nNs 30\nillum 2\n\n")
with open(obj, "w") as o:
    o.write(f"# {name}: coarse gaussian surface, spacing {spacing} A, sigma {sigma} A, iso {iso}\n")
    o.write(f"mtllib {name}.mtl\n")
    v = verts * scale
    np.savetxt(o, v, fmt="v %.5f %.5f %.5f")
    np.savetxt(o, normals, fmt="vn %.4f %.4f %.4f")
    for t, en in enumerate(ents):
        sel = faces[tri_type == t] + 1
        o.write(f"g {name}_type{t + 1}\nusemtl {name}_type{t + 1}\n")
        # skimage winding is opposite to OBJ's CCW-front convention for outward normals
        np.savetxt(o, np.column_stack([sel[:, 0], sel[:, 0], sel[:, 2], sel[:, 2], sel[:, 1], sel[:, 1]]),
                   fmt="f %d//%d %d//%d %d//%d")
        print(f"  type{t + 1} (entity {en}): {len(sel)} tris, color {palette[t % len(palette)]}")
print("wrote", obj)
