using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Where a MOAB is in its life, as seen by the server and (through <see cref="MoabEvents"/>) by every client.</summary>
public enum MoabPhase : byte
{
    /// <summary>Called in: the countdown has started.</summary>
    Armed = 0,
    /// <summary>The countdown ran out: everyone but the owner dies and the vehicles blow up.</summary>
    Detonated = 1,
    /// <summary>The countdown was stopped (the owner died or left, or the round ended) before detonation.</summary>
    Cancelled = 2,
}

/// <summary>
/// The MOAB killstreak (ticket #129). Activation starts a countdown (the streak's <c>duration_seconds</c> in
/// killstreaks.json, 5 s); when it runs out the owner's streak detonates. Only one MOAB is pending per match: a second
/// activation is refused and its streak stays held. Its kills use the <see cref="OwnedWeapon"/> id, so they pay the
/// streak award and do not advance the Streak counter.
///
/// The rules that need the world (who dies, which vehicles go) live in the server and run from <c>notify</c>
/// on <see cref="MoabPhase.Detonated"/>. This class only decides when that happens.
/// </summary>
public sealed class MoabStreak : IKillstreak
{
    /// <summary>The killstreaks.json id.</summary>
    public const string StreakId = "moab";
    /// <summary>The stats/streak key of the MOAB's kills (the server maps its special weapon byte to it).</summary>
    public const string OwnedWeapon = "moab";

    readonly Action<MoabPhase, KillstreakUse> _notify;
    KillstreakUse? _pending;

    /// <param name="notify">Called for each phase. Detonated is where the server applies the blast.</param>
    public MoabStreak(Action<MoabPhase, KillstreakUse> notify) => _notify = notify;

    public string Id => StreakId;
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string> { OwnedWeapon };

    /// <summary>True while a MOAB is counting down in this match.</summary>
    public bool Pending => _pending is { IsOpen: true };

    public bool Activate(KillstreakUse use)
    {
        if (Pending) { use.Log("refused: a MOAB is already pending"); return false; }
        _pending = use;
        use.OnEnded(reason => OnEnded(use, reason));
        _notify(MoabPhase.Armed, use);
        return true;
    }

    void OnEnded(KillstreakUse use, KillstreakEndReason reason)
    {
        if (ReferenceEquals(_pending, use)) _pending = null;
        if (reason == KillstreakEndReason.Expired) _notify(MoabPhase.Detonated, use);
        else _notify(MoabPhase.Cancelled, use);
    }

    /// <summary>
    /// Who a detonation kills: every live player but the owner. Spawn protection does not save them; the server passes
    /// that through when it applies the damage.
    /// </summary>
    public static IEnumerable<ushort> Victims(IEnumerable<ushort> alive, ushort owner) => alive.Where(id => id != owner);
}

/// <summary>Wire form of the reliable <see cref="EventKind.Moab"/> event: phase, owner, blast centre, seed, time.</summary>
public sealed record MoabInfo(MoabPhase Phase, ushort Owner, Vector3 At, uint Seed, float Seconds);

public static class MoabEvents
{
    /// <summary>Layout: u8 phase, u16 owner, 3 x f32 position, u32 seed, f32 seconds. Armed: seconds = countdown length. Detonated: seconds = match clock. Seed is for deterministic FX.</summary>
    public const int Size = 1 + 2 + 12 + 4 + 4;

    public static byte[] Encode(MoabInfo m)
    {
        var b = new byte[Size];
        b[0] = (byte)m.Phase;
        BitConverter.TryWriteBytes(b.AsSpan(1, 2), m.Owner);
        BitConverter.TryWriteBytes(b.AsSpan(3, 4), m.At.X);
        BitConverter.TryWriteBytes(b.AsSpan(7, 4), m.At.Y);
        BitConverter.TryWriteBytes(b.AsSpan(11, 4), m.At.Z);
        BitConverter.TryWriteBytes(b.AsSpan(15, 4), m.Seed);
        BitConverter.TryWriteBytes(b.AsSpan(19, 4), m.Seconds);
        return b;
    }

    /// <summary>Null on a short or malformed payload.</summary>
    public static MoabInfo? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < Size || d[0] > (byte)MoabPhase.Cancelled) return null;
        var at = new Vector3(BitConverter.ToSingle(d.Slice(3, 4)), BitConverter.ToSingle(d.Slice(7, 4)), BitConverter.ToSingle(d.Slice(11, 4)));
        return new MoabInfo((MoabPhase)d[0], BitConverter.ToUInt16(d.Slice(1, 2)), at, BitConverter.ToUInt32(d.Slice(15, 4)), BitConverter.ToSingle(d.Slice(19, 4)));
    }
}
