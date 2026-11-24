using Godot;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server.Fauna;

/// <summary>
/// Pets on the server (GLOSSARY "Pet", "Pen"): animals (wire type 50) that live in one property group and never leave it, run by a
/// <see cref="PetKeeper"/>. Each round, at its start, the keeper gives the property groups of the map their pets by <c>pets.json</c>; no map or an
/// arena mode means no pets. A pet is a hit target like a citizen: <see cref="AddTargets"/> lists the living ones under ids of their own
/// (<see cref="PetRules.FirstHitId"/> up) and <see cref="MatchSim.ApplyDamage"/> hands those ids to the keeper. A kill scores nothing and is not in the
/// kill feed. Pets leave poop (wire type 53) at times; it is cosmetic.
/// <para>
/// Property groups come from the built map: <c>groups.json</c> in the map folder (the pipeline's export writes it from the edits' <c>groups</c>). None: no pets.
/// The ground a pet stands on is the terrain (not roofs); it may walk through a house inside its pen.
/// </para>
/// </summary>
internal sealed class PetServer
{
    readonly ServerContext _ctx;
    readonly PetsFile _data;
    readonly IAnimalWorld _world;
    readonly bool _arenaMode;
    readonly string _mapDir;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly PetKeeper _keeper;
    IReadOnlyDictionary<string, Pen>? _pens;
    bool _pensTried;

    /// <summary>Registers the animal and poop types and starts the keeper. Built once from <c>_Ready</c>, after the match exists.</summary>
    public PetServer(ServerContext ctx, PetsFile data, AnimalsFile animals, AnimalWorld world, bool arenaMode, string mapDir, Drycastle.MapLoader.MapLoader? map)
    {
        _ctx = ctx; _data = data; _world = world.Pets; _arenaMode = arenaMode; _mapDir = mapDir; _map = map;
        AnimalRules.Register(WorldEntityRegistry.Shared);
        PoopRules.Register(WorldEntityRegistry.Shared);
        _keeper = new PetKeeper(data, animals, ctx.Match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}"), Crowd = world.Crowd };
        ctx.Match.Pets = _keeper;
        _keeper.Killed += (pet, by) => GD.Print($"server: #{by} killed a pet ({pet.Def.Id})");
    }

    /// <summary>The keeper, for what asks about a pet by its hit id (a run-over, the Thieves' loot).</summary>
    public PetKeeper Keeper => _keeper;

    /// <summary>A round starts: the pets of the last one are gone with the entities; new ones move into the property groups.</summary>
    public void RoundStart()
    {
        _keeper.Clear();
        if (_ctx.Match.Phase != MatchPhase.Running || _arenaMode || _mapDir == "" || _map == null || !(_data.GroupChance > 0)) return;
        _pens ??= BuildPens();
        if (_pens.Count == 0) return;
        var n = _keeper.Populate(_pens, _world);
        GD.Print($"server: {n} pets in {_pens.Count} property groups");
    }

    /// <summary>The pen of every property group of the map (built once): the parcels of the group, from the groups file.</summary>
    IReadOnlyDictionary<string, Pen> BuildPens()
    {
        var pens = new Dictionary<string, Pen>(StringComparer.Ordinal);
        if (_pensTried) return pens;
        _pensTried = true;
        var path = Path.Combine(_mapDir, "groups.json");
        if (!File.Exists(path)) { GD.PrintErr($"server: no groups.json in {_mapDir} (rebuild the map); no pets"); return pens; }
        try
        {
            foreach (var (group, ids) in PropertyGroups.Parse(File.ReadAllText(path)))
            {
                var parcels = ids.Where(id => _map!.Parcels.ContainsKey(id))
                    .Select(id => (IReadOnlyList<System.Numerics.Vector2[]>)_map!.Parcels[id].Rings.Select(r => r.Select(v => new System.Numerics.Vector2(v.X, v.Y)).ToArray()).ToList()).ToList();
                var pen = new Pen(parcels);
                if (!pen.IsEmpty) pens[group] = pen;
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        {
            GD.PrintErr($"server: {path}: {e.Message}; no pets");
            return new Dictionary<string, Pen>();
        }
        GD.Print($"server: {pens.Count} property groups with a pen, from {path}");
        return pens;
    }

    /// <summary>The living pets as hit targets, only while damage can apply to them (<see cref="MatchSim.ApplyDamage"/> ignores a pet outside Running, and a shot must not stop on a body it cannot hurt).</summary>
    public void AddTargets(List<HitTarget> into)
    {
        if (_ctx.Match.Phase == MatchPhase.Running) _keeper.AddTargets(into);
    }

    /// <summary>Once a tick, after the entities: pets whose time has come leave their poop.</summary>
    public void Tick(float dt) => _keeper.Update(dt);
}
