using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What the demon is doing: the replicated state byte. Waiting is still at its anchor (nobody inside), Dwelling rests, Telegraphing is the wind-up of a fireball. Climbing to the first anchor after the turning shows as Crawling.</summary>
public enum DemonState : byte { Transforming = 0, Waiting = 1, Dwelling = 2, Crawling = 3, Telegraphing = 4, Dead = 5 }

/// <summary>Demon world entity rules: the wire type, the id it is hit under, and how its surface normal travels.</summary>
public static class DemonRules
{
    /// <summary>Wire id of the demon world entity type (51 is the citizen).</summary>
    public const byte TypeId = 52;

    /// <summary>
    /// The id the demon has as a hit target (the weapon paths know bodies by id): just below the citizens' range
    /// (<see cref="CitizenRules.FirstHitId"/> up), above vehicles and players. There is one demon.
    /// </summary>
    public const ushort HitId = 39999;

    public static bool IsHitId(ushort id) => id == HitId;

    public static DemonState StateOf(byte state) => state <= (byte)DemonState.Dead ? (DemonState)state : DemonState.Dwelling;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "demon", HasHp: true));
    }

    /// <summary>The hit volume of a demon at the pose: a crouching figure's capsule centred <paramref name="bodyHeightM"/> off the surface along its normal.</summary>
    public static HitTarget Target(Vector3 surface, Vector3 normal, float bodyHeightM, float yaw)
    {
        var centre = surface + normal * bodyHeightM;
        return new HitTarget(HitId, centre - new Vector3(0, MoveConsts.CrouchHeight / 2, 0), Stance.Crouch, yaw);
    }

    /// <summary>A unit vector in two bytes (octahedral, about 1.5 degrees off at worst; the axes of the church are exact).</summary>
    public static ushort PackNormal(Vector3 n)
    {
        var l = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        if (!(l > 1e-6f)) return (ushort)(Q(0) | Q(0) << 8);
        var x = n.X / l; var y = n.Z / l; var up = n.Y / l;
        if (up < 0)
        {
            var ox = x; var oy = y;
            x = (1 - MathF.Abs(oy)) * (ox >= 0 ? 1 : -1);
            y = (1 - MathF.Abs(ox)) * (oy >= 0 ? 1 : -1);
        }
        return (ushort)(Q(x) | Q(y) << 8);
    }

    static int Q(float v) => Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255f), 0, 255);

    public static Vector3 UnpackNormal(ushort p)
    {
        var x = (p & 0xFF) / 255f * 2 - 1;
        var y = (p >> 8) / 255f * 2 - 1;
        var up = 1 - MathF.Abs(x) - MathF.Abs(y);
        var t = MathF.Max(-up, 0);
        x += x >= 0 ? -t : t;
        y += y >= 0 ? -t : t;
        var n = new Vector3(x, up, y);
        return n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    /// <summary>The direction a yaw and pitch of the world state mean (Godot: yaw 0 faces -Z, positive turns left).</summary>
    public static Vector3 Facing(float yaw, float pitch)
    {
        var c = MathF.Cos(pitch);
        return new Vector3(-MathF.Sin(yaw) * c, MathF.Sin(pitch), -MathF.Cos(yaw) * c);
    }
}

/// <summary>
/// The demon as a world entity (type 52). It has no behaviour of its own: the server's fight (<see cref="DemonBrain"/> and the scripted turning and
/// climb) sets its pose and state each tick, and it replicates like every world entity: the position is where the surface under it is, the owner
/// field the packed surface normal (<see cref="DemonRules.PackNormal"/>), yaw and pitch the way it faces along the surface, the state byte a
/// <see cref="DemonState"/>, and the hp byte the share of hits left (255 full, 0 dead).
/// </summary>
public sealed class DemonEntity : IWorldEntity
{
    public Vector3 Position { get; set; }
    public Vector3 Normal { get; set; } = Vector3.UnitY;
    public Vector3 Forward { get; set; } = -Vector3.UnitZ;
    public DemonState State { get; set; } = DemonState.Transforming;
    /// <summary>Share of the hits needed that it still has (1 full).</summary>
    public float HitsLeft { get; set; } = 1f;

    public byte TypeId => DemonRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; set; }

    public void Tick(float dt) { }
    public void OnDamage(float amount, ushort attacker) { } // hits go to the brain (NetServer.Demon), not through the entity manager

    public WorldState Serialize()
    {
        var f = Forward;
        var yaw = MathF.Atan2(-f.X, -f.Z);
        var pitch = MathF.Asin(Math.Clamp(f.Y, -1f, 1f));
        var hp = HitsLeft <= 0 ? 0 : MathF.Ceiling(Math.Clamp(HitsLeft, 0f, 1f) * 255f);
        return WorldState.Make(Position, yaw, pitch, DemonRules.PackNormal(Normal), (byte)State, hp);
    }
}

/// <summary>A fireball leaves the demon: where, and the velocity the arc starts with (gravity is the data's).</summary>
public readonly record struct DemonFireballMsg(ushort Id, Vector3 Origin, Vector3 Velocity);

/// <summary>A fireball ended at <see cref="At"/>: on a player, on a wall, or burnt out in the air.</summary>
public readonly record struct DemonImpactMsg(ushort Id, Vector3 At, bool HitPlayer, bool Fizzled);

/// <summary>Wire format of the demon events (<see cref="EventKind.DemonFireball"/>, <see cref="EventKind.DemonImpact"/>). Little-endian.</summary>
public static class DemonCodec
{
    public const int FireballBytes = 26, ImpactBytes = 15;

    static void Put(Span<byte> b, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b[8..], v.Z);
    }

    static Vector3 Get(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]), BinaryPrimitives.ReadSingleLittleEndian(b[8..]));

    public static byte[] EncodeFireball(DemonFireballMsg m)
    {
        var b = new byte[FireballBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        Put(b.AsSpan(2), m.Origin);
        Put(b.AsSpan(14), m.Velocity);
        return b;
    }

    public static DemonFireballMsg? DecodeFireball(ReadOnlySpan<byte> d) =>
        d.Length == FireballBytes ? new DemonFireballMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), Get(d[2..]), Get(d[14..])) : null;

    public static byte[] EncodeImpact(DemonImpactMsg m)
    {
        var b = new byte[ImpactBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        Put(b.AsSpan(2), m.At);
        b[14] = (byte)((m.HitPlayer ? 1 : 0) | (m.Fizzled ? 2 : 0));
        return b;
    }

    public static DemonImpactMsg? DecodeImpact(ReadOnlySpan<byte> d) =>
        d.Length == ImpactBytes ? new DemonImpactMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), Get(d[2..]), (d[14] & 1) != 0, (d[14] & 2) != 0) : null;
}
