namespace Drycastle.ShooterCore;

/// <summary>
/// plantation.json: every number of the marijuana plantation (GLOSSARY "Plantation", "High"): how long the interact key is held inside one, how long the high lasts, the nausea of the
/// player's own camera and the looks everyone else sees (the cigarette, its ember, light and smoke, the eyes). The defaults here are the shipped values, so a data directory without
/// the file still plays. Parsed and checked once; a bad field throws with the file and the field named. Nothing in it was seen in the game: the looks are first guesses.
/// </summary>
public sealed class PlantationFile : IVersioned
{
    public int Version { get; set; } = 1;
    /// <summary>Seconds the interact key is held inside a plantation (as the only thing in reach of the key) to get high.</summary>
    public double HoldSeconds { get; set; } = 1.5;
    /// <summary>Seconds a high lasts; a new completed hold starts them over.</summary>
    public double HighSeconds { get; set; } = 120;
    public NauseaData Nausea { get; set; } = new();
    public HighViewData View { get; set; } = new();
    public ReflectorData Reflectors { get; set; } = new();

    public static PlantationFile Parse(string json) => DataFile.Parse<PlantationFile>(json, "plantation.json").Validate();

    public static PlantationFile Load(string path) => DataFile.Load<PlantationFile>(path).Validate();

    /// <summary>The shipped numbers (the defaults of every field), checked.</summary>
    public static PlantationFile Default => new PlantationFile().Validate();

    PlantationFile Validate()
    {
        static void Pos(double v, string field)
        {
            if (!double.IsFinite(v) || v <= 0) throw new DataFileException($"plantation.json: {field} must be more than 0");
        }
        static void NonNeg(double v, string field)
        {
            if (!double.IsFinite(v) || v < 0) throw new DataFileException($"plantation.json: {field} must be 0 or more");
        }
        static void Waves(List<SwayWave> waves, string field)
        {
            foreach (var w in waves)
            {
                if (!double.IsFinite(w.Deg) || w.Deg < 0) throw new DataFileException($"plantation.json: {field}.deg must be 0 or more");
                Pos(w.PeriodS, $"{field}.period_s");
            }
        }
        static void Unit(double v, string field)
        {
            if (!(v is >= 0 and <= 1)) throw new DataFileException($"plantation.json: {field} must be between 0 and 1");
        }

        Pos(HoldSeconds, "hold_seconds"); Pos(HighSeconds, "high_seconds");
        var rf = Reflectors;
        if (rf.Head.Length != 3 || rf.Lens.Length != 2) throw new DataFileException("plantation.json: reflectors.head needs 3 numbers and lens 2");
        Pos(rf.RangeM, "reflectors.range_m"); Pos(rf.AngleDeg, "reflectors.angle_deg"); NonNeg(rf.Energy, "reflectors.energy"); NonNeg(rf.LensEnergy, "reflectors.lens_energy");
        Pos(rf.CullM, "reflectors.cull_m"); Pos(rf.Lens[0], "reflectors.lens"); Pos(rf.Lens[1], "reflectors.lens");
        var n = Nausea;
        Waves(n.Yaw, "nausea.yaw"); Waves(n.Pitch, "nausea.pitch"); Waves(n.Roll, "nausea.roll"); Waves(n.Fov, "nausea.fov");
        Pos(n.FadeS, "nausea.fade_s"); NonNeg(n.LagS, "nausea.lag_s"); Unit(n.ReducedScale, "nausea.reduced_scale");
        var v = View;
        var c = v.Cigarette;
        Pos(c.LengthM, "view.cigarette.length_m"); Pos(c.Scale, "view.cigarette.scale"); Pos(c.OutM, "view.cigarette.out_m");
        if (c.Mouth.Length != 2 || c.RotationDeg.Length != 3) throw new DataFileException("plantation.json: view.cigarette.mouth needs 2 numbers and rotation_deg 3");
        Pos(c.EmberRadiusM, "view.cigarette.ember_radius_m"); Pos(c.EmberEnergy, "view.cigarette.ember_energy");
        Pos(c.Light.Range, "view.cigarette.light.range"); NonNeg(c.Light.Energy, "view.cigarette.light.energy"); Pos(c.Light.CullM, "view.cigarette.light.cull_m");
        var s = c.Smoke;
        if (s.Amount < 1) throw new DataFileException("plantation.json: view.cigarette.smoke.amount must be at least 1");
        Pos(s.LifetimeS, "view.cigarette.smoke.lifetime_s"); Pos(s.SizeM, "view.cigarette.smoke.size_m"); NonNeg(s.SpeedMps, "view.cigarette.smoke.speed_mps"); NonNeg(s.RiseMps2, "view.cigarette.smoke.rise_mps2");
        Pos(s.CullM, "view.cigarette.smoke.cull_m");
        var fp = c.FirstPerson;
        if (fp.Offset.Length != 3 || fp.RotationDeg.Length != 3) throw new DataFileException("plantation.json: view.cigarette.first_person.offset and rotation_deg need 3 numbers");
        if (fp.SmokeAmount < 1) throw new DataFileException("plantation.json: view.cigarette.first_person.smoke_amount must be at least 1");
        Pos(fp.SmokeSizeM, "view.cigarette.first_person.smoke_size_m");
        var e = v.Eyes;
        Pos(e.Width, "view.eyes.width"); Pos(e.Height, "view.eyes.height"); Unit(e.PupilWidth, "view.eyes.pupil_width"); Unit(e.PupilHeight, "view.eyes.pupil_height");
        Pos(e.Spread, "view.eyes.spread");
        return this;
    }
}

/// <summary>One sine of the nausea's sway: its size in degrees and its period in seconds.</summary>
public sealed class SwayWave
{
    public double Deg { get; set; }
    public double PeriodS { get; set; } = 1;
}

/// <summary>
/// plantation.json "nausea": the sway of the high player's own first-person camera (<see cref="Nausea"/>). Each axis is the sum of a few sines; yaw and pitch move the real look angles
/// (the ones the client sends, so shots go where the swayed crosshair points), roll and FOV are only drawn. <see cref="LagS"/> is how slowly the view follows the sway, <see cref="FadeS"/>
/// how long it takes to come on and to go. <see cref="ReducedScale"/> is the share of all of it left with the reduce-motion setting.
/// </summary>
public sealed class NauseaData
{
    public double FadeS { get; set; } = 4;
    public double LagS { get; set; } = 0.35;
    public double ReducedScale { get; set; } = 0.15;
    public List<SwayWave> Yaw { get; set; } = new() { new() { Deg = 9.0, PeriodS = 7.3 }, new() { Deg = 3.5, PeriodS = 3.1 } };
    public List<SwayWave> Pitch { get; set; } = new() { new() { Deg = 6.0, PeriodS = 5.9 }, new() { Deg = 2.5, PeriodS = 2.3 } };
    public List<SwayWave> Roll { get; set; } = new() { new() { Deg = 14.0, PeriodS = 9.1 }, new() { Deg = 5.0, PeriodS = 3.7 } };
    public List<SwayWave> Fov { get; set; } = new() { new() { Deg = 16.0, PeriodS = 6.4 }, new() { Deg = 6.0, PeriodS = 2.7 } };
}

/// <summary>plantation.json "view": what every client draws on a high player's figure.</summary>
public sealed class HighViewData
{
    public CigaretteData Cigarette { get; set; } = new();
    public HighEyesData Eyes { get; set; } = new();
}

/// <summary>
/// The lit cigarette in the mouth. <see cref="Mouth"/> is where the filter end sits on the face plate, as shares of the plate's width and height from its middle (x to the left of the face is
/// negative, y up) and <see cref="OutM"/> metres in front of it. The model faces +Z with the filter end near its origin (<see cref="LengthM"/> long); <see cref="RotationDeg"/> tilts it. Without the paid
/// pack a thin white stick of the same length stands in.
/// </summary>
public sealed class CigaretteData
{
    public string Model { get; set; } = "props/cigarette_long_01";
    public string Atlas { get; set; } = "gang-warfare/textures/PolygonGangWarfare_01_A.png";
    public double[] Mouth { get; set; } = { 0.1, -0.3 };
    public double OutM { get; set; } = 0.015;
    public double[] RotationDeg { get; set; } = { 6, 0, 0 };
    public double Scale { get; set; } = 1;
    /// <summary>Length of the model from its origin to the tip, metres: where the ember, the light and the smoke are.</summary>
    public double LengthM { get; set; } = 0.186;
    public string EmberColour { get; set; } = "#ff6a1a";
    public double EmberRadiusM { get; set; } = 0.011;
    /// <summary>Emission energy of the ember: above the bloom threshold of 2 on purpose (ADR 0030).</summary>
    public double EmberEnergy { get; set; } = 6;
    public CigaretteLightData Light { get; set; } = new();
    public CigaretteSmokeData Smoke { get; set; } = new();
    public CigaretteFirstPersonData FirstPerson { get; set; } = new();
}

/// <summary>
/// The high player's own cigarette in the first-person view: a copy of the model fixed to the camera where the mouth is. <see cref="Offset"/> is the filter end in the camera's frame, metres (x right, y up,
/// z back: forward is negative); <see cref="RotationDeg"/> tilts it from pointing straight ahead (x negative: tip down; y: tip to the left). Its smoke is thinner than the figure's so it does not hide the aim.
/// </summary>
public sealed class CigaretteFirstPersonData
{
    public double[] Offset { get; set; } = { 0.03, -0.11, -0.05 };
    public double[] RotationDeg { get; set; } = { -10, 6, 0 };
    public int SmokeAmount { get; set; } = 28;
    public double SmokeSizeM { get; set; } = 0.018;
}

/// <summary>The small warm shadowless light at the tip that lights the wearer's face; only while the figure is within <see cref="CullM"/> of the camera.</summary>
public sealed class CigaretteLightData
{
    public string Colour { get; set; } = "#ff9a4d";
    public double Energy { get; set; } = 0.7;
    public double Range { get; set; } = 1.6;
    public double CullM { get; set; } = 30;
}

/// <summary>The smoke trail from the tip (a world-space particle emitter, so it hangs behind a walking player); only within <see cref="CullM"/> of the camera.</summary>
public sealed class CigaretteSmokeData
{
    public int Amount { get; set; } = 60;
    public double LifetimeS { get; set; } = 2.6;
    /// <summary>Radius of a puff when it is born, metres; it swells as it thins out.</summary>
    public double SizeM { get; set; } = 0.045;
    public double SpeedMps { get; set; } = 0.3;
    public double RiseMps2 { get; set; } = 0.3;
    public double CullM { get; set; } = 80;
}

/// <summary>
/// The eyes of a high player: two rectangles on the face plate, replacing the normal dark boxes. <see cref="Width"/> and <see cref="Height"/> are shares of the plate's width and height,
/// <see cref="Spread"/> how far from the middle each sits (a share of the width), <see cref="PupilWidth"/> and <see cref="PupilHeight"/> the dark pupil's share of the eye. White with a red tint.
/// </summary>
public sealed class HighEyesData
{
    public double Width { get; set; } = 0.2;
    public double Height { get; set; } = 0.34;
    public double Spread { get; set; } = 0.22;
    public double Lift { get; set; } = 0.08;
    public string Colour { get; set; } = "#f5b5ac";
    public string PupilColour { get; set; } = "#0a0a0c";
    public double PupilWidth { get; set; } = 0.38;
    public double PupilHeight { get; set; } = 0.34;
}

/// <summary>
/// The reflector stands of a plantation: every placed object of <see cref="Asset"/> shines a real spot light (the kind a vehicle's headlamp is: no shadow) from its lamp bar, on by itself at night
/// (the street lamps' curve). <see cref="Head"/> is the lamp bar in the model's own frame, metres (x right, y up, z ahead: the model shines along +Z); <see cref="PitchDeg"/> tips the beam (negative: down).
/// <see cref="Lens"/> is the width and height of the lit bar drawn in front of the lamps, its <see cref="LensEnergy"/> above the bloom threshold of 2 on purpose (ADR 0030). The light runs only
/// within <see cref="CullM"/> of the camera.
/// </summary>
public sealed class ReflectorData
{
    public string Asset { get; set; } = "pack:synty-gang-warfare/SM_Prop_Light_Portable_01";
    public double[] Head { get; set; } = { 0, 1.99, 0.07 };
    public double PitchDeg { get; set; } = -24;
    public double RangeM { get; set; } = 18;
    public double AngleDeg { get; set; } = 40;
    public double Energy { get; set; } = 5;
    public string Colour { get; set; } = "#fff1d2";
    public double[] Lens { get; set; } = { 0.54, 0.11 };
    public double LensEnergy { get; set; } = 7;
    public double CullM { get; set; } = 150;
}
