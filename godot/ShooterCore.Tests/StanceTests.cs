using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Prone stance: transitions and their timings, crawl speed, headroom, steep ground, weapon steadiness and replication.</summary>
public class StanceTests
{
    const float Dt = 1f / 30f;

    /// <summary>One tick on a flat floor at y = 0, the same shape as the movement tests.</summary>
    static void Tick(ref MoveState s, MoveInput i)
    {
        MovementStep.Step(ref s, i, Dt);
        var p = s.Position + s.Velocity * Dt;
        var v = s.Velocity;
        var floor = false;
        if (p.Y <= 0 && v.Y <= 0) { p.Y = 0; v.Y = 0; floor = true; }
        MovementStep.PostMove(ref s, p, v, floor);
    }

    static void Ticks(ref MoveState s, MoveInput i, int n)
    {
        for (var k = 0; k < n; k++) Tick(ref s, i);
    }

    static MoveState Settled(Stance stance)
    {
        var s = new MoveState { OnFloor = true };
        var hold = new MoveInput { Buttons = stance == Stance.Prone ? MoveButtons.Prone : stance == Stance.Crouch ? MoveButtons.Crouch : MoveButtons.None };
        Ticks(ref s, hold, 60);
        return s;
    }

    static float HSpeed(in MoveState s) => new Vector2(s.Velocity.X, s.Velocity.Z).Length();

    [Fact]
    public void StandToCrouchTakesAboutAFifthOfASecond()
    {
        var s = new MoveState { OnFloor = true };
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Crouch }, 3);
        Assert.Equal(Stance.Stand, s.Stance);
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Crouch }, 10);
        Assert.Equal(Stance.Crouch, s.Stance);
    }

    [Fact]
    public void ProneKeyTogglesThroughCrouchAndHoldingItKeepsProne()
    {
        var s = new MoveState { OnFloor = true };
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Prone }, 10);
        Assert.Equal(Stance.Crouch, s.Stance); // no dive: the body passes through crouch first
        Assert.Equal(Stance.Prone, s.StepTo);
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Prone }, 30);
        Assert.Equal(Stance.Prone, s.Stance);
        Assert.True(s.ProneLatched);
    }

    [Fact]
    public void NoDiveFromStanding()
    {
        var s = new MoveState { OnFloor = true };
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Prone }, 5);
        Assert.NotEqual(Stance.Prone, s.Stance);
    }

    [Fact]
    public void ASecondPressReleasesProneAndTheStandUpIsSlow()
    {
        var s = Settled(Stance.Prone);
        Ticks(ref s, new MoveInput(), 1);                       // key up
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Prone }, 1); // key down: toggle off
        Assert.False(s.ProneLatched);
        Ticks(ref s, new MoveInput(), 20);                      // 0.67 s: still lying down
        Assert.Equal(Stance.Prone, s.Stance);
        Ticks(ref s, new MoveInput(), 15);                      // 1.17 s: crouched, the stand-up is still going
        Assert.Equal(Stance.Crouch, s.Stance);
        Ticks(ref s, new MoveInput(), 15);                      // 1.67 s: standing
        Assert.Equal(Stance.Stand, s.Stance);
    }

    [Fact]
    public void CrawlSpeedIs1Point2AndSprintIsRefusedWhileStillLow()
    {
        var s = Settled(Stance.Prone);
        Ticks(ref s, new MoveInput { MoveY = 1, Buttons = MoveButtons.Prone }, 30);
        Assert.Equal(1.2f, HSpeed(s), 2);
        Ticks(ref s, new MoveInput { MoveY = 1, Buttons = MoveButtons.Sprint }, 5); // standing up, not yet sprinting
        Assert.True(HSpeed(s) < 2f);
    }

    [Fact]
    public void SprintFromCrouchStandsUpEvenWithCrouchHeldThenSprints()
    {
        var s = Settled(Stance.Crouch);
        var sprint = new MoveInput { MoveY = 1, Buttons = MoveButtons.Crouch | MoveButtons.Sprint };
        Ticks(ref s, sprint, 20);
        Assert.Equal(Stance.Stand, s.Stance);
        Ticks(ref s, sprint, 10);
        Assert.Equal(MoveConsts.SprintSpeed, HSpeed(s), 2);
    }

    [Fact]
    public void SprintFromProneClearsTheLatchAndStandsUpThroughCrouch()
    {
        var s = Settled(Stance.Prone);
        var sprint = new MoveInput { MoveY = 1, Buttons = MoveButtons.Sprint };
        Ticks(ref s, sprint, 1);
        Assert.False(s.ProneLatched);
        Ticks(ref s, sprint, 60);
        Assert.Equal(Stance.Stand, s.Stance);
        Ticks(ref s, sprint, 10);
        Assert.Equal(MoveConsts.SprintSpeed, HSpeed(s), 2);
    }

    [Fact]
    public void SprintWithoutForwardDoesNotStandUp()
    {
        var s = Settled(Stance.Prone);
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Sprint }, 60);
        Assert.True(s.ProneLatched);
        Assert.Equal(Stance.Prone, s.Stance);
    }

    [Fact]
    public void NoJumpWhileProneOrMidTransition()
    {
        var prone = Settled(Stance.Prone);
        MovementStep.Step(ref prone, new MoveInput { Buttons = MoveButtons.Prone | MoveButtons.Jump }, Dt);
        Assert.Equal(0f, prone.Velocity.Y);

        var mid = new MoveState { OnFloor = true };
        Ticks(ref mid, new MoveInput { Buttons = MoveButtons.Crouch }, 2);
        Assert.True(mid.Transitioning);
        MovementStep.Step(ref mid, new MoveInput { Buttons = MoveButtons.Crouch | MoveButtons.Jump }, Dt); // crouch still held: the step goes on
        Assert.Equal(0f, mid.Velocity.Y);
    }

    [Fact]
    public void HeadroomStopsTheStandUpFromProne()
    {
        var s = Settled(Stance.Prone);
        Ticks(ref s, new MoveInput(), 1);                               // key up
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Prone }, 1); // press: prone off, stand up wanted
        s.HeadBlocked = true;
        Ticks(ref s, new MoveInput(), 60);
        Assert.Equal(Stance.Prone, s.Stance); // waiting for headroom
        s.HeadBlocked = false;
        Ticks(ref s, new MoveInput(), 60);
        Assert.Equal(Stance.Stand, s.Stance);
    }

    [Fact]
    public void SteepGroundForcesAutoStand()
    {
        var s = Settled(Stance.Prone);
        s.FloorTooSteep = true;
        Ticks(ref s, new MoveInput(), 1);
        Assert.False(s.ProneLatched);
        Ticks(ref s, new MoveInput(), 60);
        Assert.Equal(Stance.Stand, s.Stance);
    }

    [Fact]
    public void ProneBlockedClearsTheLatch()
    {
        var s = new MoveState { OnFloor = true, ProneBlocked = true };
        Ticks(ref s, new MoveInput { Buttons = MoveButtons.Prone }, 60);
        Assert.False(s.ProneLatched);
        Assert.NotEqual(Stance.Prone, s.Stance);
    }

    [Fact]
    public void ProneGeometryIsLow()
    {
        Assert.Equal(0.7f, MovementStep.CapsuleHeight(Stance.Prone), 3);
        Assert.Equal(0.5f, MovementStep.EyeHeight(Stance.Prone), 3);
        Assert.Equal(1.8f, MovementStep.CapsuleHeight(Stance.Stand), 3);
    }

    [Fact]
    public void HeadZoneFollowsTheStance()
    {
        var feet = Vector3.Zero;
        Assert.Equal(PlayerHitBox.ProneLift, HitMath.HeadCenter(new HitTarget(1, feet, Stance.Prone, 0)).Y, 3);
        Assert.Equal(1.2f - PlayerHitBox.HeadRadius, HitMath.HeadCenter(new HitTarget(1, feet, Stance.Crouch, 0)).Y, 3);
        Assert.Equal(1.8f - PlayerHitBox.HeadRadius, HitMath.HeadCenter(new HitTarget(1, feet, Stance.Stand, 0)).Y, 3);
    }

    [Fact]
    public void ProneScalesRecoilAndSpread()
    {
        var def = new RecoilDef { VerticalPerShot = 2, AdsMultiplier = 0.5f, CrouchMultiplier = 0.25f, ProneMultiplier = 0.6f };
        Assert.Equal(0.6f, RecoilState.Multiplier(def, false, Stance.Prone), 3);
        Assert.Equal(0.3f, RecoilState.Multiplier(def, true, Stance.Prone), 3);
        var w = new WeaponDef { ProneSpreadMultiplier = 0.4f };
        Assert.Equal(0.8f, HitMath.ShotSpread(2f, w, Stance.Prone, false, 0, true), 3);
        Assert.Equal(2f, HitMath.ShotSpread(2f, w, Stance.Stand, false, 0, true), 3);
        Assert.Equal(2f, HitMath.ShotSpread(2f, w, Stance.Crouch, false, 0, true), 3);
    }

    [Fact]
    public void CrouchAndAimingTightenSpreadAndMovingWidensIt()
    {
        var w = new WeaponDef { ProneSpreadMultiplier = 0.5f, CrouchSpreadMultiplier = 0.8f, AdsSpreadMultiplier = 0.5f, MoveSpread = 2f };
        Assert.Equal(1.6f, HitMath.ShotSpread(2f, w, Stance.Crouch, false, 0, true), 3);
        Assert.Equal(1f, HitMath.ShotSpread(2f, w, Stance.Stand, true, 0, true), 3);
        Assert.Equal(0.8f, HitMath.ShotSpread(2f, w, Stance.Crouch, true, 0, true), 3); // stance and aim stack
        // moving adds in proportion to the speed, all of it at sprint speed and above
        Assert.Equal(3f, HitMath.ShotSpread(2f, w, Stance.Stand, false, MoveConsts.SprintSpeed / 2, true), 3);
        Assert.Equal(4f, HitMath.ShotSpread(2f, w, Stance.Stand, false, MoveConsts.SprintSpeed, true), 3);
        Assert.Equal(4f, HitMath.ShotSpread(2f, w, Stance.Stand, false, MoveConsts.SprintSpeed * 3, true), 3);
        // in the air counts as full movement, whatever the speed
        Assert.Equal(4f, HitMath.ShotSpread(2f, w, Stance.Stand, false, 0, false), 3);
        // the movement penalty is scaled by stance and aim too
        Assert.Equal(2f, HitMath.ShotSpread(2f, w, Stance.Stand, true, MoveConsts.SprintSpeed, true), 3);
        // the state overload reads stance, horizontal speed and the floor flag; falling speed does not count while on the floor
        var s = new MoveState { Stance = Stance.Crouch, Velocity = new Vector3(3, -9, 4), OnFloor = true };
        Assert.Equal((2f + 2f * 5f / MoveConsts.SprintSpeed) * 0.8f, HitMath.ShotSpread(2f, w, s, false), 3);
    }

    [Fact]
    public void AShotgunSpreadsByItsPelletConeAndOtherWeaponsByTheirOwnSpread()
    {
        Assert.Equal(3f, HitMath.OwnSpread(new WeaponDef { Pellets = 8, PelletSpread = 3f }, 0.5f), 3);
        Assert.Equal(0.5f, HitMath.OwnSpread(new WeaponDef { Pellets = 1, PelletSpread = 3f }, 0.5f), 3);
    }

    [Fact]
    public void ThePelletFanCarriesItsCone()
    {
        var m = new PelletFanMsg(7, 3, new Vector3(1, 2, 3), new Vector3(0, 0, -1), 0xDEADBEEF, 2.25f);
        Assert.Equal(m, WeaponCodec.DecodePelletFan(WeaponCodec.Encode(m)));
        Assert.Null(WeaponCodec.DecodePelletFan(new byte[31])); // the old layout without the cone
    }

    [Fact]
    public void StanceSurvivesTheSnapshotRoundTrip()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var prone = EntityRecord.Make(7, EntityType.Player, new Vector3(1, 0, 2), 0.5f, 0.1f, 80, EntityState.Protected, 3, Stance.Prone);
        var pkts = enc.Encode(10, 1, Wire.NoTick, new[] { prone });
        foreach (var p in pkts) Assert.True(dec.Apply(p, out _));
        Assert.Equal(Stance.Prone, dec.States[7].Stance);
        Assert.Equal(EntityState.Protected, dec.States[7].State);
        Assert.Equal(3, dec.States[7].Weapon);

        var stand = prone; stand.Stance = Stance.Stand;
        foreach (var p in enc.Encode(11, 2, 10, new[] { stand })) Assert.True(dec.Apply(p, out _));
        Assert.Equal(Stance.Stand, dec.States[7].Stance);
    }

    [Fact]
    public void ShippedWeaponsSteadyWhileProne()
    {
        var file = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
        Assert.Equal(0.4f, file.Find("sniper")!.ProneSpreadMultiplier, 3);
        Assert.Equal(0.6f, file.Find("ak47")!.Recoil.ProneMultiplier, 3);
        Assert.Equal(1f, file.Find("pistol")!.Recoil.ProneMultiplier, 3); // no recoil model: nothing to scale
        foreach (var w in file.Weapons) Assert.True(w.ProneSpreadMultiplier > 0 && w.ProneSpreadMultiplier <= 1, w.Id);
    }

    [Fact]
    public void ShippedGunsTightenWhenCrouchedOrAimedAndWidenWhenMoving()
    {
        var file = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
        foreach (var w in file.Weapons)
        {
            Assert.True(w.CrouchSpreadMultiplier > 0 && w.CrouchSpreadMultiplier <= 1, w.Id);
            Assert.True(w.AdsSpreadMultiplier > 0 && w.AdsSpreadMultiplier <= 1, w.Id);
            Assert.True(w.ProneSpreadMultiplier <= w.CrouchSpreadMultiplier, w.Id); // lower stance, steadier aim
            if (w.Kind != WeaponKind.Hitscan) continue;
            Assert.True(w.CrouchSpreadMultiplier < 1 && w.AdsSpreadMultiplier < 1 && w.MoveSpread > 0, w.Id);
        }
    }

    [Theory]
    [InlineData(Stance.Stand, "hud.stance.stand")]
    [InlineData(Stance.Crouch, "hud.stance.crouch")]
    [InlineData(Stance.Prone, "hud.stance.prone")]
    public void HudNamesEachStance(Stance stance, string key) => Assert.Equal(key, HudLogic.StanceKey(stance));

    [Fact]
    public void DeepWaterTakesProneAwayButWadingKeepsIt()
    {
        var deep = new MoveState { OnFloor = true, ProneLatched = true, WaterDepth = WaterConsts.SwimDepth + 0.5f, WaterSurfaceY = 2f, Position = new Vector3(0, 1f, 0) };
        MovementStep.Step(ref deep, new MoveInput { SpeedScale = 1 }, Dt);
        Assert.False(deep.ProneLatched);

        var shallow = new MoveState { OnFloor = true, ProneLatched = true, WaterDepth = 0.5f, WaterSurfaceY = 2f, Position = new Vector3(0, 1.5f, 0) };
        MovementStep.Step(ref shallow, new MoveInput { SpeedScale = 1 }, Dt);
        Assert.True(shallow.ProneLatched);
    }

    [Fact]
    public void ProneKeyPressInDeepWaterDoesNotLatch()
    {
        var s = new MoveState { OnFloor = true, WaterDepth = WaterConsts.SwimDepth + 0.5f, WaterSurfaceY = 2f, Position = new Vector3(0, 1f, 0) };
        MovementStep.Step(ref s, new MoveInput { Buttons = MoveButtons.Prone, SpeedScale = 1 }, Dt);
        Assert.False(s.ProneLatched);
    }

    [Fact]
    public void ButtonBitsAreDistinctSingleBits()
    {
        var all = Enum.GetValues<MoveButtons>().Where(b => b != MoveButtons.None).Select(b => (int)b).ToList();
        foreach (var v in all) Assert.Equal(v, v & -v); // one bit each
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.NotEqual((int)MoveButtons.Headlights, (int)MoveButtons.Prone);
    }

    [Fact]
    public void StatusNibblesHoldStanceAndEntityState()
    {
        foreach (var st in Enum.GetValues<EntityState>()) Assert.True((byte)st < 16, st.ToString());
        foreach (var sn in Enum.GetValues<Stance>()) Assert.True((byte)sn < 16, sn.ToString());
    }
}
