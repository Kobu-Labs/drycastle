using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class EngineSoundTests
{
    const float Top = 200f, Dt = 1f / 60f;

    /// <summary>Runs the model with a speed that changes by <paramref name="rate"/> km/h per second for <paramref name="seconds"/>.</summary>
    static float Run(EngineSoundModel m, float from, float rate, float seconds, Action<EngineSoundModel>? each = null)
    {
        var v = from;
        for (var t = 0f; t < seconds; t += Dt)
        {
            v = Math.Max(0, Math.Min(Top, v + rate * Dt));
            m.Step(v, Top, Dt);
            each?.Invoke(m);
        }
        return v;
    }

    [Fact]
    public void Standing_idles()
    {
        var m = new EngineSoundModel();
        Run(m, 0, 0, 1f);
        Assert.Equal(EnginePhase.Idle, m.Phase);
    }

    [Fact]
    public void Pulling_away_accelerates_then_holds_top_speed()
    {
        var m = new EngineSoundModel();
        var v = Run(m, 0, 30, 3f);
        Assert.Equal(EnginePhase.Accelerating, m.Phase);
        v = Run(m, v, 10, 12f);             // creeping up the last bit
        Assert.Equal(Top, v);
        Run(m, v, 0, 1f);
        Assert.Equal(EnginePhase.TopSpeed, m.Phase);
    }

    [Fact]
    public void Coming_off_the_gas_coasts_and_fires_the_lift_once()
    {
        var m = new EngineSoundModel();
        var v = Run(m, 0, 30, 3f);
        var lifts = 0;
        Run(m, v, -4, 3f, x => { if (x.TakeLift()) lifts++; });
        Assert.Equal(EnginePhase.Coasting, m.Phase);
        Assert.Equal(1, lifts);
    }

    [Fact]
    public void Lifting_after_a_blip_or_at_a_crawl_is_silent()
    {
        var m = new EngineSoundModel();
        var v = Run(m, 0, 30, 0.4f);       // a short squeeze
        var lifts = 0;
        Run(m, v, -4, 2f, x => { if (x.TakeLift()) lifts++; });
        Assert.Equal(0, lifts);
        var slow = new EngineSoundModel();
        v = Run(slow, 0, 10, 2f);          // 20 km/h: under the minimum share of the top speed
        Run(slow, v, -3, 2f, x => { if (x.TakeLift()) lifts++; });
        Assert.Equal(0, lifts);
    }

    [Fact]
    public void Braking_hard_brakes_and_stopping_idles()
    {
        var m = new EngineSoundModel();
        var v = Run(m, 0, 40, 3f);          // 120 km/h
        v = Run(m, v, -40, 1f);
        Assert.Equal(EnginePhase.Braking, m.Phase);
        Run(m, v, -40, 3f);
        Assert.Equal(EnginePhase.Idle, m.Phase);
    }

    [Fact]
    public void Lift_follows_a_braking_start_too_and_respects_the_cooldown()
    {
        var m = new EngineSoundModel();
        var v = Run(m, 0, 30, 3f);
        var lifts = 0;
        v = Run(m, v, -40, 0.6f, x => { if (x.TakeLift()) lifts++; });
        Assert.Equal(1, lifts);
        v = Run(m, v, 30, 0.3f, x => { if (x.TakeLift()) lifts++; });
        Run(m, v, -40, 0.5f, x => { if (x.TakeLift()) lifts++; });
        Assert.Equal(1, lifts); // too soon, and no long push in between
    }

    [Fact]
    public void Reversing_never_sounds_like_accelerating_nor_lifts()
    {
        var m = new EngineSoundModel();
        var lifts = 0;
        for (var t = 0f; t < 3f; t += Dt) { m.Step(-(t * 12f), Top, Dt); if (m.TakeLift()) lifts++; Assert.NotEqual(EnginePhase.Accelerating, m.Phase); }
        Assert.Equal(EnginePhase.Coasting, m.Phase);
        for (var t = 0f; t < 2f; t += Dt) { m.Step(-36f + t * 18f, Top, Dt); if (m.TakeLift()) lifts++; } // easing off
        Assert.Equal(0, lifts);
    }

    [Fact]
    public void Braking_out_of_reverse_counts_by_magnitude()
    {
        var m = new EngineSoundModel();
        for (var t = 0f; t < 2f; t += Dt) m.Step(-60f, Top, Dt);
        for (var t = 0f; t < 1f; t += Dt) m.Step(-60f + t * 40f, Top, Dt);
        Assert.Equal(EnginePhase.Braking, m.Phase);
    }

    static VehicleSoundsDef Set() => new()
    {
        Background = new VehicleSoundLayerDef { Sound = "b" },
        Idle = new VehicleSoundLayerDef { Sound = "i" },
        Accel = new VehicleSoundLayerDef { Sound = "a", FadeIn = 0.1f, FadeOut = 0.2f },
        Brake = new VehicleSoundLayerDef { Sound = "k" },
    };

    [Fact]
    public void Mix_crossfades_into_the_phase_layer_and_out_of_the_rest()
    {
        var def = Set();
        var mix = new EngineMix();
        for (var i = 0; i < 120; i++) mix.Step(def, EnginePhase.Idle, 0f, Dt);
        Assert.Equal(1f, mix.Gain[(int)EngineLayer.Background]);
        Assert.Equal(1f, mix.Gain[(int)EngineLayer.Idle]);
        Assert.Equal(0f, mix.Gain[(int)EngineLayer.Accel]);
        for (var i = 0; i < 3; i++) mix.Step(def, EnginePhase.Accelerating, 0.2f, Dt);
        Assert.InRange(mix.Gain[(int)EngineLayer.Accel], 0.1f, 0.9f); // part way up
        Assert.InRange(mix.Gain[(int)EngineLayer.Idle], 0.1f, 0.99f); // part way down
        for (var i = 0; i < 60; i++) mix.Step(def, EnginePhase.Accelerating, 0.2f, Dt);
        Assert.Equal(1f, mix.Gain[(int)EngineLayer.Accel]);
        Assert.Equal(0f, mix.Gain[(int)EngineLayer.Idle]);
    }

    [Fact]
    public void Brake_layer_is_quiet_at_walking_pace_and_a_layer_the_type_lacks_stays_silent()
    {
        var def = Set();
        var mix = new EngineMix();
        for (var i = 0; i < 300; i++) mix.Step(def, EnginePhase.Braking, 0.1f, Dt);
        Assert.Equal(0.104f, mix.Gain[(int)EngineLayer.Brake], 3); // 0.2 of the fade, eased
        for (var i = 0; i < 300; i++) mix.Step(def, EnginePhase.TopSpeed, 1f, Dt);
        Assert.Equal(0f, mix.Gain[(int)EngineLayer.Top]);
    }

    [Fact]
    public void Top_layer_takes_over_from_accel_and_without_one_accel_carries_on()
    {
        var def = Set();
        var mix = new EngineMix();
        for (var i = 0; i < 300; i++) mix.Step(def, EnginePhase.TopSpeed, 1f, Dt);
        Assert.Equal(1f, mix.Gain[(int)EngineLayer.Accel]); // no top layer: the pull carries on
        def.Top = new VehicleSoundLayerDef { Sound = "t" };
        for (var i = 0; i < 600; i++) mix.Step(def, EnginePhase.TopSpeed, 1f, Dt);
        Assert.Equal(1f, mix.Gain[(int)EngineLayer.Top]);
        Assert.Equal(0f, mix.Gain[(int)EngineLayer.Accel]);
    }

    [Fact]
    public void The_engine_never_falls_silent_off_the_gas_or_under_braking()
    {
        var def = Set();
        var mix = new EngineMix();
        for (var i = 0; i < 600; i++) mix.Step(def, EnginePhase.Coasting, 0.8f, Dt);
        Assert.InRange(mix.Gain[(int)EngineLayer.Accel], 0.4f, 0.5f);   // the engine body, under the lift
        Assert.Equal(0f, mix.Gain[(int)EngineLayer.Idle]);               // fast: no idle yet
        for (var i = 0; i < 600; i++) mix.Step(def, EnginePhase.Braking, 0.8f, Dt);
        Assert.InRange(mix.Gain[(int)EngineLayer.Accel], 0.5f, 0.7f);
        Assert.Equal(1f, mix.Gain[(int)EngineLayer.Background]);
        for (var i = 0; i < 600; i++) mix.Step(def, EnginePhase.Braking, 0.02f, Dt);
        Assert.InRange(mix.Gain[(int)EngineLayer.Idle], 0.6f, 1f);       // nearly stopped: the idle is coming in
        def.Top = new VehicleSoundLayerDef { Sound = "t" };              // a top layer is the engine body instead: a steady loop that slows with the speed
        for (var i = 0; i < 900; i++) mix.Step(def, EnginePhase.Coasting, 0.8f, Dt);
        Assert.InRange(mix.Gain[(int)EngineLayer.Top], 0.4f, 0.5f);
        Assert.Equal(0f, mix.Gain[(int)EngineLayer.Accel]);
    }

    [Fact]
    public void Pitch_runs_from_standstill_to_top_speed()
    {
        var l = new VehicleSoundLayerDef { Sound = "a", Pitch = new[] { 0.8f, 1.6f } };
        Assert.Equal(0.8f, l.PitchAt(0), 3);
        Assert.Equal(1.2f, l.PitchAt(0.5f), 3);
        Assert.Equal(1.6f, l.PitchAt(2f), 3);
    }

    [Fact]
    public void Validation_rejects_a_layer_without_a_sound_or_with_a_bad_pitch()
    {
        Assert.Throws<DataFileException>(() => VehicleSoundsDef.Validate("t", new VehicleSoundsDef { Idle = new VehicleSoundLayerDef() }));
        Assert.Throws<DataFileException>(() => VehicleSoundsDef.Validate("t", new VehicleSoundsDef { Idle = new VehicleSoundLayerDef { Sound = "x", Pitch = new[] { 0f, 1f } } }));
        Assert.Throws<DataFileException>(() => VehicleSoundsDef.Validate("t", new VehicleSoundsDef { Idle = new VehicleSoundLayerDef { Sound = "x", FadeIn = -1 } }));
        VehicleSoundsDef.Validate("t", Set());
    }

    [Fact]
    public void Every_sound_a_vehicle_names_is_in_sounds_json()
    {
        var vehicles = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
        var sounds = SoundsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "sounds.json"));
        foreach (var v in vehicles.Vehicles.Where(v => v.Sounds != null))
            foreach (var (role, l) in v.Sounds!.All())
            {
                var d = sounds.Find(l.Sound);
                Assert.True(d != null, $"{v.Id}: sounds.{role} names {l.Sound}, which sounds.json lacks");
                Assert.True(role == "lift" || d!.Loop, $"{v.Id}: sounds.{role} ({l.Sound}) must loop");
                Assert.True(role != "lift" || !d!.Loop, $"{v.Id}: the lift sound ({l.Sound}) is a one-shot");
            }
    }
}
