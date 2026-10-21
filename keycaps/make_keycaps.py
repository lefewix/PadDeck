"""Printable keycaps for the PadDeck macropad (CH57x, 6 MX-style keys + 1 knob).

Each cap carries the same line icon PadDeck shows for that button. Run with a Python that has
manifold3d, shapely, trimesh and numpy:  python make_keycaps.py [--preview]

Output (in ./out):
  single-color/Fxx-name.stl    cap with the icon cut 0.6 mm into the top (fill with paint if you like)
  two-color/Fxx-name.3mf       cap + icon inlay as two parts, for AMS / MMU printers
  knob-d-shaft.stl, knob-round-shaft.stl
  stem-fit-test.stl            five short stems, 1 to 5 dots = tight to loose cross
Everything is already in print orientation: top face down on the bed, no supports needed.
"""
import math, os, sys
import numpy as np
import manifold3d as m3
import trimesh
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import unary_union
from shapely.geometry.polygon import orient

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")

# ---- cap dimensions (mm) ----
BOTTOM = 18.0       # footprint; standard 1u spacing is 19.05
TOP = 15.0          # flat top face
HEIGHT = 7.5
R_BOTTOM, R_TOP = 1.6, 2.2   # corner radii
WALL = 1.4
ROOF = 2.0          # top thickness (the icon is cut into this)
ICON_DEPTH = 0.6    # 3 layers at 0.2
ICON_SPAN = 12.5    # the 24-unit icon grid maps onto this many mm
STROKE = 0.9        # icon line width in mm (>= 2 lines of a 0.4 nozzle)

# MX stem
STEM_OD = 5.6
CROSS_LEN = 4.15
CROSS_W = 1.30
STEM_RECESS = 0.3   # stem stops this far above the skirt bottom
SEG = 64


# ---------- 2D helpers (icons are drawn on PadDeck's 24x24 grid, y down) ----------
def arc_pts(cx, cy, r, a0, a1, n=None):
    """Points on a circle from a0 to a1 degrees (0 = right, 90 = down, like the screen)."""
    n = n or max(8, int(abs(a1 - a0) / 6))
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


def rrect_pts(x0, y0, x1, y1, r):
    pts = []
    for cx, cy, a in ((x1 - r, y0 + r, -90), (x1 - r, y1 - r, 0), (x0 + r, y1 - r, 90), (x0 + r, y0 + r, 180)):
        pts += arc_pts(cx, cy, r, a, a + 90, 8)
    return pts + [pts[0]]


def icon(key):
    """Returns (lines, dots): polylines to stroke and filled circles (cx, cy, r) on the 24 grid."""
    lines, dots = [], []
    if key == "reset":          # Segoe E72C "Refresh": open circle with an arrowhead
        a0, a1 = -60, 250
        lines.append(arc_pts(12, 12, 8, a0, a1))
        t = math.radians(a0)
        ex, ey = 12 + 8 * math.cos(t), 12 + 8 * math.sin(t)
        dx, dy = math.sin(t), -math.cos(t)   # the way the arrow points: backwards along the arc
        wing = [(ex - 4 * (dx * math.cos(w) - dy * math.sin(w)), ey - 4 * (dx * math.sin(w) + dy * math.cos(w)))
                for w in (math.radians(40), math.radians(-40))]
        lines.append([wing[0], (ex, ey), wing[1]])
    elif key == "chat":         # Segoe E8F2 "Chat": speech bubble
        p = arc_pts(18.5, 6.5, 2.5, -90, 0, 6) + arc_pts(18.5, 14, 2.5, 0, 90, 6) \
            + [(11, 16.5), (6.5, 20.5), (6.5, 16.5)] + arc_pts(5.5, 14, 2.5, 90, 180, 6) + arc_pts(5.5, 6.5, 2.5, 180, 270, 6)
        lines.append(p + [p[0]])
    elif key == "comfyui":      # PadDeck line:comfyui
        lines.append(rrect_pts(3, 3, 21, 21, 4))
        lines.append([(17.4, 7), (10.6, 7), (6.6, 12.2), (9.6, 17), (15.8, 17)])
    elif key == "claude":       # PadDeck line:claude, centre opened up so the rays don't merge
        lens = [10, 7.6, 9.2, 7.2, 10, 8, 9.6, 7.2, 10, 7.6, 9.2, 8]
        for i, l in enumerate(lens):
            a = math.radians(i * 30 - 90)
            lines.append([(12 + 3.6 * math.cos(a), 12 + 3.6 * math.sin(a)), (12 + l * math.cos(a), 12 + l * math.sin(a))])
    elif key == "obs":          # PadDeck line:obs, commas filled and tails pulled in for print gaps
        lines.append(arc_pts(12, 12, 10, 0, 360, 72))
        for i in range(3):
            a = i * 120 - 90
            dots.append((12 + 4.4 * math.cos(math.radians(a)), 12 + 4.4 * math.sin(math.radians(a)), 2.3))
            lines.append(arc_pts(12, 12, 6.6, a + 20, a + 85))
    elif key == "headphones":   # Segoe E7F6 "Headphones"
        lines.append([(4, 15)] + arc_pts(12, 12, 8, 180, 360) + [(20, 15)])
        lines.append(rrect_pts(3.5, 14, 8, 21, 1.6))
        lines.append(rrect_pts(16, 14, 20.5, 21, 1.6))
    else:
        raise KeyError(key)
    return lines, dots


def icon_shape(key):
    """The icon as a shapely shape in mm, centred on (0, 0), y up."""
    s = ICON_SPAN / 24
    lines, dots = icon(key)
    parts = [LineString([((x - 12) * s, (12 - y) * s) for x, y in l]).buffer(STROKE / 2, quad_segs=8) for l in lines]
    parts += [Point((x - 12) * s, (12 - y) * s).buffer(r * s, quad_segs=16) for x, y, r in dots]
    return unary_union(parts)


def to_cross_section(shape):
    polys = [shape] if isinstance(shape, Polygon) else list(shape.geoms)
    contours = []
    for p in polys:
        p = orient(p, 1.0)   # outer CCW, holes CW
        contours.append(np.array(p.exterior.coords[:-1]))
        contours += [np.array(h.coords[:-1]) for h in p.interiors]
    return m3.CrossSection(contours, m3.FillRule.Positive)


# ---------- 3D ----------
def rsquare(size, r):
    return m3.CrossSection.square((size - 2 * r, size - 2 * r), center=True).offset(r, m3.JoinType.Round, circular_segments=SEG)


def slab(size, r, z, t=0.01):
    return m3.Manifold.extrude(rsquare(size, r), t).translate((0, 0, z))


def frustum(b, rb, t, rt, z0, z1):
    return m3.Manifold.batch_hull([slab(b, rb, z0), slab(t, rt, z1 - 0.01)])


def stem(cross_w=CROSS_W, cross_len=CROSS_LEN, z0=STEM_RECESS, z1=HEIGHT - ROOF + 0.01):
    body = m3.Manifold.cylinder(z1 - z0, STEM_OD / 2, STEM_OD / 2, SEG).translate((0, 0, z0))
    cross =m3.Manifold.cube((cross_len, cross_w, 4.0)).translate((-cross_len / 2, -cross_w / 2, 0)) \
        + m3.Manifold.cube((cross_w, cross_len, 4.0)).translate((-cross_w / 2, -cross_len / 2, 0))
    return body - cross.translate((0, 0, z0 - 0.01))


def keycap_body():
    outer = frustum(BOTTOM, R_BOTTOM, TOP, R_TOP, 0, HEIGHT)
    inner_top_size = BOTTOM - (BOTTOM - TOP) * (HEIGHT - ROOF) / HEIGHT - 2 * WALL
    inner = frustum(BOTTOM - 2 * WALL, max(R_BOTTOM - WALL, 0.4), inner_top_size, max(R_TOP - WALL, 0.4), -0.5, HEIGHT - ROOF)
    return outer - inner + stem()


def keycap(key):
    """(cap with icon pocket, icon inlay), modelled upright."""
    inlay = m3.Manifold.extrude(to_cross_section(icon_shape(key)), ICON_DEPTH).translate((0, 0, HEIGHT - ICON_DEPTH))
    return keycap_body() - inlay, inlay


def knob(d_shaft):
    D, H = 20.0, 14.0
    body = m3.Manifold.cylinder(H, D / 2, D / 2 - 0.6, 96)
    for i in range(24):  # grip grooves
        a = i * 360 / 24
        g = m3.Manifold.cylinder(H - 2.5, 0.55, 0.55, 12).translate((D / 2 + 0.15, 0, -0.01)).rotate((0, 0, a))
        body -= g
    top_chamfer = m3.Manifold.cylinder(1.2, D / 2 + 1, D / 2 + 1, 96).translate((0, 0, H - 1.2)) \
        - m3.Manifold.cylinder(1.2, D / 2 - 0.6, D / 2 - 1.8, 96).translate((0, 0, H - 1.2))
    body -= top_chamfer
    body -= m3.Manifold.cube((1.0, D / 2 - 3, 0.6)).translate((-0.5, 2.2, H - 0.6))   # pointer groove
    hole = m3.Manifold.cylinder(11.0, 3.1, 3.1, 48).translate((0, 0, -0.01))
    if d_shaft:  # EC11 D-shaft: 6 mm round, flat at 4.5 mm
        hole -= m3.Manifold.cube((7, 3, 12)).translate((-3.5, 4.6 - 3.1, -1))
    else:        # round / knurled shaft, light press fit
        hole = m3.Manifold.cylinder(11.0, 3.0, 3.0, 48).translate((0, 0, -0.01))
    return body - hole


def stem_test():
    widths = [1.20, 1.25, 1.30, 1.35, 1.40]
    plate = m3.Manifold.cube((5 * 9 + 2, 13, 1.2)).translate((-1 - 4.5, -6.5, 0))
    out = plate
    for i, w in enumerate(widths):
        x = i * 9
        out += stem(cross_w=w, z0=0, z1=5.4).rotate((180, 0, 0)).translate((x, -1.5, 6.6))
        for j in range(i + 1):   # dots: 1 = tightest
            out += m3.Manifold.cylinder(0.6, 0.5, 0.5, 16).translate((x - (i * 1.4) / 2 + j * 1.4, 4.8, 1.2))
    # flip so the stem openings face up and the plate is on the bed
    return out


def flip(m):
    """Upright -> print orientation (top face on the bed)."""
    return m.rotate((180, 0, 0)).translate((0, 0, HEIGHT))


def tm(m):
    mesh = m.to_mesh()
    return trimesh.Trimesh(vertices=np.asarray(mesh.vert_properties)[:, :3], faces=np.asarray(mesh.tri_verts), process=True)


KEYS = [("F19", "reset", "Reset"), ("F20", "chat", "Local LLM"), ("F21", "comfyui", "ComfyUI"),
        ("F22", "claude", "Claude"), ("F23", "obs", "Save replay"), ("F24", "headphones", "Sound switch")]
KEY_ICON = {f: k for f, k, _ in KEYS}


def main():
    os.makedirs(os.path.join(OUT, "single-color"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "two-color"), exist_ok=True)
    meshes = {}
    for fkey, ik, label in KEYS:
        cap, inlay = keycap(ik)
        cap_m, inlay_m = tm(flip(cap)), tm(flip(inlay))
        assert cap_m.is_watertight and inlay_m.is_watertight, fkey
        name = f"{fkey}-{label.replace(' ', '-')}"
        cap_m.export(os.path.join(OUT, "single-color", name + ".stl"))
        scene = trimesh.Scene()
        scene.add_geometry(cap_m, node_name=f"{name}-cap", geom_name=f"{name}-cap")
        scene.add_geometry(inlay_m, node_name=f"{name}-icon", geom_name=f"{name}-icon")
        scene.export(os.path.join(OUT, "two-color", name + ".3mf"))
        meshes[fkey] = (cap, inlay, label)
        print(f"{name}: cap {cap_m.volume:.0f} mm3, icon {inlay_m.volume:.1f} mm3")
    for d, fn in ((True, "knob-d-shaft.stl"), (False, "knob-round-shaft.stl")):
        k = tm(knob(d)); assert k.is_watertight; k.export(os.path.join(OUT, fn))
    t = tm(stem_test()); assert t.is_watertight; t.export(os.path.join(OUT, "stem-fit-test.stl"))
    print("written to", OUT)
    if "--preview" in sys.argv:
        preview(meshes)


def preview(meshes):
    """Top view of each cap (skirt, top face, icon) plus a slice through the stem, drawn to scale."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.patches import PathPatch
    from matplotlib.path import Path

    def fill(ax, shape, **kw):
        for p in ([shape] if isinstance(shape, Polygon) else shape.geoms):
            p = orient(p, 1.0)   # holes run the other way, so they stay open
            ax.add_patch(PathPatch(Path.make_compound_path(*[Path(np.array(r.coords), closed=True)
                                                             for r in [p.exterior, *p.interiors]]), **kw))

    def rsq(size, r):
        return Polygon(np.array(rsquare(size, r).to_polygons()[0]))

    fig, axes = plt.subplots(2, 6, figsize=(15, 5.6), facecolor="#16191d")
    for i, (fkey, (cap, inlay, label)) in enumerate(meshes.items()):
        ax = axes[0][i]
        fill(ax, rsq(BOTTOM, R_BOTTOM), facecolor="#22262c", edgecolor="#444a53")
        fill(ax, rsq(TOP, R_TOP), facecolor="#30353d", edgecolor="#555c66")
        fill(ax, icon_shape(KEY_ICON[fkey]), facecolor="#3fd0c0", edgecolor="none")
        ax.set_title(f"{fkey}  {label}", color="#d0d4da", fontsize=10)
        ax = axes[1][i]   # slice 2 mm above the skirt bottom, seen from below
        sec = Polygon()
        for c in cap.slice(2.0).to_polygons():
            sec = sec.symmetric_difference(Polygon(c))
        fill(ax, sec, facecolor="#8a929c", edgecolor="none")
        ax.set_title("stem slice", color="#7d848e", fontsize=8)
    for ax in axes.flat:
        ax.set_xlim(-10, 10); ax.set_ylim(-10, 10); ax.set_aspect("equal"); ax.axis("off")
    plt.subplots_adjust(0.01, 0.01, 0.99, 0.93, 0.05, 0.15)
    path = os.path.join(OUT, "preview.png")
    fig.savefig(path, dpi=110)
    print("preview:", path)


if __name__ == "__main__":
    main()
