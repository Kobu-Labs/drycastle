using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Client.Gear;
using Drycastle.Shooter.Net.Client.Incidents;
using Drycastle.Shooter.Net.Client.OwnPlayer;
using Drycastle.Shooter.Net.Client.Shooting;
using Drycastle.Shooter.Net.Client.Streaks;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.HudFeeds;

/// <summary>The match as the server tells it (phase, scoreboard, kills, spawns, deaths): stored in <see cref="ClientMatchState"/>, pushed to the HUD, and what a spawn or a death of the local player starts in the features.</summary>
internal sealed class MatchEvents
{
    readonly ClientContext _ctx;
    readonly KillstreaksClient _streaks;
    readonly LoadoutClient _loadouts;
    readonly BlastDustClient _blastDust;
    readonly OwnPrediction _prediction;
    readonly StateMachine<LinkPhase> _link;
    readonly OwnWeapon _weapon;
    readonly DeathView _death;
    readonly CorpsesClient _corpses;
    readonly ScudClient _scud;
    readonly ClientBot? _bot;

    public MatchEvents(ClientContext ctx, KillstreaksClient streaks, LoadoutClient loadouts, BlastDustClient blastDust, OwnPrediction prediction, StateMachine<LinkPhase> link, OwnWeapon weapon, DeathView death, CorpsesClient corpses, ScudClient scud, ClientBot? bot)
    {
        _ctx = ctx; _streaks = streaks; _loadouts = loadouts; _blastDust = blastDust; _prediction = prediction; _link = link; _weapon = weapon; _death = death; _corpses = corpses; _scud = scud; _bot = bot;
        ctx.Match.PhaseChanged += OnPhaseChanged;
    }

    /// <summary>The round start and end sounds, on a phase change (<see cref="ClientMatchState.PhaseChanged"/>; the first state of a connection is no round start).</summary>
    void OnPhaseChanged(MatchPhase? prev, MatchPhase p)
    {
        if (p is MatchPhase.Running or MatchPhase.Ending) _streaks.Reset(); // a round reset drops earned streaks too, as on the server
        if (p == MatchPhase.Running) _scud.RoundStart(); // the burnt ground and the missiles in the air of the last round
        if (prev == null || !Sfx.Enabled) return;
        if (p == MatchPhase.Running) Sfx.Play2D("round.start");
        else if (p == MatchPhase.Ending) Sfx.Play2D("round.end");
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (!_ctx.Match.Apply(kind, payload, _ctx.Now)) return;
        switch (kind)
        {
            case EventKind.MatchState:
                _ctx.Hud?.SetTeams(_ctx.Match);
                _ctx.Hud?.SetMatch(_ctx.Match.Phase, _ctx.Match.TimeRemaining(_ctx.Now), _ctx.Match.Rows, _ctx.Match.Winners, _ctx.Match.NameOf);
                break;
            case EventKind.Kill:
                var k = _ctx.Match.KillFeed[^1];
                _ctx.Hud?.AddKill(k, _ctx.Match.NameOf(k.Killer), _ctx.Match.NameOf(k.Victim));
                _streaks.OnKill(k);
                GD.Print($"client {_ctx.PlayerName}: kill feed: {_ctx.Match.NameOf(k.Killer)} -> {_ctx.Match.NameOf(k.Victim)} (weapon {k.Weapon})");
                break;
            case EventKind.Spawn:
                var sp = _ctx.Match.LatestSpawn!; // the spawn of this event; the per-player table's last entry is another player's
                GD.Print($"client {_ctx.PlayerName}: spawn #{sp.Id} protected {sp.Protection:F1}s");
                if (sp.Id == _ctx.MyId && _ctx.Me != null)
                {
                    _loadouts.ApplySpawn(); _streaks.Arm(); // the hotbar and perk the server applied at this spawn
                    _ctx.Hotbar.Want = _ctx.Hotbar.Start;
                    _ctx.Dead = false; _blastDust.ClearRinging(); if (_link.Is(LinkPhase.Spawned)) _link.Enter(LinkPhase.Welcomed); _prediction.ClearInputs(); _prediction.ClearViewFix(); // the next snapshot carries the spawn position
                    _bot?.OnSpawn(sp.Protection);
                    _death.End(); _ctx.Hud?.ClearTransients(); _ctx.Hud?.SetDead(false); _ctx.Hud?.SetProtection(sp.Protection); _weapon.OnSpawn();
                }
                break;
            case EventKind.Death:
                var d = _ctx.Match.LatestDeath!;
                GD.Print($"client {_ctx.PlayerName}: death #{d.Victim} by #{d.Killer}, respawn in {d.RespawnDelay:F1}s");
                _ctx.Sound.Death(d);
                _corpses.NoteDeath(d);
                if (d.Victim == _ctx.MyId) { _bot?.OnOwnDeath(); _death.Begin(d.RespawnDelay, d.Flight); _ctx.Dead = true; _weapon.ClearReload(); _ctx.Hud?.SetDead(true, d.Killer == d.Victim ? "" : _ctx.Match.NameOf(d.Killer), d.RespawnDelay, fadeIn: true); _ctx.Hud?.SetProtection(0); _streaks.OnDeath(); }
                break;
        }
    }
}
