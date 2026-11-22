using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// <c>--companion-shots DIR</c>: renders the cells of the Equipment screen's Companion picker (#488) at 1280x720 and 3840x2160 and saves one PNG per size: every Companion
/// of companions.json as its live 3D view (<see cref="CompanionPortrait"/>), the first one chosen, then each as a player who has not earned it sees it (locked), then the empty cell; and each
/// Companion's own picture as <c>icon_&lt;id&gt;.png</c> (256x256, transparent), the source of a reward Companion's icon (<see cref="IconRegistry.CompanionPath"/>:
/// cropped to the animal and centred in a square by hand). The cells only, on the
/// screen's card: not the whole Equipment screen (that needs a match). Needs a display server with a renderer (not --headless).
/// </summary>
public static class CompanionShots
{
    static readonly Vector2I[] Sizes = { new(1280, 720), new(3840, 2160) };

    public static async Task Run(Node host, GameData data, string dir)
    {
        if (DisplayServer.GetName() == "headless") { GD.PrintErr("companion-shots needs a display (not --headless)"); host.GetTree().Quit(2); return; }
        Directory.CreateDirectory(dir);
        var failed = false;
        foreach (var size in Sizes)
        {
            var vp = new SubViewport { Size = size, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            host.AddChild(vp);
            HudShots.Backdrop(vp);
            var scale = size.Y / 1080f;
            var root = new Control { Theme = ShooterTheme.Build(), Scale = new Vector2(scale, scale), Size = new Vector2(size.X, size.Y) / scale };
            vp.AddChild(root);
            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(center);
            var views = new List<(string Id, CompanionPortrait View)>();
            var card = new PanelContainer { ThemeTypeVariation = "MenuCard" };
            center.AddChild(card);
            var grid = new GridContainer { Columns = Math.Max(1, data.Companions.Companions.Count * 2 + 1) };
            card.AddChild(grid);
            for (var i = 0; i < data.Companions.Companions.Count; i++)
            {
                var entry = data.Companions.Companions[i];
                var cell = new ItemCell { Ref = CellRef.Companion(i), Ghost = Glyph.Kind.Paw };
                grid.AddChild(cell);
                var view = data.Animals.Find(entry.Species) is { } animal ? CompanionPortrait.Of(animal, Math.Max(0, entry.VariantIn(animal))) : null;
                if (view != null) { view.Live = true; cell.AddChild(view); views.Add((entry.Id, view)); }
                else GD.PrintErr($"companion-shot: {entry.Id} has no model here, its cell shows the ghost");
                cell.SetPicture(view?.Picture, entry.Name, chosen: i == 0);
            }
            // the same Companions as a player who has not earned them sees them: locked
            for (var i = 0; i < data.Companions.Companions.Count; i++)
            {
                var entry = data.Companions.Companions[i];
                var cell = new ItemCell { Ref = CellRef.Companion(data.Companions.Companions.Count + i), Ghost = Glyph.Kind.Paw };
                grid.AddChild(cell);
                var view = data.Animals.Find(entry.Species) is { } animal ? CompanionPortrait.Of(animal, Math.Max(0, entry.VariantIn(animal))) : null;
                if (view != null) { view.Live = true; cell.AddChild(view); }
                cell.SetPicture(view?.Picture, entry.Name, false, locked: true);
            }
            var empty = new ItemCell { Ref = CellRef.Companion(data.Companions.Companions.Count * 2), Ghost = Glyph.Kind.Paw };
            grid.AddChild(empty);
            empty.SetPicture(null, "", false);
            await HudShots.Frames(host, 12);
            var path = Path.Combine(dir, $"companion_picker_{size.X}x{size.Y}.png");
            var err = vp.GetTexture().GetImage().SavePng(path);
            GD.Print($"companion-shot {size.X}x{size.Y}: {(err == Error.Ok ? path : "FAILED " + err)}");
            failed |= err != Error.Ok;
            if (size == Sizes[^1])
                foreach (var (id, view) in views)
                {
                    // each Companion's own picture, transparent: copy a reward's to ui/icons/companions/<id>.png (IconRegistry.CompanionPath)
                    var icon = Path.Combine(dir, $"icon_{id}.png");
                    var iconErr = view.Picture.GetImage().SavePng(icon);
                    GD.Print($"companion-icon {id}: {(iconErr == Error.Ok ? icon : "FAILED " + iconErr)}");
                    failed |= iconErr != Error.Ok;
                }
            vp.QueueFree();
        }
        GD.Print(failed ? "COMPANION_SHOTS_FAIL" : "COMPANION_SHOTS_OK");
        host.GetTree().Quit(failed ? 2 : 0);
    }
}
