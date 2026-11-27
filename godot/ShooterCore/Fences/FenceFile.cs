using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore.Fences;

/// <summary>fences.json (docs/fence-destruction.md Contract 1) as <see cref="FenceSectionInfo"/>s, by index (the position in "fences"), Godot axes (x, z = -y, height = z).</summary>
public static class FenceFile
{
    const float DefaultHeightM = 1.2f, DefaultThicknessM = 0.06f;
    const uint DefaultTint = 0x9a8064;

    /// <summary>
    /// Every section, in file order (index = position, so a section that cannot be read fully is kept as a plain one with no pieces: it never breaks).
    /// Malformed JSON throws <see cref="JsonException"/>; an unknown version or no list gives none. A list is read up to 65536 sections (the u16 index).
    /// </summary>
    public static IReadOnlyList<FenceSectionInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 1) return [];
        if (!root.TryGetProperty("fences", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var res = new List<FenceSectionInfo>(list.GetArrayLength());
        foreach (var f in list.EnumerateArray())
        {
            if (res.Count > ushort.MaxValue) break;
            res.Add(Section((ushort)res.Count, f));
        }
        return res;
    }

    static FenceSectionInfo Section(ushort index, JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) return new FenceSectionInfo { Index = index, HeightM = DefaultHeightM, ThicknessM = DefaultThicknessM, Tint = DefaultTint };
        var okA = Point(f, "a", out var a);
        var okB = Point(f, "b", out var b);
        var za = Num(f, "za", 0f);
        var type = f.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var kind = KindOf(f, type);
        var thick = Num(f, "thickness_m", DefaultThicknessM);
        var s = new FenceSectionInfo
        {
            Index = index,
            Kind = kind,
            Type = type,
            A = a,
            B = b,
            ZA = za,
            ZB = Num(f, "zb", za),
            HeightM = Num(f, "height_m", DefaultHeightM),
            ThicknessM = thick > 0 ? thick : DefaultThicknessM,
            Tint = Tint(f),
            Collider = Str(f, "collider"),
            Grid = kind == FenceKind.Wall ? Str(f, "grid") : null,
        };
        if (kind == FenceKind.Wall || !okA || !okB) return s;
        var pieces = f.TryGetProperty("pieces", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? Math.Clamp(n, 0, 64) : 0;
        var pieceM = Num(f, "piece_m", 0f);
        if (pieces == 0 || !(pieceM > 0)) return s;
        s = s with { Pieces = pieces, PieceM = pieceM, Piece0M = MathF.Min(0, Num(f, "piece0_m", 0f)), Uv = Uv(f) };
        return s with { Posts = Posts(f, s) };
    }

    // "kind" when it names one; else the older maps' type (picket, wire, a masonry type is a wall, none is plain)
    static FenceKind KindOf(JsonElement f, string? type)
    {
        switch (Str(f, "kind"))
        {
            case "plain": return FenceKind.Plain;
            case "picket": return FenceKind.Picket;
            case "wire": return FenceKind.Wire;
            case "wall": return FenceKind.Wall;
        }
        return type switch
        {
            "picket" => FenceKind.Picket,
            "wire" => FenceKind.Wire,
            "brick_red" or "brick_white" or "brick_wall" or "stone_wall" or "concrete" => FenceKind.Wall,
            _ => FenceKind.Plain,
        };
    }

    // map local (x east, y north) to Godot's flat (x, z = -north)
    static bool Point(JsonElement f, string key, out Vector2 p)
    {
        p = default;
        if (!f.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2) return false;
        if (e[0].ValueKind != JsonValueKind.Number || e[1].ValueKind != JsonValueKind.Number) return false;
        p = new Vector2(e[0].GetSingle(), -e[1].GetSingle());
        return true;
    }

    static float Num(JsonElement f, string key, float fallback) => f.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : fallback;

    static string? Str(JsonElement f, string key) => f.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    static uint Tint(JsonElement f)
    {
        var s = Str(f, "colour")?.TrimStart('#');
        return s is { Length: 6 } && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : DefaultTint;
    }

    // "posts": [[along_m, width_m, z0, z1, "#rrggbb"], ...]; an entry that cannot be read is skipped
    static IReadOnlyList<FencePost> Posts(JsonElement f, FenceSectionInfo s)
    {
        if (!f.TryGetProperty("posts", out var e) || e.ValueKind != JsonValueKind.Array) return [];
        var res = new List<FencePost>(e.GetArrayLength());
        foreach (var p in e.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 5) continue;
            var ok = true;
            for (var i = 0; i < 4; i++) ok &= p[i].ValueKind == JsonValueKind.Number;
            if (!ok) continue;
            var along = p[0].GetSingle();
            var hex = p[4].ValueKind == JsonValueKind.String ? p[4].GetString()?.TrimStart('#') : null;
            var colour = hex is { Length: 6 } && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : s.Tint;
            var piece = Math.Clamp((int)MathF.Floor((along - s.Piece0M) / s.PieceM), 0, s.Pieces - 1);
            res.Add(new FencePost(along, p[1].GetSingle(), p[2].GetSingle(), p[3].GetSingle(), colour, piece));
        }
        return res;
    }

    static Vector4 Uv(JsonElement f)
    {
        if (!f.TryGetProperty("uv", out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 4) return default;
        for (var i = 0; i < 4; i++) if (e[i].ValueKind != JsonValueKind.Number) return default;
        return new Vector4(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());
    }
}
