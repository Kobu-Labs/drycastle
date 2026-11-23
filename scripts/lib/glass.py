"""The breakable glass panes of a vehicle model (ADR 0007), shared by the model scripts that make vehicles with glass."""
from pathlib import Path

from meshkit import add, cross, dot, mul, sub, unit


class GlassSet:
    """The breakable glass panes of one vehicle (ADR 0007). Each pane is its own glTF node `glass_N` (N = its place in the list, which is
    its index in vehicles.json `glass` and on the wire) so the client can hide it when it shatters. Several slabs of one pane go under
    one node. The pane list for vehicles.json (centre, normal, bounding size and the outline polygon) comes from the outline given here,
    not from hand measuring: `outline` is the pane's visible edge as 3D points in the vehicle frame (convex, in order)."""

    def __init__(self, vehicle_id):
        self.vehicle_id, self.panes = vehicle_id, []

    def pane(self, parent, pane_id, slabs, outline, inside=None):
        """Adds the slabs (lists of triangles) as node glass_N under `parent`. `inside` is a point inside the vehicle: the normal faces away from it."""
        assert len(outline) >= 3 and len(outline) <= 12, pane_id
        n = len(self.panes)
        node = parent.child(f"glass_{n}")
        for s in slabs:
            node.add(s)
        cen = tuple(sum(p[i] for p in outline) / len(outline) for i in range(3))
        # Newell's normal of the outline, turned to face away from the inside
        nx = ny = nz = 0.0
        for a, b in zip(outline, outline[1:] + outline[:1]):
            nx += (a[1] - b[1]) * (a[2] + b[2]); ny += (a[2] - b[2]) * (a[0] + b[0]); nz += (a[0] - b[0]) * (a[1] + b[1])
        normal = unit((nx, ny, nz))
        if dot(normal, sub(cen, inside if inside is not None else (0.0, cen[1], 0.0))) < 0:
            normal = mul(normal, -1)
        # the same in-plane axes as GlassPaneRect.Make: right is horizontal, up runs up the pane (a flat pane's width runs along x)
        right = (1.0, 0.0, 0.0) if abs(normal[1]) > 0.999 else unit(cross((0.0, 1.0, 0.0), normal))
        up = unit(cross(normal, right))
        uv = [(dot(sub(p, cen), right), dot(sub(p, cen), up)) for p in outline]
        u0, u1, v0, v1 = min(u for u, _ in uv), max(u for u, _ in uv), min(v for _, v in uv), max(v for _, v in uv)
        centre = add(cen, add(mul(right, (u0 + u1) / 2), mul(up, (v0 + v1) / 2)))
        poly = [(u - (u0 + u1) / 2, v - (v0 + v1) / 2) for u, v in uv]
        if sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(poly, poly[1:] + poly[:1])) < 0:
            poly.reverse()                       # counter-clockwise seen from outside
        turns = [(b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]) for a, b, c in zip(poly, poly[1:] + poly[:1], poly[2:] + poly[:2])]
        assert all(t > -1e-6 for t in turns), f"glass pane {pane_id} is not convex (cracks and fragments are cut for convex outlines)"
        self.panes.append({"id": pane_id, "centre": centre, "normal": normal, "size": (u1 - u0, v1 - v0), "outline": poly})
        return node

    @staticmethod
    def _num(x):
        s = ("%.3f" % x).rstrip("0").rstrip(".")
        return "0" if s in ("-0", "") else s

    def json_lines(self, indent):
        f = self._num
        out = []
        for p in self.panes:
            vec = lambda v: "[" + ", ".join(f(x) for x in v) + "]"
            outline = "[" + ", ".join(vec(q) for q in p["outline"]) + "]"
            out.append(f'{indent}{{ "id": "{p["id"]}", "centre": {vec(p["centre"])}, "normal": {vec(p["normal"])}, "size": {vec(p["size"])}, "outline": {outline} }}')
        return ",\n".join(out)

    def write_vehicles_json(self, path):
        """Replaces the `glass` list of this vehicle in vehicles.json, leaving everything else of the file as it is."""
        text = Path(path).read_text()
        at = text.index(f'"id": "{self.vehicle_id}"')
        start = text.index('"glass": [', at)
        nxt = text.find('"id": "', at + 10)
        assert nxt < 0 or start < nxt, f"{self.vehicle_id} has no glass list in {path}"
        i, depth = start + len('"glass": ['), 1
        while depth:
            depth += {"[": 1, "]": -1}.get(text[i], 0)
            i += 1
        indent = "        "
        Path(path).write_text(text[:start] + '"glass": [\n' + self.json_lines(indent) + "\n      ]" + text[i:])

    def report(self):
        for i, p in enumerate(self.panes):
            print(f"glass_{i:<2d} {p['id']:18s} size={p['size'][0]:.3f}x{p['size'][1]:.3f} outline={len(p['outline'])} points")


def vehicles_json_path():
    return Path(__file__).resolve().parents[2] / "godot" / "shooter" / "data" / "vehicles.json"
