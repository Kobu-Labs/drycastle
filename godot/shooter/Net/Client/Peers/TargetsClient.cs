using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.HudFeeds;
using Drycastle.Shooter.Net.Client.Shooting;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Peers;

/// <summary>Target dummies on the client (#148): the roster gives their names; their bodies come from snapshots (entity type Target). This also keeps the name tags over players.</summary>
internal sealed class TargetsClient
{
    /// <summary>Orange name tag of a dummy.</summary>
    public static readonly Color DummyTint = new(1f, 0.55f, 0.1f);

    static readonly Color FriendTagTint = new(0.3f, 1f, 0.3f);

    readonly ClientContext _ctx;
    readonly SandboxClient _sandbox;
    readonly BlastDustClient _blastDust;

    public TargetsClient(ClientContext ctx, SandboxClient sandbox, BlastDustClient blastDust)
    {
        _ctx = ctx; _sandbox = sandbox; _blastDust = blastDust;
    }

    /// <summary>Name tags over players: none in free-for-all, only teammates (in the friend colour) in team deathmatch and squad mates in open world, a squad mate's with the squad's name after it (#379); target dummies keep their orange tag.</summary>
    public void UpdateNameTag(ushort id, RemotePlayer r)
    {
        var match = _ctx.Match;
        var dummy = TargetDummies.IsDummyId(id);
        var show = (dummy || match.AreFriends(_ctx.MyId, id)) && !_blastDust.Hides(r.Node.GlobalPosition + Vector3.Up * 1.6f); // not through dust (#345)
        var squad = !dummy && match.AreSquadMates(_ctx.MyId, id) ? match.SquadOf(id) : (byte)0;
        var retag = !dummy && r.TagSquad != squad && _ctx.Names.ContainsKey(id); // only a change rebuilds the text
        if (retag) r.TagSquad = squad;
        foreach (var label in r.Node.GetChildren().OfType<Label3D>())
        {
            label.Visible = show;
            if (dummy) continue;
            label.Modulate = FriendTagTint;
            if (retag) label.Text = squad == 0 ? _sandbox.NameTag(id, _ctx.Names[id]) : Strings.F("squad.tag", _sandbox.NameTag(id, _ctx.Names[id]), match.SquadNameOf(squad));
        }
    }

    /// <summary>The names of the target dummies (the server sends them once, after the join).</summary>
    public void OnRoster(byte[] payload)
    {
        if (TargetCodec.DecodeRoster(payload) is not { } list)
        {
            GD.PrintErr("client: bad target roster");
            return;
        }
        foreach (var (id, name) in list)
        {
            _ctx.Names[id] = name;
            _ctx.Match.NameTarget(id, name);
            // a snapshot may have drawn the dummy before the roster arrived: fix its name tag
            if (_ctx.Remotes.TryGetValue(id, out var r))
                foreach (var label in r.Node.GetChildren().OfType<Label3D>()) label.Text = name;
        }
        GD.Print($"client: {list.Count} target dummies");
    }
}
