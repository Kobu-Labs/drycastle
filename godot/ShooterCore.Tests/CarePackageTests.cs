using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #124: the care package crate, its weighted perk table, opening and the perk it grants.</summary>
public class CarePackageTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    const ushort Owner = 7, Opener = 9;
    static readonly Vector3 Ground = new(10, 0, 20);

    /// <summary>A fresh world with the care package type registered, so tests do not share the static registry.</summary>
    static WorldEntityManager World(out WorldEntityManager manager)
    {
        var types = new WorldEntityRegistry();
        CarePackageRules.Register(types);
        manager = new WorldEntityManager(types);
        return manager;
    }

    static PerkTable ShippingTable() =>
        PerkTable.Build(Shipped.Killstreaks.Find(CarePackageRules.StreakId)!.PerkWeights, Shipped.Perks);

    static PerkTable Table(params (string Id, int W)[] weights) =>
        PerkTable.Build(weights.ToDictionary(w => w.Id, w => w.W), Shipped.Perks);

    /// <summary>Drops a crate and lets it fall to the ground. Returns the book and the crate's id.</summary>
    static (CarePackageBook Book, ushort Id) LandedCrate(WorldEntityManager manager, PerkTable? table = null)
    {
        var book = new CarePackageBook(manager, table ?? ShippingTable());
        var id = book.Drop(Owner, Ground)!.Value;
        manager.Tick(CarePackageRules.StartHeightMetres / CarePackageRules.FallSpeedMetresPerSecond + 0.1f);
        return (book, id);
    }

    // ---- weighted perk pick ----

    [Fact]
    public void Shipped_table_has_the_agreed_weights_and_totals_100()
    {
        var entries = ShippingTable().Entries.ToDictionary(e => e.Id, e => e.Weight);
        Assert.Equal(22, entries["fleet_foot"]);
        Assert.Equal(22, entries["quick_hands"]);
        Assert.Equal(16, entries["swift_swap"]);
        Assert.Equal(14, entries["flak_jacket"]);
        Assert.Equal(10, entries["steady_lung"]);
        Assert.Equal(8, entries["tank_hunter"]);
        Assert.Equal(5, entries["ghost"]);
        Assert.Equal(3, entries["hot_streak"]);
        Assert.Equal(8, entries.Count);
        Assert.Equal(100, ShippingTable().Total);
    }

    [Fact]
    public void Better_perks_have_smaller_weights_than_the_common_ones()
    {
        var entries = ShippingTable().Entries.ToDictionary(e => e.Id, e => e.Weight);
        Assert.True(entries["hot_streak"] < entries["ghost"]);
        Assert.True(entries["ghost"] < entries["tank_hunter"]);
        Assert.True(entries["tank_hunter"] < entries["steady_lung"]);
        Assert.True(entries["steady_lung"] < entries["flak_jacket"]);
        Assert.True(entries["fleet_foot"] > entries["hot_streak"]);
    }

    [Theory]
    [InlineData(0, "fleet_foot")]
    [InlineData(21, "fleet_foot")]
    [InlineData(22, "quick_hands")]
    [InlineData(43, "quick_hands")]
    // bands follow perks.json order: fleet 22, quick 22, flak 14, swift 16, tank 8, hot 3, steady 10, ghost 5
    [InlineData(44, "flak_jacket")]
    [InlineData(57, "flak_jacket")]
    [InlineData(58, "swift_swap")]
    [InlineData(73, "swift_swap")]
    [InlineData(74, "tank_hunter")]
    [InlineData(81, "tank_hunter")]
    [InlineData(82, "hot_streak")]
    [InlineData(84, "hot_streak")]
    [InlineData(85, "steady_lung")]
    [InlineData(94, "steady_lung")]
    [InlineData(95, "ghost")]
    [InlineData(99, "ghost")]
    public void A_roll_picks_the_perk_whose_weight_band_it_falls_in(int roll, string expected) =>
        Assert.Equal(expected, ShippingTable().Pick(roll));

    [Fact]
    public void Over_many_rolls_each_perk_comes_up_in_proportion_to_its_weight()
    {
        var table = ShippingTable();
        var counts = new Dictionary<string, int>();
        for (var roll = 0; roll < table.Total; roll++)
        {
            var id = table.Pick(roll);
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        foreach (var (id, w) in table.Entries) Assert.Equal(w, counts[id]);
    }

    [Fact]
    public void Random_picks_stay_in_the_table_and_favour_the_common_perks()
    {
        var table = ShippingTable();
        var rng = new Random(124);
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 20000; i++)
        {
            var id = table.Pick(rng);
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        Assert.Equal(8, counts.Count);
        Assert.True(counts["fleet_foot"] > counts["hot_streak"] * 3, "a 22 weight should come up far more often than a 3");
        Assert.InRange(counts["fleet_foot"] / 20000.0, 0.19, 0.25);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_roll_outside_the_range_is_refused(int roll) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ShippingTable().Pick(roll));

    [Fact]
    public void A_table_with_an_unknown_perk_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Table(("fleet_foot", 1), ("nope", 2)));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void A_table_with_a_zero_weight_or_no_entries_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Table(("fleet_foot", 0)));
        Assert.Throws<InvalidOperationException>(() => PerkTable.Build(new Dictionary<string, int>(), Shipped.Perks));
    }

    [Fact]
    public void Killstreaks_json_gives_weights_to_the_care_package_only()
    {
        foreach (var s in Shipped.Killstreaks.Streaks)
        {
            if (s.Id == CarePackageRules.StreakId) Assert.Equal(8, s.PerkWeights.Count);
            else Assert.Empty(s.PerkWeights);
        }
    }

    /// <summary>A minimal killstreaks.json: the starter UAV and a care package whose tail (its perk weights) the test chooses.</summary>
    static string StreaksJson(string careTail) =>
        "{ \"version\": 1, \"streaks\": [ { \"id\": \"uav\", \"name\": \"UAV\", \"kills_required\": 4, \"unlock_level\": 0, \"control\": \"none\" }, " +
        "{ \"id\": \"care_package\", \"name\": \"Care Package\", \"kills_required\": 6, \"unlock_level\": 6, \"control\": \"none\"" + careTail + " } ] }";

    [Fact]
    public void A_care_package_without_perk_weights_is_refused_by_the_loader()
    {
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson("")));
        Assert.Contains("perk_weights", ex.Message);
    }

    [Fact]
    public void A_perk_weight_below_one_is_refused_by_the_loader()
    {
        Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson(", \"perk_weights\": { \"ghost\": 0 }")));
    }

    // ---- the crate: falling, landing, lifetime ----

    [Fact]
    public void The_crate_falls_on_its_parachute_and_lands_on_the_ground()
    {
        var manager = World(out _);
        var book = new CarePackageBook(manager, ShippingTable());
        var id = book.Drop(Owner, Ground)!.Value;

        var fallen = manager.Snapshot().Single(e => e.Id == id);
        Assert.Equal(CarePackageRules.StateFalling, fallen.State.State);
        Assert.Equal(Ground.Y + CarePackageRules.StartHeightMetres, fallen.State.Position.Y, 0.05f);

        manager.Tick(2f);
        Assert.Equal(CarePackageRules.StateFalling, manager.Snapshot().Single(e => e.Id == id).State.State);

        manager.Tick(10f);
        var landed = manager.Snapshot().Single(e => e.Id == id).State;
        Assert.Equal(CarePackageRules.StateLanded, landed.State);
        Assert.Equal(Ground.Y, landed.Position.Y, 0.05f);
        Assert.Equal(Ground.X, landed.Position.X, 0.05f);
    }

    [Fact]
    public void An_unopened_crate_disappears_after_sixty_seconds()
    {
        var manager = World(out _);
        var reasons = new List<WorldDespawnReason>();
        manager.Despawned += d => reasons.Add(d.Reason);
        var book = new CarePackageBook(manager, ShippingTable());
        book.Drop(Owner, Ground);

        manager.Tick(59.5f);
        Assert.Equal(1, book.Count);
        manager.Tick(1f);
        Assert.Equal(0, book.Count);
        Assert.Equal(WorldDespawnReason.Expired, reasons.Single());
    }

    // ---- opening ----

    [Fact]
    public void A_crate_still_on_its_parachute_cannot_be_opened()
    {
        var manager = World(out _);
        var book = new CarePackageBook(manager, ShippingTable());
        var id = book.Drop(Owner, Ground)!.Value;
        manager.Tick(1f);

        Assert.Equal(OpenResult.NotLanded, book.TryOpen(id, Ground, new Random(1), out var perk));
        Assert.Null(perk);
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void A_player_out_of_reach_cannot_open_a_landed_crate()
    {
        var manager = World(out _);
        var (book, id) = LandedCrate(manager);
        var far = Ground + new Vector3(CarePackageRules.ReachMetres + CarePackageRules.LatencySlackMetres + 0.5f, 0, 0);

        Assert.Equal(OpenResult.OutOfReach, book.TryOpen(id, far, new Random(1), out _));
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void A_player_within_reach_opens_the_crate_once_and_it_is_gone()
    {
        var manager = World(out _);
        var reasons = new List<WorldDespawnReason>();
        manager.Despawned += d => reasons.Add(d.Reason);
        var (book, id) = LandedCrate(manager);
        var near = Ground + new Vector3(CarePackageRules.ReachMetres - 0.5f, 0, 0);

        Assert.Equal(OpenResult.Ok, book.TryOpen(id, near, new Random(7), out var perk));
        Assert.NotNull(perk);
        Assert.Equal(0, book.Count);
        Assert.Equal(WorldDespawnReason.Opened, reasons.Single());
        Assert.Equal(OpenResult.Gone, book.TryOpen(id, near, new Random(7), out _));
    }

    [Fact]
    public void The_perk_an_opening_grants_is_drawn_from_the_table()
    {
        var manager = World(out _);
        var (book, id) = LandedCrate(manager, Table(("ghost", 1)));
        Assert.Equal(OpenResult.Ok, book.TryOpen(id, Ground, new Random(3), out var perk));
        Assert.Equal("ghost", perk);
    }

    [Fact]
    public void A_crate_takes_no_damage_and_keeps_its_owner()
    {
        var manager = World(out _);
        var (book, id) = LandedCrate(manager);
        manager.Damage(id, 500f, Opener);
        Assert.Equal(1, book.Count);
        Assert.Equal(Owner, manager.Snapshot().Single(e => e.Id == id).State.Owner);
    }

    // ---- the perk it grants ----

    [Fact]
    public void A_granted_perk_replaces_the_loadout_perk_without_stacking()
    {
        var loadout = Shipped.Perks.Find("quick_hands")!;
        var granted = Shipped.Perks.Find("fleet_foot")!;
        // the opener's values are the granted perk's alone: the loadout perk's reload bonus is gone
        var replaced = PerkModifiers.Of(granted);
        Assert.Equal(1.12f, replaced.RunSpeed);
        Assert.Equal(1f, replaced.ReloadSpeed);
        Assert.NotEqual(PerkModifiers.Of(loadout).ReloadSpeed, replaced.ReloadSpeed);
    }

    [Fact]
    public void A_hot_streak_perk_gained_mid_life_lowers_the_next_thresholds()
    {
        var killstreaks = Shipped.Killstreaks;
        var sent = new List<(ushort Owner, EventKind Kind)>();
        var service = new KillstreakService(killstreaks,
            killstreaks.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)),
            (o, k, _) => sent.Add((o, k)), _ => { });
        service.Arm(Opener, new[] { CarePackageRules.StreakId, "", "" }, 0f);
        for (var i = 0; i < 5; i++) service.OnKill(Opener, 99, false);
        Assert.Empty(service.Held(Opener)); // threshold 6 without the perk

        service.SetCostReduction(Opener, 1f);
        service.OnKill(Opener, 99, false);
        Assert.Contains(CarePackageRules.StreakId, service.Held(Opener)); // threshold 5 now
    }

    [Fact]
    public void A_perk_gained_by_one_player_does_not_change_another_players_thresholds()
    {
        var service = new KillstreakService(Shipped.Killstreaks,
            Shipped.Killstreaks.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)), (_, _, _) => { }, _ => { });
        service.Arm(Owner, new[] { CarePackageRules.StreakId, "", "" }, 0f);
        service.SetCostReduction(Opener, 1f); // opener not armed: no effect on another player
        for (var i = 0; i < 5; i++) service.OnKill(Owner, 99, false);
        Assert.Empty(service.Held(Owner));
    }

    // ---- the streak: drop ahead of the owner ----

    [Fact]
    public void The_drop_point_is_five_metres_ahead_of_the_owner()
    {
        var ahead = CarePackageRules.DropPoint(new Vector3(0, 0, 0), 0f);
        Assert.Equal(0f, ahead.X, 4);
        Assert.Equal(-CarePackageRules.DropDistanceMetres, ahead.Z, 4);

        var right = CarePackageRules.DropPoint(new Vector3(0, 0, 0), MathF.PI / 2);
        Assert.Equal(-CarePackageRules.DropDistanceMetres, right.X, 4);
        Assert.Equal(0f, right.Z, 4);
    }

    [Fact]
    public void Using_the_streak_drops_a_crate_at_the_landing_point_and_ends_the_use()
    {
        var manager = World(out _);
        var book = new CarePackageBook(manager, ShippingTable());
        var streak = new CarePackageStreak(book, _ => 0f, p => 2f);
        var killstreaks = Shipped.Killstreaks;
        var service = new KillstreakService(killstreaks,
            killstreaks.Streaks.Select(s => s.Id == CarePackageRules.StreakId ? streak : (IKillstreak)new LoggingKillstreak(s.Id)),
            (_, _, _) => { }, _ => { });
        service.Arm(Owner, new[] { CarePackageRules.StreakId, "", "" }, 0f);
        for (var i = 0; i < 6; i++) service.OnKill(Owner, 99, false);
        Assert.Contains(CarePackageRules.StreakId, service.Held(Owner));

        Assert.True(service.Activate(Owner, 0, new Vector3(0, 0, 0)));
        Assert.Equal(1, book.Count);
        Assert.Empty(service.Held(Owner));
        Assert.Equal(0, service.ActiveCount(Owner, CarePackageRules.StreakId)); // the crate lives on, the streak does not
        var crate = manager.Snapshot().Single();
        Assert.Equal(2f + CarePackageRules.StartHeightMetres, crate.State.Position.Y, 0.05f);
        Assert.Equal(-CarePackageRules.DropDistanceMetres, crate.State.Position.Z, 0.05f);
    }

    [Fact]
    public void With_no_ground_under_the_drop_point_the_streak_is_kept()
    {
        var manager = World(out _);
        var book = new CarePackageBook(manager, ShippingTable());
        var streak = new CarePackageStreak(book, _ => 0f, _ => null);
        var killstreaks = Shipped.Killstreaks;
        var service = new KillstreakService(killstreaks,
            killstreaks.Streaks.Select(s => s.Id == CarePackageRules.StreakId ? streak : (IKillstreak)new LoggingKillstreak(s.Id)),
            (_, _, _) => { }, _ => { });
        service.Arm(Owner, new[] { CarePackageRules.StreakId, "", "" }, 0f);
        for (var i = 0; i < 6; i++) service.OnKill(Owner, 99, false);

        Assert.False(service.Activate(Owner, 0, Vector3.Zero));
        Assert.Equal(0, book.Count);
        Assert.Contains(CarePackageRules.StreakId, service.Held(Owner));
    }

    // ---- wire payloads ----

    [Fact]
    public void The_open_request_round_trips()
    {
        Assert.True(CarePackageCodec.TryDecodeOpen(CarePackageCodec.EncodeOpen(513), out var crate));
        Assert.Equal(513, crate);
        Assert.False(CarePackageCodec.TryDecodeOpen(new byte[3], out _));
    }

    [Fact]
    public void The_perk_event_round_trips_and_rejects_bad_lengths()
    {
        Assert.Equal("fleet_foot", CarePackageCodec.DecodePerk(CarePackageCodec.EncodePerk("fleet_foot")));
        Assert.Null(CarePackageCodec.DecodePerk(new byte[] { 5, (byte)'a' }));
        Assert.Null(CarePackageCodec.DecodePerk(Array.Empty<byte>()));
    }
}
