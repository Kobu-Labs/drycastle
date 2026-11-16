namespace Drycastle.ShooterCore;

/// <summary>Score line data for the top-centre readout: own kills against the leader.</summary>
public readonly record struct ScoreLine(int MyKills, string LeaderName, int LeaderKills, bool IAmLeader);

/// <summary>
/// Godot-free HUD decisions (unit tested): kill feed window, score vs leader, scoreboard order, time text,
/// crosshair gap, damage indicator angle, ui scale. The Godot HUD only maps these results onto pre-built nodes.
/// </summary>
public static class HudLogic
{
    public const int FeedMax = 5;
    public const double FeedLife = 6.0;
    public const float UiScaleMin = 0.75f, UiScaleMax = 1.5f;

    /// <summary>
    /// Window into an oldest-first list of kill times: the newest <paramref name="max"/> entries that are younger than
    /// <paramref name="life"/> seconds. Entries expire in order, so the result is one contiguous range.
    /// </summary>
    public static (int Start, int Count) FeedWindow(IReadOnlyList<double> times, double now, int max = FeedMax, double life = FeedLife)
    {
        var start = Math.Max(0, times.Count - max);
        while (start < times.Count && now - times[start] >= life) start++;
        return (start, times.Count - start);
    }

    public static ScoreLine Score(IReadOnlyList<ScoreRow> rows, ushort myId)
    {
        if (rows.Count == 0) return new ScoreLine(0, "", 0, true);
        var mine = 0;
        foreach (var r in rows) if (r.Id == myId) mine = r.Kills;
        // leader = best other player when I am not strictly ahead; ties go to me (shown as leading)
        ScoreRow? best = null;
        foreach (var r in rows)
            if (r.Id != myId && (best is null || r.Kills > best.Value.Kills)) best = r;
        if (best is null || mine >= best.Value.Kills) return new ScoreLine(mine, "", mine, true);
        return new ScoreLine(mine, best.Value.Name, best.Value.Kills, false);
    }

    /// <summary>Scoreboard order: kills desc, deaths asc, id asc.</summary>
    public static List<ScoreRow> Sort(IReadOnlyList<ScoreRow> rows)
    {
        var l = new List<ScoreRow>(rows);
        l.Sort((a, b) => a.Kills != b.Kills ? b.Kills.CompareTo(a.Kills) : a.Deaths != b.Deaths ? a.Deaths.CompareTo(b.Deaths) : a.Id.CompareTo(b.Id));
        return l;
    }

    /// <summary>
    /// Team deathmatch score line data: (own team's score, other team's score, own team). A player without a team sees team A first.
    /// </summary>
    public static (int Mine, int Theirs, byte MyTeam) TeamScores(byte myTeam, int scoreA, int scoreB) =>
        myTeam == Teams.B ? (scoreB, scoreA, Teams.B) : (scoreA, scoreB, Teams.A);

    /// <summary>
    /// Team deathmatch scoreboard: one table per team, each in <see cref="Sort"/> order. The local player's team is the left
    /// table (team A for a player without a team), so the sides do not swap when the lead changes. Players without a team are apart.
    /// </summary>
    public static (byte LeftTeam, List<ScoreRow> Left, List<ScoreRow> Right, List<ScoreRow> NoTeam) SplitTeams(IReadOnlyList<ScoreRow> rows, byte myTeam)
    {
        var left = myTeam == Teams.B ? Teams.B : Teams.A;
        var right = Teams.Other(left);
        var l = Sort(rows);
        return (left, l.FindAll(r => r.Team == left), l.FindAll(r => r.Team == right), l.FindAll(r => r.Team != left && r.Team != right));
    }

    /// <summary>
    /// Whether a name in the kill feed or scoreboard is a friend, an enemy or neutral (free-for-all, no team) to the local player.
    /// The local player counts as a friend. Never the only signal: the HUD adds the team tag.
    /// </summary>
    public enum Relation { Neutral, Friend, Enemy }

    public static Relation RelationOf(byte myTeam, byte theirTeam) =>
        myTeam == Teams.None || theirTeam == Teams.None ? Relation.Neutral : myTeam == theirTeam ? Relation.Friend : Relation.Enemy;

    /// <summary>"m:ss", rounded up so the display reaches 0:00 only when time is really out.</summary>
    public static string TimeText(float seconds)
    {
        var s = (int)Math.Ceiling(Math.Max(0, seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Whole seconds shown for a countdown (ceil); used to update a label only when the number changes.</summary>
    public static int CeilSeconds(double seconds) => (int)Math.Ceiling(Math.Max(0, seconds));

    /// <summary>Translation key of the stance badge text (the icon shows the same stance by silhouette, so it is not colour-only).</summary>
    public static string StanceKey(Stance stance) => stance switch
    {
        Stance.Crouch => "hud.stance.crouch",
        Stance.Prone => "hud.stance.prone",
        _ => "hud.stance.stand",
    };

    /// <summary>
    /// Crosshair arm offset from the screen centre in pixels for a spread half-angle (degrees), vertical fov (degrees)
    /// and viewport height in pixels: the on-screen radius of the cone, never below <paramref name="minGap"/>.
    /// </summary>
    public static float CrosshairGap(float spreadDeg, float fovDeg, float viewportHeight, float minGap)
    {
        var px = MathF.Tan(spreadDeg * MathF.PI / 180f) / MathF.Tan(fovDeg * MathF.PI / 360f) * (viewportHeight / 2f);
        return Math.Max(minGap, px);
    }

    /// <summary>
    /// Map rule (#313): a vehicle with an enemy on any seat is not drawn on the minimap or tactical map. The seats are the ids the server says
    /// (ADR 0026): the driver, and <paramref name="others"/> = seats 1..3 (0 = empty). The player's own vehicle is never hidden (they sit in it).
    /// Mixed riders (friend + enemy) hide it. FFA: <paramref name="isEnemy"/> is true for everyone else.
    /// </summary>
    public static bool HideEnemyOccupiedVehicle(ushort driver, IReadOnlyList<ushort>? others, ushort myId, Func<ushort, bool> isEnemy)
    {
        var enemy = false;
        if (driver == myId) return false;
        if (driver != 0 && isEnemy(driver)) enemy = true;
        if (others != null)
            foreach (var id in others)
            {
                if (id == 0) continue;
                if (id == myId) return false;
                if (isEnemy(id)) enemy = true;
            }
        return enemy;
    }

    /// <summary>
    /// Screen angle (radians, 0 = up, clockwise positive) of a damage source for a viewer at (vx, vz) looking along
    /// <paramref name="yaw"/> (the game's convention: forward = (-sin yaw, -cos yaw)). Source in front = 0, right = +pi/2.
    /// </summary>
    public static float DamageAngle(float vx, float vz, float yaw, float sx, float sz)
    {
        var dx = sx - vx; var dz = sz - vz;
        if (dx * dx + dz * dz < 1e-6f) return 0f;
        var fx = -MathF.Sin(yaw); var fz = -MathF.Cos(yaw);
        // right vector = forward rotated clockwise seen from above: (-fz, fx) in this x/z frame
        var rx = -fz; var rz = fx;
        var a = MathF.Atan2(dx * rx + dz * rz, dx * fx + dz * fz);
        return a;
    }

    /// <summary>Reference height of the UI (1080p) and the smallest resolution factor; below it the HUD stops shrinking.</summary>
    public const float RefHeight = 1080f, MinBaseScale = 0.8f;

    /// <summary>The one UI scale: the screen height over 1080 p (never under <see cref="MinBaseScale"/>) times the UI scale setting. The HUD and every overlay use it.</summary>
    public static float ScreenScale(float viewportHeight, float uiScale) => Math.Max(MinBaseScale, viewportHeight / RefHeight) * ClampUiScale(uiScale);

    public static float ClampUiScale(float s) => Math.Clamp(float.IsNaN(s) ? 1f : s, UiScaleMin, UiScaleMax);

    /// <summary>Health bar turns to the low state at or under this many health points.</summary>
    public const int LowHealth = 25;
}

/// <summary>
/// Client-side estimate of the weapon spread for the crosshair: base spread plus per-shot growth up to the cap,
/// recovering linearly (SpreadRecovery seconds for the full cap-to-base range) after the last shot.
/// </summary>
public sealed class SpreadEstimator
{
    double _extra, _at;

    public void Reset() { _extra = 0; _at = 0; }

    public void Shot(WeaponDef w, double now)
    {
        _extra = Math.Min(Extra(w, now) + w.SpreadPerShot, Math.Max(0, w.SpreadMax - w.SpreadBase));
        _at = now;
    }

    double Extra(WeaponDef w, double now)
    {
        var range = Math.Max(0, w.SpreadMax - w.SpreadBase);
        if (w.SpreadRecovery <= 0 || range <= 0) return 0;
        return Math.Max(0, _extra - (now - _at) * range / w.SpreadRecovery);
    }

    public float Degrees(WeaponDef w, double now) => w.SpreadBase + (float)Extra(w, now);
}
