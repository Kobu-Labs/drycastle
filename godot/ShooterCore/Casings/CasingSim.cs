using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Casings;

/// <summary>What a spent casing looks like: the kind of gun it came from (#307). Size, colour and loudness live in <see cref="CasingProfiles"/>.</summary>
public enum CasingKind : byte { Pistol, Rifle, Shotgun, Heavy, Shell }

/// <summary>The size and look of one <see cref="CasingKind"/>. Metres; <see cref="Colour"/> is r, g, b in 0..1; <see cref="DbOffset"/> is added to the clink.</summary>
public readonly record struct CasingProfile(float Length, float Diameter, float Eject, float PortBack, float Colour0, float Colour1, float Colour2, float DbOffset);

/// <summary>The casing of every gun kind, and which kind a weapon category ejects.</summary>
public static class CasingProfiles
{
    public static CasingProfile Of(CasingKind k) => k switch
    {
        CasingKind.Pistol => new(0.030f, 0.011f, 2.2f, 0.10f, 0.86f, 0.66f, 0.22f, -6f),
        CasingKind.Rifle => new(0.055f, 0.014f, 2.6f, 0.20f, 0.86f, 0.66f, 0.22f, -3f),
        CasingKind.Shotgun => new(0.070f, 0.022f, 2.4f, 0.20f, 0.74f, 0.14f, 0.12f, -3f),
        CasingKind.Heavy => new(0.110f, 0.024f, 3.0f, 0.30f, 0.80f, 0.62f, 0.25f, 0f),
        _ => new(0.230f, 0.050f, 3.4f, 0.40f, 0.62f, 0.52f, 0.30f, 3f),
    };

    /// <summary>The casing a hand weapon ejects, or null for a weapon that ejects none (melee, launchers).</summary>
    public static CasingKind? Of(WeaponCategory c) => c switch
    {
        WeaponCategory.Pistol => CasingKind.Pistol,
        WeaponCategory.Smg or WeaponCategory.AssaultRifle or WeaponCategory.Sniper => CasingKind.Rifle,
        WeaponCategory.MachineGun => CasingKind.Rifle,
        WeaponCategory.Shotgun => CasingKind.Shotgun,
        _ => null,
    };
}

public enum CasingPhase : byte { Free, Flying, Resting, Fading }

/// <summary>
/// What a casing lands on: the floor's height and unit normal, its ground type (the collider's group as <see cref="SurfaceClassifier"/> reads it; water is not here, the sim
/// asks <see cref="Water"/>), and whether it is a vehicle's solid box (not terrain). Not used yet: <see cref="Ground"/> (#316 will pick a soft tink by it).
/// </summary>
public readonly record struct CasingSurface(float Y, Vector3 Normal, SurfaceKind Ground, bool OnVehicle)
{
    /// <summary>Level ground at <paramref name="y"/>.</summary>
    public static CasingSurface Flat(float y, SurfaceKind ground = SurfaceKind.Ground, bool onVehicle = false) => new(y, Vector3.UnitY, ground, onVehicle);
}

/// <summary>Where the floor is under a point: the surface below <c>from</c> within <c>depth</c> metres, false for none. One delegate, no allocation per call.</summary>
public delegate bool CasingFloor(Vector3 from, float depth, out CasingSurface surface);

/// <summary>
/// Spent casings (#307): a fixed ring of slots, each a casing that flies on a ballistic arc, bounces on the floor a few times, lies still and fades out. Pure and
/// allocation-free after construction; the client (<c>CasingFx</c>) feeds it the floor and draws the slots. Each slot is a state machine (<see cref="Spec"/>); a full ring
/// takes over the oldest slot, whatever it is doing.
/// </summary>
public sealed class CasingSim
{
    public static readonly StateMachineSpec<CasingPhase> Spec = StateMachineSpec<CasingPhase>
        .Define("Casing.Phase", "One spent casing: in the air or sliding, lying still on the floor, fading out, or a free slot.", CasingPhase.Free)
        .Allow(CasingPhase.Free, CasingPhase.Flying)
        .Allow(CasingPhase.Flying, CasingPhase.Resting, CasingPhase.Free)
        .Allow(CasingPhase.Resting, CasingPhase.Fading)
        .Allow(CasingPhase.Fading, CasingPhase.Free)
        .Note(CasingPhase.Flying, "Ballistic arc, then bounces off the surface normal (up to Bounces, each quieter) and a short slide; on a steep face it slides down instead of settling. Under water it sinks slowly, never bounces, never clinks. Ends lying still, or at once in the void (no floor 8 m below where it started).")
        .Note(CasingPhase.Resting, "Lies flat on the floor for Stay seconds (a vehicle's roof: VehicleStay). Counts a clock; no movement, so its instance is not touched.")
        .Note(CasingPhase.Fading, "Shrinks to nothing over FadeSeconds, then the slot is free.")
        .Build();

    public const int Capacity = 300;
    public const float Gravity = 9.8f, Stay = 20f, VehicleStay = 1f, FadeSeconds = 0.8f;
    /// <summary>A bounce slower than this (m/s, downwards) is not a bounce: the casing settles.</summary>
    public const float MinBounce = 0.7f;
    const int MaxBounces = 3;
    const float Restitution = 0.35f, Friction = 0.7f, Hard = 0.6f;
    /// <summary>A face steeper than this (normal Y; about 37 degrees) is no place to lie: the casing slides down it.</summary>
    public const float SlideNormalY = 0.8f;
    const float SlideDrag = 1f;
    /// <summary>Under water: the sinking speed (m/s) and how fast the casing's motion is brought to it (1/s).</summary>
    public const float SinkSpeed = 0.6f;
    const float WaterDrag = 8f;

    public sealed class Slot
    {
        public readonly int Index;
        public Slot(int index) { Index = index; }
        public readonly StateMachine<CasingPhase> Fsm = new(Spec);
        public Vector3 Pos, Vel;
        public float Yaw, Tumble, TumbleRate, FloorY = float.NaN, StartY;
        public CasingKind Kind;
        public int Bounces;
        /// <summary>Set whenever the slot's drawn transform changed this step; the caller clears it after drawing.</summary>
        public bool Dirty;
        public CasingPhase Phase => Fsm.Current;
        /// <summary>0..1 draw scale: 1 until it fades.</summary>
        public float Scale => Fsm.Is(CasingPhase.Fading) ? Math.Max(0f, Fsm.Left / FadeSeconds) : Fsm.Is(CasingPhase.Free) ? 0f : 1f;
    }

    /// <summary>One landing worth a clink: where, how hard (m/s) and of which kind.</summary>
    public readonly record struct Impact(Vector3 At, float Speed, CasingKind Kind);

    public readonly Slot[] Slots = new Slot[Capacity];
    public readonly List<Impact> Impacts = new();
    /// <summary>The map's water: a casing below its surface sinks to the bed and makes no sound. Set by the caller.</summary>
    public Water Water = Water.Empty;
    int _next;

    public CasingSim() { for (var i = 0; i < Capacity; i++) Slots[i] = new Slot(i); }

    public int Live { get { var n = 0; foreach (var s in Slots) if (s.Phase != CasingPhase.Free) n++; return n; } }

    /// <summary>
    /// A casing leaves at <paramref name="pos"/> with <paramref name="vel"/>, turning about its axis. <paramref name="floor"/> finds the floor once for the cached height.
    /// Takes the oldest slot when the ring is full.
    /// </summary>
    public Slot Spawn(Vector3 pos, Vector3 vel, float yaw, float tumbleRate, CasingKind kind, CasingFloor floor)
    {
        var s = Slots[_next];
        _next = (_next + 1) % Capacity;
        if (s.Phase != CasingPhase.Free) s.Fsm.Reset(CasingPhase.Free); // taken over
        s.Fsm.Enter(CasingPhase.Flying);
        s.Pos = pos; s.Vel = vel; s.Yaw = yaw; s.Tumble = 0; s.TumbleRate = tumbleRate; s.Kind = kind; s.Bounces = 0; s.StartY = pos.Y;
        s.FloorY = floor(pos, 60f, out var surface) ? surface.Y : float.NaN;
        s.Dirty = true;
        return s;
    }

    /// <summary>Half the height the casing's body reaches above its centre: lying flat it is the radius, standing on end half its length.</summary>
    static float Reach(Slot s, CasingProfile p) => p.Length * 0.5f * MathF.Abs(MathF.Sin(s.Tumble)) + p.Diameter * 0.5f * MathF.Abs(MathF.Cos(s.Tumble));

    public void Step(float dt, CasingFloor floor)
    {
        Impacts.Clear();
        foreach (var s in Slots)
        {
            switch (s.Phase)
            {
                case CasingPhase.Flying: Fly(s, dt, floor); break;
                case CasingPhase.Resting: if (s.Fsm.Tick(dt)) s.Fsm.Enter(CasingPhase.Fading, FadeSeconds); break;
                case CasingPhase.Fading: s.Dirty = true; if (s.Fsm.Tick(dt)) s.Fsm.Enter(CasingPhase.Free); break;
            }
        }
    }

    bool Submerged(Vector3 at) => at.Y < Water.SurfaceY(at.X, at.Z); // NaN (dry) compares false

    void Fly(Slot s, float dt, CasingFloor floor)
    {
        var p = CasingProfiles.Of(s.Kind);
        s.Dirty = true;
        var wet = Submerged(s.Pos);
        if (wet)
        {
            // water: no gravity arc, the motion is brought to a slow sink
            var k = MathF.Min(1f, WaterDrag * dt);
            s.Vel += (new Vector3(0, -SinkSpeed, 0) - s.Vel) * k;
            s.TumbleRate *= 1f - k;
        }
        else s.Vel.Y -= Gravity * dt;
        s.Pos += s.Vel * dt;
        s.Tumble += s.TumbleRate * dt;
        if (s.Pos.Y < s.StartY - 8f && float.IsNaN(s.FloorY)) { s.Fsm.Enter(CasingPhase.Free); return; } // fell into the void
        // the floor is found again only (from just above the casing: a step may end below it) when the casing is close to the one cached (or none was found)
        var surface = CasingSurface.Flat(0f);
        if (float.IsNaN(s.FloorY) || s.Pos.Y - s.FloorY < 1.2f)
        {
            if (floor(s.Pos + new Vector3(0, 0.3f, 0), 1.7f + MathF.Max(0f, -s.Vel.Y) * dt * 2f, out surface)) s.FloorY = surface.Y; else s.FloorY = float.NaN;
        }
        if (float.IsNaN(s.FloorY)) return;
        var contact = s.FloorY + Reach(s, p);
        if (s.Pos.Y > contact) return;
        s.Pos.Y = contact;
        var n = surface.Normal.LengthSquared() < 0.5f || surface.Normal.Y <= 0f ? Vector3.UnitY : Vector3.Normalize(surface.Normal);
        var vn = Vector3.Dot(s.Vel, n); // negative: into the surface
        var speed = -vn;
        var underwater = Submerged(s.Pos); // lying on the bed
        if (!underwater && speed > MinBounce && s.Bounces < MaxBounces)
        {
            s.Bounces++;
            s.Vel = (s.Vel - n * vn) * Friction + n * (speed * Restitution); // off the normal, the along-surface part slowed
            s.TumbleRate *= Hard;
            Impacts.Add(new Impact(s.Pos, speed, s.Kind));
            return;
        }
        // settled: slide along the surface, lie flat (a steep face: slide down it, never rest)
        var vt = vn < 0 ? s.Vel - n * vn : s.Vel;
        var flat = MathF.Round(s.Tumble / MathF.PI) * MathF.PI;
        s.Tumble += (flat - s.Tumble) * MathF.Min(1f, dt * 25f);
        s.TumbleRate = 0;
        if (n.Y < SlideNormalY && !underwater)
        {
            s.Vel = vt * MathF.Max(0f, 1f - SlideDrag * dt); // gravity's share along the face is already in the velocity
            return;
        }
        if (!wet) { var g = new Vector3(0, -Gravity, 0); vt -= (g - n * Vector3.Dot(g, n)) * dt; } // friction holds it on a gentle slope
        s.Vel = vt * MathF.Max(0f, 1f - 8f * dt);
        if (s.Vel.LengthSquared() < 0.0025f)
        {
            s.Tumble = flat; s.Vel = Vector3.Zero;
            s.Pos.Y = s.FloorY + Reach(s, p); // lying flat now
            s.Fsm.Enter(CasingPhase.Resting, surface.OnVehicle ? VehicleStay : Stay);
        }
    }
}
