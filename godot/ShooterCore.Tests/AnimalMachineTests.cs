using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Animal behaviour machine: golden trajectory (pins the exact timing and RNG order through the StateMachine refactor) and the spec's edges.</summary>
public class AnimalMachineTests
{
    const float Dt = 0.05f;

    sealed class World : IAnimalWorld
    {
        public Vector3? Threat;
        public float? GroundY(float x, float z) => x > 9 && x < 11 ? null : 0f; // a gap to be blocked by
        public Vector3? NearestThreat(Vector3 from, float radius) => Threat is { } t && (t - from).Length() <= radius ? t : null;
        public Vector3? PositionOf(ushort player) => new Vector3(-8, 0, 3);
    }

    static AnimalsFile Small() => AnimalsFile.Parse("""
        {"version":1,"idle_min_seconds":0.4,"idle_max_seconds":1.5,"eat_min_seconds":0.5,"eat_max_seconds":1.2,"eat_chance":0.4,"corpse_seconds":4,"alarm_seconds":3,"calm_seconds":1.5,
        "animals":[{"id":"deer","name":"Deer","model":"animated-animals/deer.glb","scale":0.33,"hp":100,
        "walk_speed":1.5,"run_speed":10,"turn_rate_deg_per_s":200,"flee_radius":25,"wander_radius":12,"hit_box":[0.5,1.4,1.5]}]}
        """);

    /// <summary>Folds the replicated state, position and yaw of every tick into one number: any change in timing, RNG order or flee/wander choice moves it.</summary>
    static ulong Trace(int seed)
    {
        var w = new World();
        var a = new Animal(Small(), 0, Vector3.Zero, 0f, w, seed);
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        for (var i = 0; i < 4000; i++)
        {
            if (i == 900) w.Threat = new Vector3(-6, 0, 0);
            if (i == 1100) w.Threat = null;
            if (i == 2000) a.OnDamage(10, 7);
            if (i == 2600) { a.OnDamage(10, 7); a.OnDamage(10, 99); }
            if (i == 3000) a.OnDamage(500, 7);
            a.Tick(Dt);
            Mix(a.Serialize().State);
            Mix((uint)BitConverter.SingleToInt32Bits(a.Position.X));
            Mix((uint)BitConverter.SingleToInt32Bits(a.Position.Z));
            Mix((uint)BitConverter.SingleToInt32Bits(a.Yaw));
            Mix(a.Ended == null ? 0UL : 1UL);
        }
        return h;
    }

    [Fact]
    public void TheGoldenScenarioVisitsEveryState()
    {
        var w = new World();
        var a = new Animal(Small(), 0, Vector3.Zero, 0f, w, 1);
        var seen = new HashSet<AnimalState>();
        for (var i = 0; i < 4000; i++)
        {
            if (i == 900) w.Threat = new Vector3(-6, 0, 0);
            if (i == 1100) w.Threat = null;
            if (i == 3000) a.OnDamage(500, 7);
            a.Tick(Dt);
            seen.Add(a.State);
        }
        // every state this species can be in: Sit and Bark are for the species that opt in (see PetBehaviourTests), Carried for a Thief to cause (PetLootTests)
        Assert.Equal(Enum.GetValues<AnimalState>().Where(s => s is not (AnimalState.Sit or AnimalState.Bark or AnimalState.Carried or AnimalState.Thrown or AnimalState.Down or AnimalState.Vanish)).Order(), seen.Order()); // Vanish is for a species with vanish_seconds (AnimalTests); Carried is the Thieves event's (PetLootTests); Thrown and Down a vehicle's (AnimalTests)
    }

    [Fact]
    public void AFleeingAnimalIsNotHandedAStaleWanderClock()
    {
        // Flee has no clock: when it calms down it stands for a fresh idle time instead of acting on what idle/eat/walk left behind.
        var w = new World();
        var a = new Animal(Small(), 0, Vector3.Zero, 0f, w, 4);
        w.Threat = new Vector3(-6, 0, 0);
        a.Tick(Dt);
        Assert.Equal(AnimalState.Flee, a.State);
        w.Threat = null;
        for (var i = 0; i < 200 && a.State == AnimalState.Flee; i++) a.Tick(Dt);
        Assert.Equal(AnimalState.Idle, a.State);
        a.Tick(Dt);
        Assert.Equal(AnimalState.Idle, a.State); // idle seconds are at least 0.4
    }

    [Theory]
    [InlineData(1, 13103103472840032317UL)]
    [InlineData(2, 14513175270788586214UL)]
    [InlineData(3, 12173215806328178894UL)]
    public void TheTrajectoryOfASeededAnimalIsUnchanged(int seed, ulong golden) => Assert.Equal(golden, Trace(seed));
}
