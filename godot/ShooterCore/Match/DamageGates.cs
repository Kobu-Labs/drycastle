namespace Drycastle.ShooterCore.Match;

/// <summary>
/// The reasons a hit on a player does nothing, in the order <see cref="MatchSim.ApplyDamage"/> asks them. The first that applies wins, so the order is
/// the rule: a pinned player stays untouched even by the void; a sandbox player in godmode is untouched even by a fall; only spawn protection and a
/// blessing's invulnerability are lifted by <c>ignoreProtection</c> (the world's own kill: out of the map).
/// </summary>
public enum DamageGate : byte
{
    /// <summary>No round is running (waiting, or the end screen), or the hit carries no damage.</summary>
    Phase,
    /// <summary>The victim is not in the match, or not alive (unspawned, or already dead).</summary>
    Alive,
    /// <summary>Pinned by the demon's pounce: nothing touches them, not even the world.</summary>
    Held,
    /// <summary>Sandbox godmode (#156), unless the player switched damage on.</summary>
    Godmode,
    /// <summary>A teammate's hit with friendly fire off (self damage is not friendly fire).</summary>
    FriendlyFire,
    /// <summary>Spawn protection: from the spawn until the time runs out or the player fires. Skipped by <c>ignoreProtection</c>.</summary>
    SpawnProtection,
    /// <summary>Invulnerable for a moment after a blessing saved the player. Skipped by <c>ignoreProtection</c>.</summary>
    Invulnerable,
}

/// <summary>What <see cref="DamageGates"/> needs to know about one hit. Plain facts, gathered by the match, so the ladder itself is pure.</summary>
public readonly record struct DamageFacts(
    bool RoundRunning, float Amount, bool VictimAlive, bool Held, bool Godmode, bool FriendlyFireBlocked,
    bool SpawnProtected, bool Invulnerable, bool IgnoreProtection);

/// <summary>
/// The ordered list of <see cref="DamageGate"/>s. Pure: <see cref="FirstBlock"/> says which gate (if any) stops a hit; <see cref="Order"/> is the order, for docs and tests.
/// After the last gate a hit lands; a lethal one is then offered to the blessing (<see cref="MatchSim.SaveFromDeath"/>), which is not a gate: it can only save, never ignore.
/// </summary>
public static class DamageGates
{
    /// <summary>The gates in the order they are asked.</summary>
    public static IReadOnlyList<DamageGate> Order { get; } = new[]
    {
        DamageGate.Phase, DamageGate.Alive, DamageGate.Held, DamageGate.Godmode, DamageGate.FriendlyFire, DamageGate.SpawnProtection, DamageGate.Invulnerable,
    };

    /// <summary>The first gate that stops the hit, or null when it lands.</summary>
    public static DamageGate? FirstBlock(in DamageFacts f)
    {
        if (!f.RoundRunning || f.Amount <= 0) return DamageGate.Phase;
        if (!f.VictimAlive) return DamageGate.Alive;
        if (f.Held) return DamageGate.Held;
        if (f.Godmode) return DamageGate.Godmode;
        if (f.FriendlyFireBlocked) return DamageGate.FriendlyFire;
        if (!f.IgnoreProtection && f.SpawnProtected) return DamageGate.SpawnProtection;
        if (!f.IgnoreProtection && f.Invulnerable) return DamageGate.Invulnerable;
        return null;
    }
}
