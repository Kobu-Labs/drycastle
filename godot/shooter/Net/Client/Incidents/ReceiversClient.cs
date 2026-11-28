using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Cartel;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Aviation;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Menu;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Incidents;

/// <summary>
/// The Heists' Receivers on the client (GLOSSARY "Receiver"; docs/cartel.md "Receivers"; world entity <see cref="ReceiverRules.TypeId"/>; cartel.json <c>receivers</c>). Each is drawn as its model (the Cartel pallet with a
/// buyer beside it, the utility helicopter with its rotors already turning and a pilot in the seat, the Battle Royale boat with a boatman at the helm; a box without the private pack) where the entity is, turned as it
/// faces, interpolated like every remote body: when the server sends it off, the helicopter's and the boat's way out is nothing but the entity's pose moving, so every client sees it. The Duffels lying on it
/// are drawn by <see cref="DuffelsClient"/> from its pose and their slot (<see cref="SlotTransform"/>). The Cartel men cannot be hurt: they are a look, nothing collides with them or is tested against them. The server decides
/// everything; this only shows it, and words the prompt of the interact key at the Cartel man (<see cref="EventKind.ReceiverReach"/>: the leader's "tell the pilot to leave", or why not).
/// </summary>
public sealed class ReceiversClient
{
    sealed class View
    {
        public Node3D Node = null!;
        public ReceiverKind Kind;
        public RotorFx? Rotor;
        public float GroundY;
        public bool Placed;
    }

    readonly ClientContext _ctx;
    readonly ReceiversData _data;
    readonly Dictionary<ushort, View> _views = new();
    ushort _reach;
    ReceiverKind _reachKind;
    ReceiverAsk _ask;
    int _onBoard, _min;
    string? _text;
    ReceiverAsk _textAsk;
    ReceiverKind _textKind;
    int _textOn, _textMin;
    Binding _textKey;

    internal ReceiversClient(ClientContext ctx, ReceiversData data)
    {
        _ctx = ctx; _data = data;
        ReceiverRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.ReceiverReach || !ReceiverCodec.TryDecodeReach(payload, out var id, out var k, out var ask, out var on, out var min)) return;
        _reach = id; _reachKind = k; _ask = ask; _onBoard = on; _min = min;
    }

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != ReceiverRules.TypeId || ReceiverRules.KindOf(e.State.State) is not { } kind) return;
        var def = _data.Of(kind);
        var node = new Node3D { Name = $"Receiver{e.Id}", Visible = false }; // shown at its first placed pose, never at the world's origin
        var model = Model(def);
        node.AddChild(model);
        var man = ContactView.Figure(def.Figure.Look);
        man.Name = "Man";
        man.Position = new Vector3(def.Figure.Pos[0], def.Figure.Pos[1], def.Figure.Pos[2]);
        man.RotationDegrees = new Vector3(0, def.Figure.YawDeg, 0);
        node.AddChild(man);
        _ctx.Owned.AddChild(node);
        _views[e.Id] = new View { Node = node, Kind = kind, Rotor = kind == ReceiverKind.Helicopter && ShotFx.Enabled ? RotorFx.Find(model) : null, GroundY = e.State.Position.Y };
    }

    void OnDespawned(ushort id)
    {
        if (_views.Remove(id, out var v))
        {
            v.Rotor?.Stop();
            v.Node.QueueFree();
        }
        if (_reach == id) { _reach = 0; _ask = ReceiverAsk.None; }
    }

    /// <summary>The model of a kind in its frame (forward -Z, origin on the ground), or a box of its size.</summary>
    Node3D Model(ReceiverKindData def)
    {
        var root = new Node3D { Name = "Model" };
        Node3D? model = null;
        if (def.Model.Length > 0)
        {
            if (def.Atlas.Length > 0) model = SyntyLib.Instance(def.Model, def.Atlas);
            else if (ModelLib.Instance(def.Model) is { } asset) // a generated glb under assets/ (the helicopter): the map's library knows only pack: and prim: ids
            {
                foreach (var co in asset.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; }
                model = asset;
            }
        }
        if (model != null)
        {
            model.RotationDegrees = new Vector3(0, def.ModelYawDeg, 0);
            root.AddChild(model);
            return root;
        }
        var size = new Vector3(def.SizeM[0], def.SizeM[1], def.SizeM[2]);
        root.AddChild(new MeshInstance3D
        {
            Name = "Box", Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromHtml(def.Colour), Roughness = 0.95f }, Position = new Vector3(0, size.Y / 2, 0),
        });
        return root;
    }

    /// <summary>Every frame: each Receiver at its pose (interpolated) and a helicopter's rotors at full speed. Runs before the Duffels are placed, which ride on the pose drawn here.</summary>
    public void Render(float dt)
    {
        if (_views.Count == 0) return;
        var rt = _ctx.RenderTick;
        foreach (var (id, v) in _views)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.Buf.At(rt) is not { } s) continue;
            v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            v.Node.Rotation = new Vector3(s.Pitch, s.Yaw, 0);
            v.Node.Visible = true; v.Placed = true;
            if (v.Rotor == null) continue;
            var up = s.Pos.Y - v.GroundY;
            var wash = up < RotorFx.WashHeight ? ((Vector3 At, float Strength, bool Water)?)(new Vector3(s.Pos.X, v.GroundY, s.Pos.Z), 1f - up / RotorFx.WashHeight, false) : null;
            var listener = _ctx.Me is { } me ? me.GlobalPosition.DistanceTo(v.Node.GlobalPosition) : 0f;
            v.Rotor.Step(dt, 1f, 0.5f, listener, wash); // already spinning when it lands in view, and for its whole way out
        }
    }

    /// <summary>The place of a loaded Duffel's slot of the Receiver of entity <paramref name="entity"/> in the world as drawn this frame (on its pose: it rides with it), or null while it is not drawn yet or has no such slot.</summary>
    public Transform3D? SlotTransform(ushort entity, int slot)
    {
        if (!_views.TryGetValue(entity, out var v) || !v.Placed || _data.Of(v.Kind).CargoVolume.Slot(slot) is not { } place) return null;
        return v.Node.GlobalTransform.Orthonormalized() * new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(place.YawDeg)), new Vector3(place.Position.X, place.Position.Y, place.Position.Z));
    }

    /// <summary>
    /// At the Cartel man of the own Job's Receiver: the leader's "tell the pilot to leave" (the interact key, which the server reads as the ordinary press) while at least the minimum is on board; fewer: why not and how many;
    /// a mate: that only the leader sends it off. Null elsewhere. Rebuilt only when what it says or the key changes.
    /// </summary>
    public string? Prompt()
    {
        if (_reach == 0 || _ask == ReceiverAsk.None || _ctx.Dead || _ctx.Driving || !_views.ContainsKey(_reach)) return null;
        var key = GameSettings.Current.Keys.Get("interact");
        if (_text != null && _textAsk == _ask && _textKind == _reachKind && _textOn == _onBoard && _textMin == _min && _textKey == key) return _text;
        _textAsk = _ask; _textKind = _reachKind; _textOn = _onBoard; _textMin = _min; _textKey = key;
        var man = Strings.T("receiver." + ReceiverRules.Man(_reachKind));
        return _text = _ask switch
        {
            ReceiverAsk.Leave => Strings.F("hud.receiver.leave." + ReceiverRules.Man(_reachKind), InputBindings.Name(key)),
            ReceiverAsk.NeedMore => Strings.F("hud.receiver.need", _onBoard, _min),
            _ => Strings.F("hud.receiver.leader", man),
        };
    }
}
