using Godot;
using Drycastle.Shooter.Audio;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Shooting;

/// <summary>A rocket, a homing missile or a thrown grenade in the air as this client draws it: the node, the newest known place and heading, and what the flight loop needs.</summary>
internal sealed class RocketView
{
    public double Born;
    public Node3D Node = null!;
    public float Tumble;
    public NVec3 Pos, Dir;
    public double At;
    public float Speed;
    public LoopVoice? Snd;
    /// <summary>A homing missile (the Lock-on launcher): the aircraft it closes on and when it gets there.</summary>
    public bool Homing;
    public ushort Target;
    public double Arrive;
    /// <summary>A thrown grenade: drawn falling along its arc from the last snapshot.</summary>
    public bool Grenade;
    public float Gravity;

    /// <summary>The flight loop follows the drawn rocket.</summary>
    public void PlayFlightLoop(Vector3 at)
    {
        if (!Sfx.Enabled) return;
        Snd ??= Sfx.Loop("rocket.flight", at);
        Snd?.Update(at);
    }
}
