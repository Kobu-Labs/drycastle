using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The Thieves event's reward on the server (docs/events/thieves/10, design "Reward"): the XP of everyone who took part, and the Gold chain a killed Gang boss leaves.
/// <para>
/// Credit (<see cref="ThievesCredit"/>): a hit on a Gang member credits its player (<see cref="CreditedGangHits"/> wraps the match's member hits, so every weapon path counts);
/// a hit on a van credits through <see cref="CreditedVanHits"/> (the same wrapper for every weapon path) and Loot sent back through <c>NoteLootReturned</c> (<c>NetServer.Thieves.Loot.cs</c>). When the event is won
/// each credited player still in the match gets the event-win XP (levels.json) once; nobody credited, no XP. A cancelled event or a round reset pays nothing.
/// </para>
/// <para>
/// The chain: a Gang boss killed by a hit (<see cref="GangRoster.Killed"/>) leaves a corpse that carries the Gold chain, lootable as the Bigfoot's head is (ADR 0016 and 0018) and
/// lasting <see cref="ThievesRewardConfig.ChainCorpseSeconds"/>. A boss who boards the van or flees off the map is never killed, so he leaves nothing.
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>Who took part in the running Thieves event. <see cref="CreditedVanHits"/> and the Loot return call it.</summary>
    public ThievesCredit Credit { get; } = new();

    /// <summary>Called from <see cref="InitThieves"/> after the roster exists: counts the hits on members, and watches for a killed Gang boss.</summary>
    void InitThievesReward()
    {
        _match.Gang = new CreditedGangHits(_gangs!, Credit);
        _match.Vans = new CreditedVanHits(_raids!, Credit);
        _gangs!.Killed += OnGangBossKilled;
    }

    /// <summary>The event is over (<see cref="ThievesFinish"/>): a win pays everyone credited, anything else pays nobody, and the list is forgotten.</summary>
    void GiveThievesReward(bool won)
    {
        var paid = Credit.Settle(won, id => _byId.ContainsKey(id) && Scores(id));
        if (paid.Count == 0) return;
        AwardXp(paid.Select(id => new XpGrant(id, Levels.Awards.EventWin, XpReason.EventWin)).ToList());
        ChatToAll(ChatLineKind.System, "", "Took part: " + string.Join(", ", paid.Select(id => _byId[id].Name).Take(8)) + (paid.Count > 8 ? " and others." : "."));
        GD.Print($"server: Thieves reward paid to {string.Join(",", paid)}");
    }

    /// <summary>A new round clears the event: nothing is paid.</summary>
    void ResetThievesReward() => Credit.Clear();

    /// <summary>A member was killed by a hit: a Gang boss leaves the Gold chain on a corpse where he fell.</summary>
    void OnGangBossKilled(GangKilled k)
    {
        if (!ThievesReward.LeavesChain(k.Kind)) return;
        var r = ThievesData.Reward;
        var item = Items.IndexOf(r.ChainItem);
        if (item < 0) { GD.PrintErr($"server: the Gang boss's reward item '{r.ChainItem}' is not in items.json: no corpse"); return; }
        var knife = Weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee); // a weapon nobody can take into a hotbar position (CorpseRules.CanReplace): the corpse is for its item
        var yaw = (float)_rng.NextDouble() * MathF.Tau - MathF.PI;
        var corpse = new Corpse(ThievesReward.ChainCorpseOwner, k.Where, yaw, Math.Max(0, knife), 0, new[] { new ItemStack(item) }, r.ChainCorpseSeconds);
        if (_corpses.Add(corpse) is not { } id) { GD.PrintErr("server: the world refused the Gang boss's corpse"); return; }
        _gear.BroadcastCorpseItems(id);
        GD.Print($"server: Gang boss corpse {id} with the {r.ChainItem} at ({k.Where.X:F1},{k.Where.Y:F1},{k.Where.Z:F1}), lasts {r.ChainCorpseSeconds:F0} s");
    }
}
