using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The first tab: a player's totals and their Life List (docs/hunters-log.md "My Log").</summary>
public sealed partial class HuntersLogScreen
{
    VBoxContainer _logHost = null!;

    void BuildLogPane() => _logHost = NewHost(_panes[0]);

    void RebuildLog()
    {
        Clear(_logHost);
        switch (Data.Log.Show)
        {
            case LoadView.Spinner:
                _logHost.AddChild(StateBox(Strings.T("log.loading"), Strings.T("log.loading.body"), spinner: true));
                break;
            case LoadView.Error:
                _logHost.AddChild(StateBox(Strings.T("log.error"), Strings.T("log.error.body"), actionText: Strings.T("log.retry"), action: () => Data.RequestLog(_nav.Subject)));
                break;
            case LoadView.Missing:
                // asking for the own log of a guest finds nobody; asking for another player's, no such player
                _logHost.AddChild(_nav.ViewingOther
                    ? StateBox(Strings.T("log.missing"), Strings.T("log.missing.body"), actionText: Strings.T("log.back"), action: BackPressed)
                    : StateBox(Strings.T("log.guest"), Strings.T("log.guest.body")));
                break;
            default:
                BuildLogContent(Data.Log.Data!);
                break;
        }
    }

    void BuildLogContent(PlayerLogView log)
    {
        // totals and bests: five metal plates, centred, wrapping rather than cut when the panel is narrow
        var stats = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        stats.AddThemeConstantOverride("h_separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        stats.AddThemeConstantOverride("v_separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        stats.AddChild(Stat(log.Hunts.ToString(), Strings.T("log.stat.hunts"), null));
        stats.AddChild(Stat($"{log.Species} / {HuntSpecies.Keys.Count}", Strings.T("log.stat.species"), null));
        stats.AddChild(Stat(Kg(log.Heaviest?.Kg), Strings.T("log.stat.heaviest"), log.Heaviest));
        stats.AddChild(Stat(Metres(log.Longest?.DistanceM), Strings.T("log.stat.longest"), log.Longest));
        _logHost.AddChild(stats);

        // what the rims mean, centred under the plates
        var legend = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        legend.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        legend.AddChild(Legend(HuntTier.Gold, "log.legend.gold"));
        legend.AddChild(Legend(HuntTier.Silver, "log.legend.silver"));
        legend.AddChild(Legend(HuntTier.Bronze, "log.legend.bronze"));
        _logHost.AddChild(legend);

        // the Life List: the game on plaques, then the Mystery creatures
        var tiles = log.Life.Select(LifeTileModel.From).ToList();
        AddSection("log.sec.game", tiles.Where(t => !t.Mystery).ToList());
        AddSection("log.sec.mystery", tiles.Where(t => t.Mystery).ToList());
    }

    void AddSection(string titleKey, IReadOnlyList<LifeTileModel> tiles)
    {
        if (tiles.Count == 0) return;
        _logHost.AddChild(_frame.Section(Strings.T(titleKey), Strings.F("log.life.count", tiles.Count(t => t.Caught), tiles.Count)));
        var grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", ShooterTheme.Space * 2);
        grid.AddThemeConstantOverride("v_separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        foreach (var m in tiles) grid.AddChild(Tile(m));
        _logHost.AddChild(grid);
    }

    /// <summary>What a rim's metal means: the medal and "Gold, top 5%".</summary>
    static Control Legend(HuntTier tier, string key)
    {
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        h.AddChild(new MedalGlyph(LogSkin.TierColor(tier)!.Value, Skin.Edge));
        h.AddChild(Text(Strings.T(key), "HudSmall"));
        return h;
    }

    /// <summary>One total on a metal plate: the value large, what it is, and (for a best) the species it was.</summary>
    Control Stat(string value, string caption, HuntView? of)
    {
        var p = new LogPlate(Skin) { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(ShooterTheme.LogStatW, 0) };
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        h.AddChild(Text(value, "InkBig"));
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        v.AddThemeConstantOverride("separation", 0);
        var cap = Text(caption, "LogCaption");
        cap.Uppercase = true;
        v.AddChild(cap);
        // the species of a best: a Mystery creature's longest shot the viewer has not caught is a blurred name
        if (of is { Hidden: true }) v.AddChild(new BlurText(Strings.T("hunt.hidden.name"), "InkSmall"));
        else v.AddChild(Text(of == null ? " " : HuntSpeciesName(of), "InkSmall"));
        h.AddChild(v);
        p.AddChild(h);
        return p;
    }

    static string TierName(HuntTier tier) => Strings.T(tier switch { HuntTier.Gold => "log.tier.gold", HuntTier.Silver => "log.tier.silver", _ => "log.tier.bronze" });

    /// <summary>
    /// A Life List tile: a plaque (<see cref="PlaqueArt"/>) with the animal on it, chalked in outline until hunted, filled once, its name on a plate, then its best weight
    /// (with the rarity in words when the rim has a metal: "187 kg, Gold") and "Longest N m" (a Mystery creature: when it was caught, and its longest shot). A hidden
    /// Mystery creature is a covered plaque with a "?" (full when the log's owner caught it, else dim) and a blurred name, and no other text. Pressing it lists that
    /// species' hunts.
    /// </summary>
    Control Tile(LifeTileModel m)
    {
        var width = ShooterTheme.LogTileW;
        var kind = m.Hidden ? PlaqueKind.Covered : !m.Caught ? PlaqueKind.Todo : m.Mystery ? PlaqueKind.Mystery : PlaqueKind.Hunted;
        var btn = new ContentButton { ThemeTypeVariation = m.Caught ? "LogTile" : "LogTileTodo", MinSize = new Vector2(width, 0) };
        btn.AddChild(new PlaqueArt(Skin, kind, m.Tier, m.Species)); // behind the content, drawing the button's states
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        var artSize = new Vector2(width - 4 * ShooterTheme.Space, ShooterTheme.LogTileArtH);
        var dim = m.Caught ? "TileText" : "TileDim";
        string line1, line2;
        if (m.Hidden)
        {
            v.AddChild(HiddenFigure(artSize, m.Caught, onTile: true));
            var name = new BlurText(Strings.T("hunt.hidden.name"), dim);
            name.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            v.AddChild(name);
            line1 = line2 = " ";
        }
        else
        {
            var pic = new AnimalPicture(artSize) { FillInk = Skin.OnTile, OutlineInk = Skin.Chalk };
            pic.SetSpecies(_env.Art.ForSpecies(m.Species), m.Caught);
            v.AddChild(pic);
            var plate = new PanelContainer { ThemeTypeVariation = m.Caught ? "LogName" : "LogNameTodo", SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
            var name = Text(_env.SpeciesName(m.Species), "TileName", HorizontalAlignment.Center);
            plate.AddChild(name);
            v.AddChild(plate);
            if (!m.Caught) { line1 = Strings.T("log.tile.todo"); line2 = " "; }
            else if (m.Mystery) { line1 = Strings.F("log.tile.caught", AgoText(m.CaughtAtUnix)); line2 = Strings.F("log.tile.longest", Metres(m.LongestM)); }
            else
            {
                var kg = m.BestKg is { } best ? HuntFormat.Kg(best) : Strings.T("log.none");
                line1 = m.Tier == HuntTier.Common ? Strings.F("log.tile.best", kg) : Strings.F("log.tile.tier", kg, TierName(m.Tier));
                line2 = Strings.F("log.tile.longest", Metres(m.LongestM));
            }
        }
        v.AddChild(Text(line1, dim, HorizontalAlignment.Center));
        v.AddChild(Text(line2, dim, HorizontalAlignment.Center));
        btn.SetContent(v);
        var species = m.Species;
        btn.Pressed += () => OpenSpeciesHunts(species);
        return btn;
    }
}
