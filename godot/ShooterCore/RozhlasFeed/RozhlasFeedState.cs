using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.RozhlasFeed;

/// <summary>Where the server's live feed is in one mic-live phase.</summary>
public enum RozhlasFeedState { Idle, Live, Draining, Failed }

public static class RozhlasFeedMachine
{
    public static readonly StateMachineSpec<RozhlasFeedState> Spec = StateMachineSpec<RozhlasFeedState>.Define(
            "RozhlasFeed.Session", "Whether the live feed (ffmpeg subprocess, 2 s delay line) is running for a mic-live phase.", RozhlasFeedState.Idle)
        .Allow(RozhlasFeedState.Idle, RozhlasFeedState.Live, RozhlasFeedState.Failed)
        .Allow(RozhlasFeedState.Live, RozhlasFeedState.Draining, RozhlasFeedState.Failed)
        .Allow(RozhlasFeedState.Draining, RozhlasFeedState.Idle, RozhlasFeedState.Live, RozhlasFeedState.Failed)
        .Allow(RozhlasFeedState.Failed, RozhlasFeedState.Idle, RozhlasFeedState.Live)
        .Note(RozhlasFeedState.Idle, "No process. Nothing is sent.")
        .Note(RozhlasFeedState.Live, "Start() ran: input is accepted and pulled at a steady 50 blocks/s into ffmpeg; the degraded output goes out 2 s after its input.")
        .Note(RozhlasFeedState.Draining, "Stop() ran: no new input; what was written still plays out, then the echo tail, then ffmpeg ends. A Start() here cuts the tail short.")
        .Note(RozhlasFeedState.Failed, "ffmpeg missing, died, or stopped producing audio: logged once, the feed is silent until the next phase. Never unprocessed audio.")
        .Build();
}

/// <summary>The client's jitter buffer: filling to its depth, or playing out.</summary>
public enum RozhlasJitterState { Priming, Playing }

public static class RozhlasJitterMachine
{
    public static readonly StateMachineSpec<RozhlasJitterState> Spec = StateMachineSpec<RozhlasJitterState>.Define(
            "RozhlasFeed.Jitter", "The client's jitter buffer for the live feed: wait for ~100 ms of frames, play them out, wait again after running dry.", RozhlasJitterState.Priming)
        .Allow(RozhlasJitterState.Priming, RozhlasJitterState.Playing)
        .Allow(RozhlasJitterState.Playing, RozhlasJitterState.Priming)
        .Note(RozhlasJitterState.Priming, "Frames are collected; nothing plays until the target depth is reached.")
        .Note(RozhlasJitterState.Playing, "One frame per 20 ms in sequence order; a missing one is concealed, a late one dropped; an empty buffer goes back to Priming.")
        .Build();
}
