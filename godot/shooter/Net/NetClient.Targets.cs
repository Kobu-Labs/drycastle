using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>Target dummies on the client (#148): the roster gives their names; their bodies come from snapshots (entity type Target).</summary>
public partial class NetClient
{
    /// <summary>Orange name tag of a dummy.</summary>
    static readonly Color TargetTint = new(1f, 0.55f, 0.1f);

    static readonly Color FriendTagTint = new(0.3f, 1f, 0.3f);

    /// <summary>Name tags over players: none in free-for-all, only teammates (in the friend colour) in team deathmatch, a squad mate's with the squad's name after it (#379); target dummies keep their orange tag.</summary>
    void UpdateNameTag(ushort id, Remote r)
    {
        var dummy = TargetDummies.IsDummyId(id);
        var show = (dummy || (Match.IsTeamMode && Match.AreFriends(_myId, id))) && !DustHides(r.Node.GlobalPosition + Vector3.Up * 1.6f); // not through dust (#345)
        var squad = !dummy && Match.AreSquadMates(_myId, id) ? Match.SquadOf(id) : (byte)0;
        var retag = !dummy && r.TagSquad != squad && _names.ContainsKey(id); // only a change rebuilds the text
        if (retag) r.TagSquad = squad;
        foreach (var label in r.Node.GetChildren().OfType<Label3D>())
        {
            label.Visible = show;
            if (dummy) continue;
            label.Modulate = FriendTagTint;
            if (retag) label.Text = squad == 0 ? NameTag(id, _names[id]) : Strings.F("squad.tag", NameTag(id, _names[id]), Match.SquadNameOf(squad));
        }
    }

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
