using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The Thieves event's reward on the server (docs/events/thieves/10, design "Reward"): the XP of everyone who took part, and the Gold chain a killed Gang boss leaves.
/// <para>
/// Credit (<see cref="ThievesCredit"/>): a hit on a Gang member or on a van credits its player (the match's member and van hits are wrapped, so every weapon path counts) and Loot sent back
/// (<see cref="NoteLootReturned"/>). When the event is won each credited player still in the match gets the event-win XP (levels.json) once; nobody credited, no XP. A cancelled event or a round reset pays nothing.
/// </para>
/// <para>
/// The chain: a Gang boss killed by a hit (<see cref="GangRoster.Killed"/>) leaves a corpse that carries the Gold chain, lootable as the Bigfoot's head is (ADR 0016 and 0018) and
/// lasting <see cref="ThievesRewardConfig.ChainCorpseSeconds"/>. A boss who boards the van or flees off the map is never killed, so he leaves nothing.
/// </para>
/// </summary>
internal sealed class ThievesPayout
{
    readonly ServerContext _ctx;
    readonly ThievesRewardConfig _cfg;
    readonly ItemsFile _items;
    readonly WeaponsFile _weapons;
    readonly CorpseBook _corpses;
    readonly Action<ushort> _broadcastCorpseItems;
    readonly int _winXp;
    readonly Random _rng;
    readonly Func<ushort, bool> _scores;
    readonly Action<IEnumerable<XpGrant>> _awardXp;
    readonly ThievesCredit _credit = new();
    readonly GangRoster _gangs;

    /// <summary>Counts the hits on members and vans, and watches for a killed Gang boss. Made after the Gang and the Raids, whose hit routing it wraps.</summary>
    public ThievesPayout(ServerContext ctx, ThievesFile data, int eventWinXp, ItemsFile items, WeaponsFile weapons, CorpseBook corpses, Action<ushort> broadcastCorpseItems, Random rng,
        Func<ushort, bool> scores, Action<IEnumerable<XpGrant>> awardXp, GangRoster gangs, RaidRoster raids)
    {
        _ctx = ctx; _cfg = data.Reward; _items = items; _weapons = weapons; _corpses = corpses; _broadcastCorpseItems = broadcastCorpseItems; _winXp = eventWinXp; _rng = rng; _scores = scores; _awardXp = awardXp; _gangs = gangs;
        ctx.Match.Gang = new CreditedGangHits(gangs, _credit, id => gangs.GangOf(id)?.Crew != GangCrew.RivalCrew); // a Job's Rival crew earns no part in the Thieves' reward
        ctx.Match.Vans = new CreditedVanHits(raids, _credit, id => raids.RaidOf(id)?.Gang.Crew != GangCrew.RivalCrew); // nor does a hit on a Heist's Assault van
        gangs.Killed += OnBossKilled;
    }

    public void NoteLootReturned(ushort player) => _credit.NoteLootReturned(player);

    /// <summary>The event is over: a win pays everyone credited, anything else pays nobody, and the list is forgotten.</summary>
    public void Settle(bool won)
    {
        var paid = _credit.Settle(won, id => _ctx.Players.ContainsKey(id) && _scores(id));
        if (paid.Count == 0) return;
        _awardXp(paid.Select(id => new XpGrant(id, _winXp, XpReason.EventWin)).ToList());
        _ctx.ChatToAll(ChatLineKind.System, "", "Took part: " + string.Join(", ", paid.Select(id => _ctx.NameOf(id)).Take(8)) + (paid.Count > 8 ? " and others." : "."));
        GD.Print($"server: Thieves reward paid to {string.Join(",", paid)}");
    }

    /// <summary>A new round clears the event: nothing is paid.</summary>
    public void Clear() => _credit.Clear();

    /// <summary>A member was killed by a hit: a Gang boss leaves the Gold chain on a corpse where he fell.</summary>
    void OnBossKilled(GangKilled k)
    {
        if (!ThievesReward.LeavesChain(k.Kind) || _gangs.GangOf(k.HitId)?.Crew == GangCrew.RivalCrew) return; // a Rival dealer (a Rival crew's Gang boss) pays nothing
        var item = _items.IndexOf(_cfg.ChainItem);
        if (item < 0) { GD.PrintErr($"server: the Gang boss's reward item '{_cfg.ChainItem}' is not in items.json: no corpse"); return; }
        var knife = _weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee); // a weapon nobody can take into a hotbar position (CorpseRules.CanReplace): the corpse is for its item
        var yaw = (float)_rng.NextDouble() * MathF.Tau - MathF.PI;
        var corpse = new Corpse(ThievesReward.ChainCorpseOwner, k.Where, yaw, Math.Max(0, knife), 0, new[] { new ItemStack(item) }, _cfg.ChainCorpseSeconds);
        if (_corpses.Add(corpse) is not { } id) { GD.PrintErr("server: the world refused the Gang boss's corpse"); return; }
        _broadcastCorpseItems(id);
        GD.Print($"server: Gang boss corpse {id} with the {_cfg.ChainItem} at ({k.Where.X:F1},{k.Where.Y:F1},{k.Where.Z:F1}), lasts {_cfg.ChainCorpseSeconds:F0} s");
    }
}
