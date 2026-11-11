using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Killstreak tracker in the HUD (ticket #119): one row per loadout streak slot with its name, kills still to go or its
/// state (ready with the activation key, active, used), and a progress bar. Rows have fixed sizes, so values never move
/// the layout. <see cref="RefreshStreaks"/> runs on the tracker's events, not per frame, and writes only what changed.
/// </summary>
public partial class Hud
{
    readonly Label[] _streakName = new Label[KillstreakService.Slots], _streakState = new Label[KillstreakService.Slots];
    readonly ProgressBar[] _streakBar = new ProgressBar[KillstreakService.Slots];
    readonly string[] _streakVariation = new string[KillstreakService.Slots];

    void BuildStreaks()
    {
        var p = Place(P("HudPanel"), 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32 + 224 + ShooterTheme.Space, 304, 232);
        _root.AddChild(p);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        p.AddChild(v);
        v.AddChild(L(Strings.T("hud.streak.title"), "HudSmall"));
        for (var i = 0; i < KillstreakService.Slots; i++)
        {
            _streakName[i] = L("", "HudHead"); _streakName[i].ClipText = true;
            v.AddChild(_streakName[i]);
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", ShooterTheme.Space);
            v.AddChild(row);
            _streakBar[i] = new ProgressBar { MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(96, 14), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ThemeTypeVariation = "ReloadBar", MouseFilter = Control.MouseFilterEnum.Ignore };
            _streakState[i] = L("", "HudSmall", minW: 150); _streakState[i].ClipText = true;
            row.AddChild(_streakBar[i]); row.AddChild(_streakState[i]);
        }
        RefreshStreaks(null, false);
    }

    /// <summary>
    /// Shows the tracker's three slots. <paramref name="alive"/> false drops the key hint from a ready streak (the server
    /// refuses use while dead). A null tracker (no killstreak data) shows empty slots.
    /// </summary>
    public void RefreshStreaks(KillstreakTracker? tracker, bool alive)
    {
        for (var i = 0; i < KillstreakService.Slots; i++)
        {
            var view = tracker?.Slot(i) ?? new StreakSlotView("", "", StreakSlotState.Empty, 0, 0);
            var empty = view.State == StreakSlotState.Empty;
            Set(_streakName[i], empty ? Strings.T("hud.streak.none") : StreakName(view));
            var bar = 0f;
            string text, variation;
            switch (view.State)
            {
                case StreakSlotState.Progress:
                    bar = view.KillsRequired > 0 ? (float)view.KillsDone / view.KillsRequired : 0f;
                    text = Strings.F("hud.streak.need", view.KillsRequired - view.KillsDone);
                    variation = "HudDim";
                    break;
                case StreakSlotState.Ready:
                    bar = 1f;
                    text = alive ? Strings.F("hud.streak.ready", InputBindings.Name("killstreak")) : Strings.T("hud.streak.ready.dead");
                    variation = "HudAccent";
                    break;
                case StreakSlotState.Active:
                    bar = 1f;
                    text = Strings.T("hud.streak.active");
                    variation = "HudAccent";
                    break;
                case StreakSlotState.Spent:
                    text = Strings.T("hud.streak.spent");
                    variation = "HudDim";
                    break;
                default:
                    text = "";
                    variation = "HudDim";
                    break;
            }
            if (!Mathf.IsEqualApprox(_streakBar[i].Value, bar)) _streakBar[i].Value = bar;
            Set(_streakState[i], text);
            if (_streakVariation[i] != variation) { _streakVariation[i] = variation; _streakState[i].ThemeTypeVariation = variation; }
        }
    }

    static string StreakName(StreakSlotView view)
    {
        var key = "streak." + view.Id;
        var t = Strings.T(key);
        return t == key ? view.Name : t;
    }
}
