using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// What the smoke tests (net, match, weapons, rpg: the <c>run_*_smoke.py</c> scripts) count on the server, and the verdict each one prints when it is over. A production server fills the counters and never asks for a verdict.
/// The vehicle smokes keep their own counters with the vehicles (<c>NetServer.Vehicles.cs</c>) and share <see cref="FinishAt"/> and <see cref="ShotsFired"/>.
/// </summary>
internal sealed class SmokeStats
{
    public int Kills, RoundsEnded, Restarts, IgnoredHits, EarlyProtectionEnds;
    public int ShotsFired, HitsLanded, Headshots, WallBlocked, Backstabs, Refills;
    public int RocketsFired, Explosions, DirectHits, SplashHits, SelfHits, WorldExplosions, ExpiredRockets, Knocked;
    public int PeakPlayers;

    /// <summary>Server time at which the running smoke test ends (0: not set yet).</summary>
    public double FinishAt;

    /// <summary>The farthest each player moved from their spawn, by name; filled only while a quit deadline is set.</summary>
    public readonly Dictionary<string, float> MovedByName = new();

    public bool Rpg()
    {
        var ok = RocketsFired >= 3 && Explosions >= 3 && DirectHits >= 1 && SplashHits >= 1 && SelfHits >= 1 && WorldExplosions >= 2 && Kills >= 1 && Refills >= 1 && Knocked >= 1;
        GD.Print($"server: rpg smoke rockets={RocketsFired} explosions={Explosions} direct={DirectHits} splash={SplashHits} self={SelfHits} world={WorldExplosions} expired={ExpiredRockets} knocked={Knocked} kills={Kills} refills={Refills}");
        GD.Print(ok ? "SHOOTER_RPG_SERVER_OK" : "SHOOTER_RPG_SERVER_FAIL");
        return ok;
    }

    public bool Weapons()
    {
        var ok = Kills >= 2 && HitsLanded >= 5 && Headshots >= 1 && WallBlocked >= 1 && Backstabs >= 1 && Refills >= 1;
        GD.Print($"server: weapons smoke shots={ShotsFired} hits={HitsLanded} headshots={Headshots} backstabs={Backstabs} wallBlocked={WallBlocked} kills={Kills} refills={Refills}");
        GD.Print(ok ? "SHOOTER_WEAPONS_SERVER_OK" : "SHOOTER_WEAPONS_SERVER_FAIL");
        return ok;
    }

    public bool Match(MatchSim match, MatchConfig config)
    {
        var ok = Kills >= config.ScoreGoal && RoundsEnded == 1 && Restarts == 1 && IgnoredHits >= 2 && EarlyProtectionEnds >= 1
                 && match.Phase == MatchPhase.Running && match.Players.All(p => p.Kills == 0 && p.Deaths == 0);
        GD.Print($"server: match smoke kills={Kills} roundsEnded={RoundsEnded} restarts={Restarts} ignoredHits(protection)={IgnoredHits} earlyProtectionEnds={EarlyProtectionEnds} phase={match.Phase}");
        GD.Print(ok ? "SHOOTER_MATCH_SERVER_OK" : "SHOOTER_MATCH_SERVER_FAIL");
        return ok;
    }

    public bool Net()
    {
        var movedOk = MovedByName.Count(kv => kv.Value >= 2f);
        var ok = PeakPlayers >= 2 && movedOk >= 2;
        GD.Print($"server: peak players {PeakPlayers}, players that moved >= 2 m: {movedOk} ({string.Join(", ", MovedByName.Select(kv => $"{kv.Key}={kv.Value:F1}m"))})");
        GD.Print(ok ? "SHOOTER_NET_SERVER_OK" : "SHOOTER_NET_SERVER_FAIL");
        return ok;
    }
}
