using System.Buffers.Binary;
using System.Text;

namespace Suchohrad.ShooterCore;

/// <summary>Event kinds carried in the reliable event envelope. Payload codecs are added by the issues that use them.</summary>
public enum EventKind : byte
{
    None = 0,
    Spawn = 1,
    Shot = 2,
    Hit = 3,
    Kill = 4,
    MatchState = 5,
    Explosion = 6,
    Death = 7,
    /// <summary>Client to server developer commands (only honoured when the server runs with --debug-cmds).</summary>
    Debug = 8,
    /// <summary>Server to the owning client: ammo, slot and reload state.</summary>
    Ammo = 9,
    /// <summary>Server broadcast: a rocket was launched (projectile id, owner, origin, velocity).</summary>
    Launch = 10,
    /// <summary>Server broadcast: a vehicle blew up (vehicle id, who dealt the last damage, centre, radius).</summary>
    VehicleExplosion = 11,
    /// <summary>Server broadcast: a world entity was spawned (id, type, full state). See <see cref="WorldCodec"/>.</summary>
    WorldSpawn = 12,
    /// <summary>Server broadcast: a world entity was despawned (id, reason). See <see cref="WorldCodec"/>.</summary>
    WorldDespawn = 13,
}

public abstract record ReliableMsg;
/// <summary>Client to server, first message after connecting. Token is the client-generated player token (ADR 0003); empty = guest.</summary>
public sealed record HelloMsg(byte Version, string Name, string Token = "") : ReliableMsg;
/// <summary>Server to client, tells the client which entity id it is and the tick rate.</summary>
public sealed record WelcomeMsg(byte Version, ushort PlayerId, byte TickRate, uint ServerTick) : ReliableMsg;
public sealed record JoinMsg(ushort PlayerId, string Name) : ReliableMsg;
public sealed record LeaveMsg(ushort PlayerId) : ReliableMsg;
/// <summary>Event envelope: a type byte plus an opaque payload decoded by the owner of that kind.</summary>
public sealed record EventMsg(byte Kind, byte[] Payload) : ReliableMsg;
/// <summary>
/// Server to the joining client, sent before the player spawns: the full profile (level, XP, unlocks, the five
/// loadouts and the active slot). Sent again after every accepted loadout change.
/// </summary>
public sealed record ProfileMsg(string Name, int Xp, int Level, int ActiveSlot, IReadOnlyList<Loadout> Loadouts, IReadOnlyList<Unlock> Unlocks) : ReliableMsg;
/// <summary>Client to server: replace one loadout slot. The server validates it; invalid changes get an <see cref="ErrorMsg"/>.</summary>
public sealed record SaveLoadoutMsg(Loadout Loadout) : ReliableMsg;
/// <summary>Client to server: make a loadout slot the active one (switched to on death).</summary>
public sealed record SelectSlotMsg(int Slot) : ReliableMsg;
/// <summary>Server to client: a human readable reason. Before the join it is followed by a disconnect (protocol mismatch etc.).</summary>
public sealed record ErrorMsg(string Reason) : ReliableMsg;

/// <summary>
/// Wire format (little-endian). Channel 0 is reliable (control + events), channel 1 is unreliable-sequenced (input, snapshots).
///
/// ch0 messages: u8 type, then
///   Hello(1):   u8 version, u8 nameLen, name utf8, u8 tokenLen, token utf8
///   Welcome(2): u8 version, u16 playerId, u8 tickRate, u32 serverTick
///   Join(3):    u16 playerId, u8 nameLen, name utf8
///   Leave(4):   u16 playerId
///   Event(5):   u8 kind, u16 len, payload
///   Profile(6): u8 nameLen, name, u32 xp, u16 level, u8 activeSlot, u8 loadoutCount, loadouts (u8 slot, 6 strings),
///               u16 unlockCount, unlocks (2 strings: kind, item id)
///   SaveLoadout(7): u8 slot, 6 strings (primary, secondary, perk, streak1..3)
///   SelectSlot(8):  u8 slot
///   Error(9):   u8 reasonLen, reason
///   Strings are u8 length + utf8 (clamped to 255 bytes). Hello also carries u8 tokenLen, token after the name.
/// ch1 client to server, Input(10): u8 count (1..3, newest last), count * 11 bytes:
///   u16 seq, i8 mx, i8 my, u16 yaw, i16 pitch, u16 buttons, u8 slot
/// ch1 server to client, Snapshot(20): u32 tick, u16 ackSeq, u8 count, then count entities:
///   u16 id, u8 type, u8 flags, then per flag
///     PosAbs(1):   i32 x, i32 y, i32 z   (1/128 m)
///     PosDelta(2): i16 dx, dy, dz        (vs the last state sent to this client)
///     Rot(4):      u16 yaw, i16 pitch
///     Status(8):   u8 health, u8 state, u8 weapon
///   Unchanged entities are omitted; a keyframe (all of the above, absolute) is sent for new entities and every 30 ticks.
/// ch1 server to client, WorldSnapshot(21): world entities (turrets, missiles, ...), see <see cref="WorldReplica"/> and <see cref="WorldCodec"/>.
/// </summary>
public static class Wire
{
    /// <summary>Bumped when the wire layout changes (v2: hello carries the player token, profile and loadout messages).</summary>
    public const byte ProtocolVersion = 2;
    public const byte TickRate = 30;
    public const int ChanReliable = 0, ChanData = 1, Channels = 2;
    public const int KeyEvery = 30;
    public const int MaxInputsPerPacket = 3;
    public const int MaxNameBytes = 24;
    public const int MaxTokenBytes = 64;
    public const int InputBytes = 11;

    public const byte THello = 1, TWelcome = 2, TJoin = 3, TLeave = 4, TEvent = 5, TProfile = 6, TSaveLoadout = 7, TSelectSlot = 8, TError = 9;
    public const byte TInput = 10, TSnapshot = 20, TWorldSnapshot = 21;
    public const byte FPosAbs = 1, FPosDelta = 2, FRot = 4, FStatus = 8, FHp = 16;
    /// <summary>Flags that make a snapshot record a keyframe (world entities; players and vehicles use the same three).</summary>
    public const byte FKey = FPosAbs | FRot | FStatus;

    /// <summary>Cuts <paramref name="n"/> to at most <paramref name="maxBytes"/> utf8 bytes without splitting a character.</summary>
    static string Fit(string n, int maxBytes)
    {
        var b = Encoding.UTF8.GetBytes(n ?? "");
        if (b.Length <= maxBytes) return n ?? "";
        var s = Encoding.UTF8.GetString(b, 0, maxBytes);
        return s.TrimEnd('�');
    }

    static string ClampName(string n) => Fit(n, MaxNameBytes);

    static void WriteStr(List<byte> o, string s)
    {
        var b = Encoding.UTF8.GetBytes(Fit(s, 255));
        o.Add((byte)b.Length);
        o.AddRange(b);
    }

    static void WriteName(List<byte> o, string name) => WriteStr(o, ClampName(name));

    static void WriteLoadout(List<byte> o, Loadout l)
    {
        o.Add((byte)l.Slot);
        WriteStr(o, l.PrimaryWeapon); WriteStr(o, l.SecondaryWeapon); WriteStr(o, l.Perk);
        WriteStr(o, l.Streak1); WriteStr(o, l.Streak2); WriteStr(o, l.Streak3);
    }

    /// <summary>Bounds-checked cursor over a reliable message; out-of-range reads throw and DecodeReliable returns null.</summary>
    ref struct Reader
    {
        readonly ReadOnlySpan<byte> d;
        int _p = 1; // byte 0 is the message type

        public Reader(ReadOnlySpan<byte> data) => d = data;
        public byte U8() => d[_p++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(d[_p..]); _p += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d[_p..]); _p += 4; return v; }
        public string Str()
        {
            int n = d[_p++];
            var s = Encoding.UTF8.GetString(d.Slice(_p, n));
            _p += n;
            return s;
        }
        public Loadout Loadout()
        {
            var slot = U8();
            return new Loadout(slot, Str(), Str(), Str(), Str(), Str(), Str());
        }
    }

    public static byte[] Encode(ReliableMsg m)
    {
        var o = new List<byte>();
        Span<byte> t = stackalloc byte[4];
        switch (m)
        {
            case HelloMsg h: o.Add(THello); o.Add(h.Version); WriteName(o, h.Name); WriteStr(o, Fit(h.Token, MaxTokenBytes)); break;
            case WelcomeMsg w:
                o.Add(TWelcome); o.Add(w.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(t, w.PlayerId); o.AddRange(t[..2].ToArray());
                o.Add(w.TickRate);
                BinaryPrimitives.WriteUInt32LittleEndian(t, w.ServerTick); o.AddRange(t.ToArray());
                break;
            case JoinMsg j:
                o.Add(TJoin);
                BinaryPrimitives.WriteUInt16LittleEndian(t, j.PlayerId); o.AddRange(t[..2].ToArray());
                WriteName(o, j.Name);
                break;
            case LeaveMsg l:
                o.Add(TLeave);
                BinaryPrimitives.WriteUInt16LittleEndian(t, l.PlayerId); o.AddRange(t[..2].ToArray());
                break;
            case EventMsg e:
                if (e.Payload.Length > ushort.MaxValue) throw new ArgumentException("event payload too large");
                o.Add(TEvent); o.Add(e.Kind);
                BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)e.Payload.Length); o.AddRange(t[..2].ToArray());
                o.AddRange(e.Payload);
                break;
            case ProfileMsg p:
                o.Add(TProfile);
                WriteName(o, p.Name);
                BinaryPrimitives.WriteUInt32LittleEndian(t, (uint)p.Xp); o.AddRange(t.ToArray());
                BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)p.Level); o.AddRange(t[..2].ToArray());
                o.Add((byte)p.ActiveSlot);
                o.Add((byte)p.Loadouts.Count);
                foreach (var l in p.Loadouts) WriteLoadout(o, l);
                BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)p.Unlocks.Count); o.AddRange(t[..2].ToArray());
                foreach (var u in p.Unlocks) { WriteStr(o, u.Kind); WriteStr(o, u.ItemId); }
                break;
            case SaveLoadoutMsg sl: o.Add(TSaveLoadout); WriteLoadout(o, sl.Loadout); break;
            case SelectSlotMsg ss: o.Add(TSelectSlot); o.Add((byte)ss.Slot); break;
            case ErrorMsg er: o.Add(TError); WriteStr(o, er.Reason); break;
            default: throw new ArgumentException("unknown message");
        }
        return o.ToArray();
    }

    /// <summary>Decode a reliable message; null if malformed or unknown.</summary>
    public static ReliableMsg? DecodeReliable(ReadOnlySpan<byte> d)
    {
        try
        {
            if (d.Length == 0) return null;
            switch (d[0])
            {
                case THello:
                {
                    int n = d[2];
                    var name = Encoding.UTF8.GetString(d.Slice(3, n));
                    // a v1 hello has no token; the server rejects it on the version before it looks at the token
                    var rest = d[(3 + n)..];
                    var token = rest.Length > 0 ? Encoding.UTF8.GetString(rest.Slice(1, rest[0])) : "";
                    return new HelloMsg(d[1], name, token);
                }
                case TWelcome:
                    return new WelcomeMsg(d[1], BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), d[4], BinaryPrimitives.ReadUInt32LittleEndian(d[5..]));
                case TJoin:
                {
                    int n = d[3];
                    return new JoinMsg(BinaryPrimitives.ReadUInt16LittleEndian(d[1..]), Encoding.UTF8.GetString(d.Slice(4, n)));
                }
                case TLeave: return new LeaveMsg(BinaryPrimitives.ReadUInt16LittleEndian(d[1..]));
                case TProfile:
                {
                    var r = new Reader(d);
                    var name = r.Str();
                    var xp = (int)r.U32();
                    var level = r.U16();
                    var active = r.U8();
                    var loadouts = new Loadout[r.U8()];
                    for (var i = 0; i < loadouts.Length; i++) loadouts[i] = r.Loadout();
                    var unlocks = new Unlock[r.U16()];
                    for (var i = 0; i < unlocks.Length; i++) unlocks[i] = new Unlock(r.Str(), r.Str());
                    return new ProfileMsg(name, xp, level, active, loadouts, unlocks);
                }
                case TSaveLoadout: return new SaveLoadoutMsg(new Reader(d).Loadout());
                case TSelectSlot: return new SelectSlotMsg(d[1]);
                case TError: return new ErrorMsg(new Reader(d).Str());
                case TEvent:
                {
                    int n = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
                    return new EventMsg(d[1], d.Slice(4, n).ToArray());
                }
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        return null;
    }

    /// <summary>Encode the newest inputs (up to 3, oldest first) as one packet.</summary>
    public static byte[] EncodeInputs(ReadOnlySpan<NetInput> inputs)
    {
        var n = Math.Min(inputs.Length, MaxInputsPerPacket);
        var b = new byte[2 + n * InputBytes];
        b[0] = TInput; b[1] = (byte)n;
        for (var i = 0; i < n; i++)
        {
            var s = b.AsSpan(2 + i * InputBytes);
            var x = inputs[inputs.Length - n + i];
            BinaryPrimitives.WriteUInt16LittleEndian(s, x.Seq);
            s[2] = (byte)x.MoveX; s[3] = (byte)x.MoveY;
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], x.Yaw);
            BinaryPrimitives.WriteInt16LittleEndian(s[6..], x.Pitch);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], x.Buttons);
            s[10] = x.Slot;
        }
        return b;
    }

    public static bool TryDecodeInputs(ReadOnlySpan<byte> d, List<NetInput> into)
    {
        if (d.Length < 2 || d[0] != TInput) return false;
        int n = d[1];
        if (n < 1 || n > MaxInputsPerPacket || d.Length < 2 + n * InputBytes) return false;
        for (var i = 0; i < n; i++)
        {
            var s = d[(2 + i * InputBytes)..];
            into.Add(new NetInput
            {
                Seq = BinaryPrimitives.ReadUInt16LittleEndian(s),
                MoveX = (sbyte)s[2], MoveY = (sbyte)s[3],
                Yaw = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
                Pitch = BinaryPrimitives.ReadInt16LittleEndian(s[6..]),
                Buttons = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
                Slot = s[10],
            });
        }
        return true;
    }
}
