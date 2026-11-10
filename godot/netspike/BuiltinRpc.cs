using Godot;

/// <summary>RPC endpoint for approach A (same node path on server and clients).</summary>
public partial class BuiltinRpc : Node
{
    public event Action<int, int, int, int, int, int>? InputReceived; // sender, seq, mx, mz, yawQ, jump
    public event Action<int>? ControlReceived;

    // Client -> server input. Unreliable, own channel.
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = 1)]
    public void SendInput(int seq, int mx, int mz, int yawQ, int jump)
    {
        InputReceived?.Invoke(Multiplayer.GetRemoteSenderId(), seq, mx, mz, yawQ, jump);
    }

    // Server -> clients, reliable.
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    public void Control(int code)
    {
        ControlReceived?.Invoke(code);
    }
}
