using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

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
