using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.Net.Server.RoundFlow;

/// <summary>The match's events on the server: the order is the match's, this is the table of what each one sets off. A round's end and start, a player's life (<see cref="LifeEvents"/>), the scoreboard (<see cref="ScoreboardServer"/>) and a squad's new leader (told to the squad).</summary>
internal sealed class MatchEventsServer
{
    readonly ServerContext _ctx;
    readonly SmokeStats _smoke;
    readonly ScoreboardServer _scoreboard;
    readonly RoundReset _reset;
    readonly LifeEvents _life;
    readonly Action _roundEnded;
    readonly Action _lossRoundStart;
    readonly Action<TargetKilled> _targetKilled;

    /// <param name="roundEnded">The end screen's XP and stats.</param>
    /// <param name="lossRoundStart">The loss test's clock starts with the round.</param>
    /// <param name="targetKilled">A target dummy died (the shooter's streak and XP).</param>
    public MatchEventsServer(ServerContext ctx, SmokeStats smoke, ScoreboardServer scoreboard, RoundReset reset, LifeEvents life, Action roundEnded, Action lossRoundStart, Action<TargetKilled> targetKilled)
    {
        _ctx = ctx; _smoke = smoke; _scoreboard = scoreboard; _reset = reset; _life = life; _roundEnded = roundEnded; _lossRoundStart = lossRoundStart; _targetKilled = targetKilled;
    }

    public void OnEvent(MatchEvent e)
    {
        switch (e)
        {
            case ScoreboardChanged: _scoreboard.Changed(); break; // sent once the tick's events are in
            case PhaseChanged { Phase: MatchPhase.Ending }: RoundEnded(); break;
            case PhaseChanged { Phase: MatchPhase.Waiting }: // too few players: the round is over without an end screen, nothing of it stays (the match cleared its entities)
                _reset.Run();
                GD.Print("server: match waiting for players");
                break;
            case PhaseChanged { Phase: MatchPhase.Running }: RoundStarted(); break; // a new round: also an admin's restart of a running one
            case PlayerSpawned s: _life.Spawned(s); break;
            case PlayerKilled k: _life.Killed(k); break;
            case PlayerTeamChanged tc: _life.TeamChanged(tc); break;
            case TargetKilled tk: _targetKilled(tk); break;
            case PlayerDied d: _life.Died(d); break;
            case SquadLeaderChanged lc: // #494: the squad hears who leads it now
                foreach (var (id, text) in SquadRules.LeaderTold(_ctx.Match, lc)) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.ChatTo(c, ChatLineKind.Info, text);
                break;
        }
    }

    void RoundEnded()
    {
        _smoke.RoundsEnded++;
        _roundEnded();
        GD.Print($"server: round over, winners {string.Join(",", _ctx.Match.Winners)}");
    }

    void RoundStarted()
    {
        _reset.Run();
        _lossRoundStart();
        if (_smoke.RoundsEnded > 0) { _smoke.Restarts++; _smoke.FinishAt = _ctx.Now + 1.5; }
        GD.Print("server: round started");
    }
}
