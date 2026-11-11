using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #126: the attack helicopter killstreak through the real <see cref="KillstreakService"/> and a fake world.</summary>
public class AttackHelicopterTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    const ushort Owner = 4;
    const ushort Enemy = 9;

    /// <summary>The server's side in memory: records launches and removals, and the gunner's inputs.</summary>
    sealed class FakeWorld : IAttackHelicopterWorld
    {
        public Vector3 Centre { get; init; } = new(10, 2, -5);
        public bool Room { get; set; } = true;
        public readonly List<(ushort Owner, Vector3 Centre, float Phase)> Launches = new();
        public readonly List<ushort> Removed = new();
        public readonly List<NetInput> Inputs = new();

        public AttackHelicopterLaunch? Launch(ushort owner, Vector3 centre, float phaseRad)
        {
            if (!Room) return null;
            Launches.Add((owner, centre, phaseRad));
            var id = (ushort)(500 + Launches.Count);
            return new AttackHelicopterLaunch(id, i => Inputs.Add(i));
        }

        public void Remove(ushort aircraftId) => Removed.Add(aircraftId);
    }

    sealed class Rig
    {
        public readonly FakeWorld World = new();
        public readonly List<(ushort Owner, EventKind Kind, byte[] Payload)> Sent = new();
        public readonly KillstreakService Service;

        public Rig()
        {
            var impl = Shipped.Killstreaks.Streaks.Select(s => s.Id == AttackHelicopterStreak.StreakId
                ? (IKillstreak)new AttackHelicopterStreakImpl(World)
                : new LoggingKillstreak(s.Id));
            Service = new KillstreakService(Shipped.Killstreaks, impl, (o, k, p) => Sent.Add((o, k, p)), _ => { });
        }

        /// <summary>Arms a life with the helicopter in its first slot and makes the ten kills that earn it.</summary>
        public void Earn()
        {
            Service.Arm(Owner, new[] { AttackHelicopterStreak.StreakId, "", "" }, 0);
            for (var i = 0; i < 10; i++) Service.OnKill(Owner, Enemy, false);
        }

        public bool Ended() => Sent.Any(s => s.Kind == EventKind.StreakEnded && s.Owner == Owner);
    }

    static NetInput Press(MoveButtons buttons) => new() { Buttons = (ushort)buttons };

    [Fact]
    public void Shipped_data_gives_the_helicopter_a_45_second_pov_streak_on_the_helicopter_kind()
    {
        var def = Shipped.Killstreaks.Find(AttackHelicopterStreak.StreakId);
        Assert.NotNull(def);
        Assert.Equal(45, def!.DurationSeconds);
        Assert.Equal(10, def.KillsRequired);
        Assert.Contains(Shipped.Killstreaks.AircraftKinds, k => k.Id == AttackHelicopterStreak.AircraftKind);
        Assert.Contains(Shipped.Killstreaks.Gunship!.Weapons, w => w.Id == AttackHelicopterStreak.GunWeaponId && w.Kind == GunshipWeaponKind.Cannon);
    }

    [Fact]
    public void Activation_launches_a_helicopter_and_gives_the_owner_a_frozen_and_shielded_body()
    {
        var rig = new Rig();
        rig.Earn();
        Assert.True(rig.Service.Activate(Owner, 100, new Vector3(30, 0, 0)));

        Assert.Single(rig.World.Launches);
        var control = rig.Service.ControlOf(Owner);
        Assert.NotNull(control);
        Assert.Equal(AttackHelicopterStreak.StreakId, control!.StreakId);
        Assert.Equal((ushort)501, control.EntityId);
        Assert.True(control.FreezesBody);
        Assert.True(control.ShieldsBody);
        Assert.Empty(rig.Service.Held(Owner)); // used, so no longer held
    }

    [Fact]
    public void The_helicopter_circles_the_map_centre_and_starts_on_the_owners_bearing()
    {
        var rig = new Rig();
        rig.Earn();
        rig.Service.Activate(Owner, 0, new Vector3(10, 0, -5 + 20)); // 20 m due +Z of the centre
        var (owner, centre, phase) = rig.World.Launches.Single();
        Assert.Equal(Owner, owner);
        Assert.Equal(rig.World.Centre, centre);
        Assert.Equal(MathF.PI / 2, phase, 4);
    }

    [Fact]
    public void Owner_input_is_routed_to_the_gunner()
    {
        var rig = new Rig();
        rig.Earn();
        rig.Service.Activate(Owner, 0, Vector3.Zero);
        Assert.True(rig.Service.RouteInput(Owner, Press(MoveButtons.Fire)));
        Assert.Single(rig.World.Inputs);
        Assert.Equal((ushort)MoveButtons.Fire, rig.World.Inputs[0].Buttons);
    }

    [Fact]
    public void The_streak_ends_at_its_duration_gives_the_gunner_back_and_removes_the_helicopter()
    {
        var rig = new Rig();
        rig.Earn();
        rig.Service.Activate(Owner, 100, Vector3.Zero);

        rig.Service.Tick(144.9);
        Assert.NotNull(rig.Service.ControlOf(Owner));
        Assert.Empty(rig.World.Removed);

        rig.Service.Tick(145);
        Assert.Null(rig.Service.ControlOf(Owner));
        Assert.Equal(new ushort[] { 501 }, rig.World.Removed);
        Assert.True(rig.Ended());
        Assert.False(rig.Service.RouteInput(Owner, Press(MoveButtons.Fire)));
    }

    [Fact]
    public void The_owners_death_ends_the_streak_and_removes_the_helicopter()
    {
        var rig = new Rig();
        rig.Earn();
        rig.Service.Activate(Owner, 0, Vector3.Zero);
        rig.Service.OnDeath(Owner);
        Assert.Null(rig.Service.ControlOf(Owner));
        Assert.Equal(new ushort[] { 501 }, rig.World.Removed);
        Assert.True(rig.Ended());
    }

    [Fact]
    public void Without_room_the_streak_is_refused_and_stays_held()
    {
        var rig = new Rig();
        rig.Earn();
        rig.World.Room = false;
        Assert.False(rig.Service.Activate(Owner, 0, Vector3.Zero));
        Assert.Null(rig.Service.ControlOf(Owner));
        Assert.Equal(new[] { AttackHelicopterStreak.StreakId }, rig.Service.Held(Owner));
    }

    [Fact]
    public void The_cannons_kills_are_the_streaks_kills_and_do_not_advance_the_counter()
    {
        var rig = new Rig();
        rig.Earn();
        Assert.True(rig.Service.IsStreakWeapon(AttackHelicopterStreak.StreakId));
        Assert.False(rig.Service.IsStreakWeapon("m82"));

        rig.Service.Activate(Owner, 0, Vector3.Zero);
        var before = rig.Service.KillCount(Owner);
        rig.Service.OnKill(Owner, Enemy, fromStreak: true);
        Assert.Equal(before, rig.Service.KillCount(Owner));
    }

    [Fact]
    public void The_pilot_gun_cannot_be_cycled_onto_the_rockets()
    {
        var gun = Shipped.Killstreaks.Gunship!;
        var cannon = gun.Weapons.ToList().FindIndex(w => w.Id == AttackHelicopterStreak.GunWeaponId);
        var aircraft = new Aircraft(Shipped.Killstreaks.AircraftKinds.Single(k => k.Id == AttackHelicopterStreak.AircraftKind), 0, Vector3.Zero, 0, false, Owner);
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(gun, aircraft, shots.Add, _ => { }, new Random(1), new[] { cannon });

        control.OnInput(GunnerInputs.Look(control, 0f, -0.5f, MoveButtons.Reload), 1.0);
        Assert.Equal(cannon, control.Weapon);
        control.OnInput(GunnerInputs.Look(control, 0f, -0.5f, MoveButtons.Fire), 1.5);
        Assert.Single(shots);
        Assert.Equal(GunshipWeaponKind.Cannon, shots[0].Kind);
    }
}
