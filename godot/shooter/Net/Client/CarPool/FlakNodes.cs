using Godot;

namespace Drycastle.Shooter.Net.Client.CarPool;

/// <summary>The nodes of a flak model that move, found by name (the model's own, see assets/drycastle-generated-models), and the barrels' recoil.</summary>
internal sealed class FlakNodes
{
    public Node3D Turret = null!, Cradle = null!;
    public Node3D? Sight, BarrelL, BarrelR;
    public Vector3 RestL, RestR;
    public float KickL, KickR;
    int _next;

    /// <summary>Metres a barrel slides back at full kick, and how fast it comes home (metres per second).</summary>
    const float RecoilM = 0.35f, ReturnMps = 2.4f;

    public static FlakNodes? Find(Node3D root)
    {
        if (root.FindChild("turret", true, false) is not Node3D turret || root.FindChild("gun_cradle", true, false) is not Node3D cradle) return null;
        var f = new FlakNodes { Turret = turret, Cradle = cradle, Sight = root.FindChild("sight", true, false) as Node3D };
        f.BarrelL = root.FindChild("barrel_l", true, false) as Node3D;
        f.BarrelR = root.FindChild("barrel_r", true, false) as Node3D;
        if (f.BarrelL != null) f.RestL = f.BarrelL.Position;
        if (f.BarrelR != null) f.RestR = f.BarrelR.Position;
        return f;
    }

    /// <summary>A round left: the next barrel kicks back (the two fire in turn, as on the server).</summary>
    public void Kick()
    {
        if ((_next++ & 1) == 0) KickL = 1f; else KickR = 1f;
    }

    public void Step(float dt)
    {
        KickL = Mathf.MoveToward(KickL, 0, dt * ReturnMps / RecoilM);
        KickR = Mathf.MoveToward(KickR, 0, dt * ReturnMps / RecoilM);
        if (BarrelL != null) BarrelL.Position = RestL + new Vector3(0, 0, KickL * RecoilM);
        if (BarrelR != null) BarrelR.Position = RestR + new Vector3(0, 0, KickR * RecoilM);
    }
}
