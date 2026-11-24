using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Net.Client.Fauna;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Gear;

/// <summary>
/// The binoculars on the client (GLOSSARY "Binoculars"): with a pair in the bag, the binoculars key raises and lowers them. Raised, the camera
/// narrows to <see cref="Binoculars.FovDeg"/>, the weapon is neither shown nor fired or aimed, and the HUD reads the distance to what is under
/// the centre of the view: the nearest of the world (ground, walls, trees), a vehicle, a wild animal or pet (its body box) and another player.
/// It is measured here, against what this client draws, ten times a second. They come down by the key, on death, in a seat, when the mouse is
/// released (a menu) and when the pair leaves the bag. The server is told only that they went up or down, and tells the others, whose clients
/// pose that player's character with the pair held at the face (<see cref="CharacterView.SetBinoculars"/>).
/// </summary>
internal sealed class BinocularsClient
{
    readonly ClientContext _ctx;
    readonly ItemsClient _items;
    readonly ItemsFile _data;
    readonly VehicleHulls _hulls;
    readonly AnimalsClient _animals;
    readonly Func<bool> _pinned;
    /// <summary>The other players who hold the binoculars up and the pair each holds (<see cref="EventKind.BinocularsRaised"/>), kept by id so a character made later is posed at once.</summary>
    readonly Dictionary<ushort, ItemDef> _by = new();
    double _measuredAt;
    int? _range;

    /// <param name="pinned">The demon has the player pinned: the binoculars come down.</param>
    public BinocularsClient(ClientContext ctx, ItemsClient items, ItemsFile data, VehicleHulls hulls, AnimalsClient animals, Func<bool> pinned)
    {
        _ctx = ctx; _items = items; _data = data; _hulls = hulls; _animals = animals; _pinned = pinned;
    }

    /// <summary>The own binoculars are up: they are the zoom, and the scope and the weapon's sway are not.</summary>
    public bool Up { get; private set; }

    /// <summary>A figure was made for a player: it holds the binoculars up when they are.</summary>
    public void OnAppeared(ushort id, RemotePlayer player) => player.Binoculars = _by.GetValueOrDefault(id);

    /// <summary>A player left.</summary>
    public void Forget(ushort player) => _by.Remove(player);


    /// <summary>The others' raised binoculars (<see cref="EventKind.BinocularsRaised"/>).</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.BinocularsRaised && Binoculars.TryDecodeRaised(payload, out var holder, out var up, out var pair)) OnRaised(holder, up, pair);
    }

    void OnRaised(ushort player, bool up, int item)
    {
        var pair = up && _data.At(item) is { Binoculars: true } def ? def : null;
        if (pair != null) _by[player] = pair; else _by.Remove(player);
        if (_ctx.Remotes.TryGetValue(player, out var r)) r.Binoculars = pair;
    }

    /// <summary>Tells the server the binoculars went up or down, so the others see it.</summary>
    void Send(bool up) => _ctx.SendHuman(EventKind.RaiseBinoculars, Binoculars.EncodeRaise(up));
    bool CarriesBinoculars => CarriedBinoculars != null;

    /// <summary>The pair in the bag (the narrowest when there are several), or null.</summary>
    ItemDef? CarriedBinoculars
    {
        get
        {
            ItemDef? best = null;
            foreach (var held in _items.BagContents)
                if (held?.Def is { Binoculars: true } pair && (best == null || Binoculars.FovOf(pair) < Binoculars.FovOf(best))) best = pair;
            return best;
        }
    }

    /// <summary>What the HUD shows of the binoculars: whether they are up, and the distance read (null: nothing in range).</summary>
    public (bool Up, int? RangeM, bool CanMark) View() => (Up, _range, CarriedBinoculars is { MarkSeconds: > 0 });

    public bool HandleInput(InputEvent e)
    {
        if (_ctx.Bot || e.IsEcho()) return false;
        if (Up && e.IsActionPressed("binoculars_mark")) { _animals.Mark(CarriedBinoculars); _ctx.Root.GetViewport().SetInputAsHandled(); return true; }
        if (!e.IsActionPressed("binoculars")) return false;
        if (Up) Lower();
        else
        {
            if (_ctx.Me == null || _ctx.Dead || _pinned() || _ctx.View.Mode.ScopeSuppressed || Input.MouseMode != Input.MouseModeEnum.Captured || !CarriesBinoculars) return false;
            Up = true;
            _measuredAt = 0; _range = null;
            Send(true);
            Sfx.Ui("ui.accept");
        }
        _ctx.Root.GetViewport().SetInputAsHandled();
        return true;
    }

    public void Lower()
    {
        if (!Up) return;
        Up = false;
        _range = null;
        if (_ctx.Me != null) _ctx.Me.HandsBusy = false;
        Send(false);
        Sfx.Ui("ui.back");
    }

    /// <summary>Once a frame, from the scope step: true while the binoculars are up (they are then the zoom), after lowering them when they cannot stay up.</summary>
    public bool Tick()
    {
        if (!Up || _ctx.Me == null) return false;
        if (_ctx.Dead || _pinned() || Input.MouseMode != Input.MouseModeEnum.Captured || !CarriesBinoculars) { Lower(); return false; }
        _ctx.Me.HandsBusy = true;
        _ctx.Me.ZoomFov = CarriedBinoculars is { } pair ? Binoculars.FovOf(pair) : Binoculars.FovDeg; _ctx.Me.IronSightZoom = false;
        _ctx.Viewmodel.Aiming = false;
        if (_ctx.Now - _measuredAt >= Binoculars.MeasureSeconds)
        {
            _measuredAt = _ctx.Now;
            _range = Binoculars.Reading(MeasureRange());
        }
        return true;
    }

    /// <summary>Metres from the eye to the nearest thing under the centre of the view, null with nothing within <see cref="Binoculars.MaxRangeM"/>.</summary>
    float? MeasureRange()
    {
        var cam = _ctx.Me!.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var best = float.PositiveInfinity;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * Binoculars.MaxRangeM, CollisionLayers.WorldShots);
        q.Exclude = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
        var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) best = from.DistanceTo(hit["position"].AsVector3());
        if (_hulls.Along(from, dir, MathF.Min(Binoculars.MaxRangeM, best), out _, out var vh, _ctx.Cars.MySeat()?.Car)) best = MathF.Min(best, vh.Distance);
        var o = new NVec3(from.X, from.Y, from.Z); var d = new NVec3(dir.X, dir.Y, dir.Z);
        foreach (var a in _animals.ById.Values)
        {
            if (!GodotObject.IsInstanceValid(a.Node) || a.HitBox.Count < 3) continue;
            var p = a.Node.GlobalPosition;
            var target = new HitTarget(0, new NVec3(p.X, p.Y, p.Z), Stance.Stand, a.Node.Rotation.Y, Box: new NVec3(a.HitBox[0], a.HitBox[1], a.HitBox[2]), BoxAhead: a.Def.HitBoxAhead * a.Size);
            if (HitMath.RayBox(o, d, target) is { } at) best = MathF.Min(best, at);
        }
        foreach (var r in _ctx.Remotes.Values)
        {
            if (!GodotObject.IsInstanceValid(r.Node) || !r.Node.Visible) continue;
            var p = r.Node.GlobalPosition;
            if (HitMath.RayCapsule(o, d, new NVec3(p.X, p.Y, p.Z), PlayerHitBox.StandHeight, PlayerHitBox.Radius) is { } at) best = MathF.Min(best, at);
        }
        return best <= Binoculars.MaxRangeM ? best : null;
    }

}
