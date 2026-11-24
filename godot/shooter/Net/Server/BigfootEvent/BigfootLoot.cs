using Godot;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.BigfootEvent;

/// <summary>What the Bigfoot leaves: its body, a corpse that carries the head (the corpse item path of ADR 0016 and 0018).</summary>
internal sealed class BigfootLoot
{
    readonly BigfootFile _data;
    readonly ItemsFile _items;
    readonly WeaponsFile _weapons;
    readonly CorpseBook _corpses;
    readonly ItemsServer _gear;

    public BigfootLoot(BigfootFile data, ItemsFile items, WeaponsFile weapons, CorpseBook corpses, ItemsServer gear)
    {
        _data = data; _items = items; _weapons = weapons; _corpses = corpses; _gear = gear;
    }

    /// <summary>The reward: when its death clip is over, its body is a corpse where it fell that carries the head, lasting <see cref="BigfootReward.CorpseSeconds"/>.</summary>
    public void SpawnBody(NVec3 fell, float yaw)
    {
        var r = _data.Reward;
        var item = _items.IndexOf(r.HeadItem);
        if (item < 0) { GD.PrintErr($"server: the Bigfoot's reward item '{r.HeadItem}' is not in items.json: no corpse"); return; }
        var knife = _weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee); // a weapon nobody can take into a hotbar position (CorpseRules.CanReplace): the corpse is for its item
        var corpse = new Corpse(BigfootRules.TargetId, fell, yaw, Math.Max(0, knife), 0, new[] { new ItemStack(item) }, r.CorpseSeconds, r.CorpseHead);
        var id = _corpses.Add(corpse);
        if (id is not { } cid) { GD.PrintErr("server: the world refused the Bigfoot's corpse"); return; }
        _gear.BroadcastCorpseItems(cid);
        GD.Print($"server: Bigfoot corpse {cid} at ({fell.X:F1},{fell.Y:F1},{fell.Z:F1}), lasts {r.CorpseSeconds:F0} s");
    }
}
