namespace Suchohrad.ShooterCore;

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
    public const string RefusedReason = "Sandbox mode is not allowed on this server. Playing normally.";

    /// <summary>The answer to a hello: no answer (no notice) unless the client asked; then granted only if the server allows it.</summary>
    public static SandboxAnswer? Decide(bool requested, bool serverAllows)
    {
        if (!requested) return null;
        return serverAllows ? new SandboxAnswer(true, GrantedReason) : new SandboxAnswer(false, RefusedReason);
    }
}

/// <summary>Payloads of the sandbox events (kinds 150 and 151).</summary>
public static class SandboxEvents
{
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

    /// <summary>SandboxGrant: a u8-length UTF-8 streak id.</summary>
    public static byte[] EncodeGrant(string streakId)
    {
        var id = System.Text.Encoding.UTF8.GetBytes(streakId);
        var n = Math.Min(id.Length, 255);
        var o = new byte[1 + n];
        o[0] = (byte)n;
        Array.Copy(id, 0, o, 1, n);
        return o;
    }

    public static string? DecodeGrant(ReadOnlySpan<byte> d) =>
        d.Length < 1 || d.Length < 1 + d[0] ? null : System.Text.Encoding.UTF8.GetString(d.Slice(1, d[0]));
}
