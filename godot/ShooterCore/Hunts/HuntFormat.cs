using System.Globalization;

namespace Drycastle.ShooterCore.Hunts;

/// <summary>How long ago, in the unit the screen words it: just now, minutes, hours, days, or (past <see cref="HuntFormat.DaysMax"/> days) a date.</summary>
public enum AgoUnit { Now, Minutes, Hours, Days, Date }

/// <summary>Where a shot hit, in words: the head, the legs (low on the body), the rest of the body; None for a hunt with no hit diagram (a run over, a catch).</summary>
public enum HitArea { None, Head, Body, Legs }

public static class HitAreas
{
    /// <summary>Height (0 ground, 1 top) below which a body hit counts as the legs.</summary>
    public const float LegsBelow = 0.3f;

    public static HitArea Of(HitDiagram? d) => d is not { } g ? HitArea.None : g.Head ? HitArea.Head : g.HeightF < LegsBelow ? HitArea.Legs : HitArea.Body;
}

/// <summary>What a Life List tile is drawn as. Hidden: a blurred blob and a "?" and nothing else.</summary>
public enum TileLook { Outline, Filled }

/// <summary>A Life List tile (<see cref="LifeEntry"/>) as the screen draws it. <see cref="Hidden"/>: the blurred blob; <see cref="Look"/>: the animal outlined until hunted, filled once.</summary>
public readonly record struct LifeTileModel(string Species, bool Hidden, TileLook Look, float? BestKg, float? LongestM, long CaughtAtUnix, bool Mystery, HuntTier Tier = HuntTier.Common)
{
    /// <summary>The rim's tier is only for a hunted index species: a mystery tile is framed in its creature's own style, a hidden one says nothing.</summary>
    public static LifeTileModel From(LifeEntry e)
    {
        var mystery = e.Hidden || Mysteries.Is(e.Species);
        return new(e.Species, e.Hidden, e.Caught ? TileLook.Filled : TileLook.Outline, e.Hidden ? null : e.BestKg, e.Hidden ? null : e.LongestM, e.Hidden ? 0 : e.CaughtAtUnix, mystery,
            mystery || !e.Caught ? HuntTier.Common : e.Tier);
    }

    public bool Caught => Look == TileLook.Filled;
}

/// <summary>What a Records card shows: a hidden creature (blurred blob and "?"), a species nobody hunted (the outline), or the two recorded hits.</summary>
public enum RecordKind { Hidden, Unhunted, Hunted }

public readonly record struct RecordCardModel(string Species, RecordKind Kind, TileLook HiddenLook, bool Mystery, HuntView? Left, HuntView? Right)
{
    /// <summary>An index species shows its heaviest and its longest shot; a mystery its first catch and its longest shot (it has no weight).</summary>
    public static RecordCardModel From(SpeciesRecordView r)
    {
        var mystery = r.Hidden || Mysteries.Is(r.Species);
        var look = r.Caught ? TileLook.Filled : TileLook.Outline;
        if (r.Hidden) return new(r.Species, RecordKind.Hidden, look, true, null, null);
        if (!r.Caught) return new(r.Species, RecordKind.Unhunted, look, mystery, null, null);
        return new(r.Species, RecordKind.Hunted, look, mystery, mystery ? r.First : r.Heaviest, r.Longest);
    }
}

/// <summary>Words and numbers of the Hunter's Log, culture-free (the log is read the same everywhere).</summary>
public static class HuntFormat
{
    public const int DaysMax = 60;

    /// <summary>A weight or distance kept to a tenth, without a trailing ".0": 231, 4.5.</summary>
    public static string Number(float v) => (MathF.Round(v, 1)).ToString("0.#", CultureInfo.InvariantCulture);

    public static string Kg(float kg) => Number(kg);

    /// <summary>The key of a hunt's picture in animal_shapes.json: the exact species for its own look, "species/variant" for another look.</summary>
    public static string ShapeKey(string exact, string variant) => variant == "" ? exact : $"{exact}/{variant}";

    /// <summary>The filter key that lists a species' hunts: itself, or for a hidden creature's id the one "Mystery hunts" key (asking for it by id would find nothing).</summary>
    public static string FilterKey(string species) => Mysteries.IsHiddenId(species) ? Mysteries.AllHidden : species;

    public static (AgoUnit Unit, int N) Ago(long nowUnix, long atUnix)
    {
        var s = Math.Max(0, nowUnix - atUnix);
        if (s < 60) return (AgoUnit.Now, 0);
        if (s < 3600) return (AgoUnit.Minutes, (int)(s / 60));
        if (s < 86400) return (AgoUnit.Hours, (int)(s / 3600));
        var d = (int)(s / 86400);
        return d <= DaysMax ? (AgoUnit.Days, d) : (AgoUnit.Date, 0);
    }

    /// <summary>The day of a hunt, "2026-11-16" (UTC), for what is older than <see cref="DaysMax"/> days.</summary>
    public static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>What a server-wide announcement (<see cref="HuntAnnounceMsg"/>) says, before it is put in words: a record (of the weight, of the shot or of a player kill) or a first.</summary>
public enum HuntLineKind { First, RecordHeaviest, RecordLongest, RecordPlayerKill }

public readonly record struct HuntLine(HuntLineKind Kind, bool Mine, bool Hidden, bool Mystery, HuntView Hunt)
{
    public bool IsRecord => Kind != HuntLineKind.First;

    /// <summary>
    /// A record beats a first (a first that is also a record is one line); of the two records the weight, which a mystery has not (so its longest shot). Mine:
    /// the hunter is the viewer (a guest, id 0, is never anyone's).
    /// </summary>
    public static HuntLine Of(HuntAnnounceMsg m, long viewerId)
    {
        var f = m.Flags;
        var kind = (f & HuntAnnounceFlags.PlayerKill) != 0 && m.Hunt.DistanceM != null ? HuntLineKind.RecordPlayerKill
            : (f & HuntAnnounceFlags.Heaviest) != 0 && m.Hunt.Kg != null ? HuntLineKind.RecordHeaviest
            : (f & HuntAnnounceFlags.Longest) != 0 && m.Hunt.DistanceM != null ? HuntLineKind.RecordLongest
            : HuntLineKind.First;
        return new HuntLine(kind, viewerId != 0 && m.Hunt.PlayerId == viewerId, m.Hunt.Hidden, m.Hunt.Hidden || Mysteries.Is(m.Hunt.Species), m.Hunt);
    }
}

/// <summary>Whether a hunt of the table holds a record of its species: the heaviest, or the longest shot.</summary>
public enum RecordMark { None, Weight, Shot }

public static class HuntRecords
{
    /// <summary>The record <paramref name="h"/> holds in <paramref name="cards"/> (the Records answer); none when the cards are not loaded. A hunt is told by its hunter, its time and its numbers.</summary>
    public static RecordMark MarkOf(IReadOnlyList<SpeciesRecordView>? cards, HuntView h)
    {
        if (cards == null) return RecordMark.None;
        foreach (var c in cards)
        {
            if (c.Species != h.Species) continue;
            if (Same(c.Heaviest, h)) return RecordMark.Weight;
            if (Same(c.Longest, h)) return RecordMark.Shot;
        }
        return RecordMark.None;
    }

    static bool Same(HuntView? a, HuntView b) => a != null && a.PlayerId == b.PlayerId && a.AtUnix == b.AtUnix && a.Kg == b.Kg && a.DistanceM == b.DistanceM && a.Weapon == b.Weapon;
}
