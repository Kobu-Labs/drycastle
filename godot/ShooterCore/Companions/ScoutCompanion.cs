using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>The numbers of the "scout" kind: those of the wing kind it flies as, and its marking. Lengths in metres, times in seconds.</summary>
public sealed class ScoutRules : WingRules
{
    /// <summary>When it begins its circles it marks the enemies of its owner within this far of its owner (flat).</summary>
    public float MarkRadiusM { get; set; } = 40f;
    /// <summary>At most this many, the nearest first.</summary>
    public int MarkMax { get; set; } = 2;
    /// <summary>A mark lasts this long.</summary>
    public float MarkSeconds { get; set; } = 6f;

    public override void Check(string what)
    {
        base.Check(what);
        if (!(MarkRadiusM > 0) || !(MarkSeconds > 0)) throw new DataFileException($"{what}: mark_radius_m and mark_seconds must be positive");
        if (MarkMax < 1 || MarkMax > ScoutCompanion.MaxMarks) throw new DataFileException($"{what}: mark_max is 1 to {ScoutCompanion.MaxMarks}");
    }
}

/// <summary>
/// The "scout" kind: a flying Companion that also marks enemies for its owner (the Eagle, #488, #473). It flies as the wing kind does (<see cref="WingCompanion"/>: perch,
/// trail, circles). Each time it begins its circles (the wing kind's circle clock; set <c>circle_height_m</c> to have it climb) it looks down: the nearest
/// <c>mark_max</c> enemies of its owner within <c>mark_radius_m</c> of the owner are marked for <c>mark_seconds</c>. Only its owner is told (<see cref="Companion.Marks"/>);
/// their client draws the marked players through walls. A circling eagle is the tell for everyone else. It sees through roofs and walls: there is no line of sight.
/// Marking is beside the flight machine: one clock (<see cref="_marked"/>), no state. All of its rules are here.
/// </summary>
public sealed class ScoutCompanion : WingCompanion
{
    /// <summary>The most players one scout marks at a time (the mark event's count is a byte, and more is no longer a mark but a radar).</summary>
    public const int MaxMarks = 8;

    public new static readonly CompanionKind Kind = new("scout", s => new ScoutCompanion(s, RulesOf(s.Def)), d => RulesOf(d));

    readonly ScoutRules _rules;
    readonly List<CompanionSighting> _seen = new();
    ushort[] _marks = Array.Empty<ushort>();
    Countdown _marked; // how long the marks still hold

    static ScoutRules RulesOf(CompanionDef def)
    {
        var r = DataFile.Section<ScoutRules>(def.Rules, $"companions.json: {def.Id}: rules");
        r.Check($"companions.json: {def.Id}: rules");
        return r;
    }

    ScoutCompanion(CompanionSpawn s, ScoutRules rules) : base(s, rules) => _rules = rules;

    public override IReadOnlyList<ushort> Marks => _marks;

    /// <summary>It begins its circles: the nearest enemies round its owner are marked. None near: the marks it had run out as they were.</summary>
    protected override void OnCircleStart()
    {
        EnemiesNear(Master.Position, _rules.MarkRadiusM, _seen);
        if (_seen.Count == 0) return;
        var from = Master.Position;
        _seen.Sort((a, b) =>
        {
            var d = Flat(a.Position, from).CompareTo(Flat(b.Position, from));
            return d != 0 ? d : a.Player.CompareTo(b.Player); // the same order every run
        });
        var n = Math.Min(_seen.Count, _rules.MarkMax);
        var marks = new ushort[n];
        for (var i = 0; i < n; i++) marks[i] = _seen[i].Player;
        _marks = marks;
        _marked.Start(_rules.MarkSeconds);
    }

    protected override void Beside(float dt)
    {
        if (_marks.Length > 0 && _marked.Tick(dt)) _marks = Array.Empty<ushort>();
    }

    /// <summary>Server to the owner, <see cref="EventKind.CompanionMarks"/>: u8 count, then a u16 player id each. A count of 0 clears the marks.</summary>
    public static byte[] EncodeMarks(IReadOnlyList<ushort> marks)
    {
        var n = Math.Min(marks.Count, byte.MaxValue);
        var b = new byte[1 + n * 2];
        b[0] = (byte)n;
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1 + i * 2), marks[i]);
        return b;
    }

    public static bool TryDecodeMarks(ReadOnlySpan<byte> p, out ushort[] marks)
    {
        marks = Array.Empty<ushort>();
        if (p.Length < 1 || p.Length != 1 + p[0] * 2) return false;
        marks = new ushort[p[0]];
        for (var i = 0; i < marks.Length; i++) marks[i] = BinaryPrimitives.ReadUInt16LittleEndian(p[(1 + i * 2)..]);
        return true;
    }
}
