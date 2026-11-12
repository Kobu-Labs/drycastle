using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>Target dummies on the client (#148): the roster gives their names; their bodies come from snapshots (entity type Target).</summary>
public partial class NetClient
{
    /// <summary>Orange name tag of a dummy.</summary>
    static readonly Color TargetTint = new(1f, 0.55f, 0.1f);

    void OnTargetRoster(byte[] payload)
    {
        if (TargetCodec.DecodeRoster(payload) is not { } list)
        {
            GD.PrintErr("client: bad target roster");
            return;
        }
        foreach (var (id, name) in list)
        {
            _names[id] = name;
            Match.NameTarget(id, name);
            // a snapshot may have drawn the dummy before the roster arrived: fix its name tag
            if (_remotes.TryGetValue(id, out var r))
                foreach (var label in r.Node.GetChildren().OfType<Label3D>()) label.Text = name;
        }
        GD.Print($"client: {list.Count} target dummies");
    }
}
