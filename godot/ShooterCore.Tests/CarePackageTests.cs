using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Ticket #124 and #151: the care package crate, its weighted reward table (killstreaks.json "streak_weights"), opening
/// (the crate despawns at once) and the reward streak it grants into the held reward slots.
/// </summary>
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

    static StreakRewards ShippingRewards() => Shipped.Killstreaks.Rewards;

    /// <summary>Drops a crate and lets it fall to the ground. Returns the book and the crate's id.</summary>
    static (CarePackageBook Book, ushort Id) LandedCrate(WorldEntityManager manager, StreakRewards? rewards = null)
    {
        var book = new CarePackageBook(manager, rewards ?? ShippingRewards());
        var id = book.Drop(Owner, Ground)!.Value;
        manager.Tick(CarePackageRules.StartHeightMetres / CarePackageRules.FallSpeedMetresPerSecond + 0.1f);
        return (book, id);
    }

    /// <summary>A killstreak service over the shipped table; every streak is a logging stub, the care package is the real one when given.</summary>
    static KillstreakService Service(List<(ushort Owner, EventKind Kind, byte[] Payload)> sent, IKillstreak? care = null)
    {
        var killstreaks = Shipped.Killstreaks;
        return new KillstreakService(killstreaks,
            killstreaks.Streaks.Select(s => s.Id == CarePackageRules.StreakId && care != null ? care : new LoggingKillstreak(s.Id)),
            (o, k, p) => sent.Add((o, k, p)), _ => { });
    }

    // ---- the reward table ----

    [Fact]
    public void Shipped_rewards_are_the_agreed_weights_and_total_97()
    {
        var entries = ShippingRewards().Entries.ToDictionary(e => e.Id, e => e.Weight);
        Assert.Equal(30, entries["uav"]);
        Assert.Equal(22, entries["attack_missile"]);
        Assert.Equal(18, entries["turret"]);
        Assert.Equal(10, entries["helicopter_helper"]);
        Assert.Equal(9, entries["attack_helicopter"]);
        Assert.Equal(6, entries["ac130"]);
        Assert.Equal(2, entries["moab"]);
        Assert.Equal(7, entries.Count);
        Assert.Equal(97, ShippingRewards().Total);
    }

    [Fact]
    public void The_care_package_never_rewards_another_care_package()
    {
        Assert.DoesNotContain(CarePackageRules.StreakId, ShippingRewards().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Better_streaks_have_smaller_weights()
    {
        var entries = ShippingRewards().Entries.ToDictionary(e => e.Id, e => e.Weight);
        Assert.True(entries["moab"] < entries["ac130"]);
        Assert.True(entries["ac130"] < entries["attack_helicopter"]);
        Assert.True(entries["attack_helicopter"] < entries["helicopter_helper"]);
        Assert.True(entries["helicopter_helper"] < entries["turret"]);
        Assert.True(entries["turret"] < entries["attack_missile"]);
        Assert.True(entries["attack_missile"] < entries["uav"]);
    }

    [Theory]
    [InlineData(0, "uav")]
    [InlineData(29, "uav")]
    [InlineData(30, "attack_missile")]
    [InlineData(51, "attack_missile")]
    [InlineData(52, "turret")]
    [InlineData(69, "turret")]
    // bands follow killstreaks.json order: uav 30, attack missile 22, turret 18, attack helicopter 9, helicopter helper 10, ac130 6, moab 2
    [InlineData(70, "attack_helicopter")]
    [InlineData(78, "attack_helicopter")]
    [InlineData(79, "helicopter_helper")]
    [InlineData(88, "helicopter_helper")]
    [InlineData(89, "ac130")]
    [InlineData(94, "ac130")]
    [InlineData(95, "moab")]
    [InlineData(96, "moab")]
    public void A_roll_picks_the_streak_whose_weight_band_it_falls_in(int roll, string expected) =>
        Assert.Equal(expected, ShippingRewards().Pick(roll));

    [Fact]
    public void Over_every_roll_each_streak_comes_up_in_proportion_to_its_weight()
    {
        var rewards = ShippingRewards();
        var counts = new Dictionary<string, int>();
        for (var roll = 0; roll < rewards.Total; roll++)
        {
            var id = rewards.Pick(roll);
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        foreach (var (id, w) in rewards.Entries) Assert.Equal(w, counts[id]);
    }

    [Fact]
    public void Random_picks_favour_the_common_streaks_and_never_give_a_care_package()
    {
        var rewards = ShippingRewards();
        var rng = new Random(151);
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 20000; i++)
        {
            var id = rewards.Pick(rng);
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        Assert.Equal(7, counts.Count);
        Assert.DoesNotContain(CarePackageRules.StreakId, counts.Keys);
        Assert.True(counts["uav"] > counts["moab"] * 5, "a 30 weight should come up far more often than a 2");
        Assert.InRange(counts["uav"] / 20000.0, 0.28, 0.34);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(97)]
    public void A_roll_outside_the_range_is_refused(int roll) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ShippingRewards().Pick(roll));

    // ---- the loader checks streak_weights ----

    /// <summary>A minimal killstreaks.json with two streaks, the top-level <paramref name="weights"/> object and the care package's tail.</summary>
    static string StreaksJson(string weights, string careTail = "") =>
        "{ \"version\": 1" + (weights == "" ? "" : ", " + weights) + ", \"streaks\": [ " +
        "{ \"id\": \"uav\", \"name\": \"UAV\", \"kills_required\": 4, \"unlock_level\": 0, \"control\": \"none\" }, " +
        "{ \"id\": \"care_package\", \"name\": \"Care Package\", \"kills_required\": 6, \"unlock_level\": 6, \"control\": \"none\"" + careTail + " } ] }";

    [Fact]
    public void A_valid_table_loads_and_keeps_the_file_order()
    {
        var file = KillstreakFile.Parse(StreaksJson("\"streak_weights\": { \"uav\": 3 }"));
        Assert.Equal(3, file.Rewards.Total);
        Assert.Equal("uav", file.Rewards.Pick(2));
    }

    [Fact]
    public void A_file_without_streak_weights_is_refused()
    {
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson("")));
        Assert.Contains("streak_weights", ex.Message);
    }

    [Fact]
    public void An_empty_streak_weights_table_is_refused()
    {
        Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson("\"streak_weights\": {}")));
    }

    [Fact]
    public void A_care_package_in_streak_weights_is_refused()
    {
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson("\"streak_weights\": { \"uav\": 1, \"care_package\": 1 }")));
        Assert.Contains("care package", ex.Message);
    }

    [Fact]
    public void A_streak_weights_id_that_is_not_a_streak_is_refused()
    {
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson("\"streak_weights\": { \"uav\": 1, \"nuke\": 1 }")));
        Assert.Contains("nuke", ex.Message);
    }

    [Fact]
    public void A_streak_weight_below_one_is_refused()
    {
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(StreaksJson("\"streak_weights\": { \"uav\": 0 }")));
        Assert.Contains("uav", ex.Message);
    }

    [Fact]
    public void The_retired_per_care_package_perk_weights_are_not_read()
    {
        // the old perk_weights key on the care package is ignored; the table above is what counts
        var file = KillstreakFile.Parse(StreaksJson("\"streak_weights\": { \"uav\": 1 }", ", \"perk_weights\": { \"ghost\": 5 }"));
        Assert.Equal(1, file.Rewards.Total);
    }

    // ---- the crate: falling, landing, lifetime ----

    [Fact]
    public void The_crate_falls_on_its_parachute_and_lands_on_the_ground()
    {
        var manager = World(out _);
        var book = new CarePackageBook(manager, ShippingRewards());
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
        var book = new CarePackageBook(manager, ShippingRewards());
        book.Drop(Owner, Ground);

        manager.Tick(59.5f);
        Assert.Equal(1, book.Count);
        manager.Tick(1f);
        Assert.Equal(0, book.Count);
        Assert.Equal(WorldDespawnReason.Expired, reasons.Single());
    }

    // ---- opening: the crate is gone at once ----

    [Fact]
    public void A_crate_still_on_its_parachute_cannot_be_opened()
    {
        var manager = World(out _);
        var book = new CarePackageBook(manager, ShippingRewards());
        var id = book.Drop(Owner, Ground)!.Value;
        manager.Tick(1f);

        Assert.Equal(OpenResult.NotLanded, book.TryOpen(id, Ground, new Random(1), out var streak));
        Assert.Null(streak);
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
    public void Opening_despawns_the_crate_at_once_with_reason_opened_and_grants_a_streak()
    {
        var manager = World(out _);
        var reasons = new List<WorldDespawnReason>();
        manager.Despawned += d => reasons.Add(d.Reason);
        var (book, id) = LandedCrate(manager);
        var near = Ground + new Vector3(CarePackageRules.ReachMetres - 0.5f, 0, 0);

        Assert.Equal(OpenResult.Ok, book.TryOpen(id, near, new Random(7), out var streak));
        Assert.NotNull(streak);
        Assert.Equal(0, book.Count);
        Assert.Empty(manager.Snapshot());
        Assert.Equal(WorldDespawnReason.Opened, reasons.Single());
        Assert.Equal(OpenResult.Gone, book.TryOpen(id, near, new Random(7), out _));
    }

    [Fact]
    public void The_streak_an_opening_grants_is_drawn_from_the_table()
    {
        var manager = World(out _);
        var (book, id) = LandedCrate(manager, new StreakRewards(new[] { ("turret", 1) }));
        Assert.Equal(OpenResult.Ok, book.TryOpen(id, Ground, new Random(3), out var streak));
        Assert.Equal("turret", streak);
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

    // ---- the reward: held slots, the counter, replacement ----

    [Fact]
    public void A_reward_goes_into_a_held_slot_at_once_and_does_not_touch_the_counter()
    {
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var service = Service(sent);
        service.Arm(Opener, new[] { "", "", "" }, 0f);
        service.OnKill(Opener, 99, false);
        service.OnKill(Opener, 99, false);

        var grant = service.Grant(Opener, "ac130");

        Assert.NotNull(grant);
        Assert.Null(grant!.Replaced);
        Assert.Equal(new[] { "ac130" }, service.HeldRewards(Opener));
        Assert.Equal(2, service.KillCount(Opener));
        var (o, kind, payload) = sent.Single();
        Assert.Equal(Opener, o);
        Assert.Equal(EventKind.StreakGranted, kind);
        var info = KillstreakEvents.DecodeGranted(payload);
        Assert.Equal("ac130", info!.StreakId);
        Assert.Null(info.ReplacedId);
    }

    [Fact]
    public void A_full_set_of_reward_slots_replaces_the_oldest_reward_and_says_so()
    {
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var service = Service(sent);
        service.Arm(Opener, new[] { "", "", "" }, 0f);
        service.Grant(Opener, "uav");
        service.Grant(Opener, "turret");
        service.Grant(Opener, "moab");

        var grant = service.Grant(Opener, "ac130");

        Assert.Equal("uav", grant!.Replaced);
        Assert.Equal(new[] { "turret", "moab", "ac130" }, service.HeldRewards(Opener));
        var info = KillstreakEvents.DecodeGranted(sent.Last().Payload);
        Assert.Equal("ac130", info!.StreakId);
        Assert.Equal("uav", info.ReplacedId);
    }

    [Fact]
    public void A_reward_is_used_with_the_activation_key_and_leaves_its_slot()
    {
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var service = Service(sent);
        service.Arm(Opener, new[] { "", "", "" }, 0f);
        service.Grant(Opener, "turret");

        Assert.True(service.Activate(Opener, 0, Vector3.Zero));
        Assert.Empty(service.HeldRewards(Opener));
        Assert.Contains(sent, s => s.Kind == EventKind.StreakUsed);
        Assert.False(service.Activate(Opener, 0, Vector3.Zero)); // nothing left to use
    }

    [Fact]
    public void Rewards_are_used_after_the_loadout_streaks()
    {
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var service = Service(sent);
        service.Arm(Opener, new[] { "uav", "", "" }, 0f);
        for (var i = 0; i < 4; i++) service.OnKill(Opener, 99, false);
        service.Grant(Opener, "turret");

        service.Activate(Opener, 0, Vector3.Zero);
        Assert.Empty(service.Held(Opener));
        Assert.Equal(new[] { "turret" }, service.HeldRewards(Opener));
    }

    [Fact]
    public void A_death_loses_the_held_rewards()
    {
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var service = Service(sent);
        service.Arm(Opener, new[] { "", "", "" }, 0f);
        service.Grant(Opener, "uav");

        service.OnDeath(Opener);

        Assert.Empty(service.HeldRewards(Opener));
    }

    [Fact]
    public void A_reward_for_a_player_without_a_life_is_refused()
    {
        var service = Service(new List<(ushort Owner, EventKind Kind, byte[] Payload)>());
        Assert.Null(service.Grant(Opener, "uav"));
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
        var book = new CarePackageBook(manager, ShippingRewards());
        var care = new CarePackageStreak(book, _ => 0f, p => 2f);
        var service = Service(new List<(ushort Owner, EventKind Kind, byte[] Payload)>(), care);
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
        var book = new CarePackageBook(manager, ShippingRewards());
        var care = new CarePackageStreak(book, _ => 0f, _ => null);
        var service = Service(new List<(ushort Owner, EventKind Kind, byte[] Payload)>(), care);
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
    public void The_granted_event_round_trips_with_and_without_a_replaced_reward()
    {
        var plain = KillstreakEvents.DecodeGranted(KillstreakEvents.EncodeGranted("moab", null));
        Assert.Equal(new StreakGrantedInfo("moab", null), plain);

        var replaced = KillstreakEvents.DecodeGranted(KillstreakEvents.EncodeGranted("ac130", "uav"));
        Assert.Equal(new StreakGrantedInfo("ac130", "uav"), replaced);

        Assert.Null(KillstreakEvents.DecodeGranted(new byte[] { 5, (byte)'a' }));
        Assert.Null(KillstreakEvents.DecodeGranted(Array.Empty<byte>()));
    }
}
