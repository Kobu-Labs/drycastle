using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class WeaponTests
{
    static readonly WeaponsFile File = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
    const double Dt = 1.0 / 30;

    sealed class Rig
    {
        public double T = 10;
        public readonly PlayerWeapons W = new(File);
        public Rig() { W.Reset(T); }
        public FireOutcome? Tick(bool fire = false, bool reload = false, int slot = 0, bool next = false)
        {
            T += Dt;
            return W.Update(T, new WeaponInput(fire, reload, slot, next));
        }
        public void Idle(double s) { for (var n = (int)Math.Round(s / Dt); n > 0; n--) Tick(slot: W.Slot); }
        public int Shots(int ticks, bool fire = true) { var n = 0; for (var i = 0; i < ticks; i++) if (Tick(fire, slot: W.Slot) != null) n++; return n; }
    }

    static WeaponDef Def(string id) => File.Find(id)!;

    [Fact]
    public void StartsWithPistolAndFullAmmo()
    {
        var r = new Rig();
        Assert.Equal(1, r.W.Slot);
        Assert.Equal("pistol", r.W.Current.Id);
        for (var s = 1; s <= File.Weapons.Count; s++) { Assert.Equal(File.Weapons[s - 1].MagazineSize, r.W.Mag(s)); Assert.Equal(File.Weapons[s - 1].ReserveAmmo, r.W.Reserve(s)); }
        Assert.Equal(File.Weapons.Count, r.W.Hotbar.Count);
        Assert.Equal("bazooka", r.W.Hotbar[2].Id);
    }

    [Fact]
    public void PistolIsSemiAuto()
    {
        var r = new Rig();
        Assert.Equal(1, r.Shots(30)); // holding fire for 1 s: one shot
        r.Tick(false, slot: 1);
        r.Idle(0.2);
        Assert.NotNull(r.Tick(true, slot: 1));
        Assert.Equal(13, r.W.Mag(1));
    }

    [Fact]
    public void AkIsFullAutoAtSixHundredRpm()
    {
        var r = new Rig();
        r.Tick(slot: 2); r.Idle(0.6);
        Assert.Equal(2, r.W.Slot);
        var n = r.Shots(30); // 1 s of held fire
        Assert.InRange(n, 9, 10);
        Assert.Equal(30 - n, r.W.Mag(2));
    }

    [Fact]
    public void SwitchCancelsReload()
    {
        var r = new Rig();
        r.Tick(slot: 2);
        Assert.NotNull(r.Tick(true, slot: 2));
        r.Tick(false, true, 2);
        Assert.True(r.W.Reloading);
        r.Tick(slot: 4);
        Assert.False(r.W.Reloading);
        Assert.Equal(4, r.W.Slot);
        r.Tick(slot: 2); r.Idle(2.7);
        Assert.Equal(29, r.W.Mag(2)); // the cancelled reload never finished and nothing was restarted
    }

    [Fact]
    public void NextWeaponCyclesOnTheEdge()
    {
        var r = new Rig();
        r.Tick(next: true); r.Tick(next: true);
        Assert.Equal(2, r.W.Slot);
        r.Tick(next: false); r.Tick(next: true);
        Assert.Equal(3, r.W.Slot);
    }

    [Fact]
    public void ReloadTakesReloadTimeAndMovesAmmo()
    {
        var r = new Rig();
        r.Tick(slot: 2); r.Idle(0.6);
        r.Shots(10);
        var used = 30 - r.W.Mag(2);
        var start = r.T;
        r.Tick(false, true, 2);
        Assert.True(r.W.Reloading);
        while (r.W.Reloading) r.Tick(slot: 2);
        Assert.InRange(r.T - start, 2.5 - 0.05, 2.5 + 0.1);
        Assert.Equal(30, r.W.Mag(2));
        Assert.Equal(90 - used, r.W.Reserve(2));
    }

    [Fact]
    public void FiringWhileReloadingDoesNothingAndEmptyMagStartsReload()
    {
        var r = new Rig();
        r.Tick(false, true);  // nothing to reload yet
        Assert.False(r.W.Reloading);
        for (var i = 0; i < 15; i++) { r.Tick(true); r.Tick(false); r.Idle(0.15); }
        Assert.Equal(0, r.W.Mag(1));
        Assert.Null(r.Tick(true));
        Assert.True(r.W.Reloading);
        Assert.Null(r.Tick(false)); Assert.Null(r.Tick(true));
    }

    [Fact]
    public void ReserveRunsOutAndKillRefills()
    {
        var r = new Rig();
        for (var i = 0; i < 15; i++) { r.Tick(true); r.Tick(false); r.Idle(0.15); }
        r.Tick(false, true); r.Idle(2);
        Assert.Equal(15, r.W.Mag(1));
        Assert.Equal(45, r.W.Reserve(1));
        r.W.RefillReserves();
        Assert.Equal(60, r.W.Reserve(1));
        Assert.Equal(90, r.W.Reserve(2));
        Assert.Equal(4, r.W.Reserve(3));
        Assert.True(r.W.Changed);
    }

    [Fact]
    public void SpreadGrowsWhileFiringAndRecovers()
    {
        var r = new Rig();
        r.Tick(slot: 2); r.Idle(0.6);
        Assert.Equal(1.0f, r.W.Spread, 3);
        var spreads = new List<float>();
        for (var i = 0; i < 30; i++) { var o = r.Tick(true, slot: 2); if (o is { } f) spreads.Add(f.SpreadDegrees); }
        Assert.Equal(1.0f, spreads[0], 3);
        Assert.Equal(1.25f, spreads[1], 3);
        Assert.True(spreads.Max() <= 4.0001f);
        Assert.True(spreads[^1] > spreads[0]);
        r.Idle(0.2);
        var high = r.W.Spread;
        r.Idle(1.0);
        Assert.True(r.W.Spread < high);
        Assert.Equal(1.0f, r.W.Spread, 3);
    }

    [Fact]
    public void KnifeSwingsAtItsInterval()
    {
        var r = new Rig();
        r.Tick(slot: 4); r.Idle(0.4);
        Assert.NotNull(r.Tick(true, slot: 4));
        Assert.Null(r.Tick(true, slot: 4));
        r.Idle(0.5);
        Assert.NotNull(r.Tick(true, slot: 4));
    }

    [Fact]
    public void SpeedMultiplierFollowsTheWantedSlot()
    {
        var r = new Rig();
        Assert.Equal(1.0f, r.W.SpeedFor(0));
        Assert.Equal(0.95f, r.W.SpeedFor(2));
        Assert.Equal(0.85f, r.W.SpeedFor(3));
        Assert.Equal(1.1f, r.W.SpeedFor(4), 3);
    }

    // ---- damage and geometry ----

    [Fact]
    public void DamageFalloffAndHeadshot()
    {
        var ak = Def("ak47"); var pistol = Def("pistol");
        Assert.Equal(30, HitMath.HitscanDamage(ak, 5, false));
        Assert.Equal(60, HitMath.HitscanDamage(ak, 5, true));
        Assert.Equal(30, HitMath.HitscanDamage(ak, 40, false));
        Assert.Equal(24, HitMath.HitscanDamage(ak, 70, false));  // 30 * (1 - 0.4 * 0.5)
        Assert.Equal(18, HitMath.HitscanDamage(ak, 100, false)); // 60 %
        Assert.Equal(18, HitMath.HitscanDamage(ak, 500, false));
        Assert.Equal(24, HitMath.HitscanDamage(pistol, 25, false));
        Assert.Equal(17, HitMath.HitscanDamage(pistol, 60, false)); // 70 % of 24 = 16.8
        Assert.Equal(48, HitMath.HitscanDamage(pistol, 10, true));
    }

    [Fact]
    public void TimeToKillMatchesTicket()
    {
        Assert.Equal(4, (int)Math.Ceiling(100 / HitMath.HitscanDamage(Def("ak47"), 5, false)));
        Assert.Equal(2, (int)Math.Ceiling(100 / HitMath.HitscanDamage(Def("ak47"), 5, true)));
        Assert.Equal(5, (int)Math.Ceiling(100 / HitMath.HitscanDamage(Def("pistol"), 5, false)));
        Assert.Equal(3, (int)Math.Ceiling(100 / HitMath.HitscanDamage(Def("pistol"), 5, true)));
    }

    static HitTarget T(ushort id, float x, float y, float z, float yaw = 0, Stance stance = Stance.Stand) => new(id, new Vector3(x, y, z), stance, yaw);

    [Fact]
    public void RayHitsBodyAndHead()
    {
        var eye = new Vector3(0, 1.65f, 0);
        var target = T(2, 0, 0, -10);
        var body = HitMath.Raycast(eye, Vector3.Normalize(new Vector3(0, 0.9f - 1.65f, -10)), 100, float.PositiveInfinity, new[] { target }, 1);
        Assert.NotNull(body); Assert.False(body!.Value.Head); Assert.InRange(body.Value.Distance, 9.6f, 10.0f);
        var head = HitMath.Raycast(eye, new Vector3(0, 0, -1), 100, float.PositiveInfinity, new[] { target }, 1);
        Assert.True(head!.Value.Head);
        var over = HitMath.Raycast(eye, Vector3.Normalize(new Vector3(0, 0.4f, -10)), 100, float.PositiveInfinity, new[] { target }, 1);
        Assert.Null(over);
        var side = HitMath.Raycast(eye, Vector3.Normalize(new Vector3(0.5f, 0, -10)), 100, float.PositiveInfinity, new[] { target }, 1);
        Assert.Null(side);
        var edge = HitMath.Raycast(eye, Vector3.Normalize(new Vector3(0.25f, 0, -10)), 100, float.PositiveInfinity, new[] { target }, 1);
        Assert.NotNull(edge); Assert.False(edge!.Value.Head);
    }

    [Fact]
    public void RayIgnoresShooterWallsRangeAndTakesNearest()
    {
        var eye = new Vector3(0, 1.65f, 0);
        var fwd = new Vector3(0, 0, -1);
        var near = T(2, 0, 0, -5); var far = T(3, 0, 0, -10);
        Assert.Equal(2, HitMath.Raycast(eye, fwd, 100, float.PositiveInfinity, new[] { far, near }, 1)!.Value.Id);
        Assert.Null(HitMath.Raycast(eye, fwd, 100, 3f, new[] { far, near }, 1));       // wall at 3 m
        Assert.Equal(2, HitMath.Raycast(eye, fwd, 100, 7f, new[] { far, near }, 1)!.Value.Id);
        Assert.Null(HitMath.Raycast(eye, fwd, 4, float.PositiveInfinity, new[] { near }, 1)); // out of range
        Assert.Null(HitMath.Raycast(eye, fwd, 100, float.PositiveInfinity, new[] { T(1, 0, 0, -5) }, 1)); // own capsule
        Assert.Null(HitMath.Raycast(eye, -fwd, 100, float.PositiveInfinity, new[] { near }, 1));  // behind
    }

    [Fact]
    public void CrouchingLowersTheHead()
    {
        var eye = new Vector3(0, 1.65f, 0);
        Assert.Null(HitMath.Raycast(eye, new Vector3(0, 0, -1), 100, float.PositiveInfinity, new[] { T(2, 0, 0, -8, stance: Stance.Crouch) }, 1)); // passes over a crouched head
        var h = HitMath.Raycast(new Vector3(0, 1.05f, 0), new Vector3(0, 0, -1), 100, float.PositiveInfinity, new[] { T(2, 0, 0, -8, stance: Stance.Crouch) }, 1);
        Assert.True(h!.Value.Head);
    }

    [Fact]
    public void SpreadStaysInsideTheCone()
    {
        var rng = new Random(5);
        var f = HitMath.Forward(0.7f, 0.2f);
        double max = 0; var off = false;
        for (var i = 0; i < 500; i++)
        {
            var d = HitMath.SpreadDirection(0.7f, 0.2f, 2f, rng);
            Assert.Equal(1f, d.Length(), 3);
            var a = Math.Acos(Math.Clamp(Vector3.Dot(d, f), -1, 1)) * 180 / Math.PI;
            max = Math.Max(max, a); off |= a > 0.1;
        }
        Assert.InRange(max, 1.5, 2.001); Assert.True(off);
        Assert.Equal(f, HitMath.SpreadDirection(0.7f, 0.2f, 0, rng));
    }

    [Fact]
    public void ForwardMatchesMovementConvention()
    {
        Assert.Equal(new Vector3(0, 0, -1), HitMath.Forward(0, 0));
        var l = HitMath.Forward(MathF.PI / 2, 0); // positive yaw turns left: faces -X
        Assert.Equal(-1f, l.X, 4);
        Assert.Equal(1f, HitMath.Forward(0, MathF.PI / 2).Y, 4);
    }

    [Fact]
    public void MeleeReachAndCone()
    {
        var knife = Def("knife");
        var me = Vector3.Zero;
        Assert.NotNull(HitMath.Melee(me, 0, knife.Reach, new[] { T(2, 0, 0, -2.0f) }, 1));     // edge at 1.65 m
        Assert.Null(HitMath.Melee(me, 0, knife.Reach, new[] { T(2, 0, 0, -2.3f) }, 1));        // edge at 1.95 m
        Assert.Null(HitMath.Melee(me, 0, knife.Reach, new[] { T(2, 0, 0, 1.0f) }, 1));         // behind me
        Assert.Null(HitMath.Melee(me, 0, knife.Reach, new[] { T(2, 0, 3f, -1.0f) }, 1));       // a floor above
        var two = HitMath.Melee(me, 0, knife.Reach, new[] { T(3, 0, 0, -1.5f), T(2, 0, 0, -0.8f) }, 1);
        Assert.Equal(2, two!.Value.Id);
    }

    [Fact]
    public void BackstabIsTheBackNinetyDegrees()
    {
        // victim at origin facing -Z (yaw 0); straight behind is +Z
        Assert.True(HitMath.IsBackstab(new Vector3(0, 0, 1.5f), Vector3.Zero, 0));
        Assert.True(HitMath.IsBackstab(new Vector3(1.0f, 0, 1.5f), Vector3.Zero, 0));    // 34 degrees off straight behind
        Assert.False(HitMath.IsBackstab(new Vector3(1.5f, 0, 1.0f), Vector3.Zero, 0));   // 56 degrees off
        Assert.False(HitMath.IsBackstab(new Vector3(0, 0, -1.5f), Vector3.Zero, 0));     // in front
        Assert.False(HitMath.IsBackstab(new Vector3(1.5f, 0, 0), Vector3.Zero, 0));      // side
        // victim turned around: now -Z is the back
        Assert.True(HitMath.IsBackstab(new Vector3(0, 0, -1.5f), Vector3.Zero, MathF.PI));
        var knife = Def("knife");
        Assert.Equal(100, HitMath.MeleeDamage(knife, true));
        Assert.Equal(50, HitMath.MeleeDamage(knife, false));
        var hit = HitMath.Melee(new Vector3(0, 0, 1.2f), 0, knife.Reach, new[] { T(2, 0, 0, 0, yaw: 0) }, 1);
        Assert.True(hit!.Value.Backstab);
    }

    [Fact]
    public void WeaponMessagesRoundTrip()
    {
        var shot = new ShotMsg(3, 1, new Vector3(1, 2, 3), new Vector3(4, 5, 6));
        Assert.Equal(shot, WeaponCodec.DecodeShot(WeaponCodec.Encode(shot)));
        var hit = new HitMsg(2, 3, 1, 60, true, false, true);
        Assert.Equal(hit, WeaponCodec.DecodeHit(WeaponCodec.Encode(hit)));
        var ammo = new AmmoMsg(2, 1.5f, new List<(ushort, ushort)> { (15, 60), (30, 90), (1, 4), (0, 0) });
        Assert.Equal(ammo, WeaponCodec.DecodeAmmo(WeaponCodec.Encode(ammo)));
        var tp = WeaponCodec.DecodeDebugTeleport(WeaponCodec.EncodeDebugTeleport(4, new Vector3(1, 2, 3)));
        Assert.Equal((ushort)4, tp!.Value.Target);
        Assert.Null(WeaponCodec.DecodeShot(new byte[3]));
        Assert.Null(WeaponCodec.DecodeAmmo(new byte[] { 1, 0, 0, 0, 0, 9 }));
    }

    [Fact]
    public void AmmoMessageReflectsWeaponState()
    {
        var r = new Rig();
        r.Tick(true);
        var m = WeaponCodec.BuildAmmo(r.W, r.T);
        Assert.Equal(1, m.Slot);
        Assert.Equal(((ushort)14, (ushort)60), m.Slots[0]);
        Assert.Equal(File.Weapons.Count, m.Slots.Count);
    }

    [Fact]
    public void SniperIsABoltActionOneShotHeadshotWithAScope()
    {
        var w = File.Find("sniper")!;
        Assert.Equal(WeaponKind.Hitscan, w.Kind);
        Assert.False(w.FullAuto);
        Assert.True(w.Damage * w.HeadshotMultiplier >= 100, "a headshot kills in one shot");
        Assert.True(w.Damage < 100, "a body shot does not");
        Assert.True(w.Range > 300 && w.ZoomFov is > 0 and < 60);
        Assert.Equal(1.4f, w.FireInterval);
    }
}
