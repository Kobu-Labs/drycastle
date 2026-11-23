using Godot;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Pets on the server (GLOSSARY "Pet", "Pen"): animals (wire type 50) that live in one property group and never leave it, run by a
/// <see cref="PetKeeper"/>. Each round, at its start, the keeper gives the property groups of the map their pets by <c>pets.json</c>; no map or an
/// arena mode means no pets. A pet is a hit target like a citizen: <see cref="LiveTargets"/> lists the living ones under ids of their own
/// (<see cref="PetRules.FirstHitId"/> up) and <see cref="MatchSim.ApplyDamage"/> hands those ids to the keeper. A kill scores nothing and is not in the
/// kill feed. Pets leave poop (wire type 53) at times; it is cosmetic.
/// <para>
/// Property groups come from the built map: <c>groups.json</c> in the map folder (the pipeline's export writes it from the edits' <c>groups</c>). None: no pets.
/// The ground a pet stands on is the terrain (not roofs); it may walk through a house inside its pen.
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>animals.json: the species table (set by the host).</summary>
    public AnimalsFile AnimalData = new();
    /// <summary>The hit shapes of the wild species (set by the host): what a bullet is tested against.</summary>
    public AnimalShapes AnimalShapes = new();
    /// <summary>pets.json: which groups keep pets and the poop rules (set by the host).</summary>
    public PetsFile PetsData = new();

    PetKeeper? _pets;
    IReadOnlyDictionary<string, Pen>? _pens;
    bool _pensTried;

    /// <summary>The deepest water a pet wades in.</summary>
    const float PetWadeM = 0.3f;

    sealed class PetGround : AnimalGroundBase
    {
        public PetGround(NetServer server) : base(server) { }

        protected override float WadeM => PetWadeM;

        public override NVec3? NearestThreat(NVec3 from, float radius) => null; // a pet fears nothing
    }

    /// <summary>Registers the animal and poop types and starts the keeper. Called once from <c>_Ready</c>, after the match exists.</summary>
    void InitPets()
    {
        AnimalRules.Register(WorldEntityRegistry.Shared);
        PoopRules.Register(WorldEntityRegistry.Shared);
        _pets = new PetKeeper(PetsData, AnimalData, _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}"), Crowd = _animalCrowd };
        _match.Pets = _pets;
        _pets.Killed += (pet, by) => GD.Print($"server: #{by} killed a pet ({pet.Def.Id})");
    }

    /// <summary>A round starts: the pets of the last one are gone with the entities; new ones move into the property groups.</summary>
    void PetsRoundStart()
    {
        if (_pets == null) return;
        _pets.Clear();
        if (_match.Phase != MatchPhase.Running || ArenaMode || MapDir == "" || Map == null || !(PetsData.GroupChance > 0)) return;
        _pens ??= BuildPens();
        if (_pens.Count == 0) return;
        var n = _pets.Populate(_pens, new PetGround(this));
        GD.Print($"server: {n} pets in {_pens.Count} property groups");
    }

    /// <summary>The pen of every property group of the map (built once): the parcels of the group, from the groups file.</summary>
    IReadOnlyDictionary<string, Pen> BuildPens()
    {
        var pens = new Dictionary<string, Pen>(StringComparer.Ordinal);
        if (_pensTried) return pens;
        _pensTried = true;
        var path = Path.Combine(MapDir, "groups.json");
        if (!File.Exists(path)) { GD.PrintErr($"server: no groups.json in {MapDir} (rebuild the map); no pets"); return pens; }
        try
        {
            foreach (var (group, ids) in PropertyGroups.Parse(File.ReadAllText(path)))
            {
                var parcels = ids.Where(id => Map!.Parcels.ContainsKey(id))
                    .Select(id => (IReadOnlyList<System.Numerics.Vector2[]>)Map!.Parcels[id].Rings.Select(r => r.Select(v => new System.Numerics.Vector2(v.X, v.Y)).ToArray()).ToList()).ToList();
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
    void AddPetTargets(List<HitTarget> into)
    {
        if (_match.Phase == MatchPhase.Running) _pets?.AddTargets(into);
    }

    /// <summary>Once a tick, after the entities: pets whose time has come leave their poop.</summary>
    void PetsTick(float dt) => _pets?.Update(dt);
}
