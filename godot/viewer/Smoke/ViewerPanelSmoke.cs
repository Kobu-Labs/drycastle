using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Drycastle.Viewer;

/// <summary>
/// `--panel-smoke --panel-smoke-plan plan.json` (run by panel_smoke.py, `make viewer-panel-smoke`): drives the
/// Fence panel of the editor mode through its controller API (no synthetic mouse) in a headless
/// viewer: pending state, Apply, groups, gates, reroll, the watcher round trip. The file after every Apply is
/// copied to the plan's steps folder; the driver validates them with the Python validator afterwards.
/// </summary>
public partial class Viewer
{
    static ulong MeshHash(Node n)
    {
        ulong h = 1469598103934665603UL;
        void Walk(Node x)
        {
            if (x is MeshInstance3D { Mesh: { } m })
                for (var s = 0; s < m.GetSurfaceCount(); s++)
                    foreach (var v in m.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                    {
                        h = (h ^ (ulong)BitConverter.SingleToInt32Bits(v.X)) * 1099511628211UL;
                        h = (h ^ (ulong)BitConverter.SingleToInt32Bits(v.Z)) * 1099511628211UL;
                    }
            foreach (var c in x.GetChildren()) Walk(c);
        }
        Walk(n);
        return h;
    }

    sealed class PanelCtx : SmokeCtx
    {
        public readonly string A, B, C, F, Gid;
        public readonly JsonElement P1, P2;
        public string SeedText = "";
        // the gate probe: the picked boundary point, a point 2 m beside it and the boundary's direction there
        public Vector3 OnP1, OffP1;
        public JsonElement Tangent;

        public PanelCtx(string planPath, string editsPath) : base(planPath, editsPath)
        {
            A = S("a"); B = S("b"); C = S("c"); F = S("f");
            Gid = S("group");
            P1 = Plan.GetProperty("probe1");
            P2 = Plan.GetProperty("probe2");
        }
    }

    Task RunPanelSmoke() => RunSmoke("panel", PanelSmokeBody);

    async Task PanelSmokeBody()
    {
        using var c = new PanelCtx(_opts["panel-smoke-plan"], _editor!.EditsPath);
        c.MakeStepsDir();
        c.SeedText = c.Disk();
        CheckSeed(c);

        SchemaConstants(c);
        await EditorModeOn(c);
        await ModeTypeHeightColour(c);
        await Gates(c);
        GateListEditAndRevert(c);
        await EscCancelsArmedGate(c);
        await SaveRealGate(c);
        await FenceNone(c);
        await ResetRestoresSeed(c);
        await PendingAcrossSelections(c);
        await MultiParcelApply(c);
        await PropertyGroupFence(c);
        await GroupScopeToggle(c);
        await EnterApplies(c);

        Check(await WaitIdle(60) && _watcher!.FailedCount == 0, $"watcher settled, no failed parcel-run ({_watcher!.FailedCount})");
    }

    // a plan point (x east, y north in map metres, plus an offset) on the terrain
    Vector3 PlanGround(SmokeCtx c, string k, double dx = 0, double dy = 0)
    {
        var a = c.Plan.GetProperty(k);
        var x = (float)(a[0].GetDouble() + dx); var z = (float)-(a[1].GetDouble() + dy);
        return new Vector3(x, _map.GroundHeightOrMean(x, z), z);
    }

    // the point `m` metres from the picked boundary point along the boundary
    static Vector3 AlongTangent(PanelCtx c, double m)
        => c.OnP1 + new Vector3((float)(c.Tangent[0].GetDouble() * m), 0, (float)(-c.Tangent[1].GetDouble() * m));

    async Task Sel(params string[] ids)
    {
        var ed = _editor!;
        ed.Click(null, false);
        for (var i = 0; i < ids.Length; i++) ed.Click(ids[i], i > 0);
        await Frame();
        ed.RefreshNow();
    }

    // Apply, check the save, copy the file for the driver, wait for the watcher's regeneration
    async Task ApplyAndWait(PanelCtx c, string name)
    {
        var ed = _editor!;
        var n0 = _watcher!.AppliedCount;
        var saved = ed.Fence.Apply();
        var status = ed.Chrome.StatusMessage;
        Check(saved && status.StartsWith("saved"), $"[{name}] Apply saves: status '{status}'");
        c.SaveStep(name);
        var ok = await WaitUntil(() => _watcher.AppliedCount > n0 || _watcher.FailedCount > 0, 40);
        Check(ok && _watcher.AppliedCount > n0 && ed.Chrome.StatusMessage.StartsWith("applied in"),
            $"[{name}] watcher regenerated: status '{ed.Chrome.StatusMessage}'");
        await Physics(4);
        ed.RefreshNow();
    }

    // ---- the schema constants match Python's tables ----
    void SchemaConstants(PanelCtx c)
    {
        var types = c.Plan.GetProperty("fence_types").EnumerateObject().Select(p => (name: p.Name, h: p.Value[0].GetDouble(), t: p.Value[1].GetDouble(), c: p.Value[2].GetString()!)).ToList();
        Check(types.Select(t => t.name).SequenceEqual(EditSchema.FenceTypeNames)
              && types.All(t => { var i = EditSchema.TypeInfo(t.name); return i.Height == t.h && i.Thickness == t.t && i.Colour == t.c; })
              && EditSchema.DefaultFenceType == c.S("default_type")
              && c.Plan.GetProperty("fence_modes").EnumerateArray().Select(m => m.GetString()).SequenceEqual(EditSchema.FenceModes),
            $"EditSchema equals edits/schema.py: types [{string.Join(", ", types.Select(t => t.name))}], modes [{string.Join(", ", EditSchema.FenceModes)}]");
    }

    // editor mode on, parcel C selected: the panel starts clean and refuses custom-only fields under auto
    async Task EditorModeOn(PanelCtx c)
    {
        var ed = _editor!;
        await Physics(3);
        Press(Key.F2);
        await WaitUntil(() => ed.Active, 3);
        Check(ed.Active, "editor mode on");
        await Sel(c.C);
        Check(ed.Fence.Panel.FenceTypeItems.SequenceEqual(EditSchema.FenceTypeNames) && ed.Fence.Panel.FenceModeItems.SequenceEqual(EditSchema.FenceModes),
            "the panel's type and mode lists are the schema's");
        Check(ed.Fence.Panel.HeaderText == c.C && !ed.Fence.Panel.DirtyShown && ed.Fence.PendingCount == 0 && ed.Fence.Panel.FooterText.Contains("no unsaved"),
            $"header shows the target '{ed.Fence.Panel.HeaderText}', nothing pending");
        Check(!ed.Fence.Panel.FieldsEnabled && ed.Fence.Current.EffectiveMode == "auto" && ed.Fence.Panel.FenceInfo.StartsWith("auto"),
            "fence mode auto: custom-only fields disabled, effective rules shown");
        Check(!ed.Fence.SetHeight(3.0) && ed.Fence.PendingCount == 0, "a custom-only field is refused while the mode is auto, nothing pending");
    }

    // ---- 1. mode / type / height / colour on one parcel ----
    async Task ModeTypeHeightColour(PanelCtx c)
    {
        var ed = _editor!;
        Check(ed.Fence.SetMode("custom") && ed.Fence.SetType("brick_wall") && ed.Fence.SetHeight(2.0) && ed.Fence.SetColour("#336699"),
            "set mode custom, type brick_wall, height 2.0, colour #336699");
        await Frame();
        Check(ed.Fence.PendingCount == 1 && ed.Fence.TargetDirty && ed.Fence.Panel.DirtyShown && c.Disk() == c.SeedText
              && ed.Fence.Panel.FooterText.Contains("1 unsaved"),
            "pending: dirty marker on the header, '1 unsaved target', file untouched");
        Check(ed.Fence.Panel.FieldsEnabled && ed.Fence.Current.Thickness == null && ed.Fence.Current.EffectiveCustom(null).thickness == 0.3,
            "custom: fields enabled, unset thickness shows the type's default (0.3)");
        Check(!ed.Fence.SetColour("336699") && !ed.Fence.SetType("nope") && !ed.Fence.SetMode("bogus"), "bad colour / type / mode are refused");
        await ApplyAndWait(c, "t1");
        FileIs(c, "t1", "custom fence");
        Check(ed.Fence.PendingCount == 0 && !ed.Fence.Panel.DirtyShown && ed.Fence.Current.Type == "brick_wall", "after Apply: nothing pending, the panel shows what was written");
        Check(ProbeHits(c.P1) && ProbeHits(c.P2), "custom brick wall: both probe rays hit the fence colliders");
    }

    // ---- 2. gates ----
    async Task Gates(PanelCtx c)
    {
        var ed = _editor!;
        ed.RefreshNow();
        Check(ed.Fence.Panel.GateButtonEnabled && ed.Fence.Gates.Arm() && ed.Fence.Gates.Armed, "[Add gate] arms the tool");
        Check(!ed.Fence.Gates.Begin(PlanGround(c, "far_point")) && ed.Fence.Gates.Armed && ed.Chrome.StatusMessage.Contains("no fence boundary"),
            $"a click more than 6 m from the boundary picks nothing ('{ed.Chrome.StatusMessage}')");
        c.Tangent = c.Plan.GetProperty("probe1_tangent"); var normal = c.Plan.GetProperty("probe1_normal");
        c.OnP1 = PlanGround(c, "probe1_pt");
        c.OffP1 = PlanGround(c, "probe1_pt", normal[0].GetDouble() * 2.0, normal[1].GetDouble() * 2.0);
        Check(ed.Fence.Gates.Begin(c.OffP1) && ed.Fence.Gates.Dragging, "click 2 m beside the fence picks the boundary point");
        (double drag, double want)[] widths = { (0.1, 3.0), (0.3, 1.0), (0.5, 1.0), (1.13, 2.5), (1.0, 2.0), (2.6, 5.0) };
        foreach (var (drag, want) in widths)
        {
            ed.Fence.Gates.Drag(AlongTangent(c, drag));
            Check(Math.Abs(ed.Fence.Gates.Width - want) < 1e-9, $"drag {drag} m from the picked point gives width {want} (min 1, snap 0.5, twice the distance): {ed.Fence.Gates.Width}");
        }
        ed.Fence.Gates.Drag(AlongTangent(c, 1.0));
        Check(_editor!.MarkerRoot.GetNodeOrNull("EditorGatePreview")?.GetChildCount() == 3, "a live preview marker (ribbon and two posts) is drawn while dragging");
        Check(ed.Fence.Gates.End() && !ed.Fence.Gates.Armed && ed.Fence.Current.Gaps.Count == 1 && ed.Fence.Current.Gaps[0].Width == 2.0 && ed.Fence.TargetDirty,
            "release adds the gate (2 m) as a pending change, the tool disarms");
        await Frame();
        ed.RefreshNow();
        Check(ed.Fence.Gates.MarkerCount == 1 && ed.Fence.Gates.Selected == 0 && ed.Fence.Panel.GapRowCount == 1, "the new gate is drawn as a marker and listed (selected)");
    }

    // edit the gate list, then revert
    void GateListEditAndRevert(PanelCtx c)
    {
        var ed = _editor!;
        Check(ed.Fence.Gates.SetWidth(0, 4.0) && ed.Fence.Current.Gaps[0].Width == 4.0, "gate width spin changes the pending gap");
        Check(ed.Fence.Gates.Arm() && ed.Fence.Gates.Begin(c.OffP1) && ed.Fence.Gates.End() && ed.Fence.Current.Gaps.Count == 2, "a second gate (click without drag)");
        Check(Math.Abs(ed.Fence.Current.Gaps[1].Width - 3.0) < 1e-9, "a click without drag gives the 3 m default");
        Check(ed.Fence.Gates.Remove(1) && ed.Fence.Current.Gaps.Count == 1, "delete removes a gate from the list");
        ed.Fence.Revert();
        Check(ed.Fence.PendingCount == 0 && ed.Fence.Current.Gaps.Count == 0 && c.Disk() == c.Expect("t1"), "Revert discards the pending gates, file untouched");
        ed.RefreshNow();
    }

    // Esc cancels
    async Task EscCancelsArmedGate(PanelCtx c)
    {
        var ed = _editor!;
        Check(ed.Fence.Gates.Arm(), "arm again");
        await PressWait(Key.Escape, frames: 3);
        Check(!ed.Fence.Gates.Armed && ed.Selection.Contains(c.C), "Esc cancels the armed tool and keeps the selection");
    }

    // the real gate
    async Task SaveRealGate(PanelCtx c)
    {
        var ed = _editor!;
        ed.Fence.Gates.Arm();
        ed.Fence.Gates.Begin(c.OffP1);
        ed.Fence.Gates.Drag(AlongTangent(c, 1.0));
        ed.Fence.Gates.End();
        var gap = ed.Fence.Current.Gaps[0];
        Check(Math.Abs(gap.Width - 2.0) < 1e-9, $"gate of width 2.0 at ({gap.X:F3}, {gap.Y:F3}) in S-JTSK");
        await ApplyAndWait(c, "t2");
        Check(!ProbeHits(c.P1) && ProbeHits(c.P2), "the gate cut a gap into the fence collider (probe 1 free, probe 2 still blocked)");
        Check(ed.Fence.Gates.MarkerCount == 1, "the saved gate stays drawn");
    }

    // ---- 3. fence none: colliders go away through the watcher ----
    async Task FenceNone(PanelCtx c)
    {
        var ed = _editor!;
        Check(ed.Fence.SetMode("none") && !ed.Fence.Current.HasCustomKeys && ed.Fence.Current.Gaps.Count == 0,
            "switching to none clears the custom-only keys (also the gaps)");
        await Frame();
        ed.RefreshNow();
        Check(!ed.Fence.SetHeight(1.0) && !ed.Fence.Panel.FieldsEnabled, "custom-only fields are disabled and refused under none");
        Check(!ed.Fence.Panel.GateButtonEnabled && !ed.Fence.Gates.Arm(), "gates are not available under none");
        await ApplyAndWait(c, "t3");
        FileIs(c, "t3", "fence none");
        var shapes = _map.PropsShapes.GetValueOrDefault(c.C) ?? new();
        var dynColliders = _map.DynamicProps.TryGetValue(c.C, out var dyn) ? CountOfType<CollisionShape3D>(dyn) : 0;
        Check(!ProbeHits(c.P1) && !ProbeHits(c.P2) && dynColliders == 0 && shapes.All(s => s.Disabled),
            $"fence none removed the colliders (dynamic={dynColliders}, baked disabled={shapes.Count})");
    }

    // ---- 5. reset removes the entries, the file is back to its original bytes ----
    async Task ResetRestoresSeed(PanelCtx c)
    {
        Check(_editor!.Fence.Reset(), "Reset (fence)");
        await ApplyAndWait(c, "t5");
        FileIs(c, "seed", "reset");
        Check(c.Disk() == c.SeedText, "reset: edits.json is byte for byte the original again (the parcel entry is gone)");
    }

    // ---- 6. pending state across selection changes ----
    async Task PendingAcrossSelections(PanelCtx c)
    {
        var ed = _editor!;
        string A = c.A, C = c.C, F = c.F;
        await Sel(C);
        ed.Fence.SetMode("custom");
        await Sel(F);
        ed.Fence.SetMode("none");
        await Frame();
        Check(ed.Fence.PendingCount == 2 && ed.Fence.Panel.FooterText.Contains("2 unsaved") && ed.Fence.Panel.DirtyShown && ed.Fence.Panel.HeaderText == F,
            $"two pending targets, dirty on {F} ('{ed.Fence.Panel.FooterText}')");
        await Sel(C);
        Check(ed.Fence.Panel.DirtyShown && ed.Fence.Current.EffectiveMode == "custom" && c.Disk() == c.SeedText,
            "back on the first parcel: its pending change was kept, file untouched");
        await Sel(A);
        Check(!ed.Fence.Panel.DirtyShown && ed.Fence.PendingCount == 2, "an untouched target is not marked dirty");
        await Sel(C);
        ed.Fence.Revert();
        Check(ed.Fence.PendingCount == 1 && !ed.Fence.TargetDirty, "Revert discards only the current target's changes");
        ed.Fence.SetMode("custom");
        var saves0 = ed.Edits.SaveCount;
        await ApplyAndWait(c, "t6");
        Check(ed.Edits.SaveCount == saves0 + 1 && ed.Fence.PendingCount == 0, "Apply wrote both targets in one save");
        FileIs(c, "t6", "two targets");
        await Sel(C); ed.Fence.Reset();
        await Sel(F); ed.Fence.Reset();
        await ApplyAndWait(c, "t6r");
        FileIs(c, "seed", "two targets reset");
    }

    // ---- 7. multi-parcel apply ----
    async Task MultiParcelApply(PanelCtx c)
    {
        var ed = _editor!;
        await Sel(c.C, c.F);
        Check(ed.Fence.Panel.HeaderText == "2 parcels" && ed.Fence.Target is { IsGroup: false }, $"multi selection: header '{ed.Fence.Panel.HeaderText}'");
        Check(!ed.Fence.Panel.GateButtonEnabled, "no gates for several loose parcels");
        Check(ed.Fence.SetMode("none"), "fence none for both");
        await ApplyAndWait(c, "t7");
        FileIs(c, "t7", "multi-parcel apply");
        await Sel(c.F);
        Check(ed.Fence.Current.EffectiveMode == "none", "each parcel now shows the shared setting");
        await Sel(c.C, c.F);
        ed.Fence.Reset();
        await ApplyAndWait(c, "t7r");
        FileIs(c, "seed", "multi reset");
    }

    // ---- 8. property group ----
    async Task PropertyGroupFence(PanelCtx c)
    {
        var ed = _editor!;
        await Sel(c.A, c.B);
        Check(ed.Fence.Target is { IsGroup: true } && ed.Fence.Panel.HeaderText == $"{c.Gid} (2 parcels)" && !ed.Fence.Panel.ScopeRowShown,
            $"group selection: header '{ed.Fence.Panel.HeaderText}'");
        Check(ed.Fence.SetMode("custom") && ed.Fence.SetType("wire") && ed.Fence.SetColour("#AABBCC"),
            "custom wire fence, colour #AABBCC");
        Check(ed.Fence.Gates.Arm() && ed.Fence.Gates.Begin(PlanGround(c, "group_gate_pt", c.Plan.GetProperty("group_gate_n")[0].GetDouble(), c.Plan.GetProperty("group_gate_n")[1].GetDouble())), "gate on the group's outer perimeter");
        var gp = PlanGround(c, "group_gate_pt");
        var gt = c.Plan.GetProperty("group_gate_t");
        ed.Fence.Gates.Drag(gp + new Vector3((float)(gt[0].GetDouble() * 1.5), 0, (float)(-gt[1].GetDouble() * 1.5)));
        Check(ed.Fence.Gates.End() && Math.Abs(ed.Fence.Current.Gaps[0].Width - 3.0) < 1e-9, "group gate, width 3.0");
        await ApplyAndWait(c, "t8");
        Check(ed.Edits.SaveCount > 0, "group saved");
        // member parcels' fence entries must not exist; the group entry holds the fence
        var fd = EditsFile.Load(c.EditsPath);
        Check(EditModel.ParcelEntry(fd, c.A)?.ContainsKey("fence") != true && EditModel.ParcelEntry(fd, c.B)?.ContainsKey("fence") != true
              && EditsFile.Groups(fd)[c.Gid] is SortedDictionary<string, object?> ge && ge.ContainsKey("fence"),
            "group edit went to the group entry, not to the member parcels");
    }

    // scope toggle: one member
    async Task GroupScopeToggle(PanelCtx c)
    {
        var ed = _editor!;
        await Sel(c.A);
        Check(ed.Fence.Panel.ScopeRowShown && ed.Fence.Target is { IsGroup: true } && ed.Fence.GroupScope, "one member selected: scope defaults to the whole group");
        ed.Fence.SetGroupScope(false);
        await Frame();
        ed.RefreshNow();
        Check(ed.Fence.Target is { IsGroup: false } && ed.Fence.Panel.HeaderText == c.A, "scope 'this parcel' targets the member itself");
        Check(ed.Fence.SetMode("none"), "own fence override: none");
        await ApplyAndWait(c, "t8b");
        Check(EditModel.ParcelEntry(EditsFile.Load(c.EditsPath), c.A)?["fence"] is SortedDictionary<string, object?> af && af.Count == 1 && af["mode"] as string == "none",
            "the member's own fence override is written to the parcel entry");
        ed.Fence.Reset();
        await ApplyAndWait(c, "t8c");
        ed.Fence.SetGroupScope(true);
        await Sel(c.A, c.B);
        ed.Fence.Reset();
        await ApplyAndWait(c, "t8d");
        FileIs(c, "seed", "group reset");
    }

    // ---- Enter applies when no text field has focus ----
    async Task EnterApplies(PanelCtx c)
    {
        var ed = _editor!;
        await Sel(c.F);
        ed.Fence.SetMode("none");
        var n1 = ed.Edits.SaveCount;
        await PressWait(Key.Enter, frames: 3);
        Check(ed.Edits.SaveCount == n1 + 1 && ed.Fence.PendingCount == 0, "Enter applies the pending changes");
        await WaitIdle(30);
        ed.Fence.Reset();
        await ApplyAndWait(c, "t9");
        FileIs(c, "seed", "back to the seed");
    }
}
