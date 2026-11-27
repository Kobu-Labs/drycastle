using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Data;

/// <summary>All data files, loaded from one directory. Levels are checked against the weapons and perks, so it loads last.</summary>
public class GameData
{
    public required WeaponsFile Weapons { get; init; }
    public required MatchConfig Match { get; init; }
    public required VehiclesFile Vehicles { get; init; }
    public required PerksFile Perks { get; init; }
    public required LevelsFile Levels { get; init; }
    /// <summary>killstreaks.json: the streak table (kill thresholds, unlock levels, rules).</summary>
    public required KillstreakFile Killstreaks { get; init; }

    /// <summary>environment.json (day/night clock). Defaults when the file is absent from the parse call.</summary>
    public EnvironmentConfig Environment { get; init; } = new();
    /// <summary>glass.json (glass pane hit points, crack stages, shards; ADR 0007). Defaults when the file is absent.</summary>
    public GlassConfig Glass { get; init; } = new();
    /// <summary>house_destruction.json (voxel destruction of the hollow houses, docs/house-destruction.md). Defaults when the file is absent.</summary>
    public DestructionConfig HouseDestruction { get; init; } = DestructionConfig.Default;
    /// <summary>animals.json (the species of wildlife and livestock and their rules). Empty when the file is absent.</summary>
    public AnimalsFile Animals { get; init; } = new();
    /// <summary>animal_shapes.json (the hit shapes of the wild species, baked from their models), with the private packs' species merged in by the host. Empty when absent: every animal is its body box.</summary>
    public AnimalShapes Shapes { get; set; } = new();
    /// <summary>items.json (equippable items: headgear and the event rewards). Empty when the file is absent.</summary>
    public ItemsFile Items { get; init; } = new();
    /// <summary>pets.json (which property groups keep pets, of which species, and the poop rules). Inert when the file is absent.</summary>
    public PetsFile Pets { get; init; } = new();
    /// <summary>wildlife.json (which wild species the map holds and how many). Empty when the file is absent.</summary>
    public WildlifeFile Wildlife { get; init; } = new();
    /// <summary>bigfoot.json (every number of the Bigfoot event, issue #264). The defaults of the file when it is absent.</summary>
    public BigfootFile Bigfoot { get; init; } = new();
    /// <summary>thieves.json (every number of the Thieves event). Only the version so far; defaults when the file is absent.</summary>
    public ThievesFile Thieves { get; init; } = new();
    /// <summary>holy_mass.json (the citizens of the Holy Mass and, later, the event itself). Defaults when the file is absent.</summary>
    public HolyMassFile HolyMass { get; init; } = new();
    /// <summary>holy_mass_demon.json (the demon of the Holy Mass, its fireball and the burn). Defaults when the file is absent (the numbers are then zero: the demon fight needs the file).</summary>
    public DemonFile Demon { get; init; } = new();
    /// <summary>scud.json (the Scud truck's launcher, targeting, missile and blast). The shipped defaults when the file is absent.</summary>
    public ScudFile Scud { get; init; } = ScudFile.Default;
    /// <summary>plantation.json (the marijuana plantation: hold and high times, the nausea and the looks of a high player). The shipped defaults when the file is absent.</summary>
    public PlantationFile Plantation { get; init; } = PlantationFile.Default;
    /// <summary>duffels.json (the Duffels of the cartel's Jobs: kinds, weights, the throw, the props). Read by the host after <see cref="Parse"/>; the shipped defaults when the file is absent.</summary>
    public Drycastle.ShooterCore.Cartel.DuffelsFile Duffels { get; set; } = Drycastle.ShooterCore.Cartel.DuffelsFile.Default;
    /// <summary>companions.json (the Companions a player can own). Empty when the file is absent.</summary>
    public CompanionsFile Companions { get; init; } = new();
    /// <summary>cartel.json (the Cartel: ranks, tiers, each kind of Job's numbers; docs/cartel.md). The shipped defaults when the file is absent.</summary>
    public CartelFile Cartel { get; init; } = CartelFile.Default;
    /// <summary>turret.json: the sentry turret's tuning (the turret killstreak).</summary>
    public required TurretFile Turret { get; init; }

    /// <summary>camo.json (camouflage presets, tuft and concealment rules). A built-in minimal file when absent.</summary>
    public CamoFile Camo { get; init; } = CamoFile.Parse(MinimalCamoJson);

    /// <summary>Used when camo.json is absent (headless hosts and old data directories): one body, one skin tone, one suit, one face preset, no tufts.</summary>
    const string MinimalCamoJson = """
        {"version":1,"suit_patterns":["blotch"],"face_patterns":["bars"],"vegetation":["grass"],"headgear":["standard","suit_cover","none"],
        "bodies":[{"id":"soldier_m","name":"Soldier (M)","model":"animated-characters/soldier-male.glb","suit":["Main","DarkGreen"],"gear":["Black"],"headgear":["Helmet"]}],
        "skin_tones":[{"id":"light","name":"Light","colour":"#e0ac84"}],"tuft_max_layers":0,
        "tuft_size_m":[0,0],"tuft_sway_m":[0,0],"pattern_scale_per_m":[1,1],"spotting_min_multiplier":0.35,
        "concealment":{"stand":0.3,"crouch":0.6,"prone":0.9,"stillness_weight":0.7,"full_speed_mps":6},
        "suit_presets":[{"id":"olive","name":"Olive","base":"#4b5a32","secondary":"#2e3a1f","pattern":"blotch","vegetation":"grass"}],
        "face_presets":[{"id":"none","name":"None","pattern":"bars","colours":["#1d2a14","#5b6b3a","#c8b98a"],"coverage":0}]}
        """;

    /// <summary>For hosts that read files themselves (Godot res:// inside an export).</summary>
    public static GameData Parse(string weaponsJson, string matchJson, string vehiclesJson, string perksJson, string levelsJson, string killstreaksJson, string turretJson, string environmentJson = "", string camoJson = "", string glassJson = "", string animalsJson = "", string itemsJson = "", string holyMassJson = "", string demonJson = "", string bigfootJson = "", string petsJson = "", string wildlifeJson = "", string thievesJson = "", string scudJson = "", string plantationJson = "", string companionsJson = "", string houseDestructionJson = "", string cartelJson = "")
    {
        var weapons = WeaponsFile.Parse(weaponsJson);
        var perks = PerksFile.Parse(perksJson);
        var killstreaks = KillstreakFile.Parse(killstreaksJson);
        var animals = animalsJson == "" ? new() : AnimalsFile.Parse(animalsJson);
        return new()
        {
            Weapons = weapons,
            Match = DataFile.Parse<MatchConfig>(matchJson, "match.json"),
            Vehicles = VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(vehiclesJson, "vehicles.json")),
            Perks = perks,
            Killstreaks = killstreaks,
            Turret = TurretFile.Parse(turretJson),
            Levels = LevelsFile.Parse(levelsJson, weapons, perks, killstreaks.UnlockLevels),
            Environment = environmentJson == "" ? new() : EnvironmentConfig.Parse(environmentJson),
            Camo = camoJson == "" ? CamoFile.Parse(MinimalCamoJson) : CamoFile.Parse(camoJson),
            Glass = glassJson == "" ? new() : GlassConfig.Parse(glassJson),
            Animals = animals,
            Pets = petsJson == "" ? new() : PetsFile.Parse(petsJson).CheckAgainst(animals),
            Wildlife = wildlifeJson == "" ? new() : WildlifeFile.Parse(wildlifeJson).CheckAgainst(animals),
            Items = itemsJson == "" ? new() : ItemsFile.Parse(itemsJson),
            Bigfoot = bigfootJson == "" ? new() : BigfootFile.Parse(bigfootJson),
            Thieves = thievesJson == "" ? new() : ThievesFile.Parse(thievesJson),
            HolyMass = holyMassJson == "" ? new() : HolyMassFile.Parse(holyMassJson),
            Demon = demonJson == "" ? new() : DemonFile.Parse(demonJson),
            Scud = scudJson == "" ? ScudFile.Default : ScudFile.Parse(scudJson),
            Plantation = plantationJson == "" ? PlantationFile.Default : PlantationFile.Parse(plantationJson),
            Companions = companionsJson == "" ? new() : CompanionsFile.Parse(companionsJson).CheckAgainst(animals),
            HouseDestruction = houseDestructionJson == "" ? DestructionConfig.Default : DestructionConfig.Parse(houseDestructionJson),
            Cartel = cartelJson == "" ? CartelFile.Default : CartelFile.Parse(cartelJson),
        };
    }

    public static GameData Load(string dir)
    {
        var weapons = WeaponsFile.Load(Path.Combine(dir, "weapons.json"));
        var perks = PerksFile.Load(Path.Combine(dir, "perks.json"));
        var killstreaks = KillstreakFile.Load(Path.Combine(dir, "killstreaks.json"));
        var animals = File.Exists(Path.Combine(dir, "animals.json")) ? AnimalsFile.Load(Path.Combine(dir, "animals.json")) : new();
        return new()
        {
            Weapons = weapons,
            Match = MatchConfig.Load(Path.Combine(dir, "match.json")),
            Vehicles = VehiclesFile.Load(Path.Combine(dir, "vehicles.json")),
            Perks = perks,
            Killstreaks = killstreaks,
            Turret = TurretFile.Load(Path.Combine(dir, "turret.json")),
            Levels = LevelsFile.Load(Path.Combine(dir, "levels.json"), weapons, perks, killstreaks.UnlockLevels),
            Environment = File.Exists(Path.Combine(dir, "environment.json")) ? EnvironmentConfig.Load(Path.Combine(dir, "environment.json")) : new(),
            Camo = File.Exists(Path.Combine(dir, "camo.json")) ? CamoFile.Load(Path.Combine(dir, "camo.json")) : CamoFile.Parse(MinimalCamoJson),
            Glass = File.Exists(Path.Combine(dir, "glass.json")) ? GlassConfig.Load(Path.Combine(dir, "glass.json")) : new(),
            Animals = animals,
            Shapes = File.Exists(Path.Combine(dir, "animal_shapes.json")) ? AnimalShapes.Load(Path.Combine(dir, "animal_shapes.json")) : new(),
            Pets = File.Exists(Path.Combine(dir, "pets.json")) ? PetsFile.Load(Path.Combine(dir, "pets.json")).CheckAgainst(animals) : new(),
            Wildlife = File.Exists(Path.Combine(dir, "wildlife.json")) ? WildlifeFile.Load(Path.Combine(dir, "wildlife.json")).CheckAgainst(animals) : new(),
            Items = File.Exists(Path.Combine(dir, "items.json")) ? ItemsFile.Load(Path.Combine(dir, "items.json")) : new(),
            Bigfoot = File.Exists(Path.Combine(dir, "bigfoot.json")) ? BigfootFile.Load(Path.Combine(dir, "bigfoot.json")) : new(),
            Thieves = File.Exists(Path.Combine(dir, "thieves.json")) ? ThievesFile.Load(Path.Combine(dir, "thieves.json")) : new(),
            HolyMass = File.Exists(Path.Combine(dir, "holy_mass.json")) ? HolyMassFile.Load(Path.Combine(dir, "holy_mass.json")) : new(),
            Demon = File.Exists(Path.Combine(dir, "holy_mass_demon.json")) ? DemonFile.Load(Path.Combine(dir, "holy_mass_demon.json")) : new(),
            Scud = File.Exists(Path.Combine(dir, "scud.json")) ? ScudFile.Load(Path.Combine(dir, "scud.json")) : ScudFile.Default,
            Plantation = File.Exists(Path.Combine(dir, "plantation.json")) ? PlantationFile.Load(Path.Combine(dir, "plantation.json")) : PlantationFile.Default,
            Companions = File.Exists(Path.Combine(dir, "companions.json")) ? CompanionsFile.Load(Path.Combine(dir, "companions.json")).CheckAgainst(animals) : new(),
            HouseDestruction = File.Exists(Path.Combine(dir, "house_destruction.json")) ? DestructionConfig.Load(Path.Combine(dir, "house_destruction.json")) : DestructionConfig.Default,
            Cartel = File.Exists(Path.Combine(dir, "cartel.json")) ? CartelFile.Load(Path.Combine(dir, "cartel.json")) : CartelFile.Default,
        };
    }
}
