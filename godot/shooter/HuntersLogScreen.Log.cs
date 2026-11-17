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
        // totals and bests
        var stats = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        stats.AddThemeConstantOverride("h_separation", ShooterTheme.Space);
        stats.AddThemeConstantOverride("v_separation", ShooterTheme.Space);
        stats.AddChild(Stat(log.Hunts.ToString(), Strings.T("log.stat.hunts"), null));
        stats.AddChild(Stat($"{log.Species} / {HuntSpecies.Keys.Count}", Strings.T("log.stat.species"), null));
        stats.AddChild(Stat(Kg(log.Heaviest?.Kg), Strings.T("log.stat.heaviest"), log.Heaviest));
        stats.AddChild(Stat(Metres(log.Longest?.DistanceM), Strings.T("log.stat.longest"), log.Longest));
        _logHost.AddChild(stats);

        var own = !_nav.ViewingOther;
        var all = Ui.Btn(own ? Strings.T("log.hunts.own") : Strings.F("log.hunts.other", log.Name), () => OpenPlayerHunts(log.PlayerId));
        all.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        all.Disabled = log.Hunts == 0;
        _logHost.AddChild(all);

        // the Life List: the index species and the mysteries in one grid
        var sub = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        sub.AddChild(Text(Strings.T("log.life.title"), "HudHead"));
        sub.AddChild(Ui.Fill());
        var caught = log.Life.Count(e => e.Caught);
        sub.AddChild(Text(Strings.F("log.life.count", caught, HuntSpecies.Keys.Count), "HudDim"));
        _logHost.AddChild(sub);

        var grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", ShooterTheme.Space);
        grid.AddThemeConstantOverride("v_separation", ShooterTheme.Space);
        foreach (var e in log.Life) grid.AddChild(Tile(LifeTileModel.From(e)));
        _logHost.AddChild(grid);
    }

    /// <summary>One total: the value large, what it is, and (for a best) the species it was.</summary>
    Control Stat(string value, string caption, HuntView? of)
    {
        var p = new PanelContainer { ThemeTypeVariation = "LogStat", CustomMinimumSize = new Vector2(ShooterTheme.LogStatW, 0), MouseFilter = MouseFilterEnum.Ignore };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        v.AddChild(Text(value, "HudBig"));
        v.AddChild(Text(caption, "LogCaption"));
        // the species of a best: a Mystery creature's longest shot the viewer has not caught is a blurred name
        if (of is { Hidden: true }) v.AddChild(new BlurText(Strings.T("hunt.hidden.name"), "HudSmall"));
        else v.AddChild(Text(of == null ? " " : HuntSpeciesName(of), "HudSmall"));
        p.AddChild(v);
        return p;
    }

    /// <summary>
    /// A Life List tile: the animal outlined until hunted, filled once, its name, then "Best N kg" and "Longest N m" (a Mystery creature: when it was caught, and its longest
    /// shot). A hidden Mystery creature is a "?" (the accent when the log's owner caught it, else dim) and a blurred name, and no other text. Pressing it lists that
    /// species' hunts.
    /// </summary>
    Control Tile(LifeTileModel m)
    {
        var width = ShooterTheme.LogTileW;
        var btn = new ContentButton { ThemeTypeVariation = m.Caught ? "LogTile" : "LogTileTodo", MinSize = new Vector2(width, 0) };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        var artSize = new Vector2(width - 4 * ShooterTheme.Space, ShooterTheme.LogTileArtH);
        string line1, line2;
        if (m.Hidden)
        {
            v.AddChild(HiddenFigure(artSize, m.Caught));
            var name = new BlurText(Strings.T("hunt.hidden.name"));
            name.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            v.AddChild(name);
            line1 = line2 = " ";
        }
        else
        {
            var pic = new AnimalPicture(artSize);
            pic.SetSpecies(_env.Art.ForSpecies(m.Species), m.Caught);
            v.AddChild(pic);
            var name = Text(_env.SpeciesName(m.Species), null, HorizontalAlignment.Center);
            name.ClipText = true;
            v.AddChild(name);
            if (!m.Caught) { line1 = Strings.T("log.tile.todo"); line2 = " "; }
            else if (m.Mystery) { line1 = Strings.F("log.tile.caught", AgoText(m.CaughtAtUnix)); line2 = Strings.F("log.tile.longest", Metres(m.LongestM)); }
            else { line1 = Strings.F("log.tile.best", m.BestKg is { } kg ? HuntFormat.Kg(kg) : Strings.T("log.none")); line2 = Strings.F("log.tile.longest", Metres(m.LongestM)); }
        }
        v.AddChild(Text(line1, "HudSmall", HorizontalAlignment.Center));
        v.AddChild(Text(line2, "HudSmall", HorizontalAlignment.Center));
        btn.SetContent(v);
        var species = m.Species;
        btn.Pressed += () => OpenSpeciesHunts(species);
        return btn;
    }
}
