using Godot;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.Net.Server;

/// <summary>The match's events on the server: what a round leaves behind and clears (<see cref="ResetRoundWorld"/>), what each match event does to the rest of the server (<see cref="OnMatchEvent"/>), and the scoreboard.</summary>
public partial class NetServer
{
    /// <summary>
    /// What a round leaves behind on the server, cleared when the next one starts and when the match drops back to waiting: the Holy Mass, the crowd, the
    /// vehicles (riders out of their seats, everything back at its origin, repaired), burning, killstreaks, shells in the air and the round's XP.
    /// </summary>
    void ResetRoundWorld()
    {
        _mass?.End(); // the Holy Mass does not outlive the round
        _crowd?.Clear(); // the match cleared the entities; those still waiting to leave go too
        _pets.RoundStart();
        _companions.RoundStart();
        _wildlife.Clear();
        _minerals.RoundStart();
        _wildlife.Populate(); // the match cleared the pets' entities too; a new round moves new ones into the property groups
        _rozhlas.RoundReset(); // the Urad workers of a detour still open sit down again
        ResetCars();
        HarvesterRoundReset(); // the car reset brought the harvester back to its old field: hidden again
        ScudRoundReset(); // the launchers start over; a Scud truck, which never returns, is retired
        StatusRoundStart();
        LockRoundReset();
        RoundStarted();
    }

    void OnMatchEvent(MatchEvent e)
    {
        switch (e)
        {
            case ScoreboardChanged: _scoreDirty = true; break; // sent once the tick's events are in (FlushScoreboard)
            case PhaseChanged { Phase: MatchPhase.Ending }:
                _smoke.RoundsEnded++;
                RoundEnded();
                GD.Print($"server: round over, winners {string.Join(",", _match.Winners)}");
                break;
            case PhaseChanged { Phase: MatchPhase.Waiting }: // too few players: the round is over without an end screen, nothing of it stays (the match cleared its entities)
                ResetRoundWorld();
                GD.Print("server: match waiting for players");
                break;
            case PhaseChanged { Phase: MatchPhase.Running }: // a new round: also an admin's restart of a running one
                ResetRoundWorld();
                LossRoundStart();
                if (_smoke.RoundsEnded > 0) { _smoke.Restarts++; _smoke.FinishAt = _elapsed + 1.5; }
                GD.Print("server: round started");
                break;
            case PlayerSpawned s:
                if (_byId.TryGetValue(s.Id, out var sc))
                {
                    sc.Body.Teleport(new Vector3(s.Position.X, s.Position.Y + 0.1f, s.Position.Z));
                    sc.Body.Health = MoveConsts.MaxHealth;
                    sc.Inputs.Clear(); sc.Start = sc.Body.GlobalPosition; sc.MaxMoved = 0;
                    sc.HeldUntilTick = 0; sc.LaunchGraceUntil = 0; sc.LaunchedAt = 0; // a new life: no Bigfoot hold or launch grace left over from the last one
                    ApplyLoadout(sc); // a loadout changed while alive applies from this spawn on
                    sc.W.Reset(_elapsed);
                    sc.Recoil.Reset();
                    sc.Breath.Reset();
                    sc.Zero.Reset();
                    SetBinoculars(sc, false); // a new life starts with them down
                    SetHeadLamp(sc, false); // and with the head lamp off
                    ItemRules.IssueStandard(Items, sc.Bag, OwnedItems(sc)); // and with the standard items (the binoculars) in the bag
                    SendEquipmentIfChanged(sc); // also what was issued at the join, before the first life
                    SendAmmo(sc);
                }
                BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(s.Id, s.Position, s.ProtectionSeconds)));
                break;
            case PlayerKilled k:
                HarvesterOnKill(k); // before the corpse: a body the harvester killed is not thrown
                if (_byId.TryGetValue(k.Victim, out var kv) && kv.Car != null) RiderDies(kv); // frees the seat; the corpse (PlayerDied) stays in a pilot's or driver's seat, else falls (ADR 0026)
                _smoke.Kills += k.Killer != k.Victim ? 1 : 0;
                BotKillLog(k);
                if (WeaponsSmoke && _smoke.Kills >= 2 && _smoke.FinishAt == 0) _smoke.FinishAt = _elapsed + 1.5;
                if (RpgSmoke && _smoke.Kills >= 1 && _smoke.FinishAt == 0) _smoke.FinishAt = _elapsed + 1.5;
                LossKill(k.Killer, k.Victim);
                RecordKill(k);
                var fromStreak = StreakKill(k);
                if (Scores(k.Killer) && Scores(k.Victim)) AwardXp(_xp.OnKill(new KillFacts(k.Killer, k.Victim, k.Cause, fromStreak), _elapsed)); // the death of a bot (#214) or of a sandbox player grants no XP
                BroadcastEvent(EventKind.Kill, MatchCodec.Encode(new KillMsg(k.Killer, k.Victim, k.Weapon, k.Cause)));
                break;
            case PlayerTeamChanged tc: OnTeamChanged(tc); break;
            case TargetKilled tk: OnTargetKilled(tk); break;
            case PlayerDied d: if (_byId.TryGetValue(d.Victim, out var dead)) SetHeadLamp(dead, false); OnStatusDeath(d.Victim); _rozhlas.OnPlayerDied(d.Victim); LossDeath(d.Victim); RecordDeath(d.Victim); StreakDeath(d.Victim); SpawnCorpse(d.Victim); BroadcastEvent(EventKind.Death, MatchCodec.Encode(new DeathMsg(d.Victim, d.Killer, d.RespawnDelay, _deathFlight.Remove(d.Victim, out var flight) ? flight : DeathFlight.None))); break;
        }
    }

    /// <summary>Set by a scoreboard change; cleared by the flush that sends the table.</summary>
    bool _scoreDirty;

    /// <summary>
    /// Sends the scoreboard when it changed this tick, and about once a second regardless (the time left and the pings move). A kill
    /// used to resend the whole table at once, so a tick with three kills sent it three times (A-04).
    /// </summary>
    void FlushScoreboard()
    {
        var due = _scoreDirty || _tick % Wire.TickRate == 0;
        _scoreDirty = false;
        if (due && _clients.Count > 0) BroadcastEvent(EventKind.MatchState, MatchCodec.Encode(BuildMatchState()));
    }

    MatchStateMsg BuildMatchState()
    {
        var rows = _match.Ranking().Select(p => new ScoreRow(p.Id, p.Sandbox ? p.Name + " [SANDBOX]" : p.Name, p.Kills, p.Deaths, Ping(p.Id), p.Team, p.Sandbox, p.Bot, p.Squad)).ToList();
        return new MatchStateMsg(_match.Phase, _match.TimeRemaining, _match.Winners, rows, _match.Mode, _match.TeamScore(Teams.A), _match.TeamScore(Teams.B), _match.SquadList);
    }

    ushort Ping(ushort id) =>
        _byId.TryGetValue(id, out var c) && c.Peer is { } peer ? (ushort)Math.Clamp(peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime), 0, ushort.MaxValue) : (ushort)0; // a bot has no ping
}
