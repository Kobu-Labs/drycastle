using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Harvest;

/// <summary>
/// The hay bales and their fire (<see cref="HayBale"/>): the field's own (the map's), the ones a harvester's swath drops, and the ones the machine throws out with a victim inside (they are corpses, world entities). A shot sets any bale
/// alight; a burning bale ignites players beside it, credited to nobody (<see cref="BurnSet.HazardSource"/>), and burns out to ash for everyone. A bale the harvester made (not the field's own) may leave senin
/// where it turned to ash (<see cref="HarvesterConfig.SeninChance"/>, via the drop callback: a dynamic Mineral node).
/// </summary>
internal sealed class BaleFire
{
    readonly ServerContext _ctx;
    readonly HarvesterConfig _cfg;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly CorpseBook _corpses;
    readonly StatusServer _status;
    readonly CropCut _cut;
    readonly Random _rng;
    readonly Action<MineralDrop> _onDrop;

    HayBale[]? _staticBales;
    readonly Dictionary<ushort, HayBale> _madeBales = new();
    readonly List<(ushort Id, NVec3 Feet)> _folk = new();

    public BaleFire(ServerContext ctx, HarvesterConfig cfg, Drycastle.MapLoader.MapLoader? map, CorpseBook corpses, StatusServer status, CropCut cut, Random rng, Action<MineralDrop> onDrop)
    {
        _ctx = ctx; _cfg = cfg; _map = map; _corpses = corpses; _status = status; _cut = cut; _rng = rng; _onDrop = onDrop;
    }

    HayBale[] StaticBales => _staticBales ??= (_map?.CropBales ?? Array.Empty<Godot.Vector3>()).Select(p => new HayBale(new NVec3(p.X, p.Y, p.Z), _cfg)).ToArray();

    /// <summary>The bale a thrown-out corpse is: it lies where the corpse does.</summary>
    public void AddMade(ushort corpseId, NVec3 position) => _madeBales[corpseId] = new HayBale(position, _cfg, corpseId);

    /// <summary>A new round: the made bales are gone with their corpses, the field's own start over.</summary>
    public void Reset() { _madeBales.Clear(); _staticBales = null; }

    /// <summary>A joiner learns the static bales that burn or burnt (the made bales are corpses: world entities), then the dropped ones.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_staticBales != null)
            for (var i = 0; i < _staticBales.Length; i++) SendFxOnJoin(c, _staticBales[i], HarvesterCodec.StaticBaleId(i));
        foreach (var sf in _cut.Swaths)
            for (var i = 0; i < sf.Bales.Count; i++) SendFxOnJoin(c, sf.Bales[i], HarvesterCodec.DroppedBaleId(i));
    }

    void SendFxOnJoin(ServerClient c, HayBale b, ushort id)
    {
        if (b.State == BaleState.Intact) return;
        _ctx.SendEvent(c, EventKind.HarvesterFx, HarvesterCodec.EncodeFx(new HarvesterCodec.FxMsg(b.State == BaleState.Burning ? HarvesterCodec.FxKind.BaleLit : HarvesterCodec.FxKind.BaleAsh, b.Position, id)));
    }

    /// <summary>A thrown bale's <see cref="HayBale"/> lies where its corpse is (fire, scorch and shots follow it until it has stopped).</summary>
    public void FollowMade()
    {
        foreach (var kv in _madeBales)
            if (_corpses.Get(kv.Key) is { } body && kv.Value.Position != body.Position) kv.Value.MoveTo(body.Position);
    }

    /// <summary>A shot along <paramref name="from"/> + t * <paramref name="dir"/> for <paramref name="length"/> m (it stopped there: a wall, a player): any bale it passes alight (the field's own and a harvester's).</summary>
    public void Shot(NVec3 from, NVec3 dir, float length)
    {
        if (!(length > 0)) return;
        foreach (var kv in _madeBales)
            if (kv.Value.State == BaleState.Intact && _corpses.Get(kv.Key) != null && kv.Value.HitBy(from, dir, length) && kv.Value.Ignite()) Fx(HarvesterCodec.FxKind.BaleLit, kv.Value.Position, kv.Key);
        foreach (var sf in _cut.Swaths)
            for (var i = 0; i < sf.Bales.Count; i++)
                if (sf.Bales[i].State == BaleState.Intact && sf.Bales[i].HitBy(from, dir, length) && sf.Bales[i].Ignite()) Fx(HarvesterCodec.FxKind.BaleLit, sf.Bales[i].Position, HarvesterCodec.DroppedBaleId(i));
        var bales = StaticBales;
        for (var i = 0; i < bales.Length; i++)
            if (bales[i].State == BaleState.Intact && bales[i].HitBy(from, dir, length) && bales[i].Ignite()) Fx(HarvesterCodec.FxKind.BaleLit, bales[i].Position, HarvesterCodec.StaticBaleId(i));
    }

    /// <summary>Burns the burning bales and sets ablaze every player beside one (credited to nobody); a bale that burnt out is ash for everyone.</summary>
    public void Step(float dt)
    {
        if (!AnyBurning()) { PruneMade(); return; }
        _folk.Clear();
        foreach (var o in _ctx.Players.Values)
            if (_ctx.IsAlive(o.Id)) _folk.Add((o.Id, new NVec3(o.Body.GlobalPosition.X, o.Body.GlobalPosition.Y, o.Body.GlobalPosition.Z)));
        void Burn(HayBale b, ushort id, bool harvesterMade)
        {
            foreach (var victim in b.Scorched(_folk)) _status.Burns.Ignite(victim, BurnSet.HazardSource, _cfg.BaleBurn);
            if (!b.Tick(dt)) return;
            Fx(HarvesterCodec.FxKind.BaleAsh, b.Position, id);
            if (harvesterMade && HarvesterRules.RollSenin(_cfg, _rng, b.Position, HarvesterCodec.IsWorldBale(id) ? (ushort)0 : id) is { } drop) _onDrop(drop);
        }
        foreach (var kv in _madeBales) if (kv.Value.Burning) Burn(kv.Value, kv.Key, true);
        if (_staticBales != null) for (var i = 0; i < _staticBales.Length; i++) if (_staticBales[i].Burning) Burn(_staticBales[i], HarvesterCodec.StaticBaleId(i), false);
        foreach (var sf in _cut.Swaths) for (var i = 0; i < sf.Bales.Count; i++) if (sf.Bales[i].Burning) Burn(sf.Bales[i], HarvesterCodec.DroppedBaleId(i), true);
        PruneMade();
    }

    bool AnyBurning()
    {
        foreach (var kv in _madeBales) if (kv.Value.Burning) return true;
        if (_staticBales != null) foreach (var b in _staticBales) if (b.Burning) return true;
        foreach (var sf in _cut.Swaths) foreach (var b in sf.Bales) if (b.Burning) return true;
        return false;
    }

    /// <summary>A bale that despawned like a corpse (its lifetime, the cap, the round) is forgotten.</summary>
    void PruneMade()
    {
        if (_madeBales.Count == 0) return;
        List<ushort>? gone = null;
        foreach (var id in _madeBales.Keys) if (_corpses.Get(id) == null) (gone ??= new()).Add(id);
        if (gone != null) foreach (var id in gone) _madeBales.Remove(id);
    }

    void Fx(HarvesterCodec.FxKind kind, NVec3 at, ushort id) => HarvestFx.Send(_ctx, kind, at, id);
}
