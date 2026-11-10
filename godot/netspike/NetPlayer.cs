using Godot;

/// <summary>
/// Player body: a CharacterBody3D (capsule, origin at the feet). The server steps it from the
/// latest client input; clients never move it, they only receive position/rotation/velocity.
/// "Tick" is the server tick that produced the state (replicated so clients can measure age).
/// </summary>
public partial class NetPlayer : CharacterBody3D
{
    public const float Speed = 5.0f, JumpV = 5.0f, Gravity = 9.8f;

    [Export] public int Tick { get; set; }
    public int Id;
    public bool IsServer;
    public int Seq; // last applied input sequence
    float _mx, _mz, _yaw; bool _jump;

    public void SetInput(int seq, int mx, int mz, int yawQ, int jump)
    {
        Seq = seq;
        _mx = Mathf.Clamp(mx, -127, 127) / 127f;
        _mz = Mathf.Clamp(mz, -127, 127) / 127f;
        _yaw = Net.DYaw(yawQ);
        _jump = jump != 0;
    }

    /// <summary>One fixed step on the real map collision.</summary>
    public void Step(float dt, int tick)
    {
        var wish = new Vector3(_mx, 0, _mz);
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();
        wish = new Basis(Vector3.Up, _yaw) * wish * Speed;
        var v = Velocity;
        v.X = wish.X; v.Z = wish.Z;
        if (IsOnFloor()) v.Y = _jump ? JumpV : -0.5f;
        else v.Y -= Gravity * dt;
        Velocity = v;
        Rotation = new Vector3(0, _yaw, 0);
        MoveAndSlide();
        Tick = tick;
    }

    public static NetPlayer Make(int id, Vector3 pos, bool server)
    {
        var p = new NetPlayer { Name = id.ToString(), Id = id, IsServer = server, Position = pos };
        p.FloorSnapLength = 0.4f;
        p.MaxSlides = 4;
        p.FloorMaxAngle = Mathf.DegToRad(50);
        if (server)
        {   // players collide with the world only (layer 2, mask 1), not each other
            p.CollisionLayer = 2; p.CollisionMask = 1;
            var cs = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f }, Position = new Vector3(0, 0.9f, 0) };
            p.AddChild(cs);
        }
        return p;
    }
}

/// <summary>Random walker with periodic jumps. Produces the quantised input that goes on the wire.</summary>
public class BotInput
{
    readonly RandomNumberGenerator _r = new();
    float _t, _jumpT, _yaw; int _mz = -127; int _jumpTicks;
    public int Seq;
    public int Mx, Mz, YawQ, Jump;

    public BotInput(ulong seed)
    {
        _r.Seed = seed;
        _yaw = _r.RandfRange(0, Mathf.Tau);
        _t = _r.RandfRange(1, 3); _jumpT = _r.RandfRange(2, 5);
    }

    public void Next(float dt)
    {
        _t -= dt; _jumpT -= dt;
        if (_t <= 0)
        {
            _yaw = _r.RandfRange(0, Mathf.Tau);
            _mz = _r.Randf() < 0.15f ? 0 : -127; // forward is -Z; sometimes stand still
            _t = _r.RandfRange(1, 3.5f);
        }
        if (_jumpT <= 0) { _jumpTicks = 3; _jumpT = _r.RandfRange(2.5f, 5f); }
        Jump = _jumpTicks > 0 ? 1 : 0;
        if (_jumpTicks > 0) _jumpTicks--;
        Mx = 0; Mz = _mz; YawQ = Net.QYaw(_yaw);
        Seq = (Seq + 1) & 0xFFFF;
    }
}
