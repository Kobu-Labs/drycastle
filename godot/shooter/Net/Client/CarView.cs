using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>A vehicle as this client draws it: the model node, the interpolation buffer, what the latest record says and the parts of its type (rotors, wheels, lamps, sound, trailer).</summary>
internal sealed class CarView
{
    public Node3D Node = null!;
    public ushort Id;
    /// <summary>A flak truck's moving nodes (null for every other type, or a model without them).</summary>
    public FlakNodes? Flak;
    /// <summary>A door gun's moving nodes (the utility helicopter; null for every other type, or a model without them).</summary>
    public DoorGunNodes? DoorGun;
    /// <summary>A flying vehicle's rotors, their sound and the downwash (null for everything that drives, or a model without rotors).</summary>
    public RotorFx? Rotor;
    /// <summary>A driven vehicle's moving nodes and water effects (the amphibious transport; null for every other type, or a model without them).</summary>
    public DriveNodes? Drive;
    /// <summary>The generic wheel animation of a ground vehicle that is neither predicted nor has its own (null otherwise, or a model without wheels).</summary>
    public VehicleWheels? Roller;
    public readonly Interpolator Buf = new();
    /// <summary>The vehicle's visible parts (<see cref="CarFx.VisualName"/>): a boat's wave bob and tilt move this, not the root.</summary>
    public Node3D? Visual;
    /// <summary>Next time (s) a wake ring is due while the vehicle moves on the water.</summary>
    public double RippleDue;
    public ushort Driver;
    public int Kind;
    public NVec3 Pos;
    public float Yaw, Kmh;
    /// <summary>The yaw of the newest interpolated snapshot sample at the render tick, before the drawn pose replaces it: the hull's yaw the riders' snapshot views are taken against (<see cref="RiderPose.Figure"/>).</summary>
    public float SnapYaw;
    public uint LastTick;
    public LoopVoice? Snd;
    /// <summary>The layered engine sounds of a type with a vehicles.json "sounds" set (null otherwise, and until driven).</summary>
    public VehicleEngineSound? Engine;
    /// <summary>The clock of the reversing beep of a type with vehicles.json "reverse_beep".</summary>
    public ReverseBeep Beep;
    /// <summary>Damage stage 0..3 from the record, whether the vehicle is destroyed, and whether its blast was already drawn (event or state, whichever came first).</summary>
    public int Stage;
    public bool Destroyed, Exploded;
    public double ExplodedAt;
    public VehicleFx.Damage? Smoke;
    /// <summary>Headlights on (the record's bit), braking (speed fell this snapshot) and the lamps (null: the type has none).</summary>
    public bool Lights, Braking;
    public CarLights? Lamps;
    /// <summary>The solid box predicted on-foot movement collides with (#217), at the last snapshot's place: the server's own box is built from the same numbers.</summary>
    public StaticBody3D? Solid;
    /// <summary>The model's wheel nodes (front left, front right, back left, back right; null entries where it has none, the array null on a headless client) and the wheels the Thieves took, as last applied (NetClient.Thieves.Loot.cs).</summary>
    public Node3D?[]? Wheels;
    public byte WheelsOff;
    /// <summary>A trailer's tractor, load and moving nodes (null for every type that is not a trailer): it is drawn from its tractor, <see cref="RenderTrailers"/>.</summary>
    public TrailerView? Trailer;

    static readonly string[] WheelNodeNames = { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };

    /// <summary>The wheel nodes of a vehicle model by wheel index (front left, front right, back left, back right; null where the model has none).</summary>
    public static Node3D?[] FindWheels(Node3D car)
    {
        var wheels = new Node3D?[WheelNodeNames.Length];
        for (var i = 0; i < wheels.Length; i++) wheels[i] = car.FindChild(WheelNodeNames[i], true, false) as Node3D;
        return wheels;
    }

    /// <summary>Hides the wheels the record says are taken (the Thieves took them) and shows the ones that are back (a change of the mask only).</summary>
    public void ApplyWheels(byte mask)
    {
        if (Wheels == null || WheelsOff == mask) return;
        WheelsOff = mask;
        for (var i = 0; i < Wheels.Length; i++)
            if (Wheels[i] is { } w && GodotObject.IsInstanceValid(w)) w.Visible = (mask & (1 << i)) == 0;
    }
}

/// <summary>What the client keeps of a trailer besides the vehicle: its tractor and load as the record says, the model's moving nodes, and what is drawn.</summary>
internal sealed class TrailerView
{
    /// <summary>The vehicle it is hitched to (0: loose) and whether it carries its load, as the newest record says. <see cref="Seen"/>: a record has been applied (the first is not a coupling).</summary>
    public ushort TractorId;
    public bool Loaded = true, Seen;
    /// <summary>The model's nodes by name (null where it has none, and on a headless client): wheels roll, the missile is shown while loaded.</summary>
    public Node3D? WheelL, WheelR, Missile;
    public float RadiusL = 0.45f, RadiusR = 0.45f;
    /// <summary>The ground fit as drawn (smoothed), the drawn speed along the heading (from the drawn motion) and where it was drawn last frame.</summary>
    public float Pitch, Roll, Speed;
    /// <summary>How it lies on the ground as the latest record says (unsmoothed, the server's own fit: <see cref="TrailerProbe.Attitude"/>); its hit box and solid box turn by it (<see cref="HullTiltOf"/>).</summary>
    public TrailerAttitude.Result? Hull;
    public Vector3 LastPos;
    public bool HasLast;
}
