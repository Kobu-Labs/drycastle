namespace Drycastle.ShooterCore;

/// <summary>
/// The player's own Respawn (GLOSSARY "Respawn", ticket #284): the client asks (<see cref="EventKind.Redeploy"/>) and the server kills them as a suicide
/// (<see cref="MatchSim.ForceDeath"/>); the normal respawn applies the active loadout. The message may name a loadout slot to select first, so
/// "apply this loadout now" is one request.
/// </summary>
public static class Redeploy
{
    /// <summary>The slot byte meaning "keep the active loadout".</summary>
    public const byte NoSlot = 255;

    /// <summary>Client to server: u8 loadout slot to select first, or <see cref="NoSlot"/>.</summary>
    public static byte[] Encode(int? slot) => new[] { slot is { } s ? (byte)s : NoSlot };

    /// <summary>False for a malformed payload. <paramref name="slot"/> is null for <see cref="NoSlot"/>; a number is not checked against the five slots here (<see cref="LoadoutRules.ValidateSlot"/>).</summary>
    public static bool TryDecode(byte[] payload, out int? slot)
    {
        slot = payload.Length == 1 && payload[0] != NoSlot ? payload[0] : null;
        return payload.Length == 1;
    }
}
