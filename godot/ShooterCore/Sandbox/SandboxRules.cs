namespace Drycastle.ShooterCore;

/// <summary>The server's answer to a sandbox request: granted or refused, with the reason sent to the client.</summary>
public readonly record struct SandboxAnswer(bool Granted, string Reason);

/// <summary>
/// Sandbox mode (#156): a dev-only player state. The client asks for it in the hello (<see cref="HelloFlags.Sandbox"/>);
/// the server grants it only when started with <c>--allow-sandbox</c>. Security is not a concern here: the client asks, the
/// server obliges when it allows it. A sandbox player takes no damage, has unlimited ammo, can use every killstreak on demand,
/// and is kept out of everything persistent or competitive (XP, stats, score, round end, ranking).
/// </summary>
public static class SandboxRules
{
    public const string GrantedReason = "Sandbox mode on: godmode, unlimited ammo, every killstreak. No XP, stats or score.";
    public const string RevokedReason = "An admin turned sandbox mode off. Playing normally.";
    public const string RefusedReason = "Sandbox mode is not allowed on this server. Playing normally.";

    /// <summary>The answer to a hello: no answer (no notice) unless the client asked; then granted only if the server allows it.</summary>
    public static SandboxAnswer? Decide(bool requested, bool serverAllows)
    {
        if (!requested) return null;
        return serverAllows ? new SandboxAnswer(true, GrantedReason) : new SandboxAnswer(false, RefusedReason);
    }
}

/// <summary>The panel's one-shot actions (event kind 152: u8 action, u8 argument).</summary>
public enum SandboxAction : byte
{
    /// <summary>Health and every magazine and reserve back to full.</summary>
    Heal = 1,
    /// <summary>Move to a random player spawn point. Argument unused.</summary>
    Teleport = 2,
    /// <summary>Spawn a vehicle in front. Argument: index into vehicles.json.</summary>
    SpawnVehicle = 3,
    /// <summary>Target dummies respawn after a kill. Argument: 1 on, 0 off. The whole server, not just this player.</summary>
    TargetRespawn = 4,
    /// <summary>Use the held streak now.</summary>
    Activate = 5,
    /// <summary>Take damage. Argument: 1 on (hurt and killed like anyone), 0 off (godmode, the default). This player only.</summary>
    TakeDamage = 6,
}

/// <summary>Payloads of the sandbox events (kinds 150-155).</summary>
public static class SandboxEvents
{
    public static byte[] EncodeAction(SandboxAction action, byte arg = 0) => new[] { (byte)action, arg };

    public static (SandboxAction Action, byte Arg)? DecodeAction(ReadOnlySpan<byte> d) =>
        d.Length < 2 || !Enum.IsDefined((SandboxAction)d[0]) ? null : ((SandboxAction)d[0], d[1]);

    /// <summary>SandboxState: u16 player id, u8 on.</summary>
    public static byte[] EncodeState(ushort id, bool on)
    {
        var o = new byte[3];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(o, id);
        o[2] = (byte)(on ? 1 : 0);
        return o;
    }

    public static (ushort Id, bool On)? DecodeState(ReadOnlySpan<byte> d) =>
        d.Length < 3 ? null : (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d), d[2] != 0);

    /// <summary>SandboxHeld and SandboxNotice: a u8-length UTF-8 string (the streak id, or the notice text).</summary>
    public static byte[] EncodeText(string text)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(text);
        var n = Math.Min(b.Length, 255);
        var o = new byte[1 + n];
        o[0] = (byte)n;
        Array.Copy(b, 0, o, 1, n);
        return o;
    }

    public static string? DecodeText(ReadOnlySpan<byte> d) =>
        d.Length < 1 || d.Length < 1 + d[0] ? null : System.Text.Encoding.UTF8.GetString(d.Slice(1, d[0]));

    /// <summary>SandboxStatus: u8 granted, then the reason as a u8-length UTF-8 string.</summary>
    public static byte[] EncodeStatus(SandboxAnswer a)
    {
        var reason = System.Text.Encoding.UTF8.GetBytes(a.Reason);
        var n = Math.Min(reason.Length, 255);
        var o = new byte[2 + n];
        o[0] = (byte)(a.Granted ? 1 : 0);
        o[1] = (byte)n;
        Array.Copy(reason, 0, o, 2, n);
        return o;
    }

    public static SandboxAnswer? DecodeStatus(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2 || d.Length < 2 + d[1]) return null;
        return new SandboxAnswer(d[0] != 0, System.Text.Encoding.UTF8.GetString(d.Slice(2, d[1])));
    }

    /// <summary>SandboxGrant: a u8-length UTF-8 streak id (the same text codec as <see cref="EncodeText"/>).</summary>
    public static byte[] EncodeGrant(string streakId) => EncodeText(streakId);

    public static string? DecodeGrant(ReadOnlySpan<byte> d) => DecodeText(d);
}
