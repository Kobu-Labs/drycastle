using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The shootable plates of the placed road signs on a client (ADR 0039, GLOSSARY "Road sign"). The server owns each plate's state
/// (<see cref="BreakableSigns"/>) and tells the changes; this bends and straightens the "Plate" node of the sign's model (the placed object's own node, found
/// by object id). A plate hangs from its upper clamp, which is the node's origin (scripts/make_road_sign_models.py): bent is a roll about it (the side from
/// <see cref="SignBend.Side"/>), a tip out at the bottom and a small sag, the same on every client; whole puts the node back as the model has it. Visual only.
/// The plate list is the map's (MapLoader.ObjectSignPlates()), the server's list, and its index is the wire's.
/// </summary>
public partial class SignPlates : Node
{
    /// <summary>The plates of this map on this client (null where none is drawn); the network client reads it (ADR 0039).</summary>
    public static SignPlates? Current { get; private set; }

    public Drycastle.MapLoader.MapLoader Map { get; init; } = null!;
    public IReadOnlyList<Drycastle.MapLoader.MapLoader.ObjectSignPlate> Plates { get; init; } = Array.Empty<Drycastle.MapLoader.MapLoader.ObjectSignPlate>();

    SignPlateState[] _state = Array.Empty<SignPlateState>();
    // the model's own place for each plate node, kept the first time it is bent
    readonly Dictionary<int, (Vector3 Position, Vector3 Rotation)> _rest = new();

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    public override void _Ready() => _state = new SignPlateState[Plates.Count];

    /// <summary>The server says plate <paramref name="plate"/> (its place in the placed objects' plate list) is now in <paramref name="state"/>.</summary>
    public void SetPlate(int plate, SignPlateState state)
    {
        if ((uint)plate >= (uint)_state.Length || _state[plate] == state) return;
        _state[plate] = state;
        Apply(plate, state);
    }

    /// <summary>Every plate not listed is whole (the join catch-up, a new round).</summary>
    public void SyncPlates(IReadOnlyList<SignPlateMsg> bent)
    {
        for (var i = 0; i < _state.Length; i++) SetPlate(i, SignPlateState.Whole);
        foreach (var m in bent) SetPlate(m.Index, m.State);
    }

    void Apply(int plate, SignPlateState state)
    {
        var p = Plates[plate];
        if (!Map.ObjectNodes.TryGetValue(p.ObjectId, out var obj) || !IsInstanceValid(obj) || obj.FindChild(p.Part, true, false) is not Node3D node) return;
        if (!_rest.TryGetValue(plate, out var rest)) _rest[plate] = rest = (node.Position, node.Rotation);
        if (state == SignPlateState.Whole)
        {
            node.Position = rest.Position;
            node.Rotation = rest.Rotation;
            return;
        }
        var side = SignBend.Side(plate);
        node.Position = rest.Position + Vector3.Down * SignBend.SagM;
        node.Rotation = rest.Rotation + new Vector3(Mathf.DegToRad(SignBend.PitchDeg), 0f, Mathf.DegToRad(SignBend.RollDeg) * side);
    }
}
