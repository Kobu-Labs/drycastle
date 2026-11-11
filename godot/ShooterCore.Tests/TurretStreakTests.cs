using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The turret killstreak through the real <see cref="KillstreakService"/>: placing ghost, confirm, cancel and expiry.</summary>
public class TurretStreakTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    const ushort Owner = 1;

    /// <summary>A player on flat ground at the origin, looking at the world through <see cref="TurretTests.FlatWorld"/>-like rules.</summary>
    sealed class Rig
    {
        public readonly WorldEntityManager Entities;
        public readonly KillstreakService Service;
        public readonly List<string> Log = new();

        public Rig(ITurretWorld world)
        {
            var registry = new WorldEntityRegistry();
            TurretRules.Register(registry);
            Entities = new WorldEntityManager(registry) { Log = Log.Add };
            var impl = Shipped.Killstreaks.Streaks.Select(s => s.Id == TurretStreak.StreakId
                ? (IKillstreak)new TurretStreak(Shipped.Turret.Def, Entities, world)
                : new LoggingKillstreak(s.Id));
            Service = new KillstreakService(Shipped.Killstreaks, impl, (_, _, _) => { }, Log.Add);
        }

        /// <summary>Arms a life with the turret in its first slot and makes the six kills that earn it.</summary>
        public void EarnTurret()
        {
            Service.Arm(Owner, new[] { TurretStreak.StreakId, "", "" }, 0);
            for (ushort victim = 100; Service.KillCount(Owner) < 6; victim++) Service.OnKill(Owner, victim, false);
        }
    }

    static NetInput Press(MoveButtons buttons, float pitch = 0f) => new()
    {
        Buttons = (ushort)buttons,
        Pitch = Quant.Pitch(pitch),
        Yaw = Quant.Yaw(0f),
    };

    static bool Armed(WorldEntityManager e) => e.Snapshot().Any(x => x.Type == TurretRules.TypeId && TurretRules.IsArmed(x.State.State));
    static bool GhostUp(WorldEntityManager e) => e.Snapshot().Any(x => x.Type == TurretRules.TypeId && TurretRules.IsGhost(x.State.State));

    [Fact]
    public void TheTurretIsEarnedAtSixKillsAndUsingItRaisesAPlacingGhost()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        Assert.Equal(new[] { TurretStreak.StreakId }, rig.Service.Held(Owner));

        Assert.True(rig.Service.Activate(Owner, 0, Vector3.Zero));
        Assert.Equal(1, rig.Entities.Count);
        Assert.True(GhostUp(rig.Entities));
        Assert.NotNull(rig.Service.ControlOf(Owner));
        Assert.Empty(rig.Service.Held(Owner));
    }

    [Fact]
    public void FireOnAValidSpotPlacesTheTurretAndEndsTheStreak()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);

        Assert.True(rig.Service.RouteInput(Owner, Press(MoveButtons.None, pitch: -0.5f))); // aim at the ground, no press yet
        Assert.True(GhostUp(rig.Entities));

        Assert.True(rig.Service.RouteInput(Owner, Press(MoveButtons.Fire, pitch: -0.5f)));
        Assert.True(Armed(rig.Entities));
        Assert.Equal(1, rig.Entities.Count);
        Assert.Null(rig.Service.ControlOf(Owner));
        Assert.Equal(0, rig.Service.ActiveCount(Owner, TurretStreak.StreakId));
    }

    [Fact]
    public void InteractAlsoPlacesAndAnInvalidAimDoesNot()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);

        rig.Service.RouteInput(Owner, Press(MoveButtons.Use, pitch: -0.2f)); // the ray misses the ground: not valid
        Assert.True(GhostUp(rig.Entities));
        Assert.False(Armed(rig.Entities));

        rig.Service.RouteInput(Owner, Press(MoveButtons.None, pitch: -0.5f));
        rig.Service.RouteInput(Owner, Press(MoveButtons.Use, pitch: -0.5f));
        Assert.True(Armed(rig.Entities));
    }

    [Fact]
    public void TheKillstreakKeyCancelsThePlacementAndRemovesTheGhost()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);
        Assert.Equal(1, rig.Entities.Count);

        rig.Service.RouteInput(Owner, Press(MoveButtons.None, pitch: -0.5f)); // the activating press was released
        rig.Service.RouteInput(Owner, Press(MoveButtons.Killstreak, pitch: -0.5f));
        Assert.Equal(0, rig.Entities.Count);
        Assert.Null(rig.Service.ControlOf(Owner));
    }

    [Fact]
    public void AKeyStillHeldFromTheActivationDoesNotCancelOrPlaceTheGhost()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);

        var held = MoveButtons.Killstreak | MoveButtons.Fire;
        for (var i = 0; i < 4; i++) rig.Service.RouteInput(Owner, Press(held, pitch: -0.5f));
        Assert.True(GhostUp(rig.Entities));
        Assert.NotNull(rig.Service.ControlOf(Owner));

        rig.Service.RouteInput(Owner, Press(MoveButtons.None, pitch: -0.5f));
        rig.Service.RouteInput(Owner, Press(MoveButtons.Fire, pitch: -0.5f));
        Assert.True(Armed(rig.Entities));
    }

    [Fact]
    public void AnUnconfirmedGhostIsRemovedWhenThePlacementWindowEnds()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);

        rig.Service.Tick(Shipped.Killstreaks.Find(TurretStreak.StreakId)!.DurationSeconds - 0.5);
        Assert.Equal(1, rig.Entities.Count);
        rig.Service.Tick(Shipped.Killstreaks.Find(TurretStreak.StreakId)!.DurationSeconds + 0.5);
        Assert.Equal(0, rig.Entities.Count);
        Assert.Null(rig.Service.ControlOf(Owner));
    }

    [Fact]
    public void TheStreakServiceLeavesAPlacedTurretToTheServerWhenItsOwnerDies()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);
        rig.Service.RouteInput(Owner, Press(MoveButtons.None, pitch: -0.5f));
        rig.Service.RouteInput(Owner, Press(MoveButtons.Fire, pitch: -0.5f));

        rig.Service.OnDeath(Owner);
        Assert.Equal(1, rig.Entities.Count);
        Assert.True(Armed(rig.Entities));
    }

    [Fact]
    public void APlacingGhostDoesNotHoldTheOwnerWhenNoPoseIsKnown()
    {
        var world = new TurretTests.FlatWorld { Owner = null }; // dead, or driving
        var rig = new Rig(world);
        rig.EarnTurret();
        Assert.False(rig.Service.Activate(Owner, 0, Vector3.Zero));
        Assert.Equal(0, rig.Entities.Count);
        Assert.Equal(new[] { TurretStreak.StreakId }, rig.Service.Held(Owner)); // refused: the streak stays held
    }

    [Fact]
    public void DespawnOwnedRemovesOnlyTheOwnersTurrets()
    {
        var rig = new Rig(new TurretTests.FlatWorld());
        rig.EarnTurret();
        rig.Service.Activate(Owner, 0, Vector3.Zero);
        rig.Service.RouteInput(Owner, Press(MoveButtons.None, pitch: -0.5f));
        rig.Service.RouteInput(Owner, Press(MoveButtons.Fire, pitch: -0.5f));
        Assert.True(Armed(rig.Entities));

        rig.Entities.DespawnOwned(Owner + 1, TurretRules.TypeId, WorldDespawnReason.Removed);
        Assert.Equal(1, rig.Entities.Count);
        rig.Entities.DespawnOwned(Owner, TurretRules.TypeId, WorldDespawnReason.Removed);
        Assert.Equal(0, rig.Entities.Count);
    }
}
