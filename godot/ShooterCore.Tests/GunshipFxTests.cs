using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// Ticket #159: the gunship FX is coalesced per server tick. These tests cover the codecs (round trips and malformed payloads), the
/// caps (tracers, impacts with kills always kept, blasts), the batch's flush order, the hit-marker flags, a byte budget for a full-rate
/// AC-130 burst driven by the real turret cadence, and the gunship rocket's own 8 m blast (killstreaks.json) against the bazooka's 6 m.
/// </summary>
public class GunshipFxTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    const ushort Owner = 7;

    /// <summary>Per-packet cost on top of each event's bytes: the UDP and ENet headers and the channel header (a conservative 40).</summary>
    const int PerPacketBytes = 40;
    /// <summary>The budget for one gunner's full-rate AC-130 burst, bytes per second, all its messages and packet overhead included.</summary>
    const int ByteBudgetPerSecond = 6000;

    static GunshipTracer Tracer(byte weapon, float x) => new(weapon, new Vector3(x, 30, 0), new Vector3(x + 1, 0, 0), 1500f, 14f);

    static GunshipImpactFx Impact(ImpactSize size, bool kill, float x = 0) => new(new ImpactInfo(new Vector3(x, 0, 0), SurfaceKind.Ground, size), kill);

    // --- codecs --------------------------------------------------------------------------------------------

    [Fact]
    public void Shots_round_trip_and_the_empty_case_too()
    {
        var m = new GunshipShotsMsg(Owner, new[] { Tracer(3, 1), Tracer(4, 2.5f) });
        var bytes = GunshipFxCodec.EncodeShots(m);
        Assert.Equal(3 + 2 * GunshipFxCodec.TracerBytes, bytes.Length);
        Assert.Equal(m.Tracers.ToList(), GunshipFxCodec.DecodeShots(bytes)!.Tracers.ToList());
        Assert.Equal(Owner, GunshipFxCodec.DecodeShots(bytes)!.Owner);

        var empty = GunshipFxCodec.DecodeShots(GunshipFxCodec.EncodeShots(new GunshipShotsMsg(Owner, Array.Empty<GunshipTracer>())));
        Assert.Empty(empty!.Tracers);
    }

    [Fact]
    public void Shots_reject_a_bad_length_and_a_count_over_the_cap()
    {
        var bytes = GunshipFxCodec.EncodeShots(new GunshipShotsMsg(Owner, new[] { Tracer(1, 0) }));
        Assert.Null(GunshipFxCodec.DecodeShots(bytes.AsSpan(0, bytes.Length - 1)));
        var over = new byte[3 + 9 * GunshipFxCodec.TracerBytes];
        over[2] = 9; // nine tracers is over the cap of eight, whatever the length says
        Assert.Null(GunshipFxCodec.DecodeShots(over));
        Assert.Throws<ArgumentException>(() => GunshipFxCodec.EncodeShots(new GunshipShotsMsg(Owner, Enumerable.Range(0, 9).Select(i => Tracer(1, i)).ToList())));
    }

    [Fact]
    public void Impacts_round_trip_with_the_kill_bit_and_reject_bad_surfaces()
    {
        var list = new List<GunshipImpactFx> { Impact(ImpactSize.Plume, false, 1), Impact(ImpactSize.Puff, true, 2) };
        var bytes = GunshipFxCodec.EncodeImpacts(list);
        Assert.Equal(1 + 2 * GunshipFxCodec.ImpactBytes, bytes.Length);
        Assert.Equal(list, GunshipFxCodec.DecodeImpacts(bytes));

        var badSurface = GunshipFxCodec.EncodeImpacts(list);
        badSurface[1 + 12] = 99;
        Assert.Null(GunshipFxCodec.DecodeImpacts(badSurface));
        var badSize = GunshipFxCodec.EncodeImpacts(list);
        badSize[1 + 13] = 0x05; // size 5 is not a size
        Assert.Null(GunshipFxCodec.DecodeImpacts(badSize));
        Assert.Null(GunshipFxCodec.DecodeImpacts(bytes.AsSpan(0, bytes.Length - 3)));
    }

    [Fact]
    public void Blasts_round_trip_with_their_hit_flags()
    {
        var m = new GunshipBlastsMsg(Owner, GunshipHitFlags.Hit | GunshipHitFlags.Kill, new[] { new GunshipBlast(new Vector3(1, 2, 3), 14f), new GunshipBlast(new Vector3(-4, 0, 9), 3f) });
        var bytes = GunshipFxCodec.EncodeBlasts(m);
        Assert.Equal(4 + 2 * GunshipFxCodec.BlastBytes, bytes.Length);
        var d = GunshipFxCodec.DecodeBlasts(bytes);
        Assert.Equal(Owner, d!.Owner);
        Assert.Equal(m.Hits, d.Hits);
        Assert.Equal(m.Blasts.ToList(), d.Blasts.ToList());

        var badFlags = GunshipFxCodec.EncodeBlasts(m);
        badFlags[2] = 0x40; // not a hit flag
        Assert.Null(GunshipFxCodec.DecodeBlasts(badFlags));
        Assert.Null(GunshipFxCodec.DecodeBlasts(bytes.AsSpan(0, 5)));
    }

    [Fact]
    public void Explosions_carry_their_blast_radius()
    {
        var e = new ExplosionMsg(0x8002, Owner, 5, new Vector3(1, 2, 3), 8f);
        Assert.Equal(e, WeaponCodec.DecodeExplosion(WeaponCodec.Encode(e)));
        Assert.Equal(21, WeaponCodec.Encode(e).Length);
        Assert.Null(WeaponCodec.DecodeExplosion(WeaponCodec.Encode(e).AsSpan(0, 17).ToArray()));
    }

    // --- caps and selection -------------------------------------------------------------------------------

    [Fact]
    public void A_gunner_keeps_its_first_eight_tracers_in_firing_order()
    {
        var rounds = Enumerable.Range(0, 10).Select(i => Tracer(1, i)).ToList();
        var kept = GunshipFxSelect.Tracers(rounds);
        Assert.Equal(GunshipFxLimits.MaxTracersPerTick, kept.Count);
        Assert.Equal(rounds.Take(8).ToList(), kept);
    }

    [Fact]
    public void Impacts_keep_every_kill_and_the_biggest_of_the_rest()
    {
        var list = new List<GunshipImpactFx>();
        for (var i = 0; i < 7; i++) list.Add(Impact(ImpactSize.Puff, false, i));
        list.Add(Impact(ImpactSize.Burst, false, 10));
        list.Add(Impact(ImpactSize.Plume, false, 11));
        list.Add(Impact(ImpactSize.Puff, true, 12)); // a kill: always kept

        var kept = GunshipFxSelect.Impacts(list);
        Assert.Equal(GunshipFxLimits.MaxImpactsPerTick, kept.Count);
        Assert.Contains(kept, i => i.Kill);
        Assert.Contains(kept, i => i.Impact.Size == ImpactSize.Plume);
        Assert.Contains(kept, i => i.Impact.Size == ImpactSize.Burst);
        Assert.Equal(3, kept.Count(i => !i.Kill && i.Impact.Size == ImpactSize.Puff)); // the smallest are the ones dropped
    }

    [Fact]
    public void A_kill_is_never_dropped_even_past_the_cap()
    {
        var list = new List<GunshipImpactFx>();
        for (var i = 0; i < 8; i++) list.Add(Impact(ImpactSize.Plume, true, i)); // eight kills
        for (var i = 0; i < 3; i++) list.Add(Impact(ImpactSize.Plume, false, 20 + i));
        var kept = GunshipFxSelect.Impacts(list);
        Assert.Equal(8, kept.Count);
        Assert.All(kept, i => Assert.True(i.Kill));
    }

    [Fact]
    public void Blasts_keep_the_biggest_up_to_the_cap()
    {
        var blasts = Enumerable.Range(1, 10).Select(r => new GunshipBlast(Vector3.Zero, r)).ToList();
        var kept = GunshipFxSelect.Blasts(blasts);
        Assert.Equal(GunshipFxLimits.MaxBlastsPerTick, kept.Count);
        Assert.Equal(Enumerable.Range(3, 8).Reverse().Select(r => (float)r), kept.Select(b => b.Radius));
    }

    // --- the tick's batch ---------------------------------------------------------------------------------

    [Fact]
    public void A_tick_flushes_shots_then_impacts_then_blasts_and_then_is_empty()
    {
        var batch = new GunshipFxBatch();
        batch.Tracer(1, Tracer(2, 0));
        batch.Tracer(5, Tracer(3, 0));
        batch.Impact(Impact(ImpactSize.Burst, false));
        batch.Blast(1, new GunshipBlast(Vector3.Zero, 3f));
        batch.Hit(1, headshot: false, kill: false);

        var msgs = batch.Flush();
        Assert.Equal(new[] { (byte)EventKind.GunshipShots, (byte)EventKind.GunshipShots, (byte)EventKind.GunshipImpacts, (byte)EventKind.GunshipBlasts },
            msgs.Select(m => m.Kind).ToArray());
        Assert.Equal(1, GunshipFxCodec.DecodeShots(msgs[0].Payload)!.Owner);
        Assert.Equal(5, GunshipFxCodec.DecodeShots(msgs[1].Payload)!.Owner);
        Assert.Single(GunshipFxCodec.DecodeImpacts(msgs[2].Payload)!);
        Assert.Equal(GunshipHitFlags.Hit, GunshipFxCodec.DecodeBlasts(msgs[3].Payload)!.Hits);
        Assert.Empty(batch.Flush());
    }

    [Fact]
    public void The_hit_marker_flags_add_up_over_the_tick()
    {
        var batch = new GunshipFxBatch();
        batch.Hit(Owner, headshot: false, kill: false);
        batch.Hit(Owner, headshot: true, kill: false);
        batch.Hit(Owner, headshot: false, kill: true);
        var blasts = GunshipFxCodec.DecodeBlasts(batch.Flush().Single().Payload);
        Assert.Equal(GunshipHitFlags.Hit | GunshipHitFlags.Headshot | GunshipHitFlags.Kill, blasts!.Hits);
        Assert.Empty(blasts.Blasts);
    }

    [Fact]
    public void A_stall_of_eight_rounds_is_one_shots_message()
    {
        var batch = new GunshipFxBatch();
        for (var i = 0; i < 8; i++) batch.Tracer(Owner, Tracer(1, i));
        var msgs = batch.Flush();
        Assert.Single(msgs, m => m.Kind == (byte)EventKind.GunshipShots);
        Assert.Equal(8, GunshipFxCodec.DecodeShots(msgs.Single(m => m.Kind == (byte)EventKind.GunshipShots).Payload)!.Tracers.Count);
    }

    // --- byte budget --------------------------------------------------------------------------------------

    /// <summary>
    /// One second of the AC-130's 25 mm held down, at the server's tick rate: the turret's own cadence fires the rounds, each round is
    /// batched (tracer, impact and a 3 m blast, as the server does), and every flushed message is counted with its packet overhead.
    /// </summary>
    [Fact]
    public void A_full_rate_ac130_burst_stays_under_the_byte_budget_per_gunner()
    {
        var ac130 = Shipped.Killstreaks.Ac130!;
        var aircraft = new Aircraft(Shipped.Killstreaks.AircraftKinds[1], 1, Vector3.Zero, 0, true, Owner);
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(ac130, aircraft, shots.Add, _ => { }, new Random(1));
        var batch = new GunshipFxBatch();
        var bytes = 0;
        var rounds = 0;
        var fire = GunnerInputs.Look(control, 0f, -0.5f, MoveButtons.Fire);

        for (var tick = 0; tick < Wire.TickRate; tick++)
        {
            shots.Clear();
            control.OnInput(fire, 1.0 + tick / (double)Wire.TickRate);
            foreach (var s in shots)
            {
                rounds++;
                var end = s.Origin + s.Direction * 200f;
                batch.Tracer(Owner, new GunshipTracer((byte)s.WeaponIndex, s.Origin, end, 1500f, 14f));
                batch.Impact(new GunshipImpactFx(new ImpactInfo(end, SurfaceKind.Ground, ImpactSize.Puff), false));
                batch.Blast(Owner, new GunshipBlast(end, 3f));
            }
            foreach (var m in batch.Flush()) bytes += Wire.Encode(m).Length + PerPacketBytes;
        }

        Assert.InRange(rounds, 24, 26); // 25 mm at 0.04 s: about 25 rounds in the second
        Assert.True(bytes < ByteBudgetPerSecond, $"{bytes} bytes in one second for {rounds} rounds, budget {ByteBudgetPerSecond}");
    }

    // --- the gunship rocket's blast --------------------------------------------------------------------------

    [Fact]
    public void Gunship_rockets_carry_their_own_8_metre_blast_and_the_bazooka_keeps_6()
    {
        var rockets = Shipped.Killstreaks.Gunship!.Weapons.Single(w => w.Id == "rockets");
        Assert.Equal(8f, rockets.Splash!.Radius);
        var bazooka = Shipped.Weapons.Weapons.Single(w => w.Id == "bazooka");
        Assert.Equal(6f, bazooka.Splash!.Radius);
    }

    [Fact]
    public void A_gunship_rocket_hurts_at_7_metres_and_a_bazooka_rocket_does_not()
    {
        var rockets = Shipped.Killstreaks.Gunship!.Weapons.Single(w => w.Id == "rockets");
        var bazooka = Shipped.Weapons.Weapons.Single(w => w.Id == "bazooka");
        var gunship = ProjectileMath.WithBlast(bazooka, rockets.Splash);
        Assert.True(ProjectileMath.SplashDamage(gunship, 7f, false) > 0);
        Assert.Equal(0f, ProjectileMath.SplashDamage(bazooka, 7f, false));
        Assert.Equal(0f, ProjectileMath.SplashDamage(gunship, 8f, false)); // the edge of the 8 m blast
        Assert.Same(bazooka, ProjectileMath.WithBlast(bazooka, null));       // no override: the weapon's own blast
    }
}
