namespace Drycastle.ShooterCore;

/// <summary>Another look of a species (animals.json, "variants"): its own model, with the species' numbers; its hit box may differ (a Chihuahua is not a Saint Bernard).</summary>
public sealed class AnimalVariant
{
    /// <summary>The asset id that names the species' base model (pets.json "pinned"); no variant may use it.</summary>
    public const string BaseAssetId = "base";

    /// <summary>Lower-case id, unique in the species ("corgi"), never <see cref="BaseAssetId"/>; what a pinned pet names as its asset.</summary>
    public string Id { get; set; } = "";
    /// <summary>The GLB, relative to the asset library, like <see cref="AnimalDef.Model"/>.</summary>
    public string Model { get; set; } = "";
    /// <summary>The body box of this look, [width, height, length] like <see cref="AnimalDef.HitBox"/>; null: the species' box.</summary>
    public List<float>? HitBox { get; set; }
    /// <summary>The weight range of this look, [min, max] kg like <see cref="AnimalDef.WeightKg"/> (a moose bull is heavier than a cow); null: the species' range.</summary>
    public List<float>? WeightKg { get; set; }
    /// <summary>Where the Body box of this look is centred, metres ahead of the position (see <see cref="AnimalDef.BodyAhead"/>); null: the species'.</summary>
    public float? BodyAhead { get; set; }
}

/// <summary>Opt-in "sits" behaviour of a species (animals.json, "sit"): from standing it now and then sits for a random while, then stands up again.</summary>
public sealed class AnimalSit
{
    /// <summary>Chance, 0..1, that the next thing after standing still is sitting (rolled first, then barking, then eating; each only for a species that has it).</summary>
    public float Chance { get; set; }
    /// <summary>How long it sits, the sit-down and stand-up clips included (the client plays them inside this time and the stand-up just after).</summary>
    public float MinSeconds { get; set; }
    public float MaxSeconds { get; set; }
}

/// <summary>Opt-in "barks" behaviour of a species (animals.json, "bark"): from standing it now and then barks once, then stands on.</summary>
public sealed class AnimalBark
{
    /// <summary>Chance, 0..1, that the next thing after standing still (and not sitting) is a bark.</summary>
    public float Chance { get; set; }
    /// <summary>The length of the bark clip: how long it stays in the bark state.</summary>
    public float Seconds { get; set; }
}

/// <summary>
/// One species of wildlife or livestock (animals.json). Lengths are metres, speeds metres per second, angles degrees,
/// times seconds. The numbers are placeholders tuned by feel (hp, speeds, radii) or measured from the model (scale, hit box).
/// </summary>
public sealed class AnimalDef
{
    /// <summary>Lower-case id ("deer", "shiba_inu"); the record of one species in the match and in tests.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The GLB, relative to the asset library (assets/), for example "animated-animals/deer.glb"; when it is not there, relative to the private packs (assets-private/, "synty/glb/pets/dog/beagle_01.glb"), and when it is in neither the client draws a box.</summary>
    public string Model { get; set; } = "";
    /// <summary>Uniform scale that brings the model, as it imports, to its real size (the packs are authored about 3 to 5 units tall).</summary>
    public float Scale { get; set; } = 1;
    /// <summary>Yaw added to the model so its head points to the entity's forward (-Z). The animal packs face +Z, so 180.</summary>
    public float ModelYawDeg { get; set; } = 180;
    /// <summary>Hit points. At most 255: the hp byte of the wire carries them as they are.</summary>
    public float Hp { get; set; }
    public float WalkSpeed { get; set; }
    /// <summary>Flee speed (the gallop clip).</summary>
    public float RunSpeed { get; set; }
    public float TurnRateDegPerS { get; set; } = 200;
    /// <summary>False for a pet: it never flees, not from threats nor when shot (a hit still plays its reaction, and still kills). The flee numbers below are then unused.</summary>
    public bool Flees { get; set; } = true;
    /// <summary>True for a pet that any hit that deals damage kills (whatever its hit points): it then has no hit reaction and no corpse, the entity ends at once and the client shows a puff.</summary>
    public bool OneHit { get; set; }
    /// <summary>Null: never sits. Set: the sit behaviour.</summary>
    public AnimalSit? Sit { get; set; }
    /// <summary>Null: never barks. Set: the bark behaviour.</summary>
    public AnimalBark? Bark { get; set; }
    /// <summary>A threat (a player or a vehicle) closer than this makes the animal flee.</summary>
    public float FleeRadius { get; set; }
    /// <summary>How far from where it spawned the animal wanders. It walks back when a flight took it further out.</summary>
    public float WanderRadius { get; set; }
    /// <summary>Body box in the entity's frame: [width, height, length], feet on the ground, centred on the position. For hit tests.</summary>
    public List<float> HitBox { get; set; } = new();
    /// <summary>Metres the body box's centre lies ahead of the position (a model whose origin is under its hind half: a deer). For a bullet's hit test and the hit diagram.</summary>
    public float HitBoxAhead { get; set; }
    /// <summary>Weight range of a wild adult, [min, max] kg. Set: every animal of the species rolls a size and a weight in it (<see cref="AnimalRules.SizeOfStep"/>), and a hunter is told the weight of one a sniper rifle killed. Null: no size roll (a pet, livestock).</summary>
    public List<float>? WeightKg { get; set; }
    /// <summary>
    /// Metres the Body box's centre lies ahead of the position (<c>body_ahead</c>), measured from the model's own bounds (<c>scripts/make_animal_silhouettes.py</c>
    /// prints it). The Body box (what contact is tested against, ADR 0028) is <see cref="HitBox"/>'s width and length, which are the model's measured size head and
    /// antlers included, centred this far ahead: the origin of most models sits under the hind half, so a cow's head lies 1.5 m ahead and its tail 0.9 m behind.
    /// Not <see cref="HitBoxAhead"/>, which a bullet's box test and the hit diagram already use and which is left as it was.
    /// </summary>
    public float BodyAhead { get; set; }
    /// <summary>
    /// animals.json "fx": the id of a client-only visual set the client attaches to every animal of the species ("unicorn": a glowing speckled horn, sparkles at its tip, a rainbow trail);
    /// empty: none. Pure looks, nothing of it travels on the wire or changes a rule. A lower case id (letters, digits, underscores); an id the client does not know draws nothing extra.
    /// </summary>
    public string Fx { get; set; } = "";
    /// <summary>
    /// animals.json "vanish_seconds": above 0, the species does not run when it is startled (a threat in its flee radius, a hit that did not kill, a herd alarm, getting up after a run-over): it becomes
    /// immune for this long (<see cref="AnimalState.Vanish"/>), then is teleported to another place of the map (<see cref="VanishMinM"/> away) and stands there. 0 (default): it flees as any animal.
    /// </summary>
    public float VanishSeconds { get; set; }
    /// <summary>animals.json "vanish_min_m": the least distance (m, flat) between where it vanished and where it reappears. Only for a species with <see cref="VanishSeconds"/>.</summary>
    public float VanishMinM { get; set; }
    /// <summary>animals.json "vanish_grace_seconds": after it reappears nothing startles it for this long, so it can be seen (and shot) where it arrived. Only for a species with <see cref="VanishSeconds"/>.</summary>
    public float VanishGraceSeconds { get; set; } = 3;
    /// <summary>Other models of the same species. Variant index 0 is <see cref="Model"/>; index i (1 up) is <c>Variants[i - 1]</c>. The index travels on the wire.</summary>
    public List<AnimalVariant> Variants { get; set; } = new();

    /// <summary>How many looks the species has: the base model and its variants.</summary>
    public int VariantCount => Variants.Count + 1;

    /// <summary>The model of a variant index (0 or out of range: the base model).</summary>
    public string ModelOf(int variant) => variant >= 1 && variant <= Variants.Count ? Variants[variant - 1].Model : Model;

    string[]? _shapeKeys;

    /// <summary>The name a look's hit shapes have in animal_shapes.json: the species id for its own model, "species/variant" for another look.</summary>
    public string ShapeKey(int variant)
    {
        _shapeKeys ??= Enumerable.Range(0, VariantCount).Select(v => v == 0 ? Id : $"{Id}/{Variants[v - 1].Id}").ToArray();
        return variant >= 0 && variant < _shapeKeys.Length ? _shapeKeys[variant] : Id;
    }

    /// <summary>The body box of a variant index (0 or out of range: the species' box; a variant without its own: the species' box).</summary>
    public IReadOnlyList<float> HitBoxOf(int variant) => variant >= 1 && variant <= Variants.Count && Variants[variant - 1].HitBox is { } b ? b : HitBox;

    /// <summary>The weight range [min, max] kg of a variant index (a variant without its own: the species'), or null when the species has none.</summary>
    public IReadOnlyList<float>? WeightKgOf(int variant) => variant >= 1 && variant <= Variants.Count && Variants[variant - 1].WeightKg is { } w ? w : WeightKg;

    /// <summary>
    /// Half the body's width, clamped to 0.1 to 0.5 m: the radius of the sphere that asks the world whether the animal fits at a spot (a spawn point, a wander
    /// target). Not the contact test, which is the box (<see cref="BodyBox"/>).
    /// </summary>
    public float BodyRadiusOf(int variant) => Math.Clamp(HitBoxOf(variant)[0] / 2, 0.1f, 0.5f);

    /// <summary>The Body box's centre ahead of the position for a variant index (a variant without its own: the species').</summary>
    public float BodyAheadOf(int variant) => variant >= 1 && variant <= Variants.Count && Variants[variant - 1].BodyAhead is { } a ? a : BodyAhead;

    /// <summary>The variant index of an asset id: a variant's id (1 up), or -1.</summary>
    public int VariantIndex(string asset) => Variants.FindIndex(v => v.Id == asset) is >= 0 and var i ? i + 1 : -1;
}

/// <summary>
/// animals.json, schema version 1: the species table and the rules every animal follows (how long a corpse lies, how
/// long a shot animal keeps fleeing, how often it idles, eats and walks).
/// </summary>
public sealed class AnimalsFile : IVersioned
{
    /// <summary>The species the wire can name (the species index travels in the owner field, so the true limit is 65535; the data stays small).</summary>
    public const int MaxSpecies = 64;

    public int Version { get; set; }
    /// <summary>Seconds a killed animal lies (its death clip plays, then the last frame holds) before it is removed.</summary>
    public float CorpseSeconds { get; set; } = 30;
    /// <summary>Seconds a shot (and living) animal keeps running from where the shot came from, whoever is near.</summary>
    public float AlarmSeconds { get; set; } = 8;
    /// <summary>The part of its full hit points a wild animal loses each time a vehicle runs it over (0.5: the second time kills a healthy one).</summary>
    public float RunOverHpFraction { get; set; } = 0.5f;
    /// <summary>Seconds an animal a vehicle threw lies where it landed before it gets up.</summary>
    public float KnockdownSeconds { get; set; } = 2.5f;
    /// <summary>Seconds without a threat in range before a fleeing animal calms down and stops.</summary>
    public float CalmSeconds { get; set; } = 2;
    /// <summary>The largest height change of the ground in one step; a bigger one blocks the animal (a wall, a ditch, water the world refuses).</summary>
    public float MaxStepM { get; set; } = 0.8f;
    /// <summary>The nearest a wander target lies from the animal.</summary>
    public float WanderMinM { get; set; } = 3;
    public float IdleMinSeconds { get; set; } = 2;
    public float IdleMaxSeconds { get; set; } = 8;
    /// <summary>Chance that the next thing after standing still is eating (head down) rather than walking somewhere.</summary>
    public float EatChance { get; set; } = 0.35f;
    public float EatMinSeconds { get; set; } = 3;
    public float EatMaxSeconds { get; set; } = 7;
    public List<AnimalDef> Animals { get; set; } = new();

    Dictionary<string, AnimalDef>? _byId;

    public static AnimalsFile Load(string path) => Validate(DataFile.Load<AnimalsFile>(path));

    public static AnimalsFile Parse(string json) => Validate(DataFile.Parse<AnimalsFile>(json, "animals.json"));

    /// <summary>The species with this id, or null.</summary>
    public AnimalDef? Find(string? id) => DataIndex.Find(ref _byId, Animals, a => a.Id, id);

    /// <summary>The species index (the position in the list, what the wire carries), or -1.</summary>
    public int IndexOf(string id) => Animals.FindIndex(a => a.Id == id);

    /// <summary>The heaviest weight a species may have: the hunt shot carries it in tenths of a kg in a u16.</summary>
    public const float MaxWeightKg = 6000;

    static bool ValidWeight(List<float> w) => w.Count == 2 && w[0] > 0 && w[1] >= w[0] && w[1] <= MaxWeightKg;

    public static AnimalsFile Validate(AnimalsFile f)
    {
        const string F = "animals.json";
        if (f.Animals.Count > MaxSpecies) throw new DataFileException($"{F}: at most {MaxSpecies} species");
        if (!(f.CorpseSeconds > 0) || !(f.AlarmSeconds >= 0) || !(f.CalmSeconds >= 0)) throw new DataFileException($"{F}: corpse_seconds must be positive, alarm_seconds and calm_seconds not negative");
        if (!(f.RunOverHpFraction > 0) || f.RunOverHpFraction > 1 || !(f.KnockdownSeconds >= 0)) throw new DataFileException($"{F}: run_over_hp_fraction must be above 0 and at most 1, knockdown_seconds not negative");
        if (!(f.MaxStepM > 0) || !(f.WanderMinM >= 0)) throw new DataFileException($"{F}: max_step_m must be positive, wander_min_m not negative");
        if (!(f.IdleMinSeconds > 0) || f.IdleMaxSeconds < f.IdleMinSeconds) throw new DataFileException($"{F}: idle_min_seconds must be positive and not above idle_max_seconds");
        if (!(f.EatMinSeconds > 0) || f.EatMaxSeconds < f.EatMinSeconds) throw new DataFileException($"{F}: eat_min_seconds must be positive and not above eat_max_seconds");
        if (f.EatChance is < 0 or > 1) throw new DataFileException($"{F}: eat_chance is 0..1");
        var seen = new HashSet<string>();
        foreach (var a in f.Animals)
        {
            if (a is null || a.Id == "" || a.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
                throw new DataFileException($"{F}: a species id is lower case letters, digits and underscores");
            if (!seen.Add(a.Id)) throw new DataFileException($"{F}: species id {a.Id} appears twice");
            if (a.Name == "") throw new DataFileException($"{F}: {a.Id}: name is empty");
            if (!a.Model.EndsWith(".glb", StringComparison.Ordinal)) throw new DataFileException($"{F}: {a.Id}: model must be a .glb path in the asset library");
            if (a.Fx.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))) throw new DataFileException($"{F}: {a.Id}: fx is a lower case id (letters, digits, underscores) or empty");
            if (!(a.VanishSeconds >= 0) || !(a.VanishMinM >= 0) || !(a.VanishGraceSeconds >= 0) || a.VanishSeconds > 0 && !a.Flees)
                throw new DataFileException($"{F}: {a.Id}: vanish_seconds, vanish_min_m and vanish_grace_seconds are not negative; a species that vanishes also flees (flees true)");
            if (!(a.Scale > 0)) throw new DataFileException($"{F}: {a.Id}: scale must be positive");
            if (!(a.Hp > 0) || a.Hp > 255) throw new DataFileException($"{F}: {a.Id}: hp must be 1..255 (the wire carries it in one byte)");
            if (!(a.WalkSpeed > 0) || a.RunSpeed < a.WalkSpeed) throw new DataFileException($"{F}: {a.Id}: walk_speed must be positive and not above run_speed");
            if (!(a.TurnRateDegPerS > 0)) throw new DataFileException($"{F}: {a.Id}: turn_rate_deg_per_s must be positive");
            if (!(a.FleeRadius > 0)) throw new DataFileException($"{F}: {a.Id}: flee_radius must be positive");
            if (!(a.WanderRadius >= f.WanderMinM)) throw new DataFileException($"{F}: {a.Id}: wander_radius must be at least wander_min_m");
            if (a.Variants.Count > 255) throw new DataFileException($"{F}: {a.Id}: at most 255 variants (the wire carries the index in one byte)");
            var variants = new HashSet<string>();
            foreach (var v in a.Variants)
            {
                if (v is null || v.Id == "" || v.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')) || !variants.Add(v.Id))
                    throw new DataFileException($"{F}: {a.Id}: a variant id is lower case letters, digits and underscores, unique in the species");
                if (v.Id == AnimalVariant.BaseAssetId) throw new DataFileException($"{F}: {a.Id}: variant id \"{AnimalVariant.BaseAssetId}\" is reserved for the base model");
                if (!v.Model.EndsWith(".glb", StringComparison.Ordinal)) throw new DataFileException($"{F}: {a.Id}: variant {v.Id}: model must be a .glb path in the asset library");
                if (v.HitBox is { } vb && (vb.Count != 3 || vb.Any(x => !(x > 0)))) throw new DataFileException($"{F}: {a.Id}: variant {v.Id}: hit_box is [width, height, length], all positive");
                if (v.WeightKg is { } vw && (a.WeightKg == null || !ValidWeight(vw))) throw new DataFileException($"{F}: {a.Id}: variant {v.Id}: weight_kg is [min, max], 0 < min <= max <= {MaxWeightKg}, and only for a species with weight_kg");
            }
            if (a.WeightKg is { } w && !ValidWeight(w)) throw new DataFileException($"{F}: {a.Id}: weight_kg is [min, max], 0 < min <= max <= {MaxWeightKg}");
            if (a.HitBox.Count != 3 || a.HitBox.Any(x => !(x > 0))) throw new DataFileException($"{F}: {a.Id}: hit_box is [width, height, length], all positive");
            if (!(MathF.Abs(a.BodyAhead) < a.HitBox[2] / 2)) throw new DataFileException($"{F}: {a.Id}: body_ahead must lie within the body (under half its length)");
            if (a.Sit is { } sit && (sit.Chance is < 0 or > 1 || !(sit.MinSeconds > 0) || sit.MaxSeconds < sit.MinSeconds))
                throw new DataFileException($"{F}: {a.Id}: sit chance is 0..1, min_seconds positive and not above max_seconds");
            if (a.Bark is { } bark && (bark.Chance is < 0 or > 1 || !(bark.Seconds > 0)))
                throw new DataFileException($"{F}: {a.Id}: bark chance is 0..1, seconds positive");
        }
        return f;
    }
}
