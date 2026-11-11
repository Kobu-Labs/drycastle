using Suchohrad.ShooterCore;
using Suchohrad.ShooterServer.Persistence;
using Suchohrad.ShooterServer.Persistence.InMemory;
using Suchohrad.ShooterServer.Persistence.Sqlite;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Camouflage framework (ticket #140): codec, validation, camo.json, concealment, wire messages and persistence.</summary>
public class CamoTests
{
    static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    static CamoFile Camo => Data.Camo;

    static CamoSpec Varied() => new()
    {
        Body = 3, SkinTone = 4, Headgear = 2,
        SuitBase = new Rgb(1, 2, 3), SuitSecondary = new Rgb(250, 251, 252), SuitPattern = 2, PatternScale = 7, Contrast = 200,
        TuftLayers = 3, TuftSize = 99, TuftHead = 10, TuftChest = 20, TuftArms = 30, TuftLegs = 40,
        TuftColourVariation = 50, TuftSway = 60, Vegetation = 1,
        FacePattern = 2, FaceColourA = new Rgb(9, 8, 7), FaceColourB = new Rgb(6, 5, 4), FaceColourC = new Rgb(3, 2, 1), FaceCoverage = 180,
    };

    [Fact]
    public void SpecRoundTripsAndFitsTheBudget()
    {
        foreach (var spec in new[] { CamoSpec.Default, Varied() })
        {
            var bytes = spec.Encode();
            Assert.Equal(CamoSpec.EncodedSize, bytes.Length);
            Assert.True(bytes.Length <= 64);
            Assert.Equal(spec, CamoSpec.TryDecode(bytes));
        }
    }

    [Fact]
    public void StoredVersion1SpecsStillDecodeWithTheDefaultCharacter()
    {
        var v2 = Varied().Encode();
        var v1 = v2.AsSpan(0, CamoSpec.EncodedSizeV1).ToArray();
        v1[0] = 1; // a profile saved before the character fields existed
        var spec = CamoSpec.TryDecode(v1);
        Assert.NotNull(spec);
        Assert.Equal(Varied() with { Body = 0, SkinTone = 0, Headgear = 0 }, spec);
        Assert.Null(CamoSpec.TryDecode(v2.AsSpan(0, CamoSpec.EncodedSizeV1))); // a v2 prefix of the same length is not a v1 spec
    }

    [Fact]
    public void CharacterIndicesAreCheckedAgainstCamoJson()
    {
        Assert.True(Camo.Bodies.Count >= 2 && Camo.SkinTones.Count >= 2 && Camo.Headgear.Count == 3);
        Assert.Null(CamoSpec.Default.Validate(Camo));
        Assert.NotNull((CamoSpec.Default with { Body = (byte)Camo.Bodies.Count }).Validate(Camo));
        Assert.NotNull((CamoSpec.Default with { SkinTone = (byte)Camo.SkinTones.Count }).Validate(Camo));
        Assert.NotNull((CamoSpec.Default with { Headgear = (byte)Camo.Headgear.Count }).Validate(Camo));
        Assert.All(Camo.Bodies, b => Assert.NotEmpty(b.Suit));
    }

    [Fact]
    public void SpecDecodeRejectsWrongLengthVersionAndEveryTruncation()
    {
        var bytes = Varied().Encode();
        Assert.Null(CamoSpec.TryDecode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(CamoSpec.TryDecode(new byte[CamoSpec.EncodedSize + 1]));
        var wrongVersion = bytes.ToArray();
        wrongVersion[0] = 99;
        Assert.Null(CamoSpec.TryDecode(wrongVersion));
        for (var cut = 0; cut < bytes.Length; cut++)
            Assert.Null(CamoSpec.TryDecode(bytes.AsSpan(0, cut)));
    }

    [Fact]
    public void AppearancePayloadRoundTripsAndRejectsMalformedInput()
    {
        var spec = Varied();
        var payload = AppearanceCodec.Encode(4242, spec);
        Assert.Equal(AppearanceCodec.PayloadSize, payload.Length);
        var decoded = AppearanceCodec.Decode(payload);
        Assert.NotNull(decoded);
        Assert.Equal((ushort)4242, decoded!.Value.PlayerId);
        Assert.Equal(spec, decoded.Value.Spec);
        Assert.Null(AppearanceCodec.Decode(payload.AsSpan(0, payload.Length - 1)));
    }

    [Fact]
    public void DefaultSpecIsValidAgainstTheShippedData()
    {
        Assert.Null(CamoSpec.Default.Validate(Camo));
    }

    [Theory]
    [InlineData("suit pattern")]
    [InlineData("face pattern")]
    [InlineData("vegetation")]
    [InlineData("tuft layers")]
    public void ValidateNamesEachBadIndex(string which)
    {
        var spec = which switch
        {
            "suit pattern" => CamoSpec.Default with { SuitPattern = (byte)Camo.SuitPatterns.Count },
            "face pattern" => CamoSpec.Default with { FacePattern = (byte)Camo.FacePatterns.Count },
            "vegetation" => CamoSpec.Default with { Vegetation = (byte)Camo.Vegetation.Count },
            _ => CamoSpec.Default with { TuftLayers = (byte)(Camo.TuftMaxLayers + 1) },
        };
        Assert.NotNull(spec.Validate(Camo));
    }

    [Fact]
    public void SuitAndFacePresetsResolveToValidSpecs()
    {
        Assert.NotEmpty(Camo.SuitPresets);
        Assert.NotEmpty(Camo.FacePresets);
        foreach (var p in Camo.SuitPresets.Concat(Camo.FacePresets))
            Assert.Null(p.Spec.Validate(Camo));
        Assert.NotNull(Camo.SuitPreset("woodland"));
        Assert.Null(Camo.SuitPreset("no-such-preset"));
    }

    [Fact]
    public void CamoFileLoadsFromTheDataDirectory()
    {
        var file = CamoFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "camo.json"));
        Assert.Equal(1, file.Version);
        Assert.Equal(file.SuitPresets.Count, Camo.SuitPresets.Count);
    }

    [Fact]
    public void CamoFileRejectsBadDataAtLoad()
    {
        var good = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "camo.json"));
        Assert.Throws<DataFileException>(() => CamoFile.Parse(good.Replace("\"version\": 1", "\"version\": 9")));
        Assert.Throws<DataFileException>(() => CamoFile.Parse(good.Replace("#4b5a32", "4b5a32")));
        Assert.Throws<DataFileException>(() => CamoFile.Parse(good.Replace("\"pattern\": \"blotch\"", "\"pattern\": \"nope\"")));
        Assert.Throws<DataFileException>(() => CamoFile.Parse(good.Replace("\"spotting_min_multiplier\": 0.35", "\"spotting_min_multiplier\": 0")));
        Assert.Throws<DataFileException>(() => CamoFile.Parse(good.Replace("\"tuft_size_m\": [0.03, 0.22]", "\"tuft_size_m\": [0.5, 0.1]")));
        Assert.Throws<DataFileException>(() => CamoFile.Parse(good.Replace("\"full_speed_mps\": 6.0", "\"full_speed_mps\": 0")));
    }

    [Fact]
    public void ConcealmentIsBestProneAndStillAndDropsWhenMoving()
    {
        var rules = Camo.Concealment;
        var proneStill = Concealment.Value(Stance.Prone, 0, rules);
        var crouchStill = Concealment.Value(Stance.Crouch, 0, rules);
        var standStill = Concealment.Value(Stance.Stand, 0, rules);
        Assert.True(proneStill > crouchStill);
        Assert.True(crouchStill > standStill);
        Assert.True(Concealment.Value(Stance.Prone, 3, rules) < proneStill);
        Assert.Equal(Concealment.Value(Stance.Prone, rules.FullSpeedMps, rules), Concealment.Value(Stance.Prone, rules.FullSpeedMps * 3, rules), 5);
        foreach (var s in new[] { Stance.Stand, Stance.Crouch, Stance.Prone })
            for (var v = 0f; v <= 20f; v += 0.5f)
                Assert.InRange(Concealment.Value(s, v, rules), 0f, 1f);
    }

    [Fact]
    public void SpottingRangeShrinksWithConcealmentToTheDataMinimum()
    {
        const float min = 0.35f;
        Assert.Equal(100f, Concealment.SpottingRange(100f, 0f, min), 4);
        Assert.Equal(35f, Concealment.SpottingRange(100f, 1f, min), 4);
        Assert.True(Concealment.SpottingRange(100f, 0.5f, min) > Concealment.SpottingRange(100f, 0.9f, min));
    }

    [Fact]
    public void ServerValidatesAndWireCarriesTheCamoMessages()
    {
        var spec = Varied();
        var save = Wire.Encode(new SaveCamoMsg(3, spec));
        Assert.Equal(Wire.TSaveCamo, save[0]);
        Assert.Equal(new SaveCamoMsg(3, spec), Wire.DecodeReliable(save));
        Assert.Null(Wire.DecodeReliable(save.AsSpan(0, save.Length - 1)));

        // every reliable message type sits below the input channel's type byte, so the router still tells them apart
        Assert.True(Wire.TSaveCamo < Wire.TInput);
        Assert.True(Wire.ProtocolVersion >= 4);
        Assert.Equal((byte)EventKind.Appearance, (byte)22);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SavedCamoIsPerLoadoutSlotAndDefaultsWhenUnset(string backend)
    {
        var curve = Data.Levels.Curve;
        var path = Path.Combine(Path.GetTempPath(), $"shooter-camo-{Guid.NewGuid():N}", "shooter.db");
        using IProgressionStore store = backend == "memory" ? new InMemoryProgressionStore(curve) : SqliteProgressionStore.Open(path, _ => { }, curve);
        var player = await store.Profiles.GetOrCreateAsync("camo-token");
        Assert.Empty(await store.Camo.GetAllAsync(player.Id));

        var spec = Varied();
        await store.Camo.SaveAsync(player.Id, 2, spec);
        await store.Camo.SaveAsync(player.Id, 4, CamoSpec.Default);
        var all = await store.Camo.GetAllAsync(player.Id);
        Assert.Equal(2, all.Count);
        Assert.Equal((2, spec), all[0]);
        Assert.Equal((4, CamoSpec.Default), all[1]);

        await store.Camo.SaveAsync(player.Id, 2, CamoSpec.Default);
        Assert.Equal((2, CamoSpec.Default), (await store.Camo.GetAllAsync(player.Id))[0]);
    }

    [Fact]
    public void TheCharacterMigrationKeepsOldRowsAndAllowsTheLongerSpec()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        void Run(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        Run("CREATE TABLE players (id INTEGER PRIMARY KEY);");
        Run("INSERT INTO players (id) VALUES (1);");
        Run("CREATE TABLE loadout_camo (player_id INTEGER NOT NULL REFERENCES players (id), slot INTEGER NOT NULL CHECK (slot BETWEEN 0 AND 4), spec BLOB NOT NULL CHECK (length(spec) = 30), PRIMARY KEY (player_id, slot));");
        Run("INSERT INTO loadout_camo (player_id, slot, spec) VALUES (1, 0, zeroblob(30));");
        using var stream = typeof(SqliteProgressionStore).Assembly.GetManifestResourceStream("migrations/0006_camo_character.sql");
        Assert.NotNull(stream);
        Run(new StreamReader(stream!).ReadToEnd());
        Run("INSERT INTO loadout_camo (player_id, slot, spec) VALUES (1, 1, zeroblob(33));");
        Assert.ThrowsAny<Exception>(() => Run("INSERT INTO loadout_camo (player_id, slot, spec) VALUES (1, 2, zeroblob(31));"));
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM loadout_camo;";
        Assert.Equal(2L, (long)q.ExecuteScalar()!);
    }

    public static IEnumerable<object[]> Backends => new[] { new object[] { "memory" }, new object[] { "sqlite" } };
}
