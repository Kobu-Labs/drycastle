using Godot;
using Drycastle.Shooter.Cartel;
using Drycastle.Shooter.HeadsUp.Badges;
using Drycastle.Shooter.HeadsUp.Prompts;
using Drycastle.Shooter.HeadsUp.RoundFlow;
using Drycastle.Shooter.HeadsUp.Screens;
using Drycastle.Shooter.HeadsUp.Vision;
using Drycastle.Shooter.HeadsUp.Vitals;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// In-match HUD (issue #88): the root that builds one widget per feature, once, in draw order, and forwards the owner's calls to them; the widgets write only what changed.
/// All pieces sit on anchors with fixed-size cells (no shift when values change). The root is scaled by viewport height / 1080 (never under 0.8) times the UI scale setting (75-150 %).
/// </summary>
public partial class Hud : CanvasLayer
{
    // ---- what the owner feeds ----
    /// <summary>Id of the local player (scoreboard highlight, score line).</summary>
    public ushort MyId { get => _state.MyId; set => _state.MyId = value; }
    /// <summary>Display name of a weapons.json index in a kill message (the killer's weapon, not the viewer's hotbar); null when unknown.</summary>
    public Func<byte, string?>? KillWeaponName { get; set; }
    /// <summary>Crosshair spread in degrees (set by the owner on shots and weapon changes; the crosshair reads it each frame).</summary>
    public Func<float>? Spread { get; set; }
    /// <summary>True while a scope is up (sniper zoom): the dark ring and thin reticle replace the crosshair.</summary>
    public Func<bool>? Scoped { get; set; }
    /// <summary>The scope that is up: its vertical FOV in degrees (the mil-dots are spaced by it) and the zero dialled, metres (0: the weapon has no bullet drop, so no dots and no zero).</summary>
    public Func<(float FovDeg, float ZeroM)>? Scope { get; set; }
    /// <summary>Iron-sight aim amount 0..1; the crosshair fades out as it rises (the weapon's own sight takes over).</summary>
    public Func<float>? Aim { get; set; }
    /// <summary>Sniper breath hold: whether the weapon has one and is scoped, the breath left (0..1) and the phase. Read each frame; the owner sets it.</summary>
    public Func<(bool Show, float Meter, BreathState.BreathPhase Phase)>? Breath { get; set; }
    /// <summary>The binoculars: whether they are up, and the distance read, metres (null: nothing in range). Read each frame; the owner sets it.</summary>
    public Func<(bool Up, int? RangeM, bool CanMark)>? Binoculars { get; set; }
    /// <summary>Admin rights and spawnable vehicles for the chat's command hints (set by the owner; read when the text changes).</summary>
    public Func<ChatContext>? ChatInfo { get; set; }
    /// <summary>The player sent a chat line (a message, or a command starting with "/").</summary>
    public Action<string>? ChatSubmit { get; set; }
    /// <summary>Picks the slot the next spawn uses (the owner sends it to the server).</summary>
    public Action<int>? PickSlot { get; set; }
    /// <summary>Opens the loadout editor from the respawn screen.</summary>
    public Action? EditLoadouts { get; set; }
    /// <summary>False while a menu screen is open, so the number keys belong to that screen.</summary>
    public Func<bool>? KeysFree { get; set; }
    /// <summary>Screenshot mode freezes the clock so transient elements (hit marker, indicators, feed, countdowns) stay as set.</summary>
    public static double? FrozenNow { get => HudUi.FrozenNow; set => HudUi.FrozenNow = value; }

    // ---- state and widgets, in the order they are built (the order is the draw order) ----
    readonly HudState _state = new();
    /// <summary>What the minimap and the tactical map both show; the one place the match feeds (through <see cref="Minimap"/>'s setters).</summary>
    readonly MapMarkers _mapMarkers = new();
    Control _root = null!;
    Vector2 _lastSize;
    DustVeil _dust = null!;
    SpawnProtection _protection = null!;
    PounceWash _pounce = null!;
    ScopeView _scope = null!;
    Crosshair _crosshair = null!;
    HitMarker _hit = null!;
    DamageIndicators _indicators = null!;
    BreathHint _breath = null!;
    HuntShotCard _huntShot = null!;
    BinocularsView _binoculars = null!;
    HealthBar _health = null!;
    StatusChips _status = null!;
    RozhlasChips _rozhlas = null!;
    SpeedReadout _speed = null!;
    ContextPrompt _prompt = null!;
    DrillPanel _drill = null!;
    AmmoReadout _ammo = null!;
    SeatPanel _seats = null!;
    TopBar _top = null!;
    JobTracker _job = null!;
    KillFeed _feed = null!;
    FpsCounter _fps = null!;
    RespawnScreen _respawn = null!;
    BloodVeil _blood = null!;
    Scoreboard _scoreboard = null!;
    XpPanel _xp = null!;
    MinimapPanel _minimap = null!;
    readonly SandboxBadge _sandbox = new(); // keeps its state until built, so the client may set it early
    OverlayScreens _screens = null!;
    ChatPanel _chat = null!;
    VolumeLine _volumes = null!;

    public override void _Ready()
    {
        Strings.Register();
        Layer = 10;
        GameSettings.Changed += OnSettingsChanged;
        _root = new Control { Theme = ShooterTheme.Build(), MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_root);
        _dust = new DustVeil(_root); // first: the dust is behind every HUD element
        _protection = new SpawnProtection(_root);
        _pounce = new PounceWash(_root);
        _scope = new ScopeView(_root);
        _crosshair = new Crosshair(_root, () => _scope.Up, () => _binoculars?.Up ?? false);
        _hit = new HitMarker(_root);
        _indicators = new DamageIndicators(_root);
        _breath = new BreathHint(_root);
        _huntShot = new HuntShotCard(_root);
        _binoculars = new BinocularsView(_root, _crosshair.Apply);
        _health = new HealthBar(_root);
        _status = new StatusChips(_root);
        _rozhlas = new RozhlasChips(_root); // the set holder's hint and ON AIR chip
        _speed = new SpeedReadout(_root);
        _prompt = new ContextPrompt(_root);
        _drill = new DrillPanel(_root); // the Drill on a Trap house's safe, under the prompt line (#496)
        _ammo = new AmmoReadout(_root);
        _seats = new SeatPanel(_root); // the vehicle seat schematic above the vehicle readout
        _top = new TopBar(_root, _state);
        _job = new JobTracker(_root, _mapMarkers); // the cartel Job's tracker and end card at the right edge, under the kill feed (#494)
        _feed = new KillFeed(this, _root.Theme, _state, id => KillWeaponName?.Invoke(id)); // beside the panels root, not under it: a control session hides the panels and keeps the feed (ticket #154)
        _fps = new FpsCounter(_root, _feed, _state); // after the feed: it moves the feed clear of the chip
        _protection.AddChip(_root);
        _respawn = new RespawnScreen(_root, () => KeysFree?.Invoke() ?? true, slot => PickSlot?.Invoke(slot), () => EditLoadouts?.Invoke());
        _blood = new BloodVeil(_root); // above the scrim
        _scoreboard = new Scoreboard(_root, _state);
        _xp = new XpPanel(_root, _state);
        _minimap = new MinimapPanel(_root, _mapMarkers);
        _sandbox.Build(_root);
        _screens = new OverlayScreens(_root, this, _minimap.Map, _mapMarkers, _sandbox, Overlays, _scoreboard.Panel, _xp.Panel);
        _chat = new ChatPanel(_root, ChatState, text => ChatSubmit?.Invoke(text), _fps.Toggle);
        _volumes = new VolumeLine(_root);
        _minimap.Apply();
        Relayout();
        GetViewport().SizeChanged += Relayout;
        SetHealth(100);
        SetMatch(MatchPhase.Waiting, 0, null);
    }

    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;

    /// <summary>The live settings changed (settings screen preview/apply): rescale, crosshair visibility and FOV, key hints.</summary>
    void OnSettingsChanged()
    {
        Relayout();
        _minimap.Apply();
        _crosshair.ResetGap();
        _rozhlas.MarkStale(); // the talk key may have been rebound
        _seats.Forget(); // the seat hint names the (maybe rebound) keys: rebuilt on the next frame
        _indicators.Recolor(); // read at build time otherwise; the palette may have switched
        _crosshair.Apply();
        _fps.Apply();
    }

    /// <summary>Root size is the viewport divided by the scale, so anchored children fill the real screen at any size and aspect.</summary>
    void Relayout()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        if (vp == _lastSize && Mathf.IsEqualApprox(_root.Scale.X, HudUi.Scale(this))) return;
        _lastSize = vp;
        _scoreboard.MarkDirty(); // its width is capped to the screen
        _root.Position = Vector2.Zero;
        HudUi.Fit(_root);
        HudUi.Fit(_feed.Root);
        _screens.Fit();
        _crosshair.ResetGap();
    }

    ChatContext ChatState() => ChatInfo?.Invoke() ?? new ChatContext(false, Array.Empty<string>());

    public override void _Process(double delta)
    {
        var now = HudUi.Now;
        _top.Tick();
        _job.Tick();
        _respawn.Tick();
        _protection.Tick(now);
        _hit.Tick(now);
        _huntShot.Tick(now);
        _indicators.Tick(now);
        _feed.Tick(now);
        _fps.Tick(now);
        UpdateSights();
        _breath.Update(Breath?.Invoke() ?? (false, 0f, BreathState.BreathPhase.Ready));
        _screens.Tick();
        _scoreboard.Tick(ChatOpen);
    }

    /// <summary>What the player looks through: the binoculars, else the scope, else the crosshair with its gap.</summary>
    void UpdateSights()
    {
        if (_crosshair.Pinned) return;
        if (_binoculars.Update(Binoculars?.Invoke() ?? (false, null, false)))
        {
            // the binoculars are up: their own view, no crosshair, and no scope left over from the moment they were raised
            _scope.Hide();
            _scope.Update((0f, 0f));
            return;
        }
        var scoped = Scoped?.Invoke() ?? false;
        if (_scope.SetUp(scoped)) _crosshair.Apply();
        _scope.Update(scoped ? Scope?.Invoke() ?? (0f, 0f) : (0f, 0f));
        if (scoped) return;
        _crosshair.Update(Aim?.Invoke() ?? 0f, Spread?.Invoke() ?? 0f);
    }

    // ---- match ----
    /// <summary>Team deathmatch: the client's match state, read for teams and team scores. Call before <see cref="SetMatch"/>; null or free-for-all hides every team cue.</summary>
    public void SetTeams(ClientMatchState? match)
    {
        _state.Team = match;
        _scoreboard.MarkDirty();
        _feed.Invalidate();
        _minimap.Map.TeamColours = _state.TeamMode;
    }

    /// <summary>Phase, seconds left (the clock runs on from there while the match is running) and the rows, winners and names (null: unchanged).</summary>
    public void SetMatch(MatchPhase phase, float timeRemaining, IReadOnlyList<ScoreRow>? rows, IReadOnlyList<ushort>? winners = null, Func<ushort, string>? nameOf = null)
    {
        _state.Phase = phase;
        if (rows != null) { _state.Rows = rows; _scoreboard.MarkDirty(); }
        if (winners != null) { _state.Winners = winners; _scoreboard.MarkDirty(); }
        if (nameOf != null) _state.NameOf = nameOf;
        _top.Set(timeRemaining);
        _xp.Refresh();
    }

    public void AddKill(KillMsg k, string killer, string victim) => _feed.AddKill(k, killer, victim);

    /// <summary>A server-wide Hunter's Log announcement (a record or a first) as a line of the kill feed.</summary>
    public void AddHuntLine(HuntFeedLine line) => _feed.AddHuntLine(line);

    public void ClearKills() => _feed.Clear();

    /// <summary>The round's XP summary from the server; shown in the ending phase.</summary>
    public void SetXpSummary(XpSummaryInfo? summary) => _xp.Set(summary);

    // ---- the player ----
    public void SetHealth(int hp) => _health.Set(hp);

    /// <summary>Display names in slot order (the selected one is named beside the ammo).</summary>
    public void SetWeapons(IReadOnlyList<string> names) => _ammo.SetWeapons(names);

    /// <summary>Selected slot (1-based) and its mag/reserve (mag &lt; 0: no ammo, melee). The per-slot list and reload fields are kept for the callers.</summary>
    public void SetAmmo(int slot, int mag, int reserve, IReadOnlyList<(int Mag, int Reserve, bool Melee)> all, float reloadLeft, float reloadTotal) => _ammo.Set(slot, mag, reserve);

    /// <summary>Context prompt under the crosshair, e.g. "E  Enter car"; null hides it.</summary>
    public void SetPrompt(string? text) => _prompt.SetPrompt(text);

    /// <summary>The hold ring above the prompt (care package open, #151; taking the rozhlas set): a fill from 0 to 1, or null to hide it.</summary>
    public void SetRadial(float? fraction) => _prompt.SetRing(fraction);

    /// <summary>The Drill on the safe a party player is near (#496); null hides the panel.</summary>
    public void SetDrill(DrillHudInfo? info) => _drill.Set(info);

    /// <summary>Vehicle speed readout (km/h); null hides it.</summary>
    public void SetVehicleSpeed(float? kmh) => _speed.SetSpeed(kmh);

    /// <summary>The vehicle readout as text (the flak gunner's ammo or a reload word) in the place of the speed; null hides it. A change of text only.</summary>
    public void SetVehicleReadout(string? text) => _speed.SetText(text);

    /// <summary>The seats of the vehicle the local player sits in, or null to hide the panel (not seated).</summary>
    public void SetSeats((VehicleDef Def, ushort[] Occupants, int Mine)? seats) => _seats.Set(seats);

    /// <summary>Shows which of the local player's statuses are on: the blessing and the curse (the mask), and burning.</summary>
    public void SetStatus(byte mask, bool burning) => _status.Set(mask, burning);

    /// <summary>The golden screen flash of a blessing that saved the player. Reduce motion: no flash.</summary>
    public void FlashBlessing() => _status.FlashBlessing();

    /// <summary>Spawn protection seconds remaining (0 hides the chip and the outline).</summary>
    public void SetProtection(float seconds) => _protection.Set(seconds);

    /// <summary>Shows the hold-the-set hint while <paramref name="holder"/>, and ON AIR while <paramref name="talking"/>.</summary>
    public void SetRozhlas(bool holder, bool talking) => _rozhlas.Set(holder, talking);

    /// <summary>Seconds left on the owner's UAV; zero or less hides the indicator.</summary>
    public void SetUavSeconds(float seconds) => _minimap.Uav.SetSeconds(seconds);

    /// <summary>The kills this life and the killstreak slots. <paramref name="alive"/> is unused by the row and kept for the callers.</summary>
    public void RefreshStreaks(KillstreakTracker? tracker, bool alive) => _minimap.Streaks.Refresh(tracker);

    /// <summary>Shows or hides the sandbox badge; <paramref name="held"/> is the held killstreak id ("" = none).</summary>
    public void SetSandbox(bool on, string held, bool takesDamage = false) => _sandbox.Set(on, held, takesDamage);

    /// <summary>The mode line of the F3 bounding-volume overlay, or null to hide it.</summary>
    public void SetVolumeMode(string? text) => _volumes.Set(text);

    /// <summary>The running count of server snapshots received (the FPS card's TPS line is its rate).</summary>
    public void SetSnapshotCount(long count) => _fps.SetSnapshotCount(count);

    /// <summary>Direction of a damage source relative to the view (radians, 0 = ahead, clockwise). Fixed: it does not follow anything.</summary>
    public void Damage(float angle) => _indicators.Add(angle);

    /// <summary>A tracked damage source: <paramref name="live"/> gives its angle every frame; a new hit from the same <paramref name="key"/> refreshes its chevron (#424).</summary>
    public void Damage(object key, Func<float?> live) => _indicators.Track(key, live);

    /// <summary>Hit marker on a successful hit; kill and headshot get a larger, stronger mark (shape, not colour only).</summary>
    public void Hit(bool headshot, bool kill) => _hit.Show(headshot, kill);

    /// <summary>Drops hit marker and damage indicators at once (used on spawn and by the screenshot mode).</summary>
    public void ClearTransients()
    {
        _hit.Clear();
        _huntShot.Clear();
        _indicators.Clear();
    }

    /// <summary>The hit diagram after a sniper kill of a wild animal.</summary>
    public void ShowHuntShot(string species, AnimalArt art, float along, float height, string line) => _huntShot.Show(species, art, along, height, line);

    /// <summary>The red wash of the demon's pounce: its opacity (0 off).</summary>
    public void SetPounceWash(float alpha) => _pounce.Set(alpha);

    /// <summary>The blood's intensity, 0 (off) to 1 (the death screen's full spatter); <paramref name="seed"/> picks the pattern.</summary>
    public void SetBlood(float intensity, int seed = 0) => _blood?.Set(intensity, seed);

    /// <summary>How much of the view the dust takes, 0 (off) to 1; <paramref name="night"/> is the night factor, 0 at full day.</summary>
    public void SetDust(float intensity, float night) => _dust.Set(intensity, night);

    /// <summary>Respawn screen with the killer ("" = none) and seconds left; <paramref name="fadeIn"/>: the scrim starts clear and the caller fades it in with <see cref="SetRespawnScrim"/> (#283).</summary>
    public void SetDead(bool dead, string killer = "", float secondsLeft = 0, bool fadeIn = false) => _respawn.SetDead(dead, killer, secondsLeft, fadeIn);

    /// <summary>The respawn scrim's opacity 0..1 (its text, the countdown and the loadout fade with it); only while dead.</summary>
    public void SetRespawnScrim(float alpha) => _respawn.SetScrim(alpha);

    /// <summary>The five loadout rows (text and whether active); redrawn when the server sends a new profile, never per frame.</summary>
    public void SetLoadoutRows(IReadOnlyList<(string Text, bool Active)> rows) => _respawn.SetLoadoutRows(rows);

    /// <summary>A panel stands in the middle of the screen (the corpse chooser): the crosshair is hidden meanwhile.</summary>
    public void CoverCrosshair(bool on) => _crosshair?.Cover(on);

    /// <summary>The local player sits where nothing can be fired (#373): no crosshair and no weapon/ammo readout meanwhile.</summary>
    public void SetUnarmedSeat(bool on)
    {
        if (_crosshair == null) return;
        _ammo.Visible = !on;
        _crosshair.SetUnarmed(on);
    }

    /// <summary>The Lock-on launcher's sight is up (<see cref="LockOnOverlay"/>): it has its own aim pip, so the crosshair is hidden meanwhile.</summary>
    public void SetLauncherHidesCrosshair(bool hide) => _crosshair?.SetLauncher(hide);

    /// <summary>The crosshair is hidden while the player lies pinned under the demon.</summary>
    public void SetPounceHidesCrosshair(bool hide) => _crosshair?.SetPounce(hide);

    /// <summary>The crosshair is hidden while the Bigfoot holds the player and while they fly from its throw (#360).</summary>
    public void SetFlungHidesCrosshair(bool hide) => _crosshair?.SetFlung(hide);

    // ---- control mode ----
    /// <summary>True while the regular HUD is hidden for a control session.</summary>
    public bool ControlMode => _state.ControlMode;

    /// <summary>
    /// The control-mode switch (ticket #152): while the owner flies a streak's point of view (a gunship turret, the attack missile) the HUD panels are hidden; the kill feed stays,
    /// compact, with only the owner's kills (ticket #154). Idempotent and cheap to call every frame.
    /// </summary>
    public void SetControlMode(bool on)
    {
        if (_state.ControlMode == on) return;
        _state.ControlMode = on;
        _root.Visible = !on;
        _fps.Apply();
        _feed.SetCompact(on);
    }

    // ---- chat ----
    public bool ChatOpen => _chat?.IsOpen ?? false;

    /// <summary>Opens the chat box. The Hunter's Log (and the other top-layer panels) closes first: it sits on the top layer, so it would cover the box and take its Q / E keys.</summary>
    public void OpenChat(string prefill = "")
    {
        _screens.CloseHuntersLog();
        _screens.CloseMineralLog();
        _screens.CloseContactPanel();
        _chat.Open(prefill);
    }

    /// <summary>Closes the chat box. True when it was open.</summary>
    public bool CloseChat() => _chat.Close();

    public void AddChatLine(ChatLine line) => _chat.AddLine(line);

    // ---- the minimap and the overlay screens ----
    /// <summary>The HUD's minimap: build it with the map, set the player each frame, feed enemy pings. The tactical map shares its data.</summary>
    public Minimap Minimap => _minimap.Map;
    /// <summary>The Scud strikes the maps show (<see cref="MapStrikes"/>).</summary>
    public MapStrikes Strikes => _mapMarkers.Strikes;
    /// <summary>The search areas of announced events (the Scud event) the tactical map draws.</summary>
    public MapSearchAreas SearchAreas => _mapMarkers.SearchAreas;

    /// <summary>What <see cref="OverlayArbiter"/> needs: the HUD knows the chat box, death, the end of the match and the control mode; the caller adds the menu and the chooser.</summary>
    public OverlayContext Overlays(bool menuOpen = false, bool chooserOpen = false) => new(ChatOpen, _respawn?.Dead ?? false, _state.Phase == MatchPhase.Ending, _state.ControlMode, menuOpen, chooserOpen);

    public bool TacticalMapOpen => _screens.TacticalMapOpen;
    public Action<Vector2>? TacticalMapTeleport { set => _screens.TacticalMapTeleport = value; }
    public void PrewarmTacticalMap(Vector2 viewport) => _screens.PrewarmTacticalMap(viewport);
    public bool ToggleTacticalMap(bool menuOpen = false) => _screens.ToggleTacticalMap(menuOpen);
    public bool CloseTacticalMap() => _screens.CloseTacticalMap();

    public bool EquipmentOpen => _screens?.EquipmentOpen ?? false;
    public void AttachEquipment(NetClient client) => _screens.AttachEquipment(client);
    public bool ToggleEquipment(bool menuOpen = false, bool chooserOpen = false) => _screens.ToggleEquipment(menuOpen, chooserOpen);
    public bool CloseEquipment() => _screens.CloseEquipment();

    public bool HuntersLogOpen => _screens?.HuntersLogOpen ?? false;
    public void AttachHuntersLog(HuntLogEnv env) => _screens.AttachHuntersLog(env);
    public bool ToggleHuntersLog(bool menuOpen = false, bool chooserOpen = false) => _screens.ToggleHuntersLog(menuOpen, chooserOpen);
    public void OpenHuntersLog() => _screens.OpenHuntersLog();
    public void CloseHuntersLog() => _screens.CloseHuntersLog();
    public bool BackHuntersLog() => _screens.BackHuntersLog();

    public bool MineralLogOpen => _screens?.MineralLogOpen ?? false;
    public void AttachMineralLog() => _screens.AttachMineralLog();
    public bool ToggleMineralLog(bool menuOpen = false, bool chooserOpen = false) => _screens.ToggleMineralLog(menuOpen, chooserOpen);
    public void SetMineralFinds(IReadOnlyDictionary<string, int> ranks) => _screens.SetMineralFinds(ranks);
    public void OpenMineralLog() => _screens.OpenMineralLog();
    public void CloseMineralLog() => _screens.CloseMineralLog();
    public bool BackMineralLog() => _screens.BackMineralLog();

    // ---- the cartel (#494) ----
    /// <summary>The player's Job tracker: its panel and its marker on the maps and in the world follow it every frame.</summary>
    public void AttachJobTracker(CartelTracker tracker, Func<string, string> kindName, Func<ushort, string> nameOf) => _job.Attach(tracker, kindName, nameOf);

    public bool ContactPanelOpen => _screens?.ContactPanelOpen ?? false;
    public void AttachContactPanel(ContactPanel panel) => _screens.AttachContactPanel(panel);
    public bool OpenContactPanel(bool menuOpen = false, bool chooserOpen = false) => _screens.OpenContactPanel(menuOpen, chooserOpen);
    public void CloseContactPanel() => _screens.CloseContactPanel();
    public bool BackContactPanel() => _screens.BackContactPanel();
}
