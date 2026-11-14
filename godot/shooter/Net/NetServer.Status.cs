using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Player statuses on the server (Holy Mass reward, GLOSSARY "Status" and "Burn"): the divine blessing and the devil's curse in
/// <see cref="Client.Status"/>, and the burn the curse causes (<see cref="BurnSet"/>). The rules are in <c>ShooterCore/Status</c>; this file
/// hooks them in. Blessing: <see cref="MatchSim.SaveFromDeath"/> negates one lethal hit (full health, <see cref="BlessingRules.InvulnerableS"/>
/// of invulnerability, no kill for the attacker, the streak is kept as no death happens). Curse: <see cref="Deal"/> ignites the victim of a hit by
/// the cursed player's own weapon; burn kills are credited to the curse owner. Statuses survive a round change and end at death
/// (<see cref="OnStatusDeath"/>). Every change reaches everyone as <see cref="EventKind.StatusState"/> (others draw the flames and the glow).
/// </summary>
public partial class NetServer
{
    /// <summary>The burn of a curse; the host may replace it with holy_mass_demon.json's <c>burn</c> (the same numbers).</summary>
    public BurnDef CurseBurnDef = CurseBurn.Default;

    readonly BurnSet _burns = new();
    readonly BurnPayout _burnPay = new();
    readonly List<BurnTick> _burnTicks = new();

    /// <summary>Hooks the blessing into the match. Called once, after the match is made.</summary>
    void StatusInit() => _match.SaveFromDeath = id => _byId.TryGetValue(id, out var c) && BlessingRules.TryConsume(c.Status);

    /// <summary>
    /// Gives the player the divine blessing (the Mass reward). False when there is no such living player or they have one already.
    /// The status is sent to everyone with the next tick's flush.
    /// </summary>
    public bool GrantBlessing(ushort clientId) => GrantStatus(clientId, StatusKind.Blessing);

    /// <summary>
    /// Gives the player the devil's curse (the demon's reward): their weapons' hits set victims ablaze. False when there is no such living
    /// player or they have it already.
    /// </summary>
    public bool GrantCurse(ushort clientId) => GrantStatus(clientId, StatusKind.Curse);

    bool GrantStatus(ushort clientId, StatusKind kind) =>
        _byId.TryGetValue(clientId, out var c) && _match.Get(clientId)?.Life == PlayerLife.Alive && c.Status.Grant(kind);

    /// <summary>Takes every status and the burn away from the player (admin testing). True when there was any.</summary>
    public bool ClearStatuses(ushort clientId)
    {
        if (!_byId.TryGetValue(clientId, out var c)) return false;
        var had = c.Status.Any || _burns.IsBurning(clientId);
        OnStatusDeath(clientId);
        return had;
    }

    /// <summary>A hit that did not kill: the cursed shooter's own weapons (not a vehicle, a turret, a streak or the burn itself) set the victim ablaze.</summary>
    void CurseOnHit(ushort attackerId, byte weaponIdx, ushort victim)
    {
        if (weaponIdx >= Weapons.Weapons.Count || !_byId.TryGetValue(attackerId, out var attacker)) return;
        CurseRules.OnHit(attacker.Status, attackerId, victim, _burns, CurseBurnDef);
    }

    /// <summary>
    /// Burns one tick. Hit events carry whole hit points, so the burn's 0.5 hp ticks are collected per victim (<see cref="BurnPayout"/>) and
    /// dealt as whole points: 1 hp every 2 s, 5 hp over a full burn. A fireball's burn (no source) is the victim's own damage.
    /// </summary>
    void StatusTick(float dt)
    {
        if (_burns.Count == 0) return;
        _burnTicks.Clear();
        _burns.Step(dt, _burnTicks);
        foreach (var t in _burnTicks)
        {
            var whole = _burnPay.Whole(t);
            if (whole <= 0) continue;
            var source = t.Source == BurnSet.NoSource || !_byId.ContainsKey(t.Source) ? t.Victim : t.Source;
            Deal(source, SpecialWeapons.Burn, t.Victim, whole, false, false);
        }
    }

    /// <summary>Death (or an admin clear): the statuses and the burn end.</summary>
    void OnStatusDeath(ushort victim)
    {
        _burns.Extinguish(victim);
        _burnPay.Forget(victim);
        if (_byId.TryGetValue(victim, out var c)) c.Status.OnDeath();
    }

    /// <summary>A new round: nobody burns on (the statuses stay).</summary>
    void StatusRoundStart()
    {
        _burns.Clear();
        _burnPay.Clear();
    }

    void StatusLeave(ushort id)
    {
        _burns.Extinguish(id);
        _burnPay.Forget(id);
    }

    /// <summary>Tells everyone about every player whose statuses or burning changed since the last send.</summary>
    void FlushStatuses()
    {
        foreach (var c in _byId.Values)
        {
            var burning = _burns.IsBurning(c.Id);
            if (!c.Status.Changed && burning == c.BurningSent) continue;
            c.Status.Changed = false;
            c.BurningSent = burning;
            if (!burning) _burnPay.Forget(c.Id);
            BroadcastEvent(EventKind.StatusState, StatusCodec.EncodeState(c.Id, c.Status.Mask, burning));
        }
    }

    /// <summary>A joiner learns the statuses of everyone who has any (the joiner has none).</summary>
    void SendStatusOnJoin(Client c)
    {
        foreach (var o in _byId.Values)
            if (o != c && (o.Status.Any || o.BurningSent)) Send(c, new EventMsg((byte)EventKind.StatusState, StatusCodec.EncodeState(o.Id, o.Status.Mask, o.BurningSent)));
    }

    /// <summary>"/status bless|curse|clear [player]": for the named human, or the admin.</summary>
    void ChatStatus(Client c, IReadOnlyList<string> args)
    {
        var target = args.Count > 1 ? FindHuman(args[1]) : c;
        if (target == null) { ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        if (args[0] == "clear")
        {
            ChatTo(c, ChatLineKind.Info, ClearStatuses(target.Id) ? $"{target.Name} has no statuses now." : $"{target.Name} had no status.");
            return;
        }
        var bless = args[0] == "bless";
        if (_match.Get(target.Id)?.Life != PlayerLife.Alive) { ChatTo(c, ChatLineKind.Error, $"{target.Name} is not alive."); return; }
        var given = bless ? GrantBlessing(target.Id) : GrantCurse(target.Id);
        ChatTo(c, ChatLineKind.Info, given ? $"{target.Name} is {(bless ? "blessed" : "cursed")} now." : $"{target.Name} is {(bless ? "blessed" : "cursed")} already.");
    }
}
