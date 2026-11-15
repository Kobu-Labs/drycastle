namespace Drycastle.ShooterCore;

/// <summary>A connection's life on the server, from the first packet to the join. A bot starts at Joined.</summary>
public enum SessionPhase
{
    /// <summary>The peer is connected and has not said hello yet (the hello deadline runs).</summary>
    Connected,
    /// <summary>The hello was refused (version, map, full): the error is sent and the peer is being disconnected.</summary>
    Rejected,
    /// <summary>Hello accepted, id given, waiting for the profile (or the profile-load timeout).</summary>
    Loading,
    /// <summary>In the match: body, welcome and spawn are done.</summary>
    Joined,
}

/// <summary>A client's link to the server, as the client sees it.</summary>
public enum LinkPhase
{
    /// <summary>Connecting and saying hello; the connect timeout runs.</summary>
    Connecting,
    /// <summary>Welcome received; no own snapshot yet (also again after each respawn, until the spawn position arrives).</summary>
    Welcomed,
    /// <summary>Own snapshot applied: the player body is placed and predicted.</summary>
    Spawned,
    /// <summary>The connection failed or was dropped (the menu is told).</summary>
    Failed,
    /// <summary>Left on purpose, or a bot's script is done.</summary>
    Left,
}

public static class ConnectionPhases
{
    public static readonly StateMachineSpec<SessionPhase> Session = StateMachineSpec<SessionPhase>.Define(
            "Session.Phase", "A connection on the server: connected, said hello (profile loading) or refused, then joined.", SessionPhase.Connected)
        .Allow(SessionPhase.Connected, SessionPhase.Rejected, SessionPhase.Loading, SessionPhase.Joined)
        .Allow(SessionPhase.Loading, SessionPhase.Joined)
        .Note(SessionPhase.Connected, "Only a hello is read; a connection with none after the hello deadline is dropped.")
        .Note(SessionPhase.Rejected, "Terminal: nothing more is read from the peer.")
        .Note(SessionPhase.Loading, "Joined when the profile arrives, or as a guest when the load fails or times out.")
        .Note(SessionPhase.Joined, "Inputs, events and the snapshots flow. A bot is created here; a guest (no profile store or token) skips Loading.")
        .Build();

    public static readonly StateMachineSpec<LinkPhase> Link = StateMachineSpec<LinkPhase>.Define(
            "Client.Link", "The client's connection to the server: connecting, welcomed, spawned (own snapshot applied), and the two ends.", LinkPhase.Connecting)
        .Allow(LinkPhase.Connecting, LinkPhase.Welcomed, LinkPhase.Failed, LinkPhase.Left)
        .Allow(LinkPhase.Welcomed, LinkPhase.Spawned, LinkPhase.Failed, LinkPhase.Left)
        .Allow(LinkPhase.Spawned, LinkPhase.Welcomed, LinkPhase.Failed, LinkPhase.Left)
        .Allow(LinkPhase.Failed, LinkPhase.Left)
        .Note(LinkPhase.Connecting, "Elapsed is the time spent connecting (the client fails after 10 s).")
        .Note(LinkPhase.Spawned, "Goes back to Welcomed at each respawn, until the new spawn position arrives.")
        .Note(LinkPhase.Failed, "Frames and bot scripts stop; the menu was told.")
        .Build();

    /// <summary>The client fails when it has not been welcomed after this many seconds.</summary>
    public const float ConnectTimeoutSeconds = 10;

    /// <summary>The phases in which the client is done: no more frames, bot scripts or smoke checks run.</summary>
    public static bool IsEnded(this LinkPhase p) => p is LinkPhase.Failed or LinkPhase.Left;
}
