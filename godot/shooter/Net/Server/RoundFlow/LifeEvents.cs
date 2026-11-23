using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Net.Server.Profiles;
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
    /// <summary>What the parts of the server that are not classes yet do at a player's life events (each a stop-gap, named for what it does).</summary>
    public sealed class Hooks
    {
        /// <summary>A loadout changed while alive applies from this spawn on.</summary>
        public required Action<ServerClient> ApplyLoadout { get; init; }
        public required Action<ServerClient> SendAmmo { get; init; }
        /// <summary>Before the corpse: a body the harvester killed is not thrown.</summary>
        public required Action<PlayerKilled> HarvesterKill { get; init; }
        /// <summary>Frees the seat; the corpse stays in a pilot's or driver's seat, else falls (ADR 0026).</summary>
        public required Action<ServerClient> RiderDies { get; init; }
        public required Action<PlayerKilled> BotKillLog { get; init; }
        public required Action<ushort, ushort> LossKill { get; init; }
        public required Action<ushort> LossDeath { get; init; }
        public required Action<PlayerKilled> RecordKill { get; init; }
        public required Action<ushort> RecordDeath { get; init; }
        public required Func<byte, string> WeaponKey { get; init; }
        public required Func<ushort, bool> Scores { get; init; }
        public required Action<IEnumerable<XpGrant>> AwardXp { get; init; }
        public required Action<ushort> SpawnCorpse { get; init; }
        public required Action<ServerClient, bool> LeaveSeat { get; init; }
    }

    readonly ServerContext _ctx;
    readonly SmokeStats _smoke;
    readonly MatchXp _xp;
    readonly ItemsFile _items;
    readonly ItemsServer _gear;
    readonly StatusServer _status;
    readonly RozhlasServer _rozhlas;
    readonly StreakServer _streaks;
    readonly DeathPush _deathPush;
    readonly bool _weaponsSmoke, _rpgSmoke;
    readonly Hooks _hooks;

    public LifeEvents(ServerContext ctx, SmokeStats smoke, MatchXp xp, ItemsFile items, ItemsServer gear, StatusServer status, RozhlasServer rozhlas, StreakServer streaks, DeathPush deathPush,
        bool weaponsSmoke, bool rpgSmoke, Hooks hooks)
    {
        _ctx = ctx; _smoke = smoke; _xp = xp; _items = items; _gear = gear; _status = status; _rozhlas = rozhlas; _streaks = streaks; _deathPush = deathPush;
        _weaponsSmoke = weaponsSmoke; _rpgSmoke = rpgSmoke; _hooks = hooks;
    }

    public void Spawned(PlayerSpawned s)
    {
        if (_ctx.Players.TryGetValue(s.Id, out var sc))
        {
            sc.Body.Teleport(new Vector3(s.Position.X, s.Position.Y + 0.1f, s.Position.Z));
            sc.Body.Health = MoveConsts.MaxHealth;
            sc.Inputs.Clear(); sc.Start = sc.Body.GlobalPosition; sc.MaxMoved = 0;
            sc.HeldUntilTick = 0; sc.LaunchGraceUntil = 0; sc.LaunchedAt = 0; // a new life: no Bigfoot hold or launch grace left over from the last one
            _hooks.ApplyLoadout(sc);
            sc.W.Reset(_ctx.Now);
            sc.Recoil.Reset();
            sc.Breath.Reset();
            sc.Zero.Reset();
            _gear.SetBinoculars(sc, false); // a new life starts with them down
            _gear.SetHeadLamp(sc, false); // and with the head lamp off
            ItemRules.IssueStandard(_items, sc.Bag, ProfileServer.OwnedItems(sc)); // and with the standard items (the binoculars) in the bag
            _gear.SendEquipmentIfChanged(sc); // also what was issued at the join, before the first life
            _hooks.SendAmmo(sc);
        }
        _ctx.Broadcast(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(s.Id, s.Position, s.ProtectionSeconds)));
    }

    public void Killed(PlayerKilled k)
    {
        _hooks.HarvesterKill(k);
        if (_ctx.Players.TryGetValue(k.Victim, out var victim) && victim.Car != null) _hooks.RiderDies(victim);
        _smoke.Kills += k.Killer != k.Victim ? 1 : 0;
        _hooks.BotKillLog(k);
        if (_weaponsSmoke && _smoke.Kills >= 2 && _smoke.FinishAt == 0) _smoke.FinishAt = _ctx.Now + 1.5;
        if (_rpgSmoke && _smoke.Kills >= 1 && _smoke.FinishAt == 0) _smoke.FinishAt = _ctx.Now + 1.5;
        _hooks.LossKill(k.Killer, k.Victim);
        _hooks.RecordKill(k);
        var fromStreak = _streaks.Kill(k, _hooks.WeaponKey(k.Weapon));
        if (_hooks.Scores(k.Killer) && _hooks.Scores(k.Victim)) _hooks.AwardXp(_xp.OnKill(new KillFacts(k.Killer, k.Victim, k.Cause, fromStreak), _ctx.Now)); // the death of a bot (#214) or of a sandbox player grants no XP
        _ctx.Broadcast(EventKind.Kill, MatchCodec.Encode(new KillMsg(k.Killer, k.Victim, k.Weapon, k.Cause)));
    }

    public void Died(PlayerDied d)
    {
        if (_ctx.Players.TryGetValue(d.Victim, out var dead)) _gear.SetHeadLamp(dead, false);
        _status.OnDeath(d.Victim);
        _rozhlas.OnPlayerDied(d.Victim);
        _hooks.LossDeath(d.Victim);
        _hooks.RecordDeath(d.Victim);
        _streaks.Death(d.Victim);
        _hooks.SpawnCorpse(d.Victim);
        _ctx.Broadcast(EventKind.Death, MatchCodec.Encode(new DeathMsg(d.Victim, d.Killer, d.RespawnDelay, _deathPush.TakeFlight(d.Victim))));
    }

    /// <summary>
    /// An admin moved a player to the other team (#312): they climb out of any vehicle, and their turrets and killstreak progress are dropped like at a death
    /// (a turret would else turn on its owner's new teammates). The respawn at the new side's spawn follows from the match.
    /// </summary>
    public void TeamChanged(PlayerTeamChanged tc)
    {
        if (_ctx.Players.TryGetValue(tc.Id, out var c) && c.Car != null) _hooks.LeaveSeat(c, false);
        _streaks.Wipe(tc.Id);
        if (tc.To == Teams.None) _ctx.Broadcast(EventKind.Death, MatchCodec.Encode(new DeathMsg(tc.Id, tc.Id, DeathMsg.WaitsForAdmin))); // out of the round: down until an admin puts them on a team, no corpse, no death on the record
        GD.Print($"server: #{tc.Id} moved from team {Teams.Tag(tc.From)} to {Teams.Tag(tc.To)}");
    }
}
