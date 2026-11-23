using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Icons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Hunts;

/// <summary>A species' baked side picture (nose to the right) as a texture, with its outline made once on first use (a species not hunted yet is drawn as the outline).</summary>
public sealed class SilhouetteArt
{
    readonly Image _source;
    Texture2D? _outline;

    public Texture2D Fill { get; }

    public SilhouetteArt(Image img)
    {
        img.Convert(Image.Format.Rgba8);
        _source = img;
        var copy = (Image)img.Duplicate();
        copy.GenerateMipmaps();
        Fill = ImageTexture.CreateFromImage(copy);
    }

    /// <summary>The edge of the picture, a band about 1/36 of its width: white with alpha, tinted by the drawer.</summary>
    public Texture2D Outline => _outline ??= BuildOutline();

    Texture2D BuildOutline()
    {
        int w = _source.GetWidth(), h = _source.GetHeight();
        var rgba = _source.GetData();
        var alpha = new byte[w * h];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = rgba[i * 4 + 3];
        var edge = HuntArtMath.Outline(alpha, w, h, Math.Max(2, w / 36));
        return HuntArt.MaskTexture(edge, w, h);
    }
}

/// <summary>
/// The pictures of the Hunter's Log, made once and shared: a species' silhouette from the pictures baked into animal_shapes.json (the hit diagram's own),
/// else the species' baked <c>animals/&lt;id&gt;.png</c> (the repository's, then the private asset folder's), and the sort arrows of the table's headers.
/// </summary>
public static class HuntArt
{
    static readonly Dictionary<AnimalShape, SilhouetteArt?> ByShape = new();
    static readonly Dictionary<string, SilhouetteArt?> BySpecies = new();
    static Texture2D? _arrowUp, _arrowDown, _arrowNone;

    const int ArrowPx = 48;

    public static SilhouetteArt? Of(AnimalShape shape)
    {
        if (ByShape.TryGetValue(shape, out var known)) return known;
        SilhouetteArt? art = null;
        if (shape.Picture is { Length: > 0 } png)
        {
            var img = new Image();
            if (img.LoadPngFromBuffer(png) == Error.Ok) art = new SilhouetteArt(img);
        }
        return ByShape[shape] = art;
    }

    /// <summary>The species' baked picture (<c>res://ui/icons/animals/&lt;id&gt;.png</c>, else <c>animal-silhouettes/&lt;id&gt;.png</c> of the private asset folder); null when it has neither.</summary>
    public static SilhouetteArt? OfSpecies(string speciesId)
    {
        if (BySpecies.TryGetValue(speciesId, out var known)) return known;
        Image? img = null;
        var res = $"{IconRegistry.Root}animals/{speciesId}.png";
        if (ResourceLoader.Exists(res)) img = GD.Load<Texture2D>(res)?.GetImage();
        else
        {
            var file = System.IO.Path.Combine(GamePaths.PrivateAssets(), "animal-silhouettes", speciesId + ".png");
            if (System.IO.File.Exists(file)) img = Image.LoadFromFile(file);
        }
        return BySpecies[speciesId] = img == null ? null : new SilhouetteArt(img);
    }

    /// <summary>A white picture with this alpha mask (one byte per pixel), mipmapped.</summary>
    public static Texture2D MaskTexture(byte[] alpha, int w, int h)
    {
        var rgba = new byte[alpha.Length * 4];
        for (var i = 0; i < alpha.Length; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = alpha[i]; }
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The sort arrow of a column header: up, down, or an empty one of the same size so a header never changes width when it is sorted.</summary>
    public static Texture2D Arrow(bool? ascending)
    {
        if (ascending == true) return _arrowUp ??= BuildArrow(up: true, empty: false);
        if (ascending == false) return _arrowDown ??= BuildArrow(up: false, empty: false);
        return _arrowNone ??= BuildArrow(up: false, empty: true);
    }

    static Texture2D? _person, _crosshair, _rifle, _tape, _calendar;

    /// <summary>The "hunter" icon: a head over shoulders (white with alpha, tinted by the drawer).</summary>
    public static Texture2D Person => _person ??= BuildMask(px =>
    {
        // head: circle centred (0.5, 0.3) r 0.2; shoulders: the top half of an ellipse from y 0.55 to 0.95
        var head = Math.Pow(px.x - 0.5, 2) + Math.Pow(px.y - 0.3, 2) <= 0.04;
        var sh = px.y >= 0.58 && px.y <= 0.95 && Math.Pow((px.x - 0.5) / 0.42, 2) + Math.Pow((px.y - 0.95) / 0.37, 2) <= 1;
        return head || sh;
    });

    /// <summary>The "hit area" icon: a crosshair, a ring with four ticks and a dot.</summary>
    public static Texture2D Crosshair => _crosshair ??= BuildMask(px =>
    {
        var dx = px.x - 0.5; var dy = px.y - 0.5; var r = Math.Sqrt(dx * dx + dy * dy);
        var ring = r is >= 0.28 and <= 0.38;
        var tick = (Math.Abs(dx) <= 0.04 || Math.Abs(dy) <= 0.04) && r is >= 0.2 and <= 0.5;
        return ring || tick || r <= 0.07;
    });

    /// <summary>The "weapon" icon: a flat sniper-rifle glyph, the same for every weapon (one flat style with <see cref="Person"/> and <see cref="Crosshair"/>; the weapon's name is the text).</summary>
    public static Texture2D Rifle => _rifle ??= BuildMask(p =>
    {
        var (x, y) = p;
        // the long barrel and the short muzzle brake; the stock, receiver and scope are drawn at 80 % of their width so the barrel gets the length
        var barrel = x is >= .40 and <= .98 && y is >= .47 and <= .54;
        var muzzle = x is >= .91 and <= .98 && y is >= .43 and <= .57;
        return barrel || muzzle || RifleBack(x / .8, y);
    });

    /// <summary>The "distance" icon: a stretch of measuring tape, its graduations notched in from the top edge, every other one longer.</summary>
    public static Texture2D Tape => _tape ??= BuildMask(p =>
    {
        var (x, y) = p;
        if (x is < .03 or > .97 || y is < .34 or > .66) return false;
        for (var i = 0; i < 11; i++)
        {
            var t = .08 + .084 * i;
            if (Math.Abs(x - t) <= .015 && y <= .34 + (i % 2 == 0 ? .16 : .09)) return false;
        }
        return true;
    });

    // ---- the record badge (a winner's medal on a ribbon, two kinds: a measuring tape for the longest shot, a weight marked "kg" for the heaviest) ----

    const double BadgeX = .5, BadgeY = .42, BadgeR = .36;
    static Texture2D? _badgeRibbon, _badgeDisc, _badgeRing, _badgeTape, _badgeWeight;

    /// <summary>The two ribbon tails behind the medal (the drawer tints them darker than the disc).</summary>
    public static Texture2D BadgeRibbon => _badgeRibbon ??= BuildMask(p =>
    {
        foreach (var sg in new[] { -1, 1 })
        {
            var pts = new (double X, double Y)[]
            {
                (BadgeX + sg * .25 * BadgeR, BadgeY + .6 * BadgeR), (BadgeX + sg * .85 * BadgeR, BadgeY + .6 * BadgeR), (BadgeX + sg * .85 * BadgeR, .97),
                (BadgeX + sg * .55 * BadgeR, .88), (BadgeX + sg * .25 * BadgeR, .97),
            };
            if (InPoly(p.x, p.y, pts)) return true;
        }
        return false;
    });

    /// <summary>The medal's disc.</summary>
    public static Texture2D BadgeDisc => _badgeDisc ??= BuildMask(p => Math.Sqrt(Math.Pow(p.x - BadgeX, 2) + Math.Pow(p.y - BadgeY, 2)) <= BadgeR);

    /// <summary>The thin ring inside the disc.</summary>
    public static Texture2D BadgeRing => _badgeRing ??= BuildMask(p =>
    {
        var d = Math.Sqrt(Math.Pow(p.x - BadgeX, 2) + Math.Pow(p.y - BadgeY, 2));
        return d <= .82 * BadgeR && d >= .82 * BadgeR - .021;
    });

    /// <summary>The longest shot's emblem: a tape strip with its ticks (the same strip as <see cref="Tape"/>, on the medal).</summary>
    public static Texture2D BadgeTape => _badgeTape ??= BuildMask(p =>
    {
        double w = 1.25 * BadgeR, h = .46 * BadgeR, x0 = BadgeX - w / 2, y0 = BadgeY - h / 2;
        if (p.x < x0 || p.x > x0 + w || p.y < y0 || p.y > y0 + h) return false;
        for (var i = 0; i < 9; i++)
            if (Math.Abs(p.x - (x0 + w * (i + .5) / 9)) <= w * .012 && p.y <= y0 + h * (i % 2 == 0 ? .55 : .32)) return false;
        return true;
    });

    /// <summary>The heaviest's emblem: an old iron weight with a ring handle and "kg" cut out of its body.</summary>
    public static Texture2D BadgeWeight => _badgeWeight ??= BuildMask(p =>
    {
        double s = 1.25 * BadgeR, cy = BadgeY + .05 * BadgeR, top = cy - .28 * s, bot = cy + .46 * s;
        // the ring handle
        double r = .2 * s, hy = top - .36 * s + r, hd = Math.Sqrt(Math.Pow(p.x - BadgeX, 2) + Math.Pow(p.y - hy, 2));
        var handle = hd <= r && hd >= r - .08 * s;
        var body = false;
        if (p.y >= top && p.y <= bot)
        {
            var half = (.28 + .22 * (p.y - top) / (bot - top)) * s;
            body = Math.Abs(p.x - BadgeX) <= half;
        }
        if (!body) return handle;
        // "kg": strokes of width t, in a box centred on the body
        double t = .085 * s, h = .46 * s, ty = cy + .12 * s, asc = ty - .5 * h, xt = ty - .12 * h, bas = ty + .3 * h, desc = ty + .5 * h, mid = (xt + bas) / 2;
        double kx = BadgeX - .2 * s, gx = BadgeX + .08 * s, gw = .2 * s;
        bool Seg(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy, u = l2 == 0 ? 0 : Math.Clamp(((p.x - ax) * dx + (p.y - ay) * dy) / l2, 0, 1);
            return Math.Sqrt(Math.Pow(p.x - (ax + u * dx), 2) + Math.Pow(p.y - (ay + u * dy), 2)) <= t / 2;
        }
        var k = Seg(kx, asc, kx, bas) || Seg(kx + .17 * s, xt, kx, mid + .02 * h) || Seg(kx + .02 * s, mid, kx + .18 * s, bas);
        double ga = gw / 2, gb = (bas - xt) / 2, rho = Math.Sqrt(Math.Pow((p.x - (gx + ga)) / ga, 2) + Math.Pow((p.y - mid) / gb, 2));
        var g = (rho <= 1 && rho >= 1 - t / Math.Min(ga, gb)) || Seg(gx + gw, xt + t / 2, gx + gw, desc - t) || Seg(gx + gw, desc - t, gx + .1 * gw, desc - .2 * t);
        return !(k || g);
    });

    /// <summary>The "when" icon: a calendar page with its two rings and a grid of days.</summary>
    public static Texture2D Calendar => _calendar ??= BuildMask(p =>
    {
        var (x, y) = p;
        var rings = (x is >= .24 and <= .34 || x is >= .66 and <= .76) && y is >= .08 and <= .30;
        var page = x is >= .10 and <= .90 && y is >= .20 and <= .90;
        var window = x is >= .18 and <= .82 && y is >= .46 and <= .82;
        var day = false;
        foreach (var cx in new[] { .32, .50, .68 }) foreach (var cy in new[] { .55, .73 }) day |= Math.Abs(x - cx) <= .055 && Math.Abs(y - cy) <= .055;
        return rings || (page && (!window || day));
    });

    static readonly (double X, double Y)[] RifleStock = { (.02, .40), (.26, .44), (.26, .60), (.15, .62), (.12, .72), (.02, .72) };
    static readonly (double X, double Y)[] RifleGrip = { (.33, .58), (.43, .58), (.40, .73), (.32, .73) };

    static bool RifleBack(double x, double y)
    {
        var body = x is >= .24 and <= .54 && y is >= .43 and <= .58;
        var mag = x is >= .46 and <= .54 && y is >= .58 and <= .68;
        var scope = x is >= .28 and <= .64 && y is >= .29 and <= .37 || x is >= .22 and <= .30 && y is >= .26 and <= .40 || x is >= .60 and <= .70 && y is >= .26 and <= .40;
        var mount = (x is >= .32 and <= .37 || x is >= .52 and <= .57) && y is >= .37 and <= .43;
        return body || mag || scope || mount || InPoly(x, y, RifleStock) || InPoly(x, y, RifleGrip);
    }

    static bool InPoly(double x, double y, (double X, double Y)[] pts)
    {
        var inside = false;
        for (var i = 0; i < pts.Length; i++)
        {
            var (x1, y1) = pts[i]; var (x2, y2) = pts[(i + 1) % pts.Length];
            if ((y1 > y) != (y2 > y) && x < (x2 - x1) * (y - y1) / (y2 - y1) + x1) inside = !inside;
        }
        return inside;
    }

    static Texture2D BuildMask(Func<(double x, double y), bool> inside)
    {
        const int n = 96;
        var a = new byte[n * n];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (inside(((x + 0.5) / n, (y + 0.5) / n))) a[y * n + x] = 255;
        return MaskTexture(a, n, n);
    }

    static Texture2D BuildArrow(bool up, bool empty)
    {
        var a = new byte[ArrowPx * ArrowPx];
        if (!empty)
            for (var y = 0; y < ArrowPx; y++)
            {
                // a triangle filling the picture: at the row's height its width grows from the tip to the base
                var t = (up ? y : ArrowPx - 1 - y) / (float)(ArrowPx - 1);
                var half = t * ArrowPx / 2f;
                for (var x = 0; x < ArrowPx; x++)
                    if (MathF.Abs(x + 0.5f - ArrowPx / 2f) <= half) a[y * ArrowPx + x] = 255;
            }
        return MaskTexture(a, ArrowPx, ArrowPx);
    }
}
