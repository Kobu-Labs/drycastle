using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The Teams screen's side of the client (#312): who may open it, and the admin's requests. The server answers through the scoreboard rows (team, pin and bot per player).</summary>
public partial class NetClient
{
    /// <summary>An admin in a team deathmatch: the Pause menu offers Teams. The server checks both again.</summary>
    public bool CanManageTeams => ChatCtx.IsAdmin && Match.IsTeamMode;

    public ushort MyId => _myId;

    public void SendTeamAssign(TeamAssign a)
    {
        if (_peer == null || !CanManageTeams) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.TeamAssign, TeamAssignCodec.Encode(a))), (int)ENetPacketPeer.FlagReliable);
    }
}
