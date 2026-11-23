using Godot;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// Match smoke (<c>--match-bot alice|bob</c>): alice kills bob with the debug command, first while he is spawn-protected (must not land) and then unprotected, and checks that a respawn takes the
/// announced delay; bob fires one second after each spawn (which ends his spawn protection early) and counts what he sees. Both end with SHOOTER_MATCH_CLIENT_OK once the round restarted.
/// </summary>
public sealed class MatchSmokeBot : ClientBot
{
    sealed class AliceState
    {
        public ushort Victim;
        public bool Attempted, Verified, Killed;
        public double AttemptAt;
        public int KillsBefore, KillsSent, Attempts;
    }

    readonly bool _isAlice;
    readonly AliceState _alice = new();
    double _spawnAt, _fireStart, _spawnProtection = 99;
    bool _sawProtected, _earlyCounted, _respawnFailed;
    int _deaths, _earlyEnds;

    public MatchSmokeBot(IBotHost host, RozhlasTestBot? chat, string role) : base(host, chat) { _isAlice = role == "alice"; }

    ClientMatchState Match => Ctx.Match;

    public override MoveInput Step()
    {
        // idle; bob fires 1 s after every spawn (ends spawn protection early), alice runs her script
        var btn = MoveButtons.None;
        if (!_isAlice && !Ctx.Dead && Ctx.Now - _spawnAt >= 1.0)
        {
            btn = MoveButtons.Fire;
            if (_fireStart == 0) _fireStart = Ctx.Now;
        }
        var mi = new MoveInput { Buttons = Ctx.Dead ? MoveButtons.None : btn, SpeedScale = 1 };
        if (_isAlice) AliceStep();
        else BobCheck();
        return mi;
    }

    public override bool TimedOut()
    {
        Result(false, "timeout");
        return true;
    }

    public override void OnSpawn(float protection)
    {
        _spawnAt = Ctx.Now; _fireStart = 0; _earlyCounted = false; _spawnProtection = protection;
    }

    public override void OnOwnDeath() => _deaths++;

    public override void OnOwnState(EntityRecord rec)
    {
        if (rec.State == EntityState.Protected) _sawProtected = true;
        if (rec.State == EntityState.Alive && _fireStart > 0 && !_earlyCounted && Ctx.Now - _fireStart < 0.6 && _fireStart - _spawnAt < _spawnProtection - 1.0) { _earlyEnds++; _earlyCounted = true; }
    }

    void Debug(ushort victim) => Ctx.Send(EventKind.Debug, MatchCodec.EncodeDebugKill(victim));

    int MyKills() => Match.Rows.FirstOrDefault(r => r.Id == Ctx.MyId).Kills;

    void AliceStep()
    {
        var a = _alice;
        var now = Ctx.Now;
        if (Match.Restarts > 0) { FinishAlice(); return; }
        if (Match.Phase != MatchPhase.Running) return;
        if (a.Victim == 0)
        {
            var row = Match.Rows.FirstOrDefault(r => r.Name == "bob");
            if (row.Name == null) return;
            a.Victim = row.Id;
        }
        if (!Host.States.TryGetValue(a.Victim, out var v)) return;
        switch (v.State)
        {
            case EntityState.Dead: a.Attempted = a.Verified = a.Killed = false; break;
            case EntityState.Protected when !a.Attempted:
                a.Attempted = true; a.AttemptAt = now; a.KillsBefore = MyKills(); a.Attempts++;
                GD.Print($"client alice: trying to kill protected bob (attempt {a.Attempts})");
                Debug(a.Victim);
                break;
            case EntityState.Protected when a.Attempted && !a.Verified && now - a.AttemptAt >= 0.4:
                if (MyKills() != a.KillsBefore) { Result(false, "kill landed on a protected player"); return; }
                a.Verified = true;
                break;
            case EntityState.Alive when !a.Killed && Match.Phase == MatchPhase.Running:
                a.Killed = true; a.KillsSent++;
                GD.Print($"client alice: killing unprotected bob (kill {a.KillsSent})");
                Debug(a.Victim);
                break;
        }
        // a respawn must take at least the announced delay
        if (Match.DiedAt.TryGetValue(a.Victim, out var died) && Match.SpawnedAt.TryGetValue(a.Victim, out var sp) && sp > died && Match.LastDeath.TryGetValue(a.Victim, out var dm)
            && sp - died < dm.RespawnDelay - 0.2 && !_respawnFailed)
        { _respawnFailed = true; Result(false, $"respawn after {sp - died:F2}s, expected {dm.RespawnDelay:F1}s"); }
    }

    void FinishAlice()
    {
        var a = _alice;
        var ok = Match.RoundsEnded == 1 && Match.Restarts == 1 && Match.Rows.All(r => r.Kills == 0 && r.Deaths == 0)
                 && a.KillsSent >= 2 && a.Attempts >= 2 && !_respawnFailed;
        GD.Print($"client alice: kills sent {a.KillsSent}, protection attempts {a.Attempts}, rounds ended {Match.RoundsEnded}, restarts {Match.Restarts}");
        Result(ok, ok ? "" : "round did not play out as expected");
    }

    void BobCheck()
    {
        if (Match.Restarts > 0)
        {
            var ok = _deaths >= 2 && _sawProtected && _earlyEnds >= 1 && Match.RoundsEnded == 1;
            GD.Print($"client bob: deaths {_deaths}, saw protection {_sawProtected}, early protection ends {_earlyEnds}, rounds ended {Match.RoundsEnded}");
            Result(ok, ok ? "" : "bob did not observe the expected events");
        }
    }

    void Result(bool ok, string why)
    {
        if (Host.LinkEnded) return;
        Host.Leave();
        if (!ok) GD.PrintErr($"client {Ctx.PlayerName}: match smoke failure: {why}");
        GD.Print(ok ? "SHOOTER_MATCH_CLIENT_OK" : "SHOOTER_MATCH_CLIENT_FAIL");
        Host.Quit(ok);
    }
}
