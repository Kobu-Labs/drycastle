using Xunit;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.ShooterCore.Tests.Hud;

/// <summary>
/// Every named predicate of <see cref="ClientModeInputs"/> against the boolean expression the old call site computed (copied here, one per site), for all
/// 1024 flag combinations. A replaced site is identical if its row passes.
///
/// Hazards the exhaustive comparison pins (old disagreements kept, not fixed):
/// - BodyIsPredicted counts the turret and Bigfoot/Mass/pin but not Rising; ShowsViewmodel counts Rising but not the Bigfoot: a held or rising body differs.
/// - CanPredictFire omits the turret (its caller guards it) and the seat check is "driving and not in an armed seat", while ScopeSuppressed is plain "driving".
/// - SkipsReconcile ignores Dead; SeatKeysLive and CanToggleCockpitView include it; RemoteViewOwnsScreen (hands off) ignores it.
/// - HeldOnFoot needs !Driving; the Mass/pin hold (ServerHoldsBody) does not.
/// </summary>
public class ClientModeTests
{
    public static IEnumerable<object[]> Combos()
    {
        for (var i = 0; i < 1 << 10; i++) yield return new object[] { i };
    }

    static ClientModeInputs Of(int bits) => new(
        Dead: (bits & 1) != 0, Driving: (bits & 2) != 0, Turret: (bits & 4) != 0, Controlling: (bits & 8) != 0, MassSeated: (bits & 16) != 0,
        Pinned: (bits & 32) != 0, HeldByBigfoot: (bits & 64) != 0, Rising: (bits & 128) != 0, StateInVehicle: (bits & 256) != 0, InArmedSeat: (bits & 512) != 0);

    [Theory, MemberData(nameof(Combos))]
    public void Predicates_equal_the_old_expressions(int bits)
    {
        var m = Of(bits);
        bool Dead = m.Dead, _driving = m.Driving, gunshipNotNull = m.Turret, _controlling = m.Controlling, _massSeated = m.MassSeated,
            _pinned = m.Pinned, _heldByBigfoot = m.HeldByBigfoot, _pRising = m.Rising, inVehicleState = m.StateInVehicle, InArmedSeat = m.InArmedSeat;
        var gunshipNull = !gunshipNotNull;

        Assert.Equal(gunshipNotNull || _controlling, m.RemoteViewOwnsScreen);          // Hud.SetControlMode, handsOff in Flight/Drive
        Assert.Equal(gunshipNull && !_controlling, m.ViewIsBody);                       // Seats:60, Cars:231, Cockpit:46
        Assert.Equal(_driving || _controlling || gunshipNotNull, m.SkipsReconcile);     // NetClient.cs:485
        Assert.Equal(_heldByBigfoot && !_driving, m.HeldOnFoot);                        // NetClient.cs:483
        Assert.Equal(_massSeated || _pinned, m.ServerHoldsBody);                        // NetClient.cs:484
        Assert.Equal(!_driving && !_controlling && gunshipNull && !_heldByBigfoot && !_massSeated && !_pinned, m.BodyIsPredicted); // NetClient.cs:616 (without the Dead term inside)
        var seated = (_driving || inVehicleState) && !InArmedSeat;
        Assert.Equal(seated, m.Seated);
        Assert.Equal(!Dead && !seated && !_massSeated && !_pinned && !_pRising && !_controlling && gunshipNull, m.ShowsViewmodel); // NetClient.cs:692 (without ZoomFov)
        Assert.Equal(_driving || _controlling || inVehicleState, m.ScopeSuppressed);    // NetClient.cs:827
        Assert.Equal(!_driving && !_controlling, m.CanScope);                           // Predict.cs:47
        Assert.Equal(!(Dead ||_heldByBigfoot || _massSeated || _pinned || _driving && !InArmedSeat || _controlling), m.CanPredictFire); // Predict.cs:64
        Assert.Equal(_driving && !Dead, m.DrivingAlive);                                // Drive.cs:75, Flight.cs:65
        Assert.Equal(!(!_driving || Dead || gunshipNotNull || _controlling), m.SeatKeysLive); // Seats.cs:33, DoorGun.cs:80
        Assert.Equal(!(Dead || gunshipNotNull || _controlling), m.CanToggleCockpitView); // Cockpit.cs:33
        Assert.Equal(!(Dead || _driving), m.HotbarKeysLive);                            // NetClient.cs ReadSlotKeys
    }

    [Theory, MemberData(nameof(Combos))]
    public void Resolve_agrees_with_OnFoot_and_the_flags(int bits)
    {
        var m = Of(bits);
        Assert.Equal(m.OnFoot, m.Resolve() == ClientMode.OnFoot);
        if (m.Dead) Assert.Equal(ClientMode.Dead, m.Resolve());
        else if (m.Controlling) Assert.Equal(ClientMode.Missile, m.Resolve());
        else if (m.Turret) Assert.Equal(ClientMode.Turret, m.Resolve());
        // OnFoot means the body is predicted and the weapon shows (when no zoom): the named predicates tell the same story
        if (m.OnFoot) { Assert.True(m.BodyIsPredicted); Assert.True(m.ShowsViewmodel); Assert.True(m.CanPredictFire); }
        // every state that hides the weapon or stops prediction has a non-OnFoot mode
        if (!m.BodyIsPredicted) Assert.NotEqual(ClientMode.OnFoot, m.Resolve());
    }

    [Fact]
    public void Every_mode_is_resolvable()
    {
        var seen = new HashSet<ClientMode>();
        for (var i = 0; i < 1 << 10; i++) seen.Add(Of(i).Resolve());
        foreach (var mode in Enum.GetValues<ClientMode>()) Assert.Contains(mode, seen);
    }
}
