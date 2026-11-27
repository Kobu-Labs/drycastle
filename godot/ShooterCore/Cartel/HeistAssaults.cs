using System.Numerics;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The Assaults of one Heist (GLOSSARY "Assault"; docs/cartel.md "Assaults"), pure over their <see cref="Raid"/>s: the ones its Job sent, where their vans stopped (the next parks away from them), and
/// the cap of <see cref="HeistData.MaxAssaultsAlive"/>. An Assault is in the fight while a member of its crew lives or is still to get out (<see cref="InFight"/>): its van does not count, since an
/// Assault van never drives off and a standing van whose crew is dead would hold the cap for the rest of the Heist. One whose crew is gone, the bodies too, is taken out (<see cref="TakeSpent"/>)
/// so its van frees the slot it shares with the Thieves' Raids (<see cref="RaidVanRules.MaxVans"/>).
/// </summary>
public sealed class HeistAssaults
{
    readonly List<Raid> _raids = new();
    readonly List<Vector2> _stops = new();
    readonly int _maxInFight;

    /// <param name="maxInFight">No new Assault while this many are in the fight (<see cref="HeistData.MaxAssaultsAlive"/>).</param>
    public HeistAssaults(int maxInFight) => _maxInFight = maxInFight;

    /// <summary>The Assaults still in the world (sent and not taken out).</summary>
    public IReadOnlyList<Raid> Raids => _raids;
    /// <summary>Where every Assault van of this Heist stopped, taken out ones too (the next parks away from them).</summary>
    public IReadOnlyList<Vector2> Stops => _stops;
    /// <summary>Assaults sent so far.</summary>
    public int Sent { get; private set; }

    /// <summary>The Assault still fights: a member of its crew is alive or still to get out of the van. A standing van or a wreck alone does not.</summary>
    public static bool InFight(Raid r) => r.StillToSpawn > 0 || r.Gang.AliveCount > 0;

    /// <summary>How many of the Assaults are in the fight now.</summary>
    public int InFightCount => _raids.Count(InFight);

    /// <summary>A due Assault may be sent: fewer than the cap are in the fight.</summary>
    public bool MaySend => InFightCount < _maxInFight;

    public void Add(Raid raid)
    {
        _raids.Add(raid);
        _stops.Add(raid.Plan.StopAt);
        Sent++;
    }

    /// <summary>
    /// Takes out the Assaults whose crew is gone: none alive, none to get out, the bodies gone too (corpse_seconds after the last death). A wreck lies its time first, as a Raid's does. The caller
    /// removes them from the world.
    /// </summary>
    public List<Raid> TakeSpent() => Take(r => !InFight(r) && r.Gang.Count == 0 && (r.Van.State != RaidVanState.Destroyed || r.Van.Ended != null));

    /// <summary>Takes out every Assault (the Job ended). The caller removes them from the world.</summary>
    public List<Raid> TakeAll() => Take(_ => true);

    List<Raid> Take(Func<Raid, bool> which)
    {
        var gone = _raids.Where(which).ToList();
        foreach (var r in gone) _raids.Remove(r);
        return gone;
    }
}
