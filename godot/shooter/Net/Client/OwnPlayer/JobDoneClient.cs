using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The client half of the "Job done" cinematic (docs/cartel.md "Job done"; the timeline and its rules are <see cref="JobDone"/>, the numbers cartel.json <c>job_done_cinematic</c>): it only applies what
/// <see cref="JobDone"/> says. The camera leaves the body in quick moves to the stages (behind and above the player, high over the party, high again and back down), the whoosh and the flash go with each move, the own
/// figure is drawn so the player sees themselves (<see cref="OwnFigure.Seen"/>; the weapon in the hand and the crosshair are hidden meanwhile), the player's own music (<c>job.done.music</c>) is driven by its level, and
/// the body's inputs are held: it stands, a driver's become the brake, a passenger just watches (<see cref="FilterInput"/>; the view's heading is held too, so the mouse turns nothing). A press of jump, fire, interact or escape,
/// damage or death ends the camera at once. The camera is the one composer's last word (<see cref="DeathView"/>) on foot, and put over the chase or seat camera in a road vehicle (<see cref="ApplyInVehicle"/>); it is kept out
/// of terrain and above roofs (<see cref="Clear"/>). With reduce motion, in the air or at a turret there is no camera and no lock: the banner and the music only (<see cref="CameraAllowed"/>).
/// </summary>
internal sealed class JobDoneClient
{
    const string MusicId = "job.done.music", WhooshId = "job.done.whoosh";
    /// <summary>How fast the party's centre is followed (1/s), how high above the camera the roof probe starts, how close a pulled-in camera may come to what it looks at, and its gap to the wall, metres.</summary>
    const float PartyFollow = 6f, ProbeUpM = 300f, MinPullM = 1.5f, WallM = 0.3f;

    readonly ClientContext _ctx;
    readonly JobDone _done;
    readonly JobDoneCinematicData _data;
    readonly Func<bool> _flies;
    readonly List<NVec3> _mates = new();
    /// <summary>The cinematic that started last seen, and the leg whose whoosh played last.</summary>
    Change<int> _run, _leg;
    Edge _escape;
    AudioStreamPlayer? _music;
    /// <summary>The view's heading when the cinematic began: every stage is placed by it, and the body is held to it.</summary>
    float _yaw;
    /// <summary>The centre of the party's living players near the player, followed smoothly.</summary>
    Vector3 _party;

    /// <param name="flies">The player rides an aircraft (its camera and controls are not filmed).</param>
    public JobDoneClient(ClientContext ctx, JobDone done, JobDoneCinematicData data, Func<bool> flies) { _ctx = ctx; _done = done; _data = data; _flies = flies; }

    /// <summary>The camera is out from the body: the own figure is drawn, the weapon in the hand and the crosshair are hidden.</summary>
    public bool Active => _done.Camera;

    bool Live => _ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me) && _ctx.Me.IsInsideTree();

    /// <summary>
    /// Whether a Job done that starts now gets the camera and the lock: not with reduce motion, not dead, not a bot, and only on foot or seated in a road vehicle (an aircraft, a turret or a missile
    /// has its own view and its own controls). Without it only the banner and the music play.
    /// </summary>
    public bool CameraAllowed()
    {
        if (GameSettings.Current.ReduceMotion || _ctx.Dead || _ctx.Bot || !Live) return false;
        var mode = _ctx.View.Mode;
        return mode.OnFoot || (mode.Resolve() == ClientMode.Seated && !_flies());
    }

    /// <summary>Every frame: the skip keys and damage, the party's centre, the whoosh, the music, the flash and the crosshair.</summary>
    public void Frame(float dt)
    {
        if (Live) _done.ObserveHealth(_ctx.Me!.Health);
        if (_run.Update(_done.Starts)) Begin();
        if (_done.Camera)
        {
            CheckSkip();
            FollowParty(dt);
            if (_done.Frame is { } f && _leg.Update(f.Leg)) Sfx.Play2D(WhooshId);
        }
        else _leg.Reset();
        TuneMusic();
        _ctx.Hud?.SetFlash(GameSettings.Current.ReduceMotion ? 0f : _done.Flash);
        _ctx.Hud?.SetCinematicHidesCrosshair(_done.Camera);
    }

    /// <summary>A new cinematic: the heading to hold, the party's centre to start from, the music from its start.</summary>
    void Begin()
    {
        if (!_done.Playing) return;
        if (Live)
        {
            var me = _ctx.Me!;
            var forward = -me.Camera.GlobalTransform.Basis.Z;
            _yaw = new Vector2(forward.X, forward.Z).Length() > 0.1f ? Mathf.Atan2(-forward.X, -forward.Z) : me.Yaw; // the view's heading, or the body's when it looks straight up or down
            _party = me.GlobalPosition;
        }
        _leg.Reset();
        _escape.Reset(Input.IsKeyPressed(Key.Escape));
        _music ??= Sfx.OwnVoice(MusicId);
        if (_music == null) return;
        _music.VolumeDb = -80f;
        _music.Play();
    }

    /// <summary>Jump, fire, interact or escape pressed (the game keys only with the mouse captured), or dead: the camera ends at once.</summary>
    void CheckSkip()
    {
        var escape = _escape.Pressed(Input.IsKeyPressed(Key.Escape));
        var captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        var key = captured && (Input.IsActionJustPressed("jump") || Input.IsActionJustPressed("fire") || Input.IsActionJustPressed("interact"));
        if (escape || key || _ctx.Dead || !Live) _done.Interrupt();
    }

    void FollowParty(float dt)
    {
        if (!Live) return;
        var me = _ctx.Me!;
        _mates.Clear();
        foreach (var (id, r) in _ctx.Remotes)
            if (r.State != EntityState.Dead && _ctx.Match.AreSquadMates(_ctx.MyId, id) && GodotObject.IsInstanceValid(r.Node)) _mates.Add(new NVec3(r.Node.GlobalPosition.X, r.Node.GlobalPosition.Y, r.Node.GlobalPosition.Z));
        var c = CinematicRules.PartyCentre(new NVec3(me.GlobalPosition.X, me.GlobalPosition.Y, me.GlobalPosition.Z), _mates, _data.PartyRadiusM);
        _party = _party.Lerp(new Vector3(c.X, c.Y, c.Z), 1f - Mathf.Exp(-PartyFollow * dt));
    }

    /// <summary>The music's voice follows <see cref="JobDone.MusicGain"/>; stopped when the cinematic's music is over.</summary>
    void TuneMusic()
    {
        if (_music == null) return;
        if (!_done.MusicOn) { if (_music.Playing) _music.Stop(); return; }
        _music.VolumeDb = Sfx.BaseDb(MusicId) + Mathf.LinearToDb(Mathf.Max(_done.MusicGain, 1e-3f));
    }

    /// <summary>
    /// The inputs this tick while the controls are off: the body stands (no movement, no buttons: no fire, jump, use or throw), the heading is the one the cinematic began with (the mouse turns nothing), and a
    /// driver's input is the handbrake alone so the car stops; a passenger sends nothing.
    /// </summary>
    public void FilterInput(ref MoveInput mi)
    {
        if (!_done.InputLocked) return;
        mi.Yaw = _yaw;
        mi.MoveX = 0; mi.MoveY = 0;
        mi.Buttons = _ctx.Cars.MySeat() is { Seat: { Role: "driver" } } ? MoveButtons.Jump : MoveButtons.None;
    }

    /// <summary>The on-foot camera hook (<see cref="DeathView.Pulled"/>): the eye's transform replaced by the cinematic's, or null (the eye).</summary>
    public Transform3D? Camera(Transform3D eye) => _done.Camera && _ctx.View.Mode.OnFoot ? Pose(eye) : null;

    /// <summary>In a road vehicle the chase or seat camera has been placed already this frame: the cinematic's camera goes over it (the eye of the moves is where that camera is).</summary>
    public void ApplyInVehicle()
    {
        if (!_done.Camera || _ctx.View.Mode.OnFoot || !Live) return;
        var cam = _ctx.Me!.Camera;
        if (Pose(cam.GlobalTransform) is { } pose) cam.GlobalTransform = pose;
    }

    /// <summary>The camera between its two stages (either may be the eye), at the frame's move and hold.</summary>
    Transform3D? Pose(Transform3D eye)
    {
        if (_done.Frame is not { } f || !Live) return null;
        var me = _ctx.Me!;
        var from = f.From < 0 ? eye : Stage(me, f.From, 1f);
        var to = f.To < 0 ? eye : Stage(me, f.To, f.Hold);
        var turn = from.Basis.GetRotationQuaternion().Slerp(to.Basis.GetRotationQuaternion(), f.Move);
        return new Transform3D(new Basis(turn), from.Origin.Lerp(to.Origin, f.Move));
    }

    /// <summary>One stage's camera, <paramref name="hold"/> (0 to 1) through its drift: round its anchor by the view's heading, looking at it, kept clear of the world.</summary>
    Transform3D Stage(Player me, int index, float hold)
    {
        var s = _data.Stages[index];
        var anchor = s.PartyAnchored ? _party : me.GlobalPosition;
        var (eye, look) = s.Offsets(_yaw, hold);
        var target = anchor + new Vector3(look.X, look.Y, look.Z);
        var at = Clear(me, anchor + new Vector3(eye.X, eye.Y, eye.Z), target, s.PullsIn);
        var dir = target - at;
        return new Transform3D(Basis.LookingAt(dir.LengthSquared() > 1e-4f ? dir.Normalized() : Vector3.Down, Vector3.Up), at);
    }

    /// <summary>
    /// Keeps a stage's camera out of the world (rays on the world layer, as the chase camera): a low stage comes in along its line when a wall is between it and what it looks at; any stage is then put at least
    /// <see cref="JobDoneCinematicData.ClearanceM"/> above the highest surface over it (a roof, a hillside), never through it.
    /// </summary>
    Vector3 Clear(Player me, Vector3 at, Vector3 target, bool pullIn)
    {
        var space = me.GetWorld3D().DirectSpaceState;
        if (pullIn)
        {
            var line = PhysicsRayQueryParameters3D.Create(target, at);
            line.CollisionMask = CollisionLayers.World;
            var hit = space.IntersectRay(line);
            if (hit.Count > 0)
            {
                var room = Mathf.Min(target.DistanceTo(at), Mathf.Max(target.DistanceTo(hit["position"].AsVector3()) - WallM, MinPullM));
                at = target + (at - target).Normalized() * room;
            }
        }
        var over = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * ProbeUpM, at + Vector3.Down * 0.5f);
        over.CollisionMask = CollisionLayers.World;
        var top = space.IntersectRay(over);
        if (top.Count > 0) at.Y = Mathf.Max(at.Y, top["position"].AsVector3().Y + _data.ClearanceM);
        return at;
    }
}
