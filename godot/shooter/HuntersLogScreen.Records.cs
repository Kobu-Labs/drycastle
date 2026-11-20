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
        var grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", ShooterTheme.Space * 2);
        grid.AddThemeConstantOverride("v_separation", ShooterTheme.Space * 2 + LogSkin.Tack);
        var answer = Data.Records.Data!;
        foreach (var r in answer.Records) grid.AddChild(RecordCard(RecordCardModel.From(r)));
        grid.AddChild(PlayerKillCard(answer.Kill));
        _recHost.AddChild(grid);
    }

    Control RecordCard(RecordCardModel m)
    {
        // a paper pinned to the board; a creature the viewer has not caught is a paper under a cloth
        var card = new LogPaper(Skin, m.Kind == RecordKind.Hidden ? "LogCover" : "LogCard") { CustomMinimumSize = new Vector2(ShooterTheme.LogCardW, 0) };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space);
        card.AddChild(v);
        var figureW = (ShooterTheme.LogCardW - 4 * ShooterTheme.Space) / 2;
        switch (m.Kind)
        {
            case RecordKind.Hidden:
                // a creature the viewer has not caught: a "?", nothing else
                v.AddChild(HiddenFigure(new Vector2(ShooterTheme.LogCardW - 4 * ShooterTheme.Space, ShooterTheme.LogFigureH * 2), m.HiddenLook == TileLook.Filled, onTile: true));
                break;
            case RecordKind.Unhunted:
            {
                v.AddChild(Text(_env.SpeciesName(m.Species), "InkHead"));
                var pic = PaperPicture(new Vector2(ShooterTheme.LogCardW - 5 * ShooterTheme.Space, ShooterTheme.LogFigureH * 2));
                pic.SetSpecies(_env.Art.ForSpecies(m.Species), filled: false);
                v.AddChild(Figure(pic));
                v.AddChild(Text(Strings.T("log.rec.todo"), "InkDimSmall", wrap: true));
                break;
            }
            default:
            {
                v.AddChild(Text(_env.SpeciesName(m.Species), "InkHead"));
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
    /// The server's longest player kill (GLOSSARY "Player kill"): no animal, so the distance and three icon lines, the hunter and the victim (links to their logs) and the weapon.
    /// Nobody has made one yet: the same lines with dashes, never a missing card.
    /// </summary>
    Control PlayerKillCard(PlayerKillView? k)
    {
        var card = new LogPaper(Skin, "LogCard") { CustomMinimumSize = new Vector2(ShooterTheme.LogCardW, 0) };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space);
        card.AddChild(v);
        v.AddChild(Text(Strings.T("log.rec.kill"), "InkHead", HorizontalAlignment.Center));
        v.AddChild(Text(Metres(k?.DistanceM), "InkHead", HorizontalAlignment.Center));
        var lines = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 2);
        lines.AddChild(k == null ? StatLine(HuntArt.Person, Strings.T("log.none"), "InkSmall", tint: true) : PersonLink(k.Killer, () => OpenPlayerLog(k.KillerId, k.Killer), "InkSmall"));
        lines.AddChild(k == null ? StatLine(HuntArt.Person, Strings.T("log.none"), "InkSmall", tint: true) : PersonLink(k.Victim, () => OpenPlayerLog(k.VictimId, k.Victim), "InkSmall"));
        lines.AddChild(StatLine(HuntArt.Rifle, k == null ? Strings.T("log.none") : _env.WeaponName(k.Weapon), "InkSmall", tint: true));
        v.AddChild(lines);
        if (k == null) v.AddChild(Text(Strings.T("log.rec.kill.todo"), "InkDimSmall", wrap: true));
        return card;
    }

    /// <summary>
    /// One recorded hit: its picture (the animal facing left, the bullet's entry marked), what the record is, the number, and who, with what, and where it hit (always the three lines).
    /// <paramref name="number"/> words the record's number (a weight, a distance, how long ago a Mystery creature was first caught); nothing recorded yet is a dash under an empty frame.
    /// </summary>
    Control Shot(string label, HuntView? h, Func<HuntView, string> number, float figureW)
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        v.AddChild(Figure(HitFigure(h, new Vector2(figureW - 2 * ShooterTheme.Space, ShooterTheme.LogFigureH))));
        var cap = Text(label, "LogCaption");
        cap.Uppercase = true;
        v.AddChild(cap);
        if (h == null)
        {
            v.AddChild(Text(Strings.T("log.none"), "InkHead"));
            return v;
        }
        v.AddChild(Text(number(h), "InkHead"));
        v.AddChild(HitFacts(h));
        return v;
    }

    /// <summary>The facts of a hit, one per line and always all of them (<paramref name="full"/> adds the distance and the time: the hit card), each with its icon so the lines align across cards: the hunter, the weapon, the hit area.</summary>
    Control HitFacts(HuntView h, string variation = "InkSmall", Action? openHunter = null, bool full = false)
    {
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 2);
        if (full) v.AddChild(StatLine(HuntArt.Tape, Metres(h.DistanceM), variation, tint: true));
        v.AddChild(openHunter == null ? StatLine(HuntArt.Person, h.Hunter, variation, tint: true) : PersonLink(h.Hunter, openHunter, variation));
        v.AddChild(StatLine(HuntArt.Rifle, _env.WeaponName(h.Weapon), variation, tint: true));
        v.AddChild(StatLine(HuntArt.Crosshair, AreaName(h), variation, tint: true));
        if (full) v.AddChild(StatLine(HuntArt.Calendar, AgoText(h.AtUnix), variation, tint: true));
        return v;
    }

    /// <summary>A person's line as a link: a button around the same line, the name underlined, a tooltip saying where it goes (their log).</summary>
    static Control PersonLink(string name, Action open, string variation)
    {
        var b = new ContentButton { ThemeTypeVariation = "LogRow", MinSize = Vector2.Zero, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, TooltipText = Strings.F("log.hit.open", name), MouseDefaultCursorShape = CursorShape.PointingHand };
        var line = StatLine(HuntArt.Person, "", variation, tint: true);
        var link = new PlayerLink { Text = name, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        line.GetChild(1).QueueFree();
        line.AddChild(link);
        b.SetContent(line);
        b.Pressed += open;
        b.MouseEntered += () => link.Hot = true;
        b.MouseExited += () => link.Hot = false;
        return b;
    }

    string AreaName(HuntView h) => HitAreas.Of(h.Diagram) switch
    {
        HitArea.Head => Strings.T("log.area.head"), HitArea.Legs => Strings.T("log.area.legs"), HitArea.Body => Strings.T("log.area.body"), _ => Strings.T("log.area.none"),
    };

    /// <summary>An icon cell of a fixed size and a clipped text. Every icon is a flat tinted glyph of one style.</summary>
    static Control StatLine(Texture2D? icon, string text, string variation, bool tint)
    {
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        var pic = new TextureRect
        {
            Texture = icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(ShooterTheme.LogStatIcon, ShooterTheme.LogStatIcon), SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        if (tint) pic.Modulate = Skin.InkDim;
        h.AddChild(pic);
        var label = Text(text, variation);
        label.ClipText = true; label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(label);
        return h;
    }

    /// <summary>A hunt's recorded picture: a "?" when hidden, else the animal with the entry marked (none for a run over or a catch: no diagram).</summary>
    Control HitFigure(HuntView? h, Vector2 size)
    {
        if (h is { Hidden: true }) return HiddenFigure(size, caught: true);
        var pic = PaperPicture(size);
        if (h == null) { pic.SetSpecies(AnimalArt.Box, filled: false); return pic; }
        var spec = HuntPicture.Resolve(_env.Art.Shapes, h);
        pic.SetHit(_env.Art.Art(spec.Standing, spec.SpeciesId), spec.HasMark, spec.Along, spec.Height);
        return pic;
    }
}
