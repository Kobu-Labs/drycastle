using System.Numerics;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. Tree cards (#362): beyond the pack tree radius a tree is drawn as three flat cards
// (front, side, top) that show the very pack model it becomes near the player, so the swap changes almost nothing. The pictures are drawn here, on the CPU,
// from the model's own triangles and textures (no render target, no frame to wait for), into one atlas for all models. Streaming: MapLoader.PackTrees.cs.

/// <summary>pack_trees.json <c>cards</c>: the far trees as cards of the pack models (see the file's _note).</summary>
public sealed class PackCardConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Width of one picture in the atlas (px); a front or side picture is twice as high, the top one square. A model takes 3 x 2 cells.</summary>
    public int CellPx { get; set; } = 128;
    /// <summary>Side of a card tile (m): all cards of a tile are one draw.</summary>
    public float TileM { get; set; } = 192f;
    /// <summary>Cards are built and drawn this far from the focus; 0 = the whole map (built once, nearest first, never freed), so no baked tree is ever seen.</summary>
    public float FarM { get; set; }
    /// <summary>Alpha below which a card's texel is not drawn.</summary>
    public float Scissor { get; set; } = 0.5f;

    public void Validate()
    {
        if (CellPx < 16 || CellPx > 1024) throw new DataFileException("pack_trees.json: cards.cell_px must be within 16 and 1024");
        if (!(TileM > 0) || FarM < 0) throw new DataFileException("pack_trees.json: cards.tile_m must be above 0 and far_m not negative");
        if (!(Scissor > 0) || Scissor >= 1) throw new DataFileException("pack_trees.json: cards.scissor must be between 0 and 1");
    }
}

/// <summary>A texture on the CPU: RGBA bytes (sRGB), row 0 at the top. Only the first W * H * 4 bytes are read (mipmaps after them are ignored).</summary>
public sealed record CardTexture(int W, int H, byte[] Rgba);

/// <summary>What a pack material looks like to the card painter: its texture (null: white), its colour (sRGB) and its alpha scissor (negative: opaque).</summary>
public sealed record CardMaterial(CardTexture? Texture, Vector4 Colour, float Scissor);

/// <summary>A picture's place in the atlas (UV, v down).</summary>
public readonly record struct CardRect(float U0, float V0, float U1, float V1);

/// <summary>
/// The cards of one model, in the model's frame (foot at the origin): the front and side cards span [-Radius, Radius] by [Y0, Y1] through the trunk axis,
/// the top card is the square of that radius at <see cref="TopY"/>.
/// </summary>
public sealed record CardModel(float Radius, float Y0, float Y1, float TopY, CardRect Front, CardRect Side, CardRect Top);

/// <summary>The atlas of all cards: colour (sRGB, alpha = cover) and normals (model frame, xyz * 0.5 + 0.5), each with its mipmaps after it; the cards by model id (null: none).</summary>
public sealed class CardAtlas
{
    public required int W { get; init; }
    public required int H { get; init; }
    public required byte[] Albedo { get; init; }
    public required byte[] Normal { get; init; }
    public required CardModel?[] Models { get; init; }
}

public static class CardPainter
{
    /// <summary>Clear pixels kept around each picture so a neighbour does not bleed in at the first mipmaps.</summary>
    public const int Pad = 4;
    const int Super = 2; // painted at twice the size, then averaged: soft edges
    const int MaxAtlas = 8192;

    public enum View { Front, Side, Top }

    /// <summary>The extent of a model's cards; null for a model with no size. The top card sits at the mean height of the outer vertices (the crown), mid height when there are none.</summary>
    public static (float Radius, float Y0, float Y1, float TopY)? Measure(PackModelMesh m)
    {
        var (lo, hi) = m.Bounds;
        var r = MathF.Max(MathF.Max(MathF.Abs(lo.X), MathF.Abs(hi.X)), MathF.Max(MathF.Abs(lo.Z), MathF.Abs(hi.Z)));
        if (!(r > 1e-4f) || !(hi.Y - lo.Y > 1e-4f)) return null;
        double sum = 0; var n = 0;
        foreach (var s in m.Surfaces)
            foreach (var v in s.Vertices)
                if (v.X * v.X + v.Z * v.Z > 0.25f * r * r) { sum += v.Y; n++; }
        return (r, lo.Y, hi.Y, n > 0 ? (float)(sum / n) : 0.5f * (lo.Y + hi.Y));
    }

    /// <summary>
    /// Where a point of the model lands in a picture of w x h pixels (x right, y down) and how near the viewer it is (bigger = nearer). Front: seen from +Z. Side: seen
    /// from +X (so -Z is to the right). Top: seen from above, Z down the picture. <see cref="PackCardMesher"/> lays the cards out to match.
    /// </summary>
    public static Vector3 Project(Vector3 p, View view, float r, float y0, float y1, float w, float h) => view switch
    {
        View.Front => new Vector3((p.X + r) / (2 * r) * w, (y1 - p.Y) / (y1 - y0) * h, p.Z),
        View.Side => new Vector3((r - p.Z) / (2 * r) * w, (y1 - p.Y) / (y1 - y0) * h, p.X),
        _ => new Vector3((p.X + r) / (2 * r) * w, (p.Z + r) / (2 * r) * h, p.Y),
    };

    /// <summary>
    /// One picture of a model: its triangles drawn flat (no lighting: the shader lights the card) with a depth test, into <paramref name="w"/> x <paramref name="h"/> pixels.
    /// Returns colour (sRGB, alpha = share of the pixel covered) and normals (model frame, packed); both are spread into the empty pixels around the model so
    /// filtering does not pull in black.
    /// </summary>
    public static (byte[] Rgba, byte[] Normal) Paint(PackModelMesh m, IReadOnlyDictionary<int, CardMaterial> materials, View view, float r, float y0, float y1, int w, int h)
    {
        int sw = w * Super, sh = h * Super;
        var depth = new float[sw * sh];
        Array.Fill(depth, float.NegativeInfinity);
        var col = new byte[sw * sh * 3];
        var nrm = new float[sw * sh * 3];
        var hit = new bool[sw * sh];
        foreach (var s in m.Surfaces)
        {
            var mat = materials.TryGetValue(s.Material, out var cm) ? cm : new CardMaterial(null, Vector4.One, -1f);
            var tintLin = new Vector3(ToLinear(mat.Colour.X), ToLinear(mat.Colour.Y), ToLinear(mat.Colour.Z));
            var plain = mat.Colour.X >= 0.999f && mat.Colour.Y >= 0.999f && mat.Colour.Z >= 0.999f;
            for (var i = 0; i + 2 < s.Indices.Length; i += 3)
            {
                int ia = s.Indices[i], ib = s.Indices[i + 1], ic = s.Indices[i + 2];
                var a = Project(s.Vertices[ia], view, r, y0, y1, sw, sh);
                var b = Project(s.Vertices[ib], view, r, y0, y1, sw, sh);
                var c = Project(s.Vertices[ic], view, r, y0, y1, sw, sh);
                var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(area) < 1e-9f) continue;
                int px0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), px1 = Math.Min(sw - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int py0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), py1 = Math.Min(sh - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                for (var py = py0; py <= py1; py++)
                    for (var px = px0; px <= px1; px++)
                    {
                        float x = px + 0.5f, y = py + 0.5f;
                        var wa = ((b.X - x) * (c.Y - y) - (b.Y - y) * (c.X - x)) / area;
                        var wb = ((c.X - x) * (a.Y - y) - (c.Y - y) * (a.X - x)) / area;
                        var wc = 1f - wa - wb;
                        if (wa < 0 || wb < 0 || wc < 0) continue;
                        var d = wa * a.Z + wb * b.Z + wc * c.Z;
                        var at = py * sw + px;
                        if (d <= depth[at]) continue;
                        var uv = wa * s.Uv[ia] + wb * s.Uv[ib] + wc * s.Uv[ic];
                        var texel = Sample(mat.Texture, uv);
                        var alpha = texel.W * mat.Colour.W;
                        if (mat.Scissor >= 0 && alpha < mat.Scissor) continue;
                        depth[at] = d;
                        hit[at] = true;
                        var rgb = new Vector3(texel.X, texel.Y, texel.Z);
                        if (!plain) rgb = new Vector3(ToSrgb(ToLinear(rgb.X) * tintLin.X), ToSrgb(ToLinear(rgb.Y) * tintLin.Y), ToSrgb(ToLinear(rgb.Z) * tintLin.Z));
                        col[at * 3] = ToByte(rgb.X); col[at * 3 + 1] = ToByte(rgb.Y); col[at * 3 + 2] = ToByte(rgb.Z);
                        var n = wa * s.Normals[ia] + wb * s.Normals[ib] + wc * s.Normals[ic];
                        var len = n.Length();
                        n = len > 1e-6f ? n / len : Vector3.UnitY;
                        nrm[at * 3] = n.X; nrm[at * 3 + 1] = n.Y; nrm[at * 3 + 2] = n.Z;
                    }
            }
        }

        // average the super-sampled pixels: colour and normal over the covered ones, alpha = the covered share
        var rgba = new byte[w * h * 4];
        var normal = new byte[w * h * 4];
        var filled = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int n = 0; float cr = 0, cg = 0, cb = 0; var ns = Vector3.Zero;
                for (var dy = 0; dy < Super; dy++)
                    for (var dx = 0; dx < Super; dx++)
                    {
                        var at = (y * Super + dy) * sw + x * Super + dx;
                        if (!hit[at]) continue;
                        n++; cr += col[at * 3]; cg += col[at * 3 + 1]; cb += col[at * 3 + 2];
                        ns += new Vector3(nrm[at * 3], nrm[at * 3 + 1], nrm[at * 3 + 2]);
                    }
                var o = (y * w + x) * 4;
                normal[o + 3] = 255;
                if (n == 0) continue;
                filled[y * w + x] = true;
                rgba[o] = (byte)(cr / n + 0.5f); rgba[o + 1] = (byte)(cg / n + 0.5f); rgba[o + 2] = (byte)(cb / n + 0.5f);
                rgba[o + 3] = (byte)(255 * n / (Super * Super));
                PackNormal(normal, o, ns);
            }
        Spread(rgba, normal, filled, w, h, Pad + 2);
        return (rgba, normal);
    }

    static void PackNormal(byte[] to, int o, Vector3 n)
    {
        var len = n.Length();
        n = len > 1e-6f ? n / len : Vector3.UnitY;
        to[o] = ToByte(n.X * 0.5f + 0.5f); to[o + 1] = ToByte(n.Y * 0.5f + 0.5f); to[o + 2] = ToByte(n.Z * 0.5f + 0.5f);
    }

    /// <summary>Gives each empty pixel next to a filled one the mean colour and normal of its filled neighbours (alpha stays 0), <paramref name="passes"/> pixels deep.</summary>
    static void Spread(byte[] rgba, byte[] normal, bool[] filled, int w, int h, int passes)
    {
        var next = new List<int>();
        for (var pass = 0; pass < passes; pass++)
        {
            next.Clear();
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (filled[y * w + x]) continue;
                    int n = 0, r = 0, g = 0, b = 0, nx = 0, ny = 0, nz = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int qx = x + dx, qy = y + dy;
                            if (qx < 0 || qy < 0 || qx >= w || qy >= h || !filled[qy * w + qx]) continue;
                            var q = (qy * w + qx) * 4;
                            n++; r += rgba[q]; g += rgba[q + 1]; b += rgba[q + 2];
                            nx += normal[q]; ny += normal[q + 1]; nz += normal[q + 2];
                        }
                    if (n == 0) continue;
                    var o = (y * w + x) * 4;
                    rgba[o] = (byte)(r / n); rgba[o + 1] = (byte)(g / n); rgba[o + 2] = (byte)(b / n);
                    normal[o] = (byte)(nx / n); normal[o + 1] = (byte)(ny / n); normal[o + 2] = (byte)(nz / n);
                    next.Add(y * w + x);
                }
            if (next.Count == 0) break;
            foreach (var i in next) filled[i] = true;
        }
    }

    static Vector4 Sample(CardTexture? t, Vector2 uv)
    {
        if (t == null || t.W <= 0 || t.H <= 0) return Vector4.One;
        var u = uv.X - MathF.Floor(uv.X); var v = uv.Y - MathF.Floor(uv.Y);
        var x = Math.Min(t.W - 1, (int)(u * t.W)); var y = Math.Min(t.H - 1, (int)(v * t.H));
        var o = (y * t.W + x) * 4;
        return new Vector4(t.Rgba[o], t.Rgba[o + 1], t.Rgba[o + 2], t.Rgba[o + 3]) / 255f;
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    static float ToLinear(float s) => s < 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    static float ToSrgb(float l) => l < 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;

    /// <summary>The atlas grid for <paramref name="count"/> models of 3 x 2 cells each, about square; the cell is halved until the atlas fits a texture.</summary>
    public static (int Cols, int Rows, int Cell) Layout(int count, int cellPx)
    {
        count = Math.Max(count, 1);
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count * 2.0 / 3.0)));
        var rows = (count + cols - 1) / cols;
        var cell = cellPx;
        while (cell > 16 && (cols * 3 * cell > MaxAtlas || rows * 2 * cell > MaxAtlas)) cell /= 2;
        return (cols, rows, cell);
    }

    /// <summary>
    /// Paints the cards of the models <paramref name="ids"/> (indices into <paramref name="models"/>) into one atlas. A model with no size gets no cards.
    /// Runs on a worker; reads nothing but its arguments.
    /// </summary>
    public static CardAtlas Build(IReadOnlyList<PackModelMesh> models, IReadOnlyList<int> ids, IReadOnlyDictionary<int, CardMaterial> materials, PackCardConfig cfg)
    {
        var (cols, rows, cell) = Layout(ids.Count, cfg.CellPx);
        int w = cols * 3 * cell, h = rows * 2 * cell;
        var albedo = new byte[w * h * 4];
        var normal = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { normal[i * 4] = 128; normal[i * 4 + 1] = 255; normal[i * 4 + 2] = 128; normal[i * 4 + 3] = 255; }
        var cards = new CardModel?[models.Count];
        var rects = new List<(int X, int Y, int W, int H)>[ids.Count];

        Parallel.For(0, ids.Count, slot =>
        {
            var id = ids[slot];
            var m = models[id];
            if (Measure(m) is not { } e) return;
            int ox = slot % cols * 3 * cell, oy = slot / cols * 2 * cell;
            var mine = new List<(int, int, int, int)>();
            CardRect Put(View view, int cx, int cw, int ch)
            {
                int iw = cw - 2 * Pad, ih = ch - 2 * Pad;
                var (rgba, nrm) = Paint(m, materials, view, e.Radius, e.Y0, e.Y1, iw, ih);
                // the picture with its spread rim, centred in the cell: paint again into the padded size would shift it, so copy with the rim clamped
                for (var y = 0; y < ch; y++)
                    for (var x = 0; x < cw; x++)
                    {
                        int sx = Math.Clamp(x - Pad, 0, iw - 1), sy = Math.Clamp(y - Pad, 0, ih - 1);
                        var inside = x >= Pad && x < cw - Pad && y >= Pad && y < ch - Pad;
                        int s = (sy * iw + sx) * 4, d = ((oy + y) * w + ox + cx + x) * 4;
                        albedo[d] = rgba[s]; albedo[d + 1] = rgba[s + 1]; albedo[d + 2] = rgba[s + 2]; albedo[d + 3] = inside ? rgba[s + 3] : (byte)0;
                        normal[d] = nrm[s]; normal[d + 1] = nrm[s + 1]; normal[d + 2] = nrm[s + 2];
                    }
                mine.Add((ox + cx, oy, cw, ch));
                return new CardRect((ox + cx + Pad) / (float)w, (oy + Pad) / (float)h, (ox + cx + cw - Pad) / (float)w, (oy + ch - Pad) / (float)h);
            }
            var front = Put(View.Front, 0, cell, 2 * cell);
            var side = Put(View.Side, cell, cell, 2 * cell);
            var top = Put(View.Top, 2 * cell, cell, cell);
            cards[id] = new CardModel(e.Radius, e.Y0, e.Y1, e.TopY, front, side, top);
            rects[slot] = mine;
        });

        var cells = rects.Where(r => r != null).SelectMany(r => r).ToList();
        return new CardAtlas { W = w, H = h, Albedo = Mipmaps(albedo, w, h, cells, cfg.Scissor), Normal = Mipmaps(normal, w, h, null, 0f), Models = cards };
    }

    /// <summary>The sizes of the mipmaps of a w x h image, the image itself first, down to 1 x 1 (as the engine counts them).</summary>
    public static List<(int W, int H)> MipSizes(int w, int h)
    {
        var l = new List<(int, int)> { (w, h) };
        while (w > 1 || h > 1) { w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); l.Add((w, h)); }
        return l;
    }

    /// <summary>
    /// The image followed by its mipmaps. With <paramref name="cells"/> (pictures of a colour atlas, pixel boxes of the full image) the colour is averaged by alpha
    /// and each picture's alpha is raised in every mipmap until as much of it passes <paramref name="scissor"/> as in the full image: plain averaging thins a
    /// sparse crown until the tree is gone at a distance.
    /// </summary>
    public static byte[] Mipmaps(byte[] level0, int w, int h, IReadOnlyList<(int X, int Y, int W, int H)>? cells, float scissor)
    {
        var sizes = MipSizes(w, h);
        var total = sizes.Sum(s => s.W * s.H * 4);
        var all = new byte[total];
        Array.Copy(level0, all, w * h * 4);
        var thr = scissor * 255f;
        float[]? cover = null;
        if (cells != null)
        {
            cover = new float[cells.Count];
            for (var i = 0; i < cells.Count; i++) cover[i] = Cover(all, 0, w, cells[i], thr, 1f);
        }
        int src = 0, sw = w, sh = h;
        for (var level = 1; level < sizes.Count; level++)
        {
            var (dw, dh) = sizes[level];
            var dst = src + sw * sh * 4;
            for (var y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (var x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    float r = 0, g = 0, b = 0, a = 0, wsum = 0; var n = 0;
                    for (var yy = y0; yy < y1; yy++)
                        for (var xx = x0; xx < x1; xx++)
                        {
                            var s = src + (yy * sw + xx) * 4;
                            var wt = cells != null ? all[s + 3] + 1f : 1f; // by alpha, so the spread rim does not tint the tree; +1 keeps a colour where all is clear
                            r += all[s] * wt; g += all[s + 1] * wt; b += all[s + 2] * wt; a += all[s + 3]; wsum += wt; n++;
                        }
                    var o = dst + (y * dw + x) * 4;
                    all[o] = (byte)(r / wsum + 0.5f); all[o + 1] = (byte)(g / wsum + 0.5f); all[o + 2] = (byte)(b / wsum + 0.5f); all[o + 3] = (byte)(a / n + 0.5f);
                }
            }
            if (cells != null)
                for (var i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    var box = (X: c.X * dw / w, Y: c.Y * dh / h, W: c.W * dw / w, H: c.H * dh / h);
                    if (box.W < 2 || box.H < 2 || cover![i] <= 0) continue;
                    float lo = 1f, hi = 16f;
                    if (Cover(all, dst, dw, box, thr, lo) >= cover[i]) continue; // not thinner than the full picture
                    for (var it = 0; it < 12; it++)
                    {
                        var mid = 0.5f * (lo + hi);
                        if (Cover(all, dst, dw, box, thr, mid) < cover[i]) lo = mid; else hi = mid;
                    }
                    for (var y = box.Y; y < box.Y + box.H; y++)
                        for (var x = box.X; x < box.X + box.W; x++)
                        {
                            var o = dst + (y * dw + x) * 4 + 3;
                            all[o] = (byte)Math.Min(255f, all[o] * hi);
                        }
                }
            src = dst; sw = dw; sh = dh;
        }
        return all;
    }

    /// <summary>The share of a box's pixels whose alpha times <paramref name="scale"/> reaches the threshold.</summary>
    static float Cover(byte[] data, int offset, int stride, (int X, int Y, int W, int H) box, float thr, float scale)
    {
        var n = 0;
        for (var y = box.Y; y < box.Y + box.H; y++)
            for (var x = box.X; x < box.X + box.W; x++)
                if (MathF.Floor(MathF.Min(255f, data[offset + (y * stride + x) * 4 + 3] * scale)) >= thr) n++; // as the byte it is stored as
        return n / (float)(box.W * box.H);
    }
}

public static class PackCardMesher
{
    /// <summary>
    /// The cards of every placement (trees and ground objects) as one bucket of <paramref name="material"/>: three quads each, posed exactly as
    /// <see cref="PackTileMesher"/> poses the model, UV2 = the base as there. The normal of a card vertex is not a normal: it is the model's X axis in the
    /// world, from which the shader turns the atlas' normals (model frame) into the world. Empty when nothing has a card.
    /// </summary>
    public static List<PackBucket> Build(IReadOnlyList<CardModel?> cards, IReadOnlyList<PackPlacement> list, Vector3 centre, int material)
    {
        var n = 0;
        foreach (var p in list) if (p.Model >= 0 && p.Model < cards.Count && cards[p.Model] != null) n++;
        if (n == 0) return new List<PackBucket>();
        var b = PackBucket.Rent(material, n * 12, n * 18);
        foreach (var p in list)
        {
            if (p.Model < 0 || p.Model >= cards.Count || cards[p.Model] is not { } c) continue;
            var pose = new PackPose(p, centre);
            var axis = pose.Turned(Vector3.UnitX);
            var col = new Vector4((p.Tint == default ? Vector3.One : p.Tint) * 0.5f, PackTileMesher.KindAlpha(p.Kind, card: true));
            void Quad(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, CardRect r)
            {
                var first = b.V.Count;
                b.V.Add(pose.Point(p00)); b.V.Add(pose.Point(p10)); b.V.Add(pose.Point(p11)); b.V.Add(pose.Point(p01));
                b.Uv.Add(new Vector2(r.U0, r.V1)); b.Uv.Add(new Vector2(r.U1, r.V1)); b.Uv.Add(new Vector2(r.U1, r.V0)); b.Uv.Add(new Vector2(r.U0, r.V0));
                for (var i = 0; i < 4; i++) { b.N.Add(axis); b.Uv2.Add(pose.BaseUv); b.Col.Add(col); }
                b.I.Add(first); b.I.Add(first + 1); b.I.Add(first + 2); b.I.Add(first); b.I.Add(first + 2); b.I.Add(first + 3);
            }
            float r0 = c.Radius, y0 = c.Y0, y1 = c.Y1;
            // corners in picture order: bottom left, bottom right, top right, top left (CardPainter.Project: front x right; side -z right; top x right, z down)
            Quad(new(-r0, y0, 0), new(r0, y0, 0), new(r0, y1, 0), new(-r0, y1, 0), c.Front);
            Quad(new(0, y0, r0), new(0, y0, -r0), new(0, y1, -r0), new(0, y1, r0), c.Side);
            Quad(new(-r0, c.TopY, r0), new(r0, c.TopY, r0), new(r0, c.TopY, -r0), new(-r0, c.TopY, -r0), c.Top);
        }
        return new List<PackBucket> { b };
    }
}
