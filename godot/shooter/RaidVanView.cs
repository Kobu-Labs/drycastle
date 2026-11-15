using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A Raid van's body on the client (GLOSSARY "Raid van", ADR 0021). With the private Military pack it is the pack's burnt van (thieves.json <c>van.view.model</c>: +Z forward, its right-hand door a separate node that
/// slides from <c>door_closed</c> to <c>door_open</c> by the server's door fraction), turned to face -Z and drawn at its own size; a van whose server door is on the left is the mirrored model. Without it a box van of
/// boxes and cylinders: a cargo box, a cab with a dark windscreen, four wheels that turn with the road and a side door as a sliding panel (the dark opening shows behind it). Hit by hits it darkens a little by the
/// stage the hp byte carries; destroyed it is the wreck: charred, canted and with its door hanging half open. The size is <see cref="VanConfig"/>'s box (the numbers in thieves.json are the model's), the same
/// numbers the server's hit volume and solid box use. The origin is on the ground under the centre, forward is -Z.
/// </summary>
public sealed partial class RaidVanView : Node3D
{
    const float ModelWheelRadius = 0.33f, WheelRadius = 0.42f, WheelWidth = 0.3f, DoorLengthM = 1.15f, DoorSlideM = 1.3f, DoorLiftOutM = 0.09f;

    readonly VanConfig _cfg;
    readonly List<(MeshInstance3D Mesh, StandardMaterial3D Own, bool Body, Color Base)> _parts = new();
    readonly List<Node3D> _wheels = new();
    readonly List<Node3D> _modelWheels = new();   // the pack's wheels: they turn about their own axle
    readonly Node3D _pivot = new() { Name = "Pivot" };   // canted when wrecked
    MeshInstance3D? _door;
    StandardMaterial3D _wreck = null!;
    Node3D? _holder, _modelDoor;      // the pack's van (turned to face -Z, mirrored for a left door) and its sliding door node
    StandardMaterial3D? _modelPaint;  // its paint: the atlas, tinted by damage and by the wreck
    readonly List<GangMemberView> _crew = new();
    int _side = 1, _stage;
    bool _wrecked;
    float _doorOpen;

    RaidVanView(VanConfig cfg) => _cfg = cfg;

    public static RaidVanView Make(VanConfig cfg)
    {
        var v = new RaidVanView(cfg);
        v.Build();
        return v;
    }

    static StandardMaterial3D Paint(Color c, float rough = 0.85f, float metal = 0.15f) => new() { AlbedoColor = c, Roughness = rough, Metallic = metal };

    void Build()
    {
        AddChild(_pivot);
        if (!BuildModel()) BuildBox();
    }

    /// <summary>The pack's van, when the private pack has it.</summary>
    bool BuildModel()
    {
        var v = _cfg.View;
        if (v.Model == "" || SyntyLib.Instance(v.Model, v.Atlas) is not { } model) return false;
        _holder = new Node3D { Name = "Model", Position = new Vector3(0, v.ModelLiftM, 0) };
        _holder.AddChild(model);
        _pivot.AddChild(_holder);
        _modelDoor = v.DoorNode == "" ? null : model.FindChild(v.DoorNode, true, false) as Node3D;
        var atlas = model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Select(m => m.MaterialOverride).OfType<StandardMaterial3D>().FirstOrDefault();
        foreach (var body in model.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) body.Free(); // the pack's own collider: the van's solid box is the server's, and a second one would catch the player
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.38f, 0.42f, 0.28f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.08f, Metallic = 0.3f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var own = atlas?.Duplicate() as StandardMaterial3D;
        if (own != null) _modelPaint = own; // this van's own copy: its tint does not touch the others
        foreach (var m in model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            if (m.Name.ToString().Contains("Glass", StringComparison.OrdinalIgnoreCase)) m.MaterialOverride = glass; // see-through: the crew in the cab shows
            else if (own != null) m.MaterialOverride = own;
        }
        foreach (var w in model.FindChildren("SM_Veh_Van_Wheel_*", "Node3D", true, false).OfType<Node3D>()) _modelWheels.Add(w);
        PlaceModel();
        return true;
    }

    /// <summary>The model faces +Z: turned to -Z, and mirrored for a left door (the pack's door is on its right). The door node slides by the open fraction.</summary>
    void PlaceModel()
    {
        _holder!.Basis = Basis.FromEuler(new Vector3(0, Mathf.Pi, 0)) * Basis.FromScale(new Vector3(_side, 1, 1));
        if (_modelDoor == null) return;
        var v = _cfg.View;
        _modelDoor.Position = new Vector3(v.DoorClosed[0], v.DoorClosed[1], v.DoorClosed[2]).Lerp(new Vector3(v.DoorOpen[0], v.DoorOpen[1], v.DoorOpen[2]), Mathf.Clamp(_doorOpen, 0f, 1f));
    }

    void BuildBox()
    {
        var v = _cfg.View;
        var hw = _cfg.HalfWidthM; var hl = _cfg.HalfLengthM; var h = _cfg.HeightM;
        var body = Color.FromHtml(v.BodyColor); var cab = Color.FromHtml(v.CabColor); var door = Color.FromHtml(v.DoorColor);
        _wreck = Paint(Color.FromHtml(v.WreckColor), 1f, 0f);
        var dark = Paint(new Color(0.07f, 0.07f, 0.08f), 0.6f, 0.1f);

        // the cargo box takes the back two thirds, the cab the front third; both rest on a dark underbody
        var cabLen = hl * 2f * 0.34f;
        var cargoLen = hl * 2f - cabLen;
        Part("Underbody", new Vector3(hw * 1.8f, 0.35f, hl * 2f - 0.2f), new Vector3(0, 0.5f, 0), dark, false);
        Part("Cargo", new Vector3(hw * 2f, h - 0.55f, cargoLen), new Vector3(0, 0.55f + (h - 0.55f) / 2f, hl - cargoLen / 2f), Paint(body), true);
        var cabH = h - 0.55f - 0.55f;
        Part("Cab", new Vector3(hw * 2f, cabH, cabLen), new Vector3(0, 0.55f + cabH / 2f, -hl + cabLen / 2f), Paint(cab), true);
        Part("Windscreen", new Vector3(hw * 1.75f, cabH * 0.42f, 0.06f), new Vector3(0, 0.55f + cabH * 0.68f, -hl + 0.02f), Paint(new Color(0.12f, 0.17f, 0.2f), 0.2f, 0.4f), false);
        Part("Bumper", new Vector3(hw * 2.05f, 0.25f, 0.25f), new Vector3(0, 0.55f, -hl - 0.05f), dark, false);
        // side door: a sliding panel on the door's side (set by <see cref="Apply"/>), over a dark opening
        var doorH = (h - 0.55f) * 0.62f;
        _door = Part("Door", new Vector3(0.06f, doorH, DoorLengthM), Vector3.Zero, Paint(door, 0.7f, 0.25f), true);
        Part("DoorOpening", new Vector3(0.05f, doorH, DoorLengthM), Vector3.Zero, dark, false);
        // wheels: cylinders on their sides
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var wheel = new Node3D { Name = "Wheel" };
            var tyre = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = WheelRadius, BottomRadius = WheelRadius, Height = WheelWidth, RadialSegments = 14 }, MaterialOverride = dark, RotationDegrees = new Vector3(0, 0, 90) };
            wheel.AddChild(tyre);
            wheel.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(WheelWidth + 0.02f, 0.12f, 0.12f) }, MaterialOverride = Paint(new Color(0.55f, 0.55f, 0.55f), 0.5f, 0.6f) }); // a spoke: the spin shows
            wheel.Position = new Vector3(x * (hw - 0.1f), WheelRadius, z * hl * 0.58f);
            _pivot.AddChild(wheel);
            _wheels.Add(wheel);
        }
        PlaceDoor();
    }

    MeshInstance3D Part(string name, Vector3 size, Vector3 at, StandardMaterial3D mat, bool body)
    {
        var m = new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size }, MaterialOverride = mat, Position = at };
        _pivot.AddChild(m);
        _parts.Add((m, mat, body, mat.AlbedoColor));
        return m;
    }

    /// <summary>
    /// Shows what the server says: the door's fraction and side, the damage stage, whether it is the wreck. The door slides back along the side and out a little as it opens (the way a van's
    /// sliding door does). A wreck is charred and canted, its door hanging.
    /// </summary>
    public void Apply(RaidVanState state, float doorOpen, bool leftDoor, int stage)
    {
        var side = leftDoor ? -1 : 1;
        var wrecked = state == RaidVanState.Destroyed;
        if (side == _side && doorOpen == _doorOpen && stage == _stage && wrecked == _wrecked) return;
        _side = side; _doorOpen = doorOpen; _stage = stage;
        PlaceDoor();
        if (_holder != null) PlaceModel();
        if (wrecked != _wrecked)
        {
            _wrecked = wrecked;
            foreach (var (mesh, own, isBody, _) in _parts) mesh.MaterialOverride = wrecked && isBody ? _wreck : own; // the painted parts char; tyres, glass and the underbody stay dark
            _pivot.RotationDegrees = wrecked ? new Vector3(-4, 0, 11 * -_side) : Vector3.Zero; // listing to the side of its door
            _pivot.Position = wrecked ? new Vector3(0, -0.12f, 0) : Vector3.Zero;
        }
        var k = 1f - _cfg.View.StageDim * stage; // each damage stage dims the paint a little
        if (_modelPaint != null) _modelPaint.AlbedoColor = (wrecked ? Color.FromHtml(_cfg.View.WreckTint) : Colors.White) * new Color(k, k, k); // the wreck: charred (the model is burnt already; the tint takes it black)
        foreach (var (_, own, isBody, baseColour) in _parts)
            if (isBody) own.AlbedoColor = new Color(baseColour.R * k, baseColour.G * k, baseColour.B * k);
    }

    /// <summary>
    /// Seats <paramref name="count"/> of the crew in the van (the seats of van.view.seats in order; the data is for a right-door van, a left door mirrors it); more or fewer than before adds or removes the last ones.
    /// Nobody is drawn when <paramref name="draw"/> is false (the camera is far away) or the van is a wreck.
    /// </summary>
    public void SetCrew(ThievesFile data, int count, bool draw, int seed)
    {
        var v = _cfg.View;
        var want = draw && !_wrecked ? Math.Min(count, v.Seats.Count) : 0;
        if (want == _crew.Count && _crewSide == _side) return;
        if (_crewSide != _side) { Clear(); _crewSide = _side; }
        while (_crew.Count > want) { _crew[^1].QueueFree(); _crew.RemoveAt(_crew.Count - 1); }
        while (_crew.Count < want)
        {
            var seat = v.Seats[_crew.Count];
            var member = GangMemberView.Make(data, GangMemberKind.Thief, (ushort)(seed * 7 + _crew.Count * 3));
            member.Position = new Vector3(seat.X * _side, v.SeatFloorM, seat.Z);
            member.RotationDegrees = new Vector3(0, seat.YawDeg * _side, 0);
            _pivot.AddChild(member);
            member.Sit(v.SeatPelvisM);
            _crew.Add(member);
        }
    }

    int _crewSide = 1;

    void Clear()
    {
        foreach (var m in _crew) m.QueueFree();
        _crew.Clear();
    }

    void PlaceDoor()
    {
        if (_door == null) return; // the pack's van moves its own door (PlaceModel)
        var x = _side * (_cfg.HalfWidthM + 0.035f + (_doorOpen > 0.02f ? DoorLiftOutM : 0));
        var z = -_cfg.DoorAlongM + _doorOpen * DoorSlideM;
        var y = 0.55f + (_cfg.HeightM - 0.55f) * 0.5f;
        _door.Position = new Vector3(x, y, z);
        foreach (var (mesh, _, _, _) in _parts)
            if (mesh.Name == "DoorOpening") mesh.Position = new Vector3(_side * (_cfg.HalfWidthM + 0.02f), y, -_cfg.DoorAlongM);
    }

    /// <summary>Turns the wheels for <paramref name="metres"/> driven.</summary>
    public void Roll(float metres)
    {
        var a = metres / WheelRadius;
        foreach (var w in _wheels) w.RotateX(-a);
        var m = metres / ModelWheelRadius;
        foreach (var w in _modelWheels) w.RotateX(m); // the model faces +Z and is turned about Y to face -Z: its X axis is the view's -X
    }
}
