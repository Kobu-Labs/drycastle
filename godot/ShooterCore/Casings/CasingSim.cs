using System.Numerics;

namespace Drycastle.ShooterCore;

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

/// <summary>Where the ground is under a point: the floor height below <c>from</c> within <c>depth</c> metres, and whether it is a vehicle's solid box (not terrain).</summary>
public delegate bool CasingFloor(Vector3 from, float depth, out float y, out bool onVehicle);

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
        .Note(CasingPhase.Flying, "Ballistic arc, then bounces (up to Bounces, each quieter) and a short slide. Ends lying still, or at once in the void (no floor 8 m below where it started).")
        .Note(CasingPhase.Resting, "Lies flat on the floor for Stay seconds (a vehicle's roof: VehicleStay). Counts a clock; no movement, so its instance is not touched.")
        .Note(CasingPhase.Fading, "Shrinks to nothing over FadeSeconds, then the slot is free.")
        .Build();

    public const int Capacity = 300;
    public const float Gravity = 9.8f, Stay = 20f, VehicleStay = 1f, FadeSeconds = 0.8f;
    /// <summary>A bounce slower than this (m/s, downwards) is not a bounce: the casing settles.</summary>
    public const float MinBounce = 0.7f;
    const int MaxBounces = 3;
    const float Restitution = 0.35f, Friction = 0.7f, Hard = 0.6f;

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
        s.FloorY = floor(pos, 60f, out var y, out _) ? y : float.NaN;
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

    void Fly(Slot s, float dt, CasingFloor floor)
    {
        var p = CasingProfiles.Of(s.Kind);
        s.Dirty = true;
        s.Vel.Y -= Gravity * dt;
        s.Pos += s.Vel * dt;
        s.Tumble += s.TumbleRate * dt;
        if (s.Pos.Y < s.StartY - 8f && float.IsNaN(s.FloorY)) { s.Fsm.Enter(CasingPhase.Free); return; } // fell into the void
        // the floor is found again only (from just above the casing: a step may end below it) when the casing is close to the one cached (or none was found)
        var onVehicle = false;
        if (float.IsNaN(s.FloorY) || s.Pos.Y - s.FloorY < 1.2f)
        {
            if (floor(s.Pos + new Vector3(0, 0.3f, 0), 1.7f + MathF.Max(0f, -s.Vel.Y) * dt * 2f, out var y, out onVehicle)) s.FloorY = y; else s.FloorY = float.NaN;
        }
        if (float.IsNaN(s.FloorY)) return;
        var contact = s.FloorY + Reach(s, p);
        if (s.Pos.Y > contact) return;
        s.Pos.Y = contact;
        var speed = -s.Vel.Y;
        if (speed > MinBounce && s.Bounces < MaxBounces)
        {
            s.Bounces++;
            s.Vel = new Vector3(s.Vel.X * Friction, speed * Restitution, s.Vel.Z * Friction);
            s.TumbleRate *= Hard;
            Impacts.Add(new Impact(s.Pos, speed, s.Kind));
            return;
        }
        // settled: slide to a stop, lie flat
        s.Vel.Y = 0;
        var k = MathF.Max(0f, 1f - 8f * dt);
        s.Vel.X *= k; s.Vel.Z *= k;
        var flat = MathF.Round(s.Tumble / MathF.PI) * MathF.PI;
        s.Tumble += (flat - s.Tumble) * MathF.Min(1f, dt * 25f);
        s.TumbleRate = 0;
        if (s.Vel.X * s.Vel.X + s.Vel.Z * s.Vel.Z < 0.0025f)
        {
            s.Tumble = flat; s.Vel = Vector3.Zero;
            s.Fsm.Enter(CasingPhase.Resting, onVehicle ? VehicleStay : Stay);
        }
    }
}
