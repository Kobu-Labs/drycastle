using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// How a Gang member is drawn (client only; thieves.json <c>gang.view</c>, docs/events/thieves/08). With the private Gang Warfare pack: a model of <see cref="Models"/> by kind and look number, one of the
/// twelve <see cref="Atlases"/>, the gang-rig clips of <see cref="Clips"/> (the name has <c>{g}</c> where the pack splits a clip into masc and femn: see <see cref="GangLook.ClipName"/>), the blows, the climb,
/// the carry overlay and what it wears. Without the pack a coloured capsule per kind. Every name and offset is data, so a pose that looks wrong in the game is nudged here, not in code.
/// </summary>
public sealed class GangView
{
    public GangModels Models { get; set; } = new();
    /// <summary>The atlases under assets-private/synty (the 12 the pack ships): one per member, a plain uniform pick by the look number, so every client agrees.</summary>
    public List<string> Atlases { get; set; } = Enumerable.Range(1, 4).SelectMany(set => "ABC".Select(tone => $"gang-warfare/textures/PolygonGangWarfare_0{set}_{tone}.png")).ToList();
    public GangClips Clips { get; set; } = new();
    public GangPlayback Playback { get; set; } = new();
    public GangBlows Blows { get; set; } = new();
    public GangClimbView Climb { get; set; } = new();
    public GangGestures Gestures { get; set; } = new();
    public GangDress Dress { get; set; } = new();
    public GangCarry Carry { get; set; } = new();
    /// <summary>The capsule fallback: colour (html) and height in metres of a scale 1 member, per kind.</summary>
    public string ThiefColor { get; set; } = "#7d6b4f";
    public string EnforcerColor { get; set; } = "#8a3b32";
    public string GangBossColor { get; set; } = "#c9a227";
    public float CapsuleHeightM { get; set; } = 1.8f;

    public void Validate(string file)
    {
        void Need(bool ok, string what) { if (!ok) throw new DataFileException($"{file}: gang.view.{what}"); }
        Need(Models.Thief.Count > 0 && Models.Enforcer.Count > 0 && Models.GangBoss.Count > 0, "models lists at least one model per kind");
        Need(Atlases.Count > 0, "atlases lists at least one atlas");
        Need(Clips.Idle != "" && Clips.Walk != "" && Clips.Run != "", "clips.idle, walk and run are named");
        Need(Playback.BlendS >= 0 && Playback.StealEnterSeconds > 0 && Playback.LandSeconds > 0 && Playback.HopSpeed > 0, "playback: blend_s is not negative, the rest positive");
        foreach (var blow in new[] { Blows.Thief, Blows.Enforcer, Blows.GangBoss }.SelectMany(b => b))
            Need(blow.Clip != "" && blow.PeakS > blow.SeekS && blow.SeekS >= 0, "blows: a clip, peak_s after seek_s");
        Need(Climb.ClipSpeedMps > 0 && Climb.SurfaceM >= Climb.WallPlaneM && Climb.WallPlaneM >= 0 && Climb.SlideSeconds >= 0, "climb: clip_speed_mps positive, surface_m not under wall_plane_m");
        Need(Gestures.MinGapSeconds > 0 && Gestures.MaxGapSeconds >= Gestures.MinGapSeconds, "gestures: min_gap_seconds positive, max_gap_seconds not below it");
        Need(Dress.Cigarette.OneIn >= 1 && Dress.Speaker.Scale > 0 && Dress.Chain.Scale > 0, "dress: cigarette.one_in at least 1, scales positive");
        Need(Dress.Pipe.Position.Length == 3 && Dress.Pipe.Rotation.Length == 4 && Dress.Pipe.ModelRotationDeg.Length == 3 && Dress.Pipe.ModelOffset.Length == 3, "dress.pipe: position 3 numbers, rotation 4, model_rotation_deg 3, model_offset 3");
        foreach (var o in new[] { Dress.Cigarette.Offset, Dress.Cigarette.RotationDeg, Dress.Speaker.Offset, Dress.Speaker.RotationDeg, Dress.Chain.Offset, Dress.Chain.RotationDeg })
            Need(o.Length == 3, "dress: every offset and rotation_deg is three numbers");
        Carry.Validate(file);
    }
}

/// <summary>The models under assets-private/synty/glb by kind (picked by look number), and which of them are the feminine body (the pack splits clips masc and femn).</summary>
public sealed class GangModels
{
    public List<string> Thief { get; set; } = new() { "gang_thief_01", "gang_thief_02" };
    public List<string> Enforcer { get; set; } = new() { "gang_enforcer_01" };
    public List<string> GangBoss { get; set; } = new() { "gang_boss_01" };
    public List<string> Femn { get; set; } = new() { "gang_thief_02" };

    public List<string> Of(GangMemberKind kind) => kind switch { GangMemberKind.Enforcer => Enforcer, GangMemberKind.GangBoss => GangBoss, _ => Thief };
}

/// <summary>
/// Gang-rig clip names (anim/baked) by what a member does. <c>{g}</c> stands for <c>masc</c> or <c>femn</c> by the model (the data names the femn models). An empty name plays nothing (the
/// body is held in the pose it had, or leans in code for a blow).
/// </summary>
public sealed class GangClips
{
    public string Idle { get; set; } = "idle_standing_{g}_g";
    public string Walk { get; set; } = "walk_f_{g}_g";
    public string Run { get; set; } = "run_f_{g}";
    /// <summary>Fleeing and hauling Loot.</summary>
    public string Sprint { get; set; } = "sprint_f_{g}";
    /// <summary>The Thief kneeling at a target (Steal): the clip that goes down, then the one that loops. There is no clip that gets up (the pack's is 7.5 s): the next state blends out of the loop.</summary>
    public string Steal { get; set; } = "kneel_enter_{g}";
    public string StealLoop { get; set; } = "kneel_loop_{g}";
    /// <summary>The climbs (tickets 06 and 07): loops, played at a speed that matches <c>loot.climb_speed</c> (<see cref="GangClimbView.ClipSpeedMps"/>).</summary>
    public string ClimbUp { get; set; } = "climb_up";
    public string ClimbDown { get; set; } = "climb_down";
    public string ClimbWork { get; set; } = "climb_work";
    /// <summary>The electrocution (ticket 07): falling, lying knocked down, getting up, and stunned at the top.</summary>
    public string KnockdownBegin { get; set; } = "knockdown_begin";
    public string KnockdownLoop { get; set; } = "knockdown_loop";
    public string KnockdownEnd { get; set; } = "knockdown_end";
    public string StunLoop { get; set; } = "stun_loop";
    /// <summary>The Thief hopping over a pet's fence (HopIn and HopOut, ticket 05) and the landing after it.</summary>
    public string HopIn { get; set; } = "jump_running_{g}";
    public string HopOut { get; set; } = "jump_running_{g}";
    public string Land { get; set; } = "land_soft_{g}";
    /// <summary>One of these by the look number (front, back, left).</summary>
    public List<string> Death { get; set; } = new() { "death_front_sword", "death_back_sword_g", "death_left_sword_g" };
}

/// <summary>Clip timing that is not in the clips themselves.</summary>
public sealed class GangPlayback
{
    /// <summary>The crossfade between two clips, seconds (it is also how a kneel is left: blended, not played out).</summary>
    public float BlendS { get; set; } = 0.2f;
    /// <summary>The kneel's way down is sped up to take this long (the pack's takes 3.2 s; picking Loot up takes a second).</summary>
    public float StealEnterSeconds { get; set; } = 1f;
    /// <summary>The jump: where in jump_running the play starts (seconds), how fast it plays (the hop itself is <c>loot.pet.hop_seconds</c> long). The landing is a pseudo-state: land_soft for land_seconds after a hop.</summary>
    public float HopSeekS { get; set; } = 0.5f;
    public float HopSpeed { get; set; } = 1.5f;
    public float LandSeconds { get; set; } = 0.5f;
    /// <summary>Getting up after the electrocution: knockdown_end holds the body lying for about 2.3 s, then rises. The clip is played from here, sped up so the rise takes <c>loot.connector.get_up_seconds</c>.</summary>
    public float GetUpSeekS { get; set; } = 2.3f;
}

/// <summary>One blow clip: from where it plays (<see cref="SeekS"/>: before it the pose is held or only a wind-in) to the moment the blow lands (<see cref="PeakS"/>), both seconds into the clip.</summary>
public sealed class GangBlow
{
    public string Clip { get; set; } = "";
    public float SeekS { get; set; }
    public float PeakS { get; set; }
}

/// <summary>
/// What a member plays for Windup and Swing, by kind (one blow of the list by the look number and the count of blows, so they vary). The server's blow lands at the start of Swing, <c>windup_seconds</c> after Windup began:
/// the view starts the clip so its <see cref="GangBlow.PeakS"/> falls there (<see cref="BlowPlan"/>). An empty list: the body leans back and then forward, as the capsule does.
/// </summary>
public sealed class GangBlows
{
    public List<GangBlow> Thief { get; set; } = new();
    public List<GangBlow> Enforcer { get; set; } = new();
    public List<GangBlow> GangBoss { get; set; } = new();

    public List<GangBlow> Of(GangMemberKind kind) => kind switch { GangMemberKind.Enforcer => Enforcer, GangMemberKind.GangBoss => GangBoss, _ => Thief };
}

/// <summary>
/// Climbing (the clips are ours: hands and feet alternate on a vertical line, wall plane at <see cref="WallPlaneM"/> in front of the model's origin, one loop = <see cref="ClipSpeedMps"/> times its length
/// of travel). The server's member stands <see cref="SurfaceM"/> from the surface it climbs (the dish's wall, the pole's column: <c>loot.dish.base_out_m</c>, <c>loot.connector.base_offset_m</c>), so the body is
/// slid forward by the difference while it hangs there.
/// </summary>
public sealed class GangClimbView
{
    public float ClipSpeedMps { get; set; } = 0.489f;
    public float WallPlaneM { get; set; } = 0.27f;
    public float SurfaceM { get; set; } = 0.9f;
    /// <summary>How long the body takes to slide to the wall and back.</summary>
    public float SlideSeconds { get; set; } = 0.25f;
}

/// <summary>An idle Gang boss (and an Enforcer on guard) now and then plays a gesture: a taunt, or, for a member who smokes, a drag. Client only, random by entity id and time, never in a fight.</summary>
public sealed class GangGestures
{
    public List<string> Taunts { get; set; } = new() { "taunt_laugh_point_{g}", "taunt_shake_fist_{g}", "taunt_beckon_{g}", "taunt_menacing_fists_{g}" };
    /// <summary>For a member with a cigarette.</summary>
    public string Drink { get; set; } = "drink_sip_{g}";
    /// <summary>The wait between two gestures of one member, a random time between these, seconds.</summary>
    public float MinGapSeconds { get; set; } = 8;
    public float MaxGapSeconds { get; set; } = 25;
}

/// <summary>What a member wears. Offsets are metres in the rest pose's axes (the model faces +Z, up is +Y, its right hand is -X) from the bone's origin, so a bone's own roll does not matter; the pipe's grip alone is in its hand bone's frame.</summary>
public sealed class GangDress
{
    public GangPipe Pipe { get; set; } = new();
    public GangCigarette Cigarette { get; set; } = new();
    public GangSpeakerMount Speaker { get; set; } = new();
    public GangChain Chain { get; set; } = new();
}

/// <summary>The Enforcer's pipe. <see cref="Position"/> and <see cref="Rotation"/> (x, y, z, w) are the grip in the bone's frame (Synty weapons: blade along +Y, grip at the origin); the model is turned and shifted to put its handle there.</summary>
public sealed class GangPipe
{
    public string Model { get; set; } = "props/pipe_01";
    public string Atlas { get; set; } = "gang-warfare/textures/PolygonGangWarfare_01_A.png";
    public string Bone { get; set; } = "Hand_R";
    public float[] Position { get; set; } = { -0.0879f, 0.0391f, 0.0163f };
    public float[] Rotation { get; set; } = { -0.4785f, -0.3531f, 0.3529f, 0.7224f };
    /// <summary>pipe_01 spans y -0.566 to 0.335 with the handle at the low end: turned 180 degrees about Z it points the other way, then shifted along its Y.</summary>
    public float[] ModelRotationDeg { get; set; } = { 0, 0, 180 };
    public float[] ModelOffset { get; set; } = { 0, 0.215f, 0 };
}

/// <summary>A lit cigarette at the mouth of one member in <see cref="OneIn"/> (by the look number). The model is 0.147 m long along +Z, the filter at the origin.</summary>
public sealed class GangCigarette
{
    public string Model { get; set; } = "props/cigarette_lit_01";
    public string Atlas { get; set; } = "gang-warfare/textures/PolygonGangWarfare_01_A.png";
    public string Bone { get; set; } = "Head";
    public float[] Offset { get; set; } = { -0.025f, 0.07f, 0.1f };
    public float[] RotationDeg { get; set; } = { 12, 0, 0 };
    public int OneIn { get; set; } = 3;
}

/// <summary>The speaker on a member's hip (docs 09): a props model scaled down, in place of the box <c>ThiefAudio</c> draws without the pack.</summary>
public sealed class GangSpeakerMount
{
    public string Model { get; set; } = "props/speaker_01";
    public string Atlas { get; set; } = "glb/atlas/military_01_A.png";
    public string Bone { get; set; } = "Hips";
    public float[] Offset { get; set; } = { -0.25f, 0, 0 };
    public float[] RotationDeg { get; set; } = { 0, 0, 0 };
    /// <summary>speaker_01 is 0.48 m tall: 0.42 makes it about 0.2 m.</summary>
    public float Scale { get; set; } = 0.42f;
}

/// <summary>The Gang boss's gold chain: the model of the Item (items.json) at the neck bone. Its model is authored facing -Z, so it is turned round.</summary>
public sealed class GangChain
{
    public string Bone { get; set; } = "Neck";
    public float[] Offset { get; set; } = { 0, 0, 0 };
    public float[] RotationDeg { get; set; } = { 0, 180, 0 };
    public float Scale { get; set; } = 1f;
}

/// <summary>
/// The carry overlay (docs 08): while a member carries Loot, <see cref="Poses"/> of the kind's <see cref="CarryKind.Pose"/> are blended over the clip it plays (the arms only), and the kind's prop is held at
/// <see cref="CarryKind.PropAt"/>. Keys of <see cref="Kinds"/> are the Loot kinds, lower case.
/// </summary>
public sealed class GangCarry
{
    /// <summary>How fast the overlay fades in and out, seconds.</summary>
    public float BlendS { get; set; } = 0.2f;
    /// <summary>The chest bone the hands and the prop ride on (the targets follow its motion).</summary>
    public string ChestBone { get; set; } = "Spine_03";
    public Dictionary<string, CarryPoseData> Poses { get; set; } = new();
    public Dictionary<string, CarryKind> Kinds { get; set; } = new();

    public void Validate(string file)
    {
        foreach (var (name, k) in Kinds)
        {
            if (!Poses.ContainsKey(k.Pose)) throw new DataFileException($"{file}: gang.view.carry.kinds.{name}.pose names a pose of carry.poses");
            if (k.PropAt.Length != 3) throw new DataFileException($"{file}: gang.view.carry.kinds.{name}.prop_at is three numbers");
        }
        foreach (var (name, p) in Poses)
            foreach (var v in new[] { p.Right, p.Left, p.PetOffset })
                if (v != null && v.Length != 3) throw new DataFileException($"{file}: gang.view.carry.poses.{name}: right, left and pet_offset are three numbers");
    }
}

/// <summary>
/// One arm pose. <see cref="Right"/> and <see cref="Left"/> (the member's own sides) are where the hand goes, in the rest pose's axes; null leaves that arm to the clip. With <see cref="PetOffset"/> the right hand
/// goes to where the server holds a pet (<c>loot.pet.carry_*</c>) plus this offset (right, up, forward in the member's frame). The elbow bends <see cref="ElbowDown"/> down and <see cref="ElbowOut"/> outward
/// (weights), and the hand turns <see cref="HandRollDeg"/> about the forearm (right hand; the left one the opposite way).
/// </summary>
public sealed class CarryPoseData
{
    public float[]? Right { get; set; }
    public float[]? Left { get; set; }
    public float[]? PetOffset { get; set; }
    public float ElbowDown { get; set; } = 1f;
    public float ElbowOut { get; set; } = 0.4f;
    public float HandRollDeg { get; set; }
}

/// <summary>A kind of Loot a member carries: the pose of its arms and where its prop is held (rest-pose axes, metres, centre of the prop). Model and atlas replace the primitive prop where the kind has a pack model.</summary>
public sealed class CarryKind
{
    public string Pose { get; set; } = "";
    public float[] PropAt { get; set; } = { 0, 1, 0.3f };
    public string Model { get; set; } = "";
    public string Atlas { get; set; } = "";
}
