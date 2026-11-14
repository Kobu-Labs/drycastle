using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's body on the client, all of it procedural (no pack clip crawls, throws or twitches): a Synty model (<c>demon_01</c>, the peasants' skeleton) driven
/// by the server's entity. The turning (<see cref="DemonState.Transforming"/>): the priest model burns where the pastor stood (flames, a flickering light, it
/// swells and shakes and chars), is swapped at <see cref="DemonFightDef.TransformSwapS"/> for the demon standing upright, which plays the roar and drops on all fours
/// in the last <see cref="DemonFightDef.TransformDropS"/>. From then on <see cref="DemonPose"/> carries the crawl, oriented by the surface normal (<see cref="Place"/>
/// turns the body smoothly), with a gait driven by the distance it travels. The head follows the nearest player on its own, clamped, with sudden twitches and now and
/// then a slow over-rotation and a tilt; waiting, it roars (the pack's roar clip over the crawl), rears and tilts its head. A fireball is a wind-up of the right arm with a
/// glowing orb in the hand, then a fling (<see cref="Fling"/>). The server's roar (<see cref="DemonState.Roaring"/>) stands it up on the surface for the pack's roar
/// clip, with shock rings and a flare. A leap (<see cref="DemonState.Crouching"/>, <see cref="DemonState.Leaping"/>) crouches, stretches out in flight and squashes on landing.
/// Fire from the mouth (<see cref="DemonState.Breathing"/>) rears, then a particle stream and a light from the head bone follow <see cref="FlameAt"/>.
/// Dead, it lets go of the surface, falls, lies and burns out. The views obey <see cref="ChurchInterior"/>.
/// </summary>
public sealed partial class DemonView : Node3D
{
    const string Roar = "emote_aggressive_roar_low_masc", Idle = "idle_base_masc", PriestIdle = "idle_standing_masc";
    const float SpeedAtFullGait = 4.5f;

    readonly DemonFile _data;
    readonly MassConfig _mass;
    readonly Random _rng = new();

    Node3D? _priest, _demon;
    AnimationPlayer? _demonAnim, _priestAnim;
    DemonPose? _pose;
    Skeleton3D? _skel;
    Node3D? _orb;
    OmniLight3D? _orbLight, _fireLight, _eyeLight, _flameLight;
    CpuParticles3D? _flames, _stream;
    Node3D? _mouth;
    LoopVoice? _flameVoice;
    readonly List<StandardMaterial3D> _priestMats = new(), _demonMats = new();

    DemonState _state = (DemonState)255;
    double _t, _stateT;
    bool _swapped;
    float _crawl, _crawlTo, _crawlSpeed = 1f;
    float _limp;

    // roar, leap, flame
    /// <summary>Where the flame goes (the target's chest), set every frame by the client while it breathes; null falls back to <see cref="LookAtWorld"/>.</summary>
    public Vector3? FlameAt;
    /// <summary>The roar began at this point: the client shakes the camera.</summary>
    public Action<Vector3>? RoarCue;
    float _crouch, _stretch, _land, _fireK;
    double _nextRing;
    Vector3 _flameDir = Vector3.Forward;
    bool _hasFlameDir;

    // movement
    Vector3 _lastPos;
    bool _hasLast;
    float _speed, _gait;
    Quaternion _rot = Quaternion.Identity;
    bool _hasRot;
    Vector3 _up = Vector3.Up;

    // head and emotes
    public Vector3? LookAtWorld;
    float _yaw, _pitch, _roll, _twitchYaw, _twitchPitch;
    double _nextTwitch, _twitchEnd, _nextEmote, _emoteStart, _emoteLen, _overStart = -1, _nextOver = 8;
    int _emote; // 0 none, 1 roar, 2 arch, 3 tilt
    float _overSign = 1;

    // the throw
    double _flingAt = -10;
    float _wind;

    // hurt
    double _hurtAt = -10;

    // death
    bool _detached, _landed;
    Vector3 _vel, _spin;
    public float FloorY;
    double _deadAt;

    DemonView(DemonFile data, MassConfig mass) { _data = data; _mass = mass; }

    /// <summary>True once it let go of the surface (dead): the caller stops placing it.</summary>
    public bool Detached => _detached;

    public static DemonView Make(DemonFile data, MassConfig mass)
    {
        var v = new DemonView(data, mass) { Name = "Demon" };
        v.Build();
        return v;
    }

    void Build()
    {
        Visible = ChurchInterior.Visible;
        ChurchInterior.Changed += OnInterior;
        _priest = SyntyLib.Instance(_mass.PastorModel, _mass.Atlas);
        _demon = SyntyLib.Instance("demon_01", "glb/atlas/horror_mansion_01_A.png");
        if (_priest != null) { Prepare(_priest, _priestMats, out _priestAnim, PriestIdle, false); _priest.Visible = false; }
        if (_demon != null)
        {
            Prepare(_demon, _demonMats, out _demonAnim, Idle, true);
            _demon.Visible = false;
            _skel = _demon.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
            if (_skel != null)
            {
                _pose = new DemonPose { Name = "Crawl", Influence = 0 };
                _skel.AddChild(_pose);
                _orb = new Node3D { Visible = false };
                _orb.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.16f, Height = 0.32f, Material = OrbMaterial() }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                _orbLight = new OmniLight3D { LightColor = new Color(1f, 0.5f, 0.15f), LightEnergy = 2.5f, OmniRange = 5f, ShadowEnabled = false };
                _orb.AddChild(_orbLight);
                var hand = new BoneAttachment3D { BoneName = "Hand_R", Name = "HandR" };
                _skel.AddChild(hand);
                hand.AddChild(_orb);
                var head = new BoneAttachment3D { BoneName = "Head", Name = "HeadMark" };
                _skel.AddChild(head);
                _mouth = new Node3D { Name = "Mouth" };
                head.AddChild(_mouth);
            }
        }
        if (ShotFx.Enabled)
        {
            _fireLight = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.15f), LightEnergy = 0, OmniRange = 12f, ShadowEnabled = false, Position = new Vector3(0, 1f, 0) };
            _eyeLight = new OmniLight3D { LightColor = new Color(1f, 0.15f, 0.05f), LightEnergy = 0, OmniRange = 6f, ShadowEnabled = false, Position = new Vector3(0, 0.6f, 0) };
            _flameLight = new OmniLight3D { LightColor = new Color(1f, 0.5f, 0.12f), LightEnergy = 0, OmniRange = 11f, ShadowEnabled = false };
            AddChild(_fireLight); AddChild(_eyeLight); AddChild(_flameLight);
        }
        _stream = DemonFx.Stream(this, _data.Flame.RangeM > 0 ? _data.Flame.RangeM : 10f);
        _flames = DemonFx.Flames(this, 70, 1.2f, new Vector3(0, 0.9f, 0));
        _nextTwitch = 1; _nextEmote = 3;
    }

    static StandardMaterial3D OrbMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.6f, 0.2f), EmissionEnabled = true, Emission = new Color(1f, 0.5f, 0.1f), EmissionEnergyMultiplier = 3f,
    };

    /// <summary>Puts a model under this node (turned to face -Z), gives each mesh a material of its own (it flashes and chars), and its clips.</summary>
    void Prepare(Node3D model, List<StandardMaterial3D> mats, out AnimationPlayer? anim, string idle, bool demon)
    {
        model.RotationDegrees = new Vector3(0, 180, 0); // the pack faces +Z, the view's forward is -Z
        AddChild(model);
        foreach (var m in model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            if (m.MaterialOverride is StandardMaterial3D sm && sm.Duplicate() is StandardMaterial3D own) { m.MaterialOverride = own; mats.Add(own); }
        var lib = new AnimationLibrary();
        foreach (var name in new[] { idle, Roar }.Distinct())
            if (SyntyLib.Clip(name) is { } clip) lib.AddAnimation(name, clip);
        anim = new AnimationPlayer { Name = "Clips" };
        model.AddChild(anim); // root node "..": the tracks are Skeleton3D:Bone under the model
        anim.AddAnimationLibrary("", lib);
        if (anim.HasAnimation(idle)) anim.Play(idle);
    }

    public override void _ExitTree()
    {
        ChurchInterior.Changed -= OnInterior;
        _flameVoice?.Release();
        _flameVoice = null;
    }

    void OnInterior(bool visible) => Visible = visible;

    // ---- what the server says ----

    /// <summary>Shows the state the entity is in. A joiner finds it mid-fight: the demon stands on the surface at once.</summary>
    public void Show(DemonState state)
    {
        if (state == _state) return;
        var first = _state == (DemonState)255;
        var prev = _state;
        _state = state;
        _stateT = 0;
        if (prev == DemonState.Leaping) _land = 1; // it lands: a squash
        if (state == DemonState.Roaring && !first) BeginRoar();
        else if (prev == DemonState.Roaring && state != DemonState.Dead && _demonAnim != null && _demonAnim.HasAnimation(Idle)) _demonAnim.Play(Idle, 0.3);
        if (state == DemonState.Breathing) _hasFlameDir = false;
        Audio.SetBackground(state != DemonState.Dead);
        _crawlSpeed = state == DemonState.Roaring || prev == DemonState.Roaring ? 3f : 1f / Math.Max(0.1f, _data.Fight.TransformDropS);
        switch (state)
        {
            case DemonState.Transforming: _swapped = false; BeginBurn(); break;
            case DemonState.Dead: BeginDeath(); break;
            default:
                if (!_swapped) Swap(false);
                _crawlTo = state == DemonState.Roaring ? 0 : 1; // the roar stands it up on the surface
                if (first) _crawl = _crawlTo;
                if (_flames != null && !_detached) ParticleFx.Run(_flames, state == DemonState.Roaring);
                break;
        }
    }

    /// <summary>The aggressive roar: the pack's clip, the sound, a flare and a camera shake; the rings follow in <see cref="_Process"/>.</summary>
    void BeginRoar()
    {
        _nextRing = 0.25;
        if (_demonAnim != null && _demonAnim.HasAnimation(Roar)) { _demonAnim.Play(Roar, 0.2); _demonAnim.Queue(Idle); }
        if (!Audio.Scream() && Sfx.Enabled) Sfx.Play3D("demon.roar", GlobalPosition + _up * 1.4f);
        RoarCue?.Invoke(GlobalPosition);
    }

    /// <summary>A hit that counted: the body jerks and flashes.</summary>
    public void Hurt() => _hurtAt = _t;

    /// <summary>The fireball left the hand: the arm flings.</summary>
    public void Fling() => _flingAt = _t;

    // ---- placement ----

    /// <summary>Puts the body at <paramref name="pos"/> on a surface with this normal, crawling along <paramref name="forward"/>. The body turns smoothly; the gait follows the distance moved.</summary>
    public void Place(Vector3 pos, Vector3 normal, Vector3 forward, double dt)
    {
        if (_detached) return;
        var up = normal.LengthSquared() > 1e-4f ? normal.Normalized() : Vector3.Up;
        var f = forward - up * forward.Dot(up);
        if (f.LengthSquared() < 1e-4f) f = up.Cross(MathF.Abs(up.X) > 0.9f ? Vector3.Forward : Vector3.Right);
        var q = Basis.LookingAt(f.Normalized(), up).GetRotationQuaternion();
        if (!_hasRot) { _rot = q; _hasRot = true; }
        else
        {
            if (_rot.Dot(q) < 0) q = -q;
            _rot = _rot.Slerp(q, 1f - MathF.Exp(-(_state == DemonState.Leaping ? 20f : 9f) * (float)dt)).Normalized();
        }
        _up = new Basis(_rot) * Vector3.Up;
        GlobalTransform = new Transform3D(new Basis(_rot), pos);
        if (_hasLast && dt > 1e-4)
        {
            var moved = pos.DistanceTo(_lastPos);
            if (moved < 3f) { _speed = Mathf.Lerp(_speed, moved / (float)dt, 1f - MathF.Exp(-8f * (float)dt)); _gait += moved / DemonPose.CycleM; }
        }
        _lastPos = pos; _hasLast = true;
    }

    // ---- the turning ----

    void BeginBurn()
    {
        if (_priest != null) { _priest.Visible = true; _priestAnim?.Play(PriestIdle); }
        if (_demon != null) _demon.Visible = false;
        _crawl = 0; _crawlTo = 0;
        if (_flames != null) ParticleFx.Run(_flames, true);
        Audio.Transform();
    }

    DemonAudio? _audio;
    DemonAudio Audio => _audio ??= DemonAudio.Attach(this);

    /// <summary>The priest is gone and the demon stands in his place, roaring.</summary>
    void Swap(bool burst)
    {
        _swapped = true;
        if (_priest != null) _priest.Visible = false;
        if (_demon != null) _demon.Visible = true;
        if (burst)
        {
            if (IsInsideTree()) RocketFx.Explode(GetParent<Node3D>(), GlobalPosition + Vector3.Up, 2.5f);
            if (_fireLight != null) _fireLight.LightEnergy = 14;
            if (_demonAnim != null && _demonAnim.HasAnimation(Roar)) { _demonAnim.Play(Roar, 0.05); _demonAnim.Queue(Idle); }
        }
    }

    void UpdateTransform(double dt)
    {
        var fight = _data.Fight;
        if (_stateT < fight.TransformSwapS)
        {
            var k = (float)(_stateT / fight.TransformSwapS);
            if (_priest != null)
            {
                _priest.Scale = Vector3.One * (1f + 0.4f * k * k);
                _priest.Position = new Vector3(((float)_rng.NextDouble() - 0.5f) * 0.06f, 0, ((float)_rng.NextDouble() - 0.5f) * 0.06f) * (0.3f + k * 2f);
                var burn = k * k;
                foreach (var m in _priestMats)
                {
                    m.AlbedoColor = new Color(1, 1, 1).Lerp(new Color(0.12f, 0.05f, 0.03f), burn);
                    m.EmissionEnabled = true; m.Emission = new Color(1f, 0.35f, 0.05f); m.EmissionEnergyMultiplier = burn * 3f * (0.7f + 0.3f * DemonFx.Flicker(_t, 1, 0, 1));
                }
            }
            if (_fireLight != null) _fireLight.LightEnergy = DemonFx.Flicker(_t, 3, 1.5f, 3f + 6f * k);
        }
        else
        {
            if (!_swapped) Swap(true);
            if (_priest != null) _priest.Position = Vector3.Zero;
            if (_fireLight != null) _fireLight.LightEnergy = Mathf.MoveToward(_fireLight.LightEnergy, DemonFx.Flicker(_t, 3, 0.5f, 2f), (float)dt * 14f);
            var dropAt = fight.TransformS - fight.TransformDropS;
            _crawlTo = _stateT >= dropAt ? 1 : 0;
            if (_stateT >= dropAt && _stateT < dropAt + dt * 2 && _flames != null) ParticleFx.Run(_flames, false);
        }
    }

    // ---- death ----

    void BeginDeath()
    {
        if (!_swapped) Swap(false);
        _detached = true; _deadAt = _t; _landed = false;
        _crawlTo = 1; _crawl = 1;
        // it lets go: a push off the surface, a tumble
        _vel = _up * 1.5f + new Vector3(((float)_rng.NextDouble() - 0.5f) * 2f, 0.5f, ((float)_rng.NextDouble() - 0.5f) * 2f);
        _spin = new Vector3(((float)_rng.NextDouble() - 0.5f) * 5f, ((float)_rng.NextDouble() - 0.5f) * 3f, ((float)_rng.NextDouble() - 0.5f) * 5f);
        if (_flames != null) ParticleFx.Run(_flames, true);
        _demonAnim?.Play(Idle, 0.2);
    }

    void UpdateDead(double dt)
    {
        var age = (float)(_t - _deadAt);
        _limp = Mathf.MoveToward(_limp, 1f, (float)dt * 2.5f);
        if (!_landed)
        {
            _vel += new Vector3(0, -9.8f * (float)dt, 0);
            var p = GlobalPosition + _vel * (float)dt;
            var b = GlobalTransform.Basis;
            var spin = new Quaternion(_spin.Normalized(), _spin.Length() * (float)dt);
            b = new Basis(spin) * b;
            if (p.Y <= FloorY + 0.05f && _vel.Y < 0) { p.Y = FloorY + 0.05f; _landed = true; _vel = Vector3.Zero; }
            GlobalTransform = new Transform3D(b.Orthonormalized(), p);
        }
        else
        {
            // lies down: the surface it lies on is the floor
            var cur = GlobalTransform.Basis.GetRotationQuaternion();
            var ahead = -GlobalTransform.Basis.Z; ahead.Y = 0;
            var flat = Basis.LookingAt(ahead.LengthSquared() > 0.01f ? ahead.Normalized() : Vector3.Forward, Vector3.Up).GetRotationQuaternion();
            GlobalTransform = new Transform3D(new Basis(cur.Slerp(flat, 1f - MathF.Exp(-6f * (float)dt))), GlobalPosition);
        }
        // burns out over the corpse time
        var life = _data.Fight.CorpseS;
        var burn = Mathf.Clamp(age / life, 0f, 1f);
        foreach (var m in _demonMats)
        {
            m.AlbedoColor = new Color(1, 1, 1).Lerp(new Color(0.15f, 0.08f, 0.06f), Mathf.Min(1f, burn * 3f));
            m.EmissionEnabled = true; m.Emission = new Color(1f, 0.3f, 0.05f);
            m.EmissionEnergyMultiplier = (1f - burn) * 2.5f * (0.7f + 0.3f * DemonFx.Flicker(_t, 5, 0, 1));
        }
        if (_fireLight != null) _fireLight.LightEnergy = (1f - burn) * DemonFx.Flicker(_t, 3, 1.5f, 4f);
        if (_eyeLight != null) _eyeLight.LightEnergy = Mathf.MoveToward(_eyeLight.LightEnergy, 0, (float)dt * 3f);
        if (_flames != null && burn > 0.75f) ParticleFx.Run(_flames, false);
    }

    // ---- every frame ----

    public override void _Process(double delta)
    {
        _t += delta; _stateT += delta;
        var dt = (float)delta;
        if (_state == DemonState.Transforming) UpdateTransform(delta);
        else if (_state == DemonState.Dead) UpdateDead(delta);
        else
        {
            var roaring = _state == DemonState.Roaring;
            if (_eyeLight != null) _eyeLight.LightEnergy = roaring ? DemonFx.Flicker(_t, 9, 3f, 7f) : DemonFx.Flicker(_t, 9, 0.5f, 1.4f);
            if (_fireLight != null) _fireLight.LightEnergy = roaring ? DemonFx.Flicker(_t, 3, 1.5f, 4f) : Mathf.MoveToward(_fireLight.LightEnergy, 0, dt * 8f);
            if (roaring && _stateT >= _nextRing && _mouth != null && IsInsideTree())
            {
                _nextRing += 0.8;
                DemonFx.RoarRing(GetParent<Node3D>(), _mouth.GlobalPosition, _up);
            }
        }
        UpdateFlame(dt);
        _crawl = Mathf.MoveToward(_crawl, _crawlTo, dt * _crawlSpeed);
        if (_pose == null) return;
        _pose.Influence = _crawl * _crawl * (3 - 2 * _crawl);

        // gait
        var move = Mathf.Clamp(_speed / SpeedAtFullGait, 0f, 1f);
        _pose.Gait = _gait; _pose.Move = move;
        _pose.Limp = _limp;

        // the leap: crouch on the anchor, stretch in the air, squash on landing
        var crouchTo = _state == DemonState.Crouching ? 1f : 0f;
        _crouch = Mathf.MoveToward(_crouch, crouchTo, dt * (crouchTo > _crouch ? 1f / Math.Max(0.15f, _data.Leap.CrouchS * 0.7f) : 8f));
        _land = Mathf.MoveToward(_land, 0f, dt * 2.2f);
        _stretch = Mathf.MoveToward(_stretch, _state == DemonState.Leaping ? 1f : 0f, dt * (_state == DemonState.Leaping ? 8f : 3f));
        _pose.Crouch = Math.Max(_crouch, 0.9f * _land);
        _pose.Stretch = _stretch;

        // the flame: it rears back through the windup
        var windup = Math.Max(0.2f, _data.Flame.WindupS);
        _fireK = Mathf.MoveToward(_fireK, _state == DemonState.Breathing ? 1f : 0f, dt * (_state == DemonState.Breathing ? 1f / windup : 4f));

        // the throw: the right arm winds up for the telegraph and flings when the ball leaves
        var tele = _data.Fireball.TelegraphS;
        var windTo = _state == DemonState.Telegraphing ? Mathf.Clamp((float)(_stateT / tele), 0f, 1f) : 0f;
        var sinceFling = _t - _flingAt;
        _wind = Mathf.MoveToward(_wind, sinceFling < 0.12 ? 0f : windTo, dt * (sinceFling < 0.12 ? 12f : 3f));
        var fling = sinceFling < 0.12 ? (float)(sinceFling / 0.12) : sinceFling < 0.55 ? 1f - (float)((sinceFling - 0.12) / 0.43) : 0f;
        _pose.Wind = _wind; _pose.Fling = Mathf.Clamp(fling, 0f, 1f);
        if (_orb != null)
        {
            var show = _wind > 0.05f && sinceFling > 0.1;
            _orb.Visible = show;
            if (show) { _orb.Scale = Vector3.One * (0.3f + 0.9f * _wind); if (_orbLight != null) _orbLight.LightEnergy = DemonFx.Flicker(_t, 2, 1.5f, 3.5f) * _wind; }
        }

        // hurt: a flinch and a flash
        var hurt = Mathf.Clamp(1f - (float)((_t - _hurtAt) / 0.3), 0f, 1f);
        if (hurt > 0 && _state != DemonState.Dead)
            foreach (var m in _demonMats) { m.EmissionEnabled = true; m.Emission = new Color(1f, 0.25f, 0.1f); m.EmissionEnergyMultiplier = hurt * 3f; }
        else if (_state != DemonState.Dead && _state != DemonState.Transforming)
            foreach (var m in _demonMats) m.EmissionEnergyMultiplier = 0;

        Emotes(dt, hurt);
        Head(dt);
    }

    /// <summary>The stream of fire from the head bone: once the windup is over it runs along the line to <see cref="FlameAt"/>, with a light and a roaring loop.</summary>
    void UpdateFlame(float dt)
    {
        var on = _state == DemonState.Breathing && _stateT >= _data.Flame.WindupS && _mouth != null && IsInsideTree();
        if (_flameLight != null) _flameLight.LightEnergy = Mathf.MoveToward(_flameLight.LightEnergy, on ? DemonFx.Flicker(_t, 6, 3f, 7f) : 0f, dt * (on ? 40f : 14f));
        if (!on)
        {
            if (_stream != null && _stream.Emitting) ParticleFx.Run(_stream, false);
            if (_flameVoice != null) { _flameVoice.Release(); _flameVoice = null; }
            return;
        }
        var origin = _mouth!.GlobalPosition;
        var target = FlameAt ?? LookAtWorld ?? origin - GlobalTransform.Basis.Z;
        var want = target - origin;
        want = want.LengthSquared() > 1e-4f ? want.Normalized() : -GlobalTransform.Basis.Z;
        if (!_hasFlameDir) { _flameDir = want; _hasFlameDir = true; }
        else _flameDir = _flameDir.Slerp(want, 1f - MathF.Exp(-5f * dt)).Normalized();
        if (_stream != null)
        {
            var up = MathF.Abs(_flameDir.Y) > 0.97f ? Vector3.Forward : Vector3.Up;
            _stream.GlobalTransform = new Transform3D(Basis.LookingAt(_flameDir, up), origin + _flameDir * 0.25f);
            ParticleFx.Run(_stream, true);
        }
        if (_flameLight != null) _flameLight.GlobalPosition = origin + _flameDir * 2.5f;
        if (Sfx.Enabled)
        {
            _flameVoice ??= Sfx.Loop("demon.flame", origin);
            _flameVoice?.Update(origin + _flameDir * 2f);
        }
    }

    void Emotes(float dt, float hurt)
    {
        var waiting = _state is DemonState.Waiting or DemonState.Dwelling && _crawl > 0.9f;
        var rear = 0f; var tilt = 0f;
        if (waiting && _t >= _nextEmote && _emote == 0)
        {
            _emote = 2 + _rng.Next(2); // the roar is the server's now (Roaring)
            _emoteStart = _t; _emoteLen = _emote == 2 ? 1.6 : 2.5;
        }
        if (_emote != 0)
        {
            var u = (float)((_t - _emoteStart) / _emoteLen);
            if (u >= 1 || !waiting) { _emote = 0; _nextEmote = _t + 3 + _rng.NextDouble() * 6; }
            else
            {
                var env = MathF.Sin(u * MathF.PI);
                if (_emote == 2) rear = 0.9f * env;
                else tilt = 0.55f * env;
            }
        }
        // rearing for the throw too: it sits back on its legs
        rear = Math.Max(rear, 0.35f * _wind);
        rear = Math.Max(rear, 0.7f * _fireK);
        if (_pose == null) return;
        _pose.Rear = rear;
        _pose.Breath = 0.03f * MathF.Sin((float)_t * 2.1f) + 0.35f * hurt;
        _roll = Mathf.Lerp(_roll, tilt + (_overRoll), 1f - MathF.Exp(-6f * dt));
    }

    float _overRoll;

    /// <summary>The head: tracks the nearest player within a clamp, twitches, and now and then turns too far, slowly.</summary>
    void Head(float dt)
    {
        if (_pose == null) return;
        float wantYaw, wantPitch;
        if (LookAtWorld is { } p && _state != DemonState.Dead)
        {
            var local = GlobalTransform.AffineInverse() * p - new Vector3(0, 0.55f, 0.45f);
            var sk = new Vector3(-local.X, local.Y, -local.Z); // the model is turned half a circle
            wantYaw = MathF.Atan2(sk.X, sk.Z);
            wantPitch = MathF.Asin(Mathf.Clamp(sk.Y / Math.Max(sk.Length(), 0.01f), -1f, 1f));
        }
        else { wantYaw = MathF.Sin((float)_t * 0.35f) * 0.7f; wantPitch = -0.1f; }

        // slow over-rotation: now and then it keeps turning past where a head goes, with a tilt, then comes back
        var over = 0f;
        if (_t >= _nextOver && _overStart < 0) { _overStart = _t; _overSign = wantYaw >= 0 ? 1 : -1; }
        if (_overStart >= 0)
        {
            var u = (float)((_t - _overStart) / 5.0);
            if (u >= 1) { _overStart = -1; _nextOver = _t + 10 + _rng.NextDouble() * 15; }
            else over = MathF.Sin(u * MathF.PI);
        }
        _overRoll = 0.45f * over * _overSign;

        // twitches: the head snaps off to the side for a moment
        if (_t >= _nextTwitch)
        {
            _twitchYaw = ((float)_rng.NextDouble() - 0.5f) * 1.1f; _twitchPitch = ((float)_rng.NextDouble() - 0.5f) * 0.6f;
            _twitchEnd = _t + 0.12 + _rng.NextDouble() * 0.3;
            _nextTwitch = _twitchEnd + 1.5 + _rng.NextDouble() * 4;
        }
        var twitching = _t < _twitchEnd;
        var limit = Mathf.Lerp(1.3f, 2.4f, over);
        var yawT = Mathf.Clamp(wantYaw, -limit, limit) + _overSign * over * 0.5f + (twitching ? _twitchYaw : 0);
        var pitchT = Mathf.Clamp(wantPitch, -0.75f, 0.55f) + (twitching ? _twitchPitch : 0);
        if (_state == DemonState.Dead) { yawT = 0.4f; pitchT = -0.7f; }
        var rate = twitching ? 40f : 4f; // radians per second: a twitch is a snap, tracking is a slow turn
        _yaw = Mathf.MoveToward(_yaw, yawT, rate * dt);
        _pitch = Mathf.MoveToward(_pitch, pitchT, rate * dt);
        _pose.LookYaw = _yaw; _pose.LookPitch = _pitch; _pose.HeadRoll = _roll;
    }
}
