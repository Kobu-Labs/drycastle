using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// All persisted settings in one config file (user://shooter_settings.cfg; sections graphics, audio, controls,
/// gameplay, accessibility, connect, window). <see cref="Current"/> is the live, applied copy; the settings screen edits a
/// <see cref="Clone"/> and calls <see cref="Apply"/> for live preview, <see cref="Save"/> on Apply.
/// </summary>
public sealed class GameSettings
{
    public const string Path = "user://shooter_settings.cfg";
    public const float BaseSensitivity = 0.0025f;
    public static readonly string[] Buses = { "Master", "Music", "SFX", "UI" };

    // graphics
    public bool Fullscreen;
    public bool VSync = true;
    public int Msaa = 2; // index into MsaaNames: 0 off, 1 2x, 2 4x, 3 8x
    /// <summary>Display brightness: exposure multiplier on the day/night lighting (1 = as designed).</summary>
    public float Brightness = 1f;
    // audio (0..1 per bus, same order as Buses)
    public float[] Volume = { 0.8f, 0.7f, 0.8f, 0.8f };
    // gameplay
    public float Sensitivity = 1f;
    public bool InvertY;
    public float Fov = 90;
    public bool ShowCrosshair = true;
    // accessibility
    public float UiScale = 1f;
    public bool ReduceMotion;
    public bool ColorBlind;
    // controls
    public BindingMap Keys = InputBindings.NewMap();

    public static readonly string[] MsaaNames = { "Off", "2x", "4x", "8x" };

    static GameSettings? _cur;
    public static GameSettings Current => _cur ??= Load();
    /// <summary>Raised after <see cref="Apply"/>: the HUD, the player and the shell re-read the live settings.</summary>
    public static event Action? Changed;

    public GameSettings Clone()
    {
        var c = (GameSettings)MemberwiseClone();
        c.Volume = (float[])Volume.Clone();
        c.Keys = Keys.Clone();
        return c;
    }

    public bool SameAs(GameSettings o) =>
        Fullscreen == o.Fullscreen && VSync == o.VSync && Msaa == o.Msaa && Brightness == o.Brightness && Volume.SequenceEqual(o.Volume)
        && Sensitivity == o.Sensitivity && InvertY == o.InvertY && Fov == o.Fov && ShowCrosshair == o.ShowCrosshair
        && UiScale == o.UiScale && ReduceMotion == o.ReduceMotion && ColorBlind == o.ColorBlind && Keys.SameAs(o.Keys);

    public static GameSettings Load()
    {
        var s = new GameSettings();
        var cf = new ConfigFile();
        if (cf.Load(Path) == Error.Ok)
        {
            s.Fullscreen = (bool)cf.GetValue("graphics", "fullscreen", false);
            s.VSync = (bool)cf.GetValue("graphics", "vsync", true);
            s.Msaa = Math.Clamp((int)cf.GetValue("graphics", "msaa", 2), 0, MsaaNames.Length - 1);
            s.Brightness = SettingsRanges.ClampBrightness((float)(double)cf.GetValue("graphics", "brightness", 1.0));
            for (var i = 0; i < Buses.Length; i++)
                s.Volume[i] = SettingsRanges.ClampVolume((float)(double)cf.GetValue("audio", Buses[i].ToLowerInvariant(), (double)s.Volume[i]));
            s.Sensitivity = SettingsRanges.ClampSens((float)(double)cf.GetValue("gameplay", "sensitivity", 1.0));
            s.InvertY = (bool)cf.GetValue("gameplay", "invert_y", false);
            s.Fov = SettingsRanges.ClampFov((float)(double)cf.GetValue("gameplay", "fov", 90.0));
            s.ShowCrosshair = (bool)cf.GetValue("gameplay", "crosshair", true);
            s.UiScale = HudLogic.ClampUiScale((float)(double)cf.GetValue("accessibility", "ui_scale", 1.0));
            s.ReduceMotion = (bool)cf.GetValue("accessibility", "reduce_motion", false);
            s.ColorBlind = (bool)cf.GetValue("accessibility", "colour_blind", false);
            foreach (var a in s.Keys.Actions.ToList()) s.Keys.Load(a, (string)cf.GetValue("controls", a, ""));
        }
        return s;
    }

    public void Save()
    {
        var cf = new ConfigFile();
        cf.Load(Path); // keep other sections (recent servers, window)
        cf.SetValue("graphics", "fullscreen", Fullscreen);
        cf.SetValue("graphics", "vsync", VSync);
        cf.SetValue("graphics", "msaa", Msaa);
        cf.SetValue("graphics", "brightness", (double)SettingsRanges.ClampBrightness(Brightness));
        for (var i = 0; i < Buses.Length; i++) cf.SetValue("audio", Buses[i].ToLowerInvariant(), (double)Volume[i]);
        cf.SetValue("gameplay", "sensitivity", (double)SettingsRanges.ClampSens(Sensitivity));
        cf.SetValue("gameplay", "invert_y", InvertY);
        cf.SetValue("gameplay", "fov", (double)SettingsRanges.ClampFov(Fov));
        cf.SetValue("gameplay", "crosshair", ShowCrosshair);
        cf.SetValue("accessibility", "ui_scale", (double)HudLogic.ClampUiScale(UiScale));
        cf.SetValue("accessibility", "reduce_motion", ReduceMotion);
        cf.SetValue("accessibility", "colour_blind", ColorBlind);
        foreach (var a in Keys.Actions) cf.SetValue("controls", a, Keys.Get(a).ToString());
        cf.Save(Path);
    }

    /// <summary>Makes <paramref name="s"/> the live settings and applies every side effect (window, audio buses, input map, theme).</summary>
    public static void Apply(GameSettings s, bool notify = true)
    {
        s.UiScale = HudLogic.ClampUiScale(s.UiScale);
        s.Brightness = SettingsRanges.ClampBrightness(s.Brightness);
        s.Sensitivity = SettingsRanges.ClampSens(s.Sensitivity);
        s.Fov = SettingsRanges.ClampFov(s.Fov);
        _cur = s;
        InputBindings.Apply(s.Keys);
        ShooterTheme.SetColorBlind(s.ColorBlind);
        AudioBuses.Apply(s.Volume);
        if (DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetMode(s.Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetVsyncMode(s.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
            if (Engine.GetMainLoop() is SceneTree t)
                t.Root.Msaa3D = s.Msaa switch { 1 => Viewport.Msaa.Msaa2X, 2 => Viewport.Msaa.Msaa4X, 3 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Disabled };
        }
        if (notify) Changed?.Invoke();
    }

    // ---- window size and recent servers share the file but are not part of the Apply/Revert set ----

    public static void SaveWindowSize()
    {
        if (DisplayServer.GetName() == "headless" || DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed) return;
        var cf = new ConfigFile();
        cf.Load(Path);
        var sz = DisplayServer.WindowGetSize();
        cf.SetValue("window", "width", sz.X); cf.SetValue("window", "height", sz.Y);
        cf.Save(Path);
    }

    public static void RestoreWindowSize()
    {
        if (DisplayServer.GetName() == "headless") return;
        var cf = new ConfigFile();
        if (cf.Load(Path) != Error.Ok || !cf.HasSectionKey("window", "width")) return;
        var sz = new Vector2I((int)cf.GetValue("window", "width"), (int)cf.GetValue("window", "height"));
        if (sz.X >= 640 && sz.Y >= 360 && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed) DisplayServer.WindowSetSize(sz);
    }

    public static string LoadConnect(string key, string def)
    {
        var cf = new ConfigFile();
        return cf.Load(Path) == Error.Ok ? (string)cf.GetValue("connect", key, def) : def;
    }

    public static void SaveConnect(string key, string value)
    {
        var cf = new ConfigFile();
        cf.Load(Path);
        cf.SetValue("connect", key, value);
        cf.Save(Path);
    }

    /// <summary>Remembered UI state, such as the last-open loadout tab: a per-player convenience in the "ui" section.</summary>
    public static string LoadUi(string key, string def)
    {
        var cf = new ConfigFile();
        return cf.Load(Path) == Error.Ok ? (string)cf.GetValue("ui", key, def) : def;
    }

    public static void SaveUi(string key, string value)
    {
        var cf = new ConfigFile();
        cf.Load(Path);
        cf.SetValue("ui", key, value);
        cf.Save(Path);
    }

    /// <summary>The player token (ADR 0003): a GUID made on first use and kept in the connect section. It is the player's identity.</summary>
    public static string PlayerToken()
    {
        var t = LoadConnect("player_token", "");
        if (Guid.TryParse(t, out _)) return t;
        t = Guid.NewGuid().ToString();
        SaveConnect("player_token", t);
        return t;
    }
}

/// <summary>The one input map: action ids, defaults and the mapping onto Godot's InputMap (physical keys, so WASD follows position).</summary>
public static class InputBindings
{
    public static readonly (string Action, Binding Default)[] Defaults =
    {
        ("move_forward", Binding.Key((int)Key.W)), ("move_back", Binding.Key((int)Key.S)),
        ("move_left", Binding.Key((int)Key.A)), ("move_right", Binding.Key((int)Key.D)),
        ("jump", Binding.Key((int)Key.Space)), ("sprint", Binding.Key((int)Key.Shift)), ("crouch", Binding.Key((int)Key.Ctrl)), ("prone", Binding.Key((int)Key.Z)),
        ("fire", Binding.MouseButton((int)MouseButton.Left)),
        ("reload", Binding.Key((int)Key.R)), ("interact", Binding.Key((int)Key.E)), ("scoreboard", Binding.Key((int)Key.Tab)),
        ("slot_1", Binding.Key((int)Key.Key1)), ("slot_2", Binding.Key((int)Key.Key2)), ("slot_3", Binding.Key((int)Key.Key3)), ("slot_4", Binding.Key((int)Key.Key4)), ("slot_5", Binding.Key((int)Key.Key5)),
        ("zoom", Binding.MouseButton((int)MouseButton.Right)),
        ("headlights", Binding.Key((int)Key.H)),
    };

    public static BindingMap NewMap() => new(Defaults);

    public static string LabelKey(string action) => "bind." + action;

    /// <summary>Display name of a binding ("W", "Space", "Mouse 1").</summary>
    public static string Name(Binding b)
    {
        if (b.Mouse) return Strings.F("bind.mouse", b.Code);
        var k = DisplayServer.GetName() == "headless" ? (Key)b.Code : DisplayServer.KeyboardGetKeycodeFromPhysical((Key)b.Code);
        if (k == Key.None) k = (Key)b.Code;
        return OS.GetKeycodeString(k);
    }

    public static string Name(string action) => Name(GameSettings.Current.Keys.Get(action));

    public static void Apply(BindingMap map)
    {
        foreach (var a in map.Actions)
        {
            if (!InputMap.HasAction(a)) InputMap.AddAction(a);
            InputMap.ActionEraseEvents(a);
            var b = map.Get(a);
            InputEvent ev = b.Mouse ? new InputEventMouseButton { ButtonIndex = (MouseButton)b.Code } : new InputEventKey { PhysicalKeycode = (Key)b.Code };
            InputMap.ActionAddEvent(a, ev);
        }
    }

    /// <summary>The binding an event would set: key (physical) or mouse button; null for events that cannot be bound (Esc is reserved for Back/Pause).</summary>
    public static Binding? FromEvent(InputEvent e) => e switch
    {
        InputEventKey { Pressed: true, Echo: false } k when k.PhysicalKeycode != Key.Escape && k.PhysicalKeycode != Key.None => Binding.Key((int)k.PhysicalKeycode),
        InputEventMouseButton { Pressed: true } m when m.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle or MouseButton.Xbutton1 or MouseButton.Xbutton2 => Binding.MouseButton((int)m.ButtonIndex),
        _ => null,
    };
}

/// <summary>Audio buses (Master exists; Music, SFX and UI are created when missing) and their volume.</summary>
public static class AudioBuses
{
    public static void Apply(float[] volumes)
    {
        for (var i = 0; i < GameSettings.Buses.Length; i++)
        {
            var idx = AudioServer.GetBusIndex(GameSettings.Buses[i]);
            if (idx < 0)
            {
                AudioServer.AddBus();
                idx = AudioServer.BusCount - 1;
                AudioServer.SetBusName(idx, GameSettings.Buses[i]);
                AudioServer.SetBusSend(idx, "Master");
            }
            AudioServer.SetBusVolumeDb(idx, volumes[i] <= 0.001f ? -80f : Mathf.LinearToDb(volumes[i]));
        }
    }
}
