using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.Statuses;

/// <summary>
/// Player statuses on the server (Holy Mass reward, GLOSSARY "Status" and "Burn"): the divine blessing and the devil's curse in
/// <see cref="ServerClient.Status"/>, and the burn the curse causes (<see cref="BurnSet"/>). The rules are in <c>ShooterCore/Status</c>; this class
/// hooks them in. Blessing: <see cref="MatchSim.SaveFromDeath"/> negates one lethal hit (full health, <see cref="BlessingRules.InvulnerableS"/>
/// of invulnerability, no kill for the attacker, the streak is kept as no death happens). Curse: <see cref="OnHit"/> ignites the victim of a hit by
/// the cursed player's own weapon; burn kills are credited to the curse owner. Statuses survive a round change and end at death
/// (<see cref="OnDeath"/>). Every change reaches everyone as <see cref="EventKind.StatusState"/> (others draw the flames and the glow).
/// </summary>
internal sealed class StatusServer
{
    readonly ServerContext _ctx;
    readonly DealDamage _deal;
    readonly WeaponsFile _weapons;
    readonly Func<ServerClient, bool> _getHigh;
    readonly BurnPayout _pay = new();
    readonly List<BurnTick> _ticks = new();

    /// <summary>The burn of a curse; the host may replace it with holy_mass_demon.json's <c>burn</c> (the same numbers).</summary>
    public BurnDef CurseBurn { get; set; } = Drycastle.ShooterCore.Status.CurseBurn.Default;

    /// <summary>Everyone who burns: the curse's burn, a fireball's, a bale's. The demon and the harvester ignite and put out through it.</summary>
    public BurnSet Burns { get; } = new();

    /// <summary>Hooks the blessing into the match. Built once, after the match is made.</summary>
    /// <param name="getHigh">Gets the player high (the plantation's), for "/status high".</param>
    public StatusServer(ServerContext ctx, DealDamage deal, WeaponsFile weapons, Func<ServerClient, bool> getHigh)
    {
        _ctx = ctx; _deal = deal; _weapons = weapons; _getHigh = getHigh;
        ctx.Match.SaveFromDeath = id => ctx.Players.TryGetValue(id, out var c) && BlessingRules.TryConsume(c.Status);
    }

    /// <summary>
    /// Gives the player the divine blessing (the Mass reward). False when there is no such living player or they have one already.
    /// The status is sent to everyone with the next tick's flush.
    /// </summary>
    public bool GrantBlessing(ushort clientId) => Grant(clientId, StatusKind.Blessing);

    /// <summary>
    /// Gives the player the devil's curse (the demon's reward): their weapons' hits set victims ablaze. False when there is no such living
    /// player or they have it already.
    /// </summary>
    public bool GrantCurse(ushort clientId) => Grant(clientId, StatusKind.Curse);

    bool Grant(ushort clientId, StatusKind kind) =>
        _ctx.Players.TryGetValue(clientId, out var c) && _ctx.IsAlive(clientId) && c.Status.Grant(kind);

    /// <summary>Takes every status and the burn away from the player (admin testing). True when there was any.</summary>
    public bool Clear(ushort clientId)
    {
        if (!_ctx.Players.TryGetValue(clientId, out var c)) return false;
        var had = c.Status.Any || Burns.IsBurning(clientId);
        OnDeath(clientId);
        return had;
    }

    /// <summary>A hit that did not kill: the cursed shooter's own weapons (not a vehicle, a turret, a streak or the burn itself) set the victim ablaze.</summary>
    public void OnHit(ushort attackerId, byte weaponIdx, ushort victim)
    {
        if (weaponIdx >= _weapons.Weapons.Count || !_ctx.Players.TryGetValue(attackerId, out var attacker)) return;
        CurseRules.OnHit(attacker.Status, attackerId, victim, Burns, CurseBurn);
    }

    /// <summary>
    /// Burns one tick. Hit events carry whole hit points, so the burn's 0.5 hp ticks are collected per victim (<see cref="BurnPayout"/>) and
    /// dealt as whole points: 1 hp every 2 s, 5 hp over a full burn. A fireball's burn (no source) and a burning bale's (<see cref="BurnSet.HazardSource"/>) are the victim's own damage (<see cref="BurnCredit"/>).
    /// </summary>
    public void Tick(float dt)
    {
        if (Burns.Count == 0) return;
        _ticks.Clear();
        Burns.Step(dt, _ticks);
        foreach (var t in _ticks)
        {
            var whole = _pay.Whole(t);
            if (whole <= 0) continue;
            var source = BurnCredit.Attacker(t.Source, t.Victim, _ctx.Players.ContainsKey(t.Source)); // a fireball's, a bale's and a gone igniter's burn is the victim's own damage: nobody scores
            _deal(source, SpecialWeapons.Burn, t.Victim, whole, false, false);
        }
    }

    /// <summary>Death (or an admin clear): the statuses, the burn and the high end (a respawned player is sober).</summary>
    public void OnDeath(ushort victim)
    {
        Burns.Extinguish(victim);
        _pay.Forget(victim);
        if (!_ctx.Players.TryGetValue(victim, out var c)) return;
        c.High.Clear();
        c.PlantHold.Reset();
        c.Status.OnDeath();
    }

    /// <summary>A new round: nobody burns on (the statuses stay).</summary>
    public void RoundStart()
    {
        Burns.Clear();
        _pay.Clear();
    }

    public void Leave(ushort id)
    {
        Burns.Extinguish(id);
        _pay.Forget(id);
    }

    /// <summary>Tells everyone about every player whose statuses or burning changed since the last send.</summary>
    public void Flush()
    {
        foreach (var c in _ctx.Players.Values)
        {
            var burning = Burns.IsBurning(c.Id);
            if (!c.Status.Changed && burning == c.BurningSent) continue;
            c.Status.Changed = false;
            c.BurningSent = burning;
            if (!burning) _pay.Forget(c.Id);
            _ctx.Broadcast(EventKind.StatusState, StatusCodec.EncodeState(c.Id, c.Status.Mask, burning));
        }
    }

    /// <summary>A joiner learns the statuses of everyone who has any (the joiner has none).</summary>
    public void SendOnJoin(ServerClient c)
    {
        foreach (var o in _ctx.Players.Values)
            if (o != c && (o.Status.Any || o.BurningSent)) _ctx.SendEvent(c, EventKind.StatusState, StatusCodec.EncodeState(o.Id, o.Status.Mask, o.BurningSent));
    }

    /// <summary>"/status bless|curse|clear [player]": for the named human, or the admin.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        var target = args.Count > 1 ? _ctx.FindHuman(args[1]) : c;
        if (target == null) { _ctx.ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        if (args[0] == "clear")
        {
            _ctx.ChatTo(c, ChatLineKind.Info, Clear(target.Id) ? $"{target.Name} has no statuses now." : $"{target.Name} had no status.");
            return;
        }
        if (!_ctx.IsAlive(target.Id)) { _ctx.ChatTo(c, ChatLineKind.Error, $"{target.Name} is not alive."); return; }
        if (args[0] == "high")
        {
            _ctx.ChatTo(c, ChatLineKind.Info, _getHigh(target) ? $"{target.Name} is high now." : $"{target.Name} cannot get high (a bot).");
            return;
        }
        var bless = args[0] == "bless";
        var given = bless ? GrantBlessing(target.Id) : GrantCurse(target.Id);
        _ctx.ChatTo(c, ChatLineKind.Info, given ? $"{target.Name} is {(bless ? "blessed" : "cursed")} now." : $"{target.Name} is {(bless ? "blessed" : "cursed")} already.");
    }
}
