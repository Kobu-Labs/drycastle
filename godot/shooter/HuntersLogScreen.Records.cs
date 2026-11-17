using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The Records tab: a card per species with the heaviest and the longest shot as the recorded hit pictures (docs/hunters-log.md "Records").</summary>
public sealed partial class HuntersLogScreen
{
    VBoxContainer _recHost = null!;

    void BuildRecordsPane() => _recHost = NewHost(_panes[1]);

    void RebuildRecords()
    {
        Clear(_recHost);
        switch (Data.Records.Show)
        {
            case LoadView.Spinner:
                _recHost.AddChild(StateBox(Strings.T("log.loading"), Strings.T("log.loading.body"), spinner: true));
                return;
            case LoadView.Error or LoadView.Missing:
                _recHost.AddChild(StateBox(Strings.T("log.error"), Strings.T("log.error.body"), actionText: Strings.T("log.retry"), action: () => Data.RequestRecords()));
                return;
        }
        var grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", ShooterTheme.Space);
        grid.AddThemeConstantOverride("v_separation", ShooterTheme.Space);
        foreach (var r in Data.Records.Data!) grid.AddChild(RecordCard(RecordCardModel.From(r)));
        _recHost.AddChild(grid);
    }

    Control RecordCard(RecordCardModel m)
    {
        var card = new PanelContainer { ThemeTypeVariation = "LogCard", CustomMinimumSize = new Vector2(ShooterTheme.LogCardW, 0), MouseFilter = MouseFilterEnum.Ignore };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space);
        card.AddChild(v);
        var figureW = (ShooterTheme.LogCardW - 4 * ShooterTheme.Space) / 2;
        switch (m.Kind)
        {
            case RecordKind.Hidden:
                // a creature the viewer has not caught: a "?", nothing else
                v.AddChild(HiddenFigure(new Vector2(ShooterTheme.LogCardW - 4 * ShooterTheme.Space, ShooterTheme.LogFigureH * 2), m.HiddenLook == TileLook.Filled));
                break;
            case RecordKind.Unhunted:
            {
                v.AddChild(Text(_env.SpeciesName(m.Species), "HudHead"));
                var pic = new AnimalPicture(new Vector2(ShooterTheme.LogCardW - 4 * ShooterTheme.Space, ShooterTheme.LogFigureH * 2));
                pic.SetSpecies(_env.Art.ForSpecies(m.Species), filled: false);
                v.AddChild(pic);
                v.AddChild(Text(Strings.T("log.rec.todo"), "HudDim", wrap: true));
                break;
            }
            default:
            {
                v.AddChild(Text(_env.SpeciesName(m.Species), "HudHead"));
                var shots = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                shots.AddThemeConstantOverride("separation", ShooterTheme.Space);
                shots.AddChild(m.Mystery ? Shot(Strings.T("log.rec.first"), m.Left, h => AgoText(h.AtUnix), figureW) : Shot(Strings.T("log.rec.heaviest"), m.Left, h => Kg(h.Kg), figureW));
                shots.AddChild(Shot(Strings.T("log.rec.longest"), m.Right, h => Metres(h.DistanceM), figureW));
                v.AddChild(shots);
                var species = m.Species;
                v.AddChild(Ui.Btn(Strings.F("log.rec.open", _env.SpeciesName(m.Species)), () => OpenSpeciesHunts(species)));
                break;
            }
        }
        return card;
    }

    /// <summary>
    /// One recorded hit: its picture (the animal facing left, the bullet's entry marked), what the record is, the number, and who, with what, and HEAD when it was one.
    /// <paramref name="number"/> words the record's number (a weight, a distance, how long ago a Mystery creature was first caught); nothing recorded yet is a dash under an empty frame.
    /// </summary>
    Control Shot(string label, HuntView? h, Func<HuntView, string> number, float figureW)
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        v.AddChild(HitFigure(h, new Vector2(figureW, ShooterTheme.LogFigureH)));
        v.AddChild(Text(label, "LogCaption"));
        if (h == null)
        {
            v.AddChild(Text(Strings.T("log.none"), "HudHead"));
            return v;
        }
        v.AddChild(Text(number(h), "HudHead"));
        v.AddChild(Text(HitLine(h), "HudSmall", wrap: true));
        return v;
    }

    /// <summary>"Vesna  |  Sniper  |  HEAD": the hunter, the weapon and where it hit.</summary>
    string HitLine(HuntView h)
    {
        var parts = new List<string> { h.Hunter, _env.WeaponName(h.Weapon) };
        if (h.Diagram is { Head: true }) parts.Add(Strings.T("log.head"));
        return string.Join("  |  ", parts);
    }

    /// <summary>A hunt's recorded picture: a "?" when hidden, else the animal with the entry marked (none for a run over or a catch: no diagram).</summary>
    Control HitFigure(HuntView? h, Vector2 size)
    {
        if (h is { Hidden: true }) return HiddenFigure(size, caught: true);
        var pic = new AnimalPicture(size);
        if (h == null) { pic.SetSpecies(AnimalArt.Box, filled: false); return pic; }
        var spec = HuntPicture.Resolve(_env.Art.Shapes, h);
        pic.SetHit(_env.Art.Art(spec.Standing, spec.SpeciesId), spec.HasMark, spec.Along, spec.Height);
        return pic;
    }
}
