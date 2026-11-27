using Drycastle.Viewer.Edits;
using Godot;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The small texture icons of the fence types in the Fence page's dropdown: a corner of the type's panel texture
/// (assets/drycastle-generated-textures/, the same file the pipeline draws the fence with) tinted with the type's colour,
/// over the light dim tone so the cut-outs of picket and the dark wire mesh read against the dark dropdown. Null when the texture is missing: the item shows text only.
/// </summary>
public static class FenceIcons
{
    const int Size = EditorTheme.Space * 3;
    const int Crop = 128; // of the 256 px tile: about two pickets, a few brick courses
    static readonly Dictionary<string, Texture2D?> Cache = new();

    public static Texture2D? For(string type)
    {
        if (Cache.TryGetValue(type, out var have)) return have;
        var info = EditSchema.TypeInfo(type);
        var path = ProjectSettings.GlobalizePath("res://") + $"../../assets/drycastle-generated-textures/{info.Texture}.png";
        Texture2D? tex = null;
        if (FileAccess.FileExists(path) && Image.LoadFromFile(path) is { } src && !src.IsEmpty())
        {
            src.Convert(Image.Format.Rgba8);
            var c = Mathf.Min(Crop, Mathf.Min(src.GetWidth(), src.GetHeight()));
            var img = src.GetRegion(new Rect2I(0, 0, c, c));
            img.Resize(Size, Size, Image.Interpolation.Lanczos);
            var tint = Color.FromHtml(info.Colour);
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var p = img.GetPixel(x, y);
                    var t = new Color(p.R * tint.R, p.G * tint.G, p.B * tint.B);
                    var edge = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
                    img.SetPixel(x, y, edge ? EditorTheme.Border : EditorTheme.Dim.Lerp(t, p.A));
                }
            tex = ImageTexture.CreateFromImage(img);
        }
        Cache[type] = tex;
        return tex;
    }
}
