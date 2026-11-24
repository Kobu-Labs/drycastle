using Godot;
using Drycastle.Shooter.Net.Server.BotRoster;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Net.Server.Profiles;
using Drycastle.Shooter.Net.Server.Remains;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.Net.Server.RoundFlow;

/// <summary>What a player's spawn, kill and death and an admin's team change do to the rest of the server, feature by feature in the order they run (the match decides when; this decides what follows).</summary>
internal sealed class LifeEvents
{
    readonly ServerContext _ctx;
    readonly SmokeStats _smoke;
    readonly ProgressionServer _progress;
    readonly BotServer _bots;
    readonly ItemsFile _items;
    readonly ItemsServer _gear;
    readonly StatusServer _status;
    readonly RozhlasServer _rozhlas;
    readonly StreakServer _streaks;
    readonly DeathPush _deathPush;
    readonly CorpseServer _remains;
    readonly CarSeats _seats;
    readonly bool _weaponsSmoke, _rpgSmoke;
    readonly Action<PlayerKilled> _harvesterKill;
    readonly Action<ushort, ushort> _lossKill;
    readonly Action<ushort> _lossDeath;

    /// <param name="harvesterKill">Before the corpse: a body the harvester killed is not thrown.</param>
    /// <param name="lossKill">The loss test's count of the kills and the deaths (killer, victim / victim).</param>
    public LifeEvents(ServerContext ctx, SmokeStats smoke, ProgressionServer progress, BotServer bots, ItemsFile items, ItemsServer gear, StatusServer status, RozhlasServer rozhlas, StreakServer streaks,
        DeathPush deathPush, CorpseServer remains, CarSeats seats, bool weaponsSmoke, bool rpgSmoke, Action<PlayerKilled> harvesterKill, Action<ushort, ushort> lossKill, Action<ushort> lossDeath)
    {
        _ctx = ctx; _smoke = smoke; _progress = progress; _bots = bots; _items = items; _gear = gear; _status = status; _rozhlas = rozhlas; _streaks = streaks; _deathPush = deathPush; _remains = remains; _seats = seats;
        _weaponsSmoke = weaponsSmoke; _rpgSmoke = rpgSmoke; _harvesterKill = harvesterKill; _lossKill = lossKill; _lossDeath = lossDeath;
    }

    public void Spawned(PlayerSpawned s)
    {
        if (_ctx.Players.TryGetValue(s.Id, out var sc))
        {
            sc.Body.Teleport(new Vector3(s.Position.X, s.Position.Y + 0.1f, s.Position.Z));
            sc.Body.Health = MoveConsts.MaxHealth;
            sc.Inputs.Clear(); sc.Start = sc.Body.GlobalPosition; sc.MaxMoved = 0;
            sc.HeldUntilTick = 0; sc.LaunchGraceUntil = 0; sc.LaunchedAt = 0; // a new life: no Bigfoot hold or launch grace left over from the last one
            _progress.ApplyLoadout(sc);
            sc.W.Reset(_ctx.Now);
            sc.Recoil.Reset();
            sc.Breath.Reset();
            sc.Zero.Reset();
            _gear.SetBinoculars(sc, false); // a new life starts with them down
            _gear.SetHeadLamp(sc, false); // and with the head lamp off
            ItemRules.IssueStandard(_items, sc.Bag, ProfileServer.OwnedItems(sc)); // and with the standard items (the binoculars) in the bag
            _gear.SendEquipmentIfChanged(sc); // also what was issued at the join, before the first life
            _ctx.SendAmmo(sc);
        }
        _ctx.Broadcast(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(s.Id, s.Position, s.ProtectionSeconds)));
    }

    public void Killed(PlayerKilled k)
    {
        _harvesterKill(k);
        if (_ctx.Players.TryGetValue(k.Victim, out var victim) && victim.Car != null) _remains.RiderDies(victim);
        _smoke.Kills += k.Killer != k.Victim ? 1 : 0;
        _bots.LogKill(k);
        if (_weaponsSmoke && _smoke.Kills >= 2 && _smoke.FinishAt == 0) _smoke.FinishAt = _ctx.Now + 1.5;
        if (_rpgSmoke && _smoke.Kills >= 1 && _smoke.FinishAt == 0) _smoke.FinishAt = _ctx.Now + 1.5;
        _lossKill(k.Killer, k.Victim);
        _progress.RecordKill(k);
        var fromStreak = _streaks.Kill(k, _progress.WeaponKey(k.Weapon));
        if (_progress.Scores(k.Killer) && _progress.Scores(k.Victim)) _progress.Kill(new KillFacts(k.Killer, k.Victim, k.Cause, fromStreak)); // the death of a bot (#214) or of a sandbox player grants no XP
        _ctx.Broadcast(EventKind.Kill, MatchCodec.Encode(new KillMsg(k.Killer, k.Victim, k.Weapon, k.Cause)));
    }

    public void Died(PlayerDied d)
    {
        if (_ctx.Players.TryGetValue(d.Victim, out var dead)) _gear.SetHeadLamp(dead, false);
        _status.OnDeath(d.Victim);
        _rozhlas.OnPlayerDied(d.Victim);
        _lossDeath(d.Victim);
        _progress.RecordDeath(d.Victim);
        _streaks.Death(d.Victim);
        _remains.Spawn(d.Victim);
        _ctx.Broadcast(EventKind.Death, MatchCodec.Encode(new DeathMsg(d.Victim, d.Killer, d.RespawnDelay, _deathPush.TakeFlight(d.Victim))));
    }

    /// <summary>
    /// An admin moved a player to the other team (#312): they climb out of any vehicle, and their turrets and killstreak progress are dropped like at a death
    /// (a turret would else turn on its owner's new teammates). The respawn at the new side's spawn follows from the match.
    /// </summary>
    public void TeamChanged(PlayerTeamChanged tc)
    {
        if (_ctx.Players.TryGetValue(tc.Id, out var c) && c.Car != null) _seats.Leave(c, false);
        _streaks.Wipe(tc.Id);
        if (tc.To == Teams.None) _ctx.Broadcast(EventKind.Death, MatchCodec.Encode(new DeathMsg(tc.Id, tc.Id, DeathMsg.WaitsForAdmin))); // out of the round: down until an admin puts them on a team, no corpse, no death on the record
        GD.Print($"server: #{tc.Id} moved from team {Teams.Tag(tc.From)} to {Teams.Tag(tc.To)}");
    }
}
