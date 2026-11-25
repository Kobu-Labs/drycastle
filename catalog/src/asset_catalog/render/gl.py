"""Offscreen OpenGL (EGL) drawing of a Scene: lit views with outline, and an albedo-only pass."""

from __future__ import annotations

from collections import OrderedDict

import moderngl
import numpy as np
from PIL import Image

from . import surfaces
from .camera import Layout
from .gltf import Doc
from .scene import Scene
from .surfaces import Surface

BG = (218, 218, 218)
SUPERSAMPLE = 2
TEX_CACHE = 10
# One-sided surfaces are drawn two-sided too: a wall or pane seen from behind must not vanish.
FORCE_DOUBLE_SIDED = True

MESH_VS = """
#version 330
uniform mat4 mvp;
in vec3 in_pos; in vec3 in_nrm; in vec2 in_uv; in vec4 in_col;
out vec3 v_nrm; out vec2 v_uv; out vec4 v_col;
void main() { gl_Position = mvp * vec4(in_pos, 1.0); v_nrm = in_nrm; v_uv = in_uv; v_col = in_col; }
"""

MESH_FS = """
#version 330
uniform sampler2D tex;
uniform bool has_tex;
uniform vec4 color;
uniform int alpha_mode;   // 0 opaque 1 blend 2 mask 3 hash
uniform float cutoff;
uniform int cull;         // 0 back 1 front 2 none
uniform bool lit;         // false: albedo only
uniform bool unlit_surface;
uniform mat3 vrot;
in vec3 v_nrm; in vec2 v_uv; in vec4 v_col;
layout(location = 0) out vec4 f0;
layout(location = 1) out vec4 f1;
layout(location = 2) out vec4 f2;

vec3 s2l(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c)); }
vec3 l2s(vec3 c) {
    c = clamp(c, 0.0, 1.0);
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}
float bayer(vec2 p) {
    int x = int(mod(p.x, 4.0)), y = int(mod(p.y, 4.0));
    int m[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
    return (float(m[y * 4 + x]) + 0.5) / 16.0;
}
void main() {
    if (cull == 0 && !gl_FrontFacing) discard;
    if (cull == 1 && gl_FrontFacing) discard;
    vec4 t = has_tex ? texture(tex, v_uv) : vec4(1.0);
    vec3 base = s2l(t.rgb) * color.rgb * v_col.rgb;
    float a = alpha_mode == 0 ? 1.0 : t.a * color.a * v_col.a;
    if (alpha_mode == 2 && a < cutoff) discard;
    if (alpha_mode == 3 && a < bayer(gl_FragCoord.xy)) discard;
    vec3 n = normalize(v_nrm);
    if (!gl_FrontFacing) n = -n;
    vec3 nv = normalize(vrot * n);
    vec3 outc = base;
    if (lit && !unlit_surface) {
        float hemi = mix(0.40, 0.54, n.y * 0.5 + 0.5);
        float key = max(dot(nv, normalize(vec3(-0.45, 0.60, 0.65))), 0.0);
        float fill = max(dot(nv, normalize(vec3(0.70, -0.10, 0.50))), 0.0);
        outc = base * (hemi + 0.55 * key + 0.16 * fill);
    }
    f0 = vec4(l2s(outc), alpha_mode == 1 ? a : 1.0);
    f1 = vec4(nv * 0.5 + 0.5, 1.0);
    f2 = vec4(1.0);
}
"""

GROUND_FS = """
#version 330
uniform sampler2D foot; uniform vec3 bg;
in vec2 v_uv;
layout(location = 0) out vec4 f0;
void main() {
    vec2 s = texture(foot, v_uv).rg;
    float sh = 0.20 * s.r + 0.16 * s.g;
    f0 = vec4(bg * (1.0 - sh), 1.0);
}
"""
GROUND_VS = """
#version 330
uniform mat4 mvp; uniform float y; uniform vec2 lo; uniform vec2 size;
in vec2 in_xz; out vec2 v_uv;
void main() {
    v_uv = (in_xz - lo) / size;
    gl_Position = mvp * vec4(in_xz.x, y, in_xz.y, 1.0);
    gl_Position.z = 0.0;
}
"""

FULL_VS = """
#version 330
in vec2 in_p; void main() { gl_Position = vec4(in_p, 0.0, 1.0); }
"""
RESET_FS = """
#version 330
layout(location = 1) out vec4 f1; layout(location = 2) out vec4 f2;
void main() { f1 = vec4(0.0); f2 = vec4(0.0); }
"""

POST_FS = """
#version 330
uniform sampler2D t0; uniform sampler2D t1; uniform sampler2D t2; uniform sampler2D td;
uniform float range_m; uniform float thr_m; uniform int ss;
out vec4 o;
float dep(ivec2 p) { return texelFetch(td, p, 0).r * range_m; }
vec3 shade(ivec2 p) {
    vec4 c = texelFetch(t0, p, 0);
    vec4 n = texelFetch(t1, p, 0);
    if (n.a < 0.5) return c.rgb;
    float d = dep(p);
    float dl = dep(p + ivec2(-1, 0)), dr = dep(p + ivec2(1, 0));
    float du = dep(p + ivec2(0, 1)), dd = dep(p + ivec2(0, -1));
    float lap = max(abs(dl + dr - 2.0 * d), abs(du + dd - 2.0 * d));
    float edge = lap > thr_m ? 0.50 : 0.0;
    vec3 nc = n.rgb * 2.0 - 1.0;
    float mind = 1.0;
    ivec2 offs[4] = ivec2[4](ivec2(-1, 0), ivec2(1, 0), ivec2(0, 1), ivec2(0, -1));
    for (int i = 0; i < 4; i++) {
        vec4 q = texelFetch(t1, p + offs[i], 0);
        if (q.a > 0.5) mind = min(mind, dot(nc, q.rgb * 2.0 - 1.0));
    }
    edge = max(edge, 0.32 * (1.0 - smoothstep(0.35, 0.80, mind)));
    return c.rgb * (1.0 - edge);
}
void main() {
    ivec2 base = ivec2(gl_FragCoord.xy) * ss;
    vec3 acc = vec3(0.0); float cov = 0.0;
    for (int j = 0; j < ss; j++) for (int i = 0; i < ss; i++) {
        ivec2 p = base + ivec2(i, j);
        acc += shade(p); cov += texelFetch(t2, p, 0).r;
    }
    float k = 1.0 / float(ss * ss);
    o = vec4(acc * k, cov * k);
}
"""


class GpuScene:
    """A Scene uploaded: one VAO per batch, its texture bound by key."""

    def __init__(
        self,
        items: list[tuple[Surface, moderngl.VertexArray, moderngl.Texture | None]],
        missing: list[str],
    ):
        self.items = items
        self.missing = missing


class Renderer:
    def __init__(self, size: int = 768):
        self.size = size
        self.n = size * SUPERSAMPLE
        self.ctx = moderngl.create_standalone_context(backend="egl")
        c = self.ctx
        self.mesh = c.program(vertex_shader=MESH_VS, fragment_shader=MESH_FS)
        self.ground = c.program(vertex_shader=GROUND_VS, fragment_shader=GROUND_FS)
        self.reset = c.program(vertex_shader=FULL_VS, fragment_shader=RESET_FS)
        self.post = c.program(vertex_shader=FULL_VS, fragment_shader=POST_FS)
        tri = np.array([-1, -1, 3, -1, -1, 3], "f4")
        self._full = c.vertex_array(self.reset, [(c.buffer(tri.tobytes()), "2f", "in_p")])
        self._post_vao = c.vertex_array(self.post, [(c.buffer(tri.tobytes()), "2f", "in_p")])
        self._tex: OrderedDict[str, moderngl.Texture | None] = OrderedDict()
        n = self.n
        self.t0, self.t1, self.t2 = (
            c.texture((n, n), 4, dtype="f1"),
            c.texture((n, n), 4, dtype="f1"),
            c.texture((n, n), 1, dtype="f1"),
        )
        self.depth = c.depth_texture((n, n))
        self.depth.compare_func = ""
        for t in (self.t0, self.t1, self.t2, self.depth):
            t.filter = (moderngl.NEAREST, moderngl.NEAREST)
        self.fbo = c.framebuffer([self.t0, self.t1, self.t2], self.depth)
        self.out_tex = c.texture((size, size), 4, dtype="f1")
        self.out_fbo = c.framebuffer([self.out_tex])

    def upload(self, scene: Scene, doc: Doc) -> GpuScene:
        c = self.ctx
        items, missing = [], []
        for b in scene.batches:
            surf, tex = b.surface, None
            if surf.tex:
                tex = self._texture(surf.tex, scene, doc)
                if tex is None:
                    missing.append(surf.tex.rsplit("/", 1)[-1])
                    surf = scene.resolved.placeholder_surface()
            vbo = c.buffer(b.verts.tobytes())
            vao = c.vertex_array(
                self.mesh, [(vbo, "3f 3f 2f 4f", "in_pos", "in_nrm", "in_uv", "in_col")]
            )
            items.append((surf, vao, tex))
        return GpuScene(items, missing)

    def _texture(self, key: str, scene: Scene, doc: Doc) -> moderngl.Texture | None:
        if key in self._tex:
            self._tex.move_to_end(key)
            return self._tex[key]
        img = surfaces.load_image(scene.resolved, key, doc)
        tex = None
        if img is not None:
            tex = self.ctx.texture(img.size, 4, img.tobytes())
            tex.build_mipmaps()
            tex.filter = (moderngl.LINEAR_MIPMAP_LINEAR, moderngl.LINEAR)
            tex.anisotropy = 8.0
        self._tex[key] = tex
        while len(self._tex) > TEX_CACHE:
            _, old = self._tex.popitem(last=False)
            if old is not None:
                old.release()
        return tex

    def release(self, gpu: GpuScene) -> None:
        for _, vao, _ in gpu.items:
            vao.release()

    def _draw(self, gpu: GpuScene, lay: Layout, lit: bool, pick) -> None:
        c, m = self.ctx, self.mesh
        m["mvp"].write(lay.mvp.T.astype("f4").tobytes())
        m["vrot"].write(lay.rot.T.astype("f4").tobytes())
        m["lit"] = lit
        for surf, vao, tex in pick:
            blend = surf.alpha == "blend"
            m["has_tex"] = tex is not None
            if tex is not None:
                tex.repeat_x = tex.repeat_y = surf.wrap != 1
                tex.use(0)
                m["tex"] = 0
            m["color"] = surf.color
            m["alpha_mode"] = (
                {"opaque": 0, "blend": 1, "mask": 2, "hash": 3}[surf.alpha]
                if lit
                else (0 if blend else {"opaque": 0, "mask": 2, "hash": 3}[surf.alpha])
            )
            m["cutoff"] = surf.cutoff
            m["cull"] = surf.cull if not FORCE_DOUBLE_SIDED else 2
            m["unlit_surface"] = surf.unlit
            if lit and blend:
                c.enable(moderngl.BLEND)
                c.blend_func = (
                    moderngl.SRC_ALPHA,
                    moderngl.ONE_MINUS_SRC_ALPHA,
                    moderngl.ONE,
                    moderngl.ONE_MINUS_SRC_ALPHA,
                )
                self.fbo.depth_mask = False
                self.fbo.color_mask = ((True,) * 4, (False,) * 4, (True,) * 4)
            else:
                c.disable(moderngl.BLEND)
                self.fbo.depth_mask = True
                self.fbo.color_mask = ((True,) * 4,) * 3
            vao.render(moderngl.TRIANGLES)
        c.disable(moderngl.BLEND)
        self.fbo.depth_mask = True
        self.fbo.color_mask = ((True,) * 4,) * 3

    def lit_view(self, scene: Scene, gpu: GpuScene, lay: Layout) -> tuple[np.ndarray, np.ndarray]:
        """(rgb uint8 HxWx3, coverage float HxW 0..1) of one shaded view with outline and shadow."""
        c = self.ctx
        self.fbo.use()
        c.viewport = (0, 0, self.n, self.n)
        self.fbo.color_mask = ((True,) * 4,) * 3
        self.fbo.depth_mask = True
        self.fbo.clear(BG[0] / 255, BG[1] / 255, BG[2] / 255, 1.0, depth=1.0)
        c.disable(moderngl.DEPTH_TEST)
        self.fbo.color_mask = ((False,) * 4, (True,) * 4, (True,) * 4)
        self._full.render(moderngl.TRIANGLES)  # zero the normal and coverage buffers
        c.enable(moderngl.DEPTH_TEST)
        if not lay.top_down:
            self._draw_ground(scene, lay)
        order = sorted(gpu.items, key=lambda it: it[0].alpha == "blend")
        self._draw(gpu, lay, True, order)
        return self._resolve(scene, lay)

    def _draw_ground(self, scene: Scene, lay: Layout) -> None:
        c = self.ctx
        foot, lo, size = footprint(scene)
        tex = c.texture((foot.shape[1], foot.shape[0]), 2, foot.tobytes())
        tex.filter = (moderngl.LINEAR, moderngl.LINEAR)
        tex.repeat_x = tex.repeat_y = False
        tex.use(5)
        g = self.ground
        g["mvp"].write(lay.mvp.T.astype("f4").tobytes())
        g["y"] = float(scene.lo[1])
        g["lo"] = (float(lo[0]), float(lo[1]))
        g["size"] = (float(size[0]), float(size[1]))
        g["foot"] = 5
        g["bg"] = tuple(v / 255 for v in BG)
        quad = np.array(
            [
                lo[0],
                lo[1],
                lo[0] + size[0],
                lo[1],
                lo[0],
                lo[1] + size[1],
                lo[0] + size[0],
                lo[1] + size[1],
            ],
            "f4",
        )
        buf = c.buffer(quad.tobytes())
        vao = c.vertex_array(g, [(buf, "2f", "in_xz")])
        c.disable(moderngl.DEPTH_TEST)
        self.fbo.color_mask = ((True,) * 4, (False,) * 4, (False,) * 4)
        vao.render(moderngl.TRIANGLE_STRIP)
        c.enable(moderngl.DEPTH_TEST)
        self.fbo.color_mask = ((True,) * 4,) * 3
        for o in (vao, buf, tex):
            o.release()

    def _resolve(self, scene: Scene, lay: Layout) -> tuple[np.ndarray, np.ndarray]:
        c = self.ctx
        self.out_fbo.use()
        c.viewport = (0, 0, self.size, self.size)
        c.disable(moderngl.DEPTH_TEST)
        p = self.post
        for i, t in enumerate((self.t0, self.t1, self.t2, self.depth)):
            t.use(i)
        p["t0"], p["t1"], p["t2"], p["td"] = 0, 1, 2, 3
        diag = float(np.linalg.norm(scene.hi - scene.lo))
        p["range_m"] = float(lay.depth_range_m)
        p["thr_m"] = 0.006 * diag + 2.0 / lay.px_per_m / SUPERSAMPLE
        p["ss"] = SUPERSAMPLE
        self._post_vao.render(moderngl.TRIANGLES)
        c.enable(moderngl.DEPTH_TEST)
        raw = np.frombuffer(self.out_fbo.read(components=4), np.uint8).reshape(
            self.size, self.size, 4
        )
        raw = raw[::-1]
        return np.ascontiguousarray(raw[..., :3]), raw[..., 3].astype(np.float32) / 255.0

    def albedo_view(
        self, scene: Scene, gpu: GpuScene, lay: Layout
    ) -> tuple[np.ndarray, np.ndarray]:
        """Unlit albedo-only render (no ground, no outline): (rgb HxWx3, opaque mask HxW bool)."""
        c = self.ctx
        self.fbo.use()
        c.viewport = (0, 0, self.n, self.n)
        self.fbo.color_mask = ((True,) * 4,) * 3
        self.fbo.clear(0.0, 0.0, 0.0, 0.0, depth=1.0)
        c.enable(moderngl.DEPTH_TEST)
        real = [it for it in gpu.items if not it[0].placeholder]
        solid = [it for it in real if it[0].alpha != "blend"]
        self._draw(gpu, lay, False, solid or real)
        raw = np.frombuffer(self.fbo.read(attachment=0, components=4), np.uint8)
        raw = raw.reshape(self.n, self.n, 4)[::-1][::SUPERSAMPLE, ::SUPERSAMPLE]
        return np.ascontiguousarray(raw[..., :3]), raw[..., 3] > 127


def _blur(a: np.ndarray, r: int) -> np.ndarray:
    """Box blur of radius r along both axes, applied twice (close to a gaussian)."""
    if r < 1:
        return a
    for _ in range(2):
        for axis in (0, 1):
            pad = [(0, 0), (0, 0)]
            pad[axis] = (r + 1, r)
            cs = np.cumsum(np.pad(a, pad), axis=axis)
            n = a.shape[axis]
            hi = np.take(cs, np.arange(2 * r + 1, 2 * r + 1 + n), axis=axis)
            lo = np.take(cs, np.arange(0, n), axis=axis)
            a = (hi - lo) / (2 * r + 1)
    return a


def footprint(scene: Scene, cells: int = 96) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Soft top-down occupancy of the model: (uint8 [h,w,2] soft+tight, xz origin, xz size)."""
    pts = np.concatenate([b.verts[:, :3] for b in scene.batches])
    tri = pts.reshape(-1, 3, 3)
    if len(tri) > 120_000:
        tri = tri[:: len(tri) // 120_000 + 1]
    samples = np.concatenate(
        [
            tri.reshape(-1, 3),
            tri.mean(1),
            (tri[:, 0] + tri[:, 1]) / 2,
            (tri[:, 1] + tri[:, 2]) / 2,
            (tri[:, 0] + tri[:, 2]) / 2,
        ]
    )[:, [0, 2]]
    ext = np.maximum(scene.hi - scene.lo, 1e-4)[[0, 2]]
    big = float(ext.max())
    pad = 0.3 * big
    lo = scene.lo[[0, 2]] - pad
    size = ext + 2 * pad
    cell = float(size.max()) / cells
    gx, gz = int(np.ceil(size[0] / cell)), int(np.ceil(size[1] / cell))
    ix = np.clip(((samples[:, 0] - lo[0]) / cell).astype(int), 0, gx - 1)
    iz = np.clip(((samples[:, 1] - lo[1]) / cell).astype(int), 0, gz - 1)
    occ = (np.bincount(iz * gx + ix, minlength=gx * gz).reshape(gz, gx) > 0).astype(np.float32)
    close = _blur(occ, max(1, round(0.03 * big / cell)))
    soft = _blur(occ, max(2, round(0.09 * big / cell)))
    out = np.stack([soft, np.minimum(close * 1.4, 1.0)], -1)
    return (out * 255).astype(np.uint8), lo, np.array([gx * cell, gz * cell])


def to_image(rgb: np.ndarray) -> Image.Image:
    return Image.fromarray(rgb, "RGB")
