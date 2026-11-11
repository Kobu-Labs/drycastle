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
    /// <summary>Server broadcast: a shotgun fired its pellet fan (shooter, weapon, origin, aim, seed). Clients expand the seed into the same pellets for FX.</summary>
    PelletFan = 12,
    /// <summary>Server broadcast: a world entity was spawned (id, type, full state). See <see cref="WorldCodec"/>.</summary>
    WorldSpawn = 13,
    /// <summary>Server broadcast: a world entity was despawned (id, reason). See <see cref="WorldCodec"/>.</summary>
    WorldDespawn = 14,
    /// <summary>Server to one player on join: the target dummies' ids and names (<see cref="TargetCodec"/>). Kinds 140-149 belong to the target dummies (#148); 141-149 are free.</summary>
    TargetRoster = 140,
    /// <summary>Server to one player: the player reached a new level (<see cref="ProgressionEvents"/>).</summary>
    LevelUp = 15,
    /// <summary>Server to one player at the end of a round: XP earned in it, with the reasons (<see cref="ProgressionEvents"/>).</summary>
    XpSummary = 16,
    /// <summary>Server to the owner: a killstreak was earned (<see cref="KillstreakEvents"/>).</summary>
    StreakEarned = 17,
    /// <summary>Server to the owner: a held killstreak was used (<see cref="KillstreakEvents"/>).</summary>
    StreakUsed = 18,
    /// <summary>Server to the owner: an active killstreak ended, with the reason (<see cref="KillstreakEvents"/>).</summary>
    StreakEnded = 19,
    /// <summary>Server to the owner: a care package granted a reward streak, and the older reward it replaced if the slots were full (<see cref="KillstreakEvents"/>).</summary>
    StreakGranted = 23,
    /// <summary>Client to server: take a corpse's weapon into a hotbar position (<see cref="CorpseCodec"/>).</summary>
    PickUpCorpse = 20,
    /// <summary>Server to one player: the hotbar after a corpse pickup (<see cref="CorpseCodec"/>).</summary>
    Hotbar = 21,
    /// <summary>Server broadcast: a player's camouflage (player id and <see cref="CamoSpec"/>), on join and after a change. See <see cref="AppearanceCodec"/>.</summary>
    Appearance = 22,
    /// <summary>Server broadcast: a MOAB countdown armed, detonated or was cancelled (<see cref="MoabEvents"/>).</summary>
    Moab = 70,
    /// <summary>Client to server: open a landed care package (<see cref="CarePackageCodec"/>).</summary>
    OpenCarePackage = 50,
    // 51 was PerkGained (the care package's perk, retired in protocol 9); it is not reused.
    /// <summary>Server broadcast: an attack missile blew up (id, owner, centre, splash radius). See <see cref="MissileCodec"/>.</summary>
    MissileBlast = 30,
    /// <summary>Server to the owner of a gunship: weapon, ammo, heat, zoom (<see cref="GunshipCodec"/>). Kinds 60-69 belong to the aircraft streaks.</summary>
    GunshipStatus = 60,
    /// <summary>Server broadcast: an impact on a surface from a gunship round or shell (<see cref="ImpactCodec"/>).</summary>
    ImpactFx = 61,
    /// <summary>Server to the owner only: the UAV's enemy pulse and seconds left (<see cref="UavEvents"/>), every second while up.</summary>
    UavPing = 90,
    // 120 was Ac130Blast (one message per shell, protocol 11); protocol 12 coalesces the gunship FX into 121-123, so 120 is not reused.
    /// <summary>Server broadcast: one gunner's cannon tracers this tick (<see cref="GunshipFxCodec"/>). Kinds 120-129 belong to the gunship streaks; 120 is retired.</summary>
    GunshipShots = 121,
    /// <summary>Server broadcast: the impacts of the gunship rounds this tick, kills always included (<see cref="GunshipFxCodec"/>).</summary>
    GunshipImpacts = 122,
    /// <summary>Server broadcast: one gunner's blasts this tick and its hit-marker flags (<see cref="GunshipFxCodec"/>).</summary>
    GunshipBlasts = 123,
    /// <summary>Server to the one player a helicopter helper is about to fire on: red-dot warning (<see cref="HelperWarningCodec"/>). Kinds 110-119 belong to the helicopter helper.</summary>
    HelperWarning = 110,
    /// <summary>Client to server: copy one loadout slot (and its camouflage) into another (<see cref="LoadoutEvents"/>). Kinds 130-139 belong to the loadout editor (#145).</summary>
    CopyLoadout = 130,
    /// <summary>Client to server: restore one loadout slot to the starter kit (<see cref="LoadoutEvents"/>).</summary>
    ResetLoadout = 131,
    /// <summary>Server to the editing player: the answer to a SaveLoadout, ok or the reason (<see cref="LoadoutEvents"/>).</summary>
    LoadoutAck = 132,
    /// <summary>Server to the joining player: whether sandbox mode was granted, and the reason (<see cref="SandboxEvents"/>). Kinds 150-159 belong to sandbox mode (#156).</summary>
    SandboxStatus = 150,
    /// <summary>Client to server: use one killstreak now, sandbox owners only (<see cref="SandboxEvents"/>).</summary>
    SandboxGrant = 151,
    /// <summary>Client to server: a sandbox action from the panel (<see cref="SandboxAction"/>, <see cref="SandboxEvents"/>).</summary>
    SandboxAction = 152,
    /// <summary>Server to everyone: a player is in sandbox mode (u16 player id, u8 on). Sent to the joiner for everyone already in it.</summary>
    SandboxState = 153,
    /// <summary>Server to the owner: the held sandbox streak (empty = none). Sent after every grant and on join.</summary>
    SandboxHeld = 154,
    /// <summary>Server to the owner: why a sandbox action was refused (a short English line, shown as a toast).</summary>
    SandboxNotice = 155,
}

public abstract record ReliableMsg;
/// <summary>Client to server, first message after connecting. Token is the client-generated player token (ADR 0003); empty = guest.</summary>
/// <summary>Flags are <see cref="HelloFlags"/> bits (v10). A v9 hello has none and decodes as 0.</summary>
public sealed record HelloMsg(byte Version, string Name, string Token = "", byte Flags = 0) : ReliableMsg;

/// <summary>Bits of <see cref="HelloMsg.Flags"/>.</summary>
public static class HelloFlags
{
    /// <summary>The client asks for sandbox mode (#156). The server grants it only with --allow-sandbox.</summary>
    public const byte Sandbox = 1;
}
/// <summary>Server to client, tells the client which entity id it is and the tick rate.</summary>
/// <summary>Server to the joining client. The last three fields are the server's day/night clock (<see cref="DayClock"/>): start minute, day length in ms, frozen.</summary>
public sealed record WelcomeMsg(byte Version, ushort PlayerId, byte TickRate, uint ServerTick, ushort StartMinute = 0, uint DayLengthMs = 0, bool Frozen = false) : ReliableMsg;
public sealed record JoinMsg(ushort PlayerId, string Name) : ReliableMsg;
public sealed record LeaveMsg(ushort PlayerId) : ReliableMsg;
/// <summary>Event envelope: a type byte plus an opaque payload decoded by the owner of that kind.</summary>
public sealed record EventMsg(byte Kind, byte[] Payload) : ReliableMsg;
/// <summary>
/// Server to the joining client, sent before the player spawns: the full profile (level, XP, unlocks, the five
/// loadouts and the active slot). Sent again after every accepted loadout change.
/// </summary>
/// <summary>The camouflage of each loadout slot (<see cref="Camos"/>, index = slot) rides along, five entries.</summary>
public sealed record ProfileMsg(string Name, int Xp, int Level, int ActiveSlot, IReadOnlyList<Loadout> Loadouts, IReadOnlyList<Unlock> Unlocks, IReadOnlyList<CamoSpec>? Camos = null) : ReliableMsg;
/// <summary>Client to server: replace one loadout slot. The server validates it; invalid changes get an <see cref="ErrorMsg"/>.</summary>
/// <remarks>Seq is the client's save number; the server echoes it in <see cref="LoadoutEvents"/>'s ack so autosave can match answers.</remarks>
public sealed record SaveLoadoutMsg(Loadout Loadout, uint Seq = 0) : ReliableMsg;
/// <summary>Client to server: make a loadout slot the active one (switched to on death).</summary>
public sealed record SelectSlotMsg(int Slot) : ReliableMsg;
/// <summary>Client to server: replace one loadout slot's camouflage (ticket #141). The server validates it against camo.json; invalid changes get an <see cref="ErrorMsg"/>.</summary>
public sealed record SaveCamoMsg(int Slot, CamoSpec Spec) : ReliableMsg;
/// <summary>Server to client: a human readable reason. Before the join it is followed by a disconnect (protocol mismatch etc.).</summary>
public sealed record ErrorMsg(string Reason) : ReliableMsg;

/// <summary>
/// Wire format (little-endian). Channel 0 is reliable (control + events), channel 1 is unreliable-sequenced (input, snapshots).
///
/// ch0 messages: u8 type, then
///   Hello(1):   u8 version, u8 nameLen, name utf8, u8 tokenLen, token utf8, u8 flags (v10, see HelloFlags)
///   Welcome(2): u8 version, u16 playerId, u8 tickRate, u32 serverTick, u16 startMinute, u32 dayLengthMs, u8 frozen
///   Join(3):    u16 playerId, u8 nameLen, name utf8
///   Leave(4):   u16 playerId
///   Event(5):   u8 kind, u16 len, payload
///   Profile(6): u8 nameLen, name, u32 xp, u16 level, u8 activeSlot, u8 loadoutCount, loadouts (u8 slot, 6 strings),
///               u16 unlockCount, unlocks (2 strings: kind, item id)
///   SaveLoadout(7): u8 slot, 6 strings (primary, secondary, perk, streak1..3), string name, u32 seq (#145)
///   SelectSlot(8):  u8 slot
///   Error(9):   u8 reasonLen, reason
///   SaveCamo(10): u8 slot, then the fixed-size CamoSpec encoding (<see cref="CamoSpec"/>)
///   Profile(6) also ends with: u8 camoCount, camoCount * CamoSpec (one per slot, #141)
///   Strings are u8 length + utf8 (clamped to 255 bytes). Hello also carries u8 tokenLen, token after the name.
/// ch1 client to server, Input(11): u8 count (1..3, newest last), count inputs of 12 bytes:
///   u16 seq, i8 mx, i8 my, u16 yaw, i16 pitch, u16 buttons, u8 slot, u8 flags (bit 0: aim)
///   when bit 0 is set, 12 more bytes follow the input: i32 aimX, i32 aimY, i32 aimZ (Quant.Pos units, #161)
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
    /// <summary>Bumped when the wire layout changes (v2: hello carries the player token, profile and loadout messages; v3: welcome carries the day/night clock; v4: camo; v5: camo per loadout; v6: killstreak, aircraft, turret, missile, care package, corpse and MOAB event kinds and world entity types, so a v5 client cannot show them; v7: target dummies, entity type 3 and event kind 140; v8: loadout autosave, rename, copy and reset (event kinds 130-132, SaveLoadout carries name and seq); v9: care package rewards a random killstreak (event kind 23 StreakGranted replaces the retired PerkGained, kind 51); v10: hello flags (sandbox request), event kinds 150-155 (sandbox status, grant, action, state, held streak and notice, #156); v11: GunshipStatus is weapon and zoom only (2 bytes; ammo and heat are gone, #157); v12: the gunship FX is coalesced per tick (event kinds 121-123 replace the per-round Shot, ImpactFx and Ac130Blast of the gunships, #159), and the Explosion carries its blast radius (a gunship rocket's is 8 m); v13: GunshipShots carries each tracer's speed and length (#160); v14: the camo spec grows from 30 to 33 bytes (codec 2: body, skin tone, headgear); v15: an input carries the gunner's aim, a ground point (flags byte, 12 more bytes when set), so the rounds land on the reticle (#161); v16: a gunship rocket flies at its own speed (killstreaks.json projectile_speed_mps) and lives out its range, so clients animate it at its Launch velocity's speed, not the bazooka's 40 m/s.</summary>
    public const byte ProtocolVersion = 16;
    public const byte TickRate = 30;
    public const int ChanReliable = 0, ChanData = 1, Channels = 2;
    public const int KeyEvery = 30;
    public const int MaxInputsPerPacket = 3;
    public const int MaxNameBytes = 24;
    public const int MaxTokenBytes = 64;
    /// <summary>An input is this many bytes; a flags byte (bit 0: the gunner's aim follows) is its last.</summary>
    public const int InputBytes = 12;
    /// <summary>The gunner's aim (#161): three int32 quantised positions, after the input when its flags say so.</summary>
    public const int AimBytes = 12;
    public const byte InputFlagAim = 1;

    public const byte THello = 1, TWelcome = 2, TJoin = 3, TLeave = 4, TEvent = 5, TProfile = 6, TSaveLoadout = 7, TSelectSlot = 8, TError = 9, TSaveCamo = 10;
    public const byte TInput = 11, TSnapshot = 20, TWorldSnapshot = 21;
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
        WriteStr(o, l.Name);
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
            return new Loadout(slot, Str(), Str(), Str(), Str(), Str(), Str(), Str());
        }
        /// <summary>A fixed-size camo spec. A bad version throws, which DecodeReliable turns into null.</summary>
        public CamoSpec Camo()
        {
            var spec = CamoSpec.TryDecode(d.Slice(_p, CamoSpec.EncodedSize)) ?? throw new ArgumentOutOfRangeException(nameof(d), "bad camo spec");
            _p += CamoSpec.EncodedSize;
            return spec;
        }
    }

    public static byte[] Encode(ReliableMsg m)
    {
        var o = new List<byte>();
        Span<byte> t = stackalloc byte[4];
        switch (m)
        {
            case HelloMsg h: o.Add(THello); o.Add(h.Version); WriteName(o, h.Name); WriteStr(o, Fit(h.Token, MaxTokenBytes)); o.Add(h.Flags); break;
            case WelcomeMsg w:
                o.Add(TWelcome); o.Add(w.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(t, w.PlayerId); o.AddRange(t[..2].ToArray());
                o.Add(w.TickRate);
                BinaryPrimitives.WriteUInt32LittleEndian(t, w.ServerTick); o.AddRange(t.ToArray());
                BinaryPrimitives.WriteUInt16LittleEndian(t, w.StartMinute); o.AddRange(t[..2].ToArray());
                BinaryPrimitives.WriteUInt32LittleEndian(t, w.DayLengthMs); o.AddRange(t.ToArray());
                o.Add(w.Frozen ? (byte)1 : (byte)0);
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
                var camos = p.Camos ?? Array.Empty<CamoSpec>();
                o.Add((byte)camos.Count);
                foreach (var cs in camos) o.AddRange(cs.Encode());
                break;
            case SaveLoadoutMsg sl:
                o.Add(TSaveLoadout); WriteLoadout(o, sl.Loadout);
                BinaryPrimitives.WriteUInt32LittleEndian(t, sl.Seq); o.AddRange(t.ToArray());
                break;
            case SelectSlotMsg ss: o.Add(TSelectSlot); o.Add((byte)ss.Slot); break;
            case SaveCamoMsg sc: o.Add(TSaveCamo); o.Add((byte)sc.Slot); o.AddRange(sc.Spec.Encode()); break;
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
                    int p = 3 + n;
                    var token = "";
                    if (p < d.Length) { int tl = d[p]; token = Encoding.UTF8.GetString(d.Slice(p + 1, tl)); p += 1 + tl; }
                    var flags = p < d.Length ? d[p] : (byte)0;
                    return new HelloMsg(d[1], name, token, flags);
                }
                case TWelcome:
                    return new WelcomeMsg(d[1], BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), d[4], BinaryPrimitives.ReadUInt32LittleEndian(d[5..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(d[9..]), BinaryPrimitives.ReadUInt32LittleEndian(d[11..]), d[15] != 0);
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
                    var camos = new CamoSpec[r.U8()];
                    for (var i = 0; i < camos.Length; i++) camos[i] = r.Camo();
                    return new ProfileMsg(name, xp, level, active, loadouts, unlocks, camos);
                }
                case TSaveLoadout:
                {
                    var r = new Reader(d);
                    var l = r.Loadout();
                    return new SaveLoadoutMsg(l, r.U32());
                }
                case TSelectSlot: return new SelectSlotMsg(d[1]);
                case TSaveCamo: return CamoSpec.TryDecode(d[2..]) is { } cs ? new SaveCamoMsg(d[1], cs) : null;
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

    /// <summary>Encode the newest inputs (up to 3, oldest first) as one packet. An input with an aim adds <see cref="AimBytes"/>.</summary>
    public static byte[] EncodeInputs(ReadOnlySpan<NetInput> inputs)
    {
        var n = Math.Min(inputs.Length, MaxInputsPerPacket);
        var size = 2;
        for (var i = 0; i < n; i++) size += InputSize(inputs[inputs.Length - n + i]);
        var b = new byte[size];
        b[0] = TInput; b[1] = (byte)n;
        var at = 2;
        for (var i = 0; i < n; i++)
        {
            var x = inputs[inputs.Length - n + i];
            var s = b.AsSpan(at);
            BinaryPrimitives.WriteUInt16LittleEndian(s, x.Seq);
            s[2] = (byte)x.MoveX; s[3] = (byte)x.MoveY;
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], x.Yaw);
            BinaryPrimitives.WriteInt16LittleEndian(s[6..], x.Pitch);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], x.Buttons);
            s[10] = x.Slot;
            s[11] = x.HasAim ? InputFlagAim : (byte)0;
            at += InputBytes;
            if (x.HasAim)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), x.AimX);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 4), x.AimY);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 8), x.AimZ);
                at += AimBytes;
            }
        }
        return b;
    }

    static int InputSize(in NetInput x) => InputBytes + (x.HasAim ? AimBytes : 0);

    /// <summary>Decodes a packet of inputs (the newest up to 3 in order). False when the packet is malformed or truncated.</summary>
    public static bool TryDecodeInputs(ReadOnlySpan<byte> d, List<NetInput> into)
    {
        if (d.Length < 2 || d[0] != TInput) return false;
        int n = d[1];
        if (n < 1 || n > MaxInputsPerPacket) return false;
        var at = 2;
        for (var i = 0; i < n; i++)
        {
            if (d.Length < at + InputBytes) return false;
            var s = d[at..];
            var flags = s[11];
            var x = new NetInput
            {
                Seq = BinaryPrimitives.ReadUInt16LittleEndian(s),
                MoveX = (sbyte)s[2], MoveY = (sbyte)s[3],
                Yaw = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
                Pitch = BinaryPrimitives.ReadInt16LittleEndian(s[6..]),
                Buttons = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
                Slot = s[10],
            };
            at += InputBytes;
            if ((flags & InputFlagAim) != 0)
            {
                if (d.Length < at + AimBytes) return false;
                x.HasAim = true;
                x.AimX = BinaryPrimitives.ReadInt32LittleEndian(d[at..]);
                x.AimY = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 4)..]);
                x.AimZ = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 8)..]);
                at += AimBytes;
            }
            into.Add(x);
        }
        return true;
    }
}
