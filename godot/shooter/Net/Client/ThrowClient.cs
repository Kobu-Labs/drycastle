using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The grenade throw key on the client (#420): hold to aim (the grenade is in the hand, the weapon hidden, a semi-transparent arc shows the flight), release to throw,
/// the aim key with it to put it away. The arc is <see cref="GrenadeArc"/>, the server's own flight rule, so it shows where the grenade goes. The key is sent as
/// <see cref="MoveButtons.Throw"/>; the server runs the same <see cref="GrenadeThrower"/> and throws on the release.
/// </summary>
internal sealed class ThrowClient
{
    readonly ClientContext _ctx;
    readonly BinocularsClient _binoculars;
    readonly AircraftClient _aircraft;
    readonly ScudClient _scud;
    readonly BodyFlings _flings;

    public ThrowClient(ClientContext ctx, BinocularsClient binoculars, AircraftClient aircraft, ScudClient scud, BodyFlings flings)
    {
        _ctx = ctx; _binoculars = binoculars; _aircraft = aircraft; _scud = scud; _flings = flings;
    }

    readonly GrenadeThrower _grenadeKey = new();
    MeshInstance3D? _arcRibbon, _arcMark;
    ImmediateMesh? _arcMesh;

    /// <summary>The weapons.json index of the hotbar's grenade, or -1.</summary>
    int GrenadeWeapon()
    {
        if (_ctx.Weapons == null) return -1;
        foreach (var i in _ctx.Hotbar.Slots) if (i >= 0 && i < _ctx.Weapons.Weapons.Count && _ctx.Weapons.Weapons[i].Grenade != null) return i;
        return -1;
    }

    /// <summary>Runs the throw key for this input: aims on a press where G is the grenade's, and keeps the key (and the trigger, the hidden weapon's) out of the input where it is not.</summary>
    public void FilterInput(ref MoveInput mi)
    {
        var gi = GrenadeWeapon();
        var hasRound = gi >= 0 && _ctx.Ammo is { } a && gi < a.Slots.Count && a.Slots[gi].Mag > 0;
        var onFoot = _ctx.Me != null && !_ctx.Bot && _ctx.View.Mode.ShowsViewmodel && !_ctx.Driving && !_flings.OwnFlung && !_aircraft.AtTurret && Input.MouseMode == Input.MouseModeEnum.Captured;
        var canAim = GrenadeThrower.CanAim(onFoot, !_binoculars.Up, _ctx.Me?.State.OnLadder ?? false, hasRound, _scud.ScreenOn);
        _grenadeKey.Tick(mi.Has(MoveButtons.Throw), mi.Has(MoveButtons.Aim), canAim);
        if (!canAim) mi.Buttons &= ~MoveButtons.Throw;
        if (_grenadeKey.Aiming) mi.Buttons &= ~MoveButtons.Fire; // the weapon is out of sight: the trigger does nothing
    }

    /// <summary>The weapon id to show in the hand while the throw key is held (the grenade), or null.</summary>
    public string? HeldId() => _grenadeKey.Aiming && GrenadeWeapon() is var gi and >= 0 ? _ctx.Weapons!.Weapons[gi].Id : null;

    /// <summary>Every frame: draws the arc while aiming, hides it otherwise.</summary>
    public void UpdateArc()
    {
        if (!_grenadeKey.Aiming || _ctx.Me == null || _ctx.Weapons == null || GrenadeWeapon() is not (var gi and >= 0) || _ctx.Weapons.Weapons[gi] is not { Grenade: { } g } w)
        {
            if (_arcRibbon != null) { _arcRibbon.Visible = false; _arcMark!.Visible = false; }
            return;
        }
        if (_arcRibbon == null) MakeArc();
        var cam = _ctx.Me.Camera;
        var from = cam.GlobalPosition;
        var fwd = -cam.GlobalTransform.Basis.Z;
        var space = _ctx.World.GetWorld3D().DirectSpaceState;
        var self = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
        var trace = GrenadeArc.Fly(g, new NVec3(from.X, from.Y, from.Z), new NVec3(fwd.X, fwd.Y, fwd.Z) * w.ProjectileSpeed, (a, dir, len) =>
        {
            var o = new Vector3(a.X, a.Y, a.Z); var d = new Vector3(dir.X, dir.Y, dir.Z);
            var q = PhysicsRayQueryParameters3D.Create(o, o + d * len, CollisionLayers.WorldShots);
            q.Exclude = self;
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return new GrenadeStop(float.PositiveInfinity, NVec3.UnitY);
            var n = hit["normal"].AsVector3();
            return new GrenadeStop(o.DistanceTo(hit["position"].AsVector3()), new NVec3(n.X, n.Y, n.Z));
        });
        // a ribbon facing the camera: one quad per step, fading out along the flight
        var m = _arcMesh!;
        m.ClearSurfaces();
        var pts = trace.Points;
        m.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (var k = 0; k < pts.Count; k++)
        {
            var p = new Vector3(pts[k].X, pts[k].Y, pts[k].Z);
            var next = new Vector3(pts[Math.Min(k + 1, pts.Count - 1)].X, pts[Math.Min(k + 1, pts.Count - 1)].Y, pts[Math.Min(k + 1, pts.Count - 1)].Z);
            var prev = new Vector3(pts[Math.Max(k - 1, 0)].X, pts[Math.Max(k - 1, 0)].Y, pts[Math.Max(k - 1, 0)].Z);
            var tangent = (next - prev).Normalized();
            var side = tangent.Cross((p - from).Normalized()).Normalized() * ArcWidth * 0.5f;
            var c = new Color(1, 1, 1, 0.5f * (1f - 0.7f * k / Math.Max(1, pts.Count - 1)));
            m.SurfaceSetColor(c); m.SurfaceAddVertex(p - side);
            m.SurfaceSetColor(c); m.SurfaceAddVertex(p + side);
        }
        m.SurfaceEnd();
        _arcRibbon!.Visible = pts.Count > 1;
        var land = trace.Bounces.Count > 0 ? trace.Bounces[0] : pts[^1];
        _arcMark!.GlobalPosition = new Vector3(land.X, land.Y, land.Z);
        _arcMark.Visible = true;
    }

    const float ArcWidth = 0.05f;

    void MakeArc()
    {
        _arcMesh = new ImmediateMesh();
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = Colors.White,
        };
        _arcRibbon = new MeshInstance3D { Mesh = _arcMesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, ExtraCullMargin = 200f };
        _arcMark = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(1, 1, 1, 0.45f),
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true,
        };
        _ctx.Owned.AddChild(_arcRibbon);
        _ctx.Owned.AddChild(_arcMark);
    }

}
