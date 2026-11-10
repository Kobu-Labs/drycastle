using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--panel-smoke --panel-smoke-plan plan.json` (run by panel_smoke.py, `make viewer-panel-smoke`): drives the
/// Fence and Trees panel of the editor mode through its controller API (no synthetic mouse) in a headless
/// viewer: pending state, Apply, groups, gates, reroll, the watcher round trip. The file after every Apply is
/// copied to the plan's steps folder; the driver validates them with the Python validator afterwards.
/// </summary>
public partial class Viewer
{
    async Task RunPanelSmoke()
    {
        try { await PanelSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL panel smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "PANEL SMOKE PASS" : "PANEL SMOKE FAIL");
        GetTree().Quit(_editOk ? 0 : 1);
    }

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

    async Task PanelSmokeBody()
    {
        var ed = _editor!;
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["panel-smoke-plan"]));
        var plan = planDoc.RootElement;
        string S(string k) => plan.GetProperty(k).GetString()!;
        string A = S("a"), B = S("b"), C = S("c"), F = S("f");
        var gid = S("group");
        var editsPath = ed.EditsPath;
        var stepsDir = S("steps_dir");
        Directory.CreateDirectory(stepsDir);
        Vector3 Ground(string k, double dx = 0, double dy = 0)
        {
            var a = plan.GetProperty(k);
            var x = (float)(a[0].GetDouble() + dx); var z = (float)-(a[1].GetDouble() + dy);
            return new Vector3(x, _map.GroundHeightOrMean(x, z), z);
        }
        string Expect(string k) => File.ReadAllText(plan.GetProperty("expect").GetProperty(k).GetString()!);
        var seedText = File.ReadAllText(editsPath);
        Check(seedText == Expect("seed"), "scratch edits file is the planned seed");

        async Task Frame(int n = 2) { for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Sel(params string[] ids)
        {
            ed.Click(null, false);
            for (var i = 0; i < ids.Length; i++) ed.Click(ids[i], i > 0);
            await Frame();
            ed.RefreshNow();
        }
        async Task<bool> ApplyAndWait(string name, bool expectRun = true)
        {
            var n0 = _watcher!.AppliedCount;
            var saved = ed.Apply();
            var status = ed.StatusMessage;
            Check(saved && status.StartsWith("saved"), $"[{name}] Apply saves: status '{status}'");
            File.Copy(editsPath, Path.Combine(stepsDir, name + ".json"), true);
            if (!expectRun) return saved;
            var ok = await WaitUntil(() => _watcher.AppliedCount > n0 || _watcher.FailedCount > 0, 40);
            Check(ok && _watcher.AppliedCount > n0 && ed.StatusMessage.StartsWith("applied in"),
                $"[{name}] watcher regenerated: status '{ed.StatusMessage}'");
            await Physics(4);
            ed.RefreshNow();
            return saved;
        }
        void FileIs(string step, string what)
        {
            var now = File.ReadAllText(editsPath);
            var want = Expect(step);
            Check(now == want, $"{what}: edits.json is exactly the expected text" + (now == want ? "" : $"\n--- got\n{now}--- want\n{want}"));
        }

        // ---- the schema constants match Python's tables ----
        var types = plan.GetProperty("fence_types").EnumerateObject().Select(p => (name: p.Name, h: p.Value[0].GetDouble(), t: p.Value[1].GetDouble(), c: p.Value[2].GetString()!)).ToList();
        Check(types.Select(t => t.name).SequenceEqual(EditSchema.FenceTypeNames)
              && types.All(t => { var i = EditSchema.TypeInfo(t.name); return i.Height == t.h && i.Thickness == t.t && i.Colour == t.c; })
              && EditSchema.DefaultFenceType == S("default_type")
              && plan.GetProperty("fence_modes").EnumerateArray().Select(m => m.GetString()).SequenceEqual(EditSchema.FenceModes),
            $"EditSchema equals edits.py: types [{string.Join(", ", types.Select(t => t.name))}], modes [{string.Join(", ", EditSchema.FenceModes)}]");

        await Physics(3);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F2, Pressed = true });
        await WaitUntil(() => ed.Active, 3);
        Check(ed.Active, "editor mode on");
        await Sel(C);
        Check(ed.FenceTypeItems.SequenceEqual(EditSchema.FenceTypeNames) && ed.FenceModeItems.SequenceEqual(EditSchema.FenceModes),
            "the panel's type and mode lists are the schema's");
        Check(ed.PanelHeaderText == C && !ed.PanelDirtyShown && ed.PendingCount == 0 && ed.PanelFooterText.Contains("no unsaved"),
            $"header shows the target '{ed.PanelHeaderText}', nothing pending");
        Check(!ed.FenceFieldsEnabled && ed.CurrentFence.EffectiveMode == "auto" && ed.PanelFenceInfo.StartsWith("auto"),
            "fence mode auto: custom-only fields disabled, effective rules shown");
        Check(Math.Abs(ed.RulesDensity - plan.GetProperty("rules_density").GetDouble()) < 1e-9 && ed.PanelTreesNote == "",
            $"trees: rules density for the kind shown ({ed.RulesDensity})");
        Check(!ed.SetFenceHeight(3.0) && ed.PendingCount == 0, "a custom-only field is refused while the mode is auto, nothing pending");

        // ---- 1. mode / type / height / colour on one parcel ----
        Check(ed.SetFenceMode("custom") && ed.SetFenceType("brick_wall") && ed.SetFenceHeight(2.0) && ed.SetFenceColour("#336699"),
            "set mode custom, type brick_wall, height 2.0, colour #336699");
        await Frame();
        Check(ed.PendingCount == 1 && ed.TargetDirty && ed.PanelDirtyShown && File.ReadAllText(editsPath) == seedText
              && ed.PanelFooterText.Contains("1 unsaved"),
            "pending: dirty marker on the header, '1 unsaved target', file untouched");
        Check(ed.FenceFieldsEnabled && ed.CurrentFence.Thickness == null && ed.CurrentFence.EffectiveCustom(null).thickness == 0.3,
            "custom: fields enabled, unset thickness shows the type's default (0.3)");
        Check(!ed.SetFenceColour("336699") && !ed.SetFenceType("nope") && !ed.SetFenceMode("bogus"), "bad colour / type / mode are refused");
        await ApplyAndWait("t1");
        FileIs("t1", "custom fence");
        Check(ed.PendingCount == 0 && !ed.PanelDirtyShown && ed.CurrentFence.Type == "brick_wall", "after Apply: nothing pending, the panel shows what was written");
        var p1 = plan.GetProperty("probe1"); var p2 = plan.GetProperty("probe2");
        Check(ProbeHits(p1) && ProbeHits(p2), "custom brick wall: both probe rays hit the fence colliders");

        // ---- 2. gates ----
        ed.RefreshNow();
        Check(ed.GateButtonEnabled && ed.ArmGate() && ed.GateArmed, "[Add gate] arms the tool");
        Check(!ed.GateBegin(Ground("far_point")) && ed.GateArmed && ed.StatusMessage.Contains("no fence boundary"),
            $"a click more than 6 m from the boundary picks nothing ('{ed.StatusMessage}')");
        var tangent = plan.GetProperty("probe1_tangent"); var normal = plan.GetProperty("probe1_normal");
        var onP1 = Ground("probe1_pt");
        var offP1 = Ground("probe1_pt", normal[0].GetDouble() * 2.0, normal[1].GetDouble() * 2.0);
        Check(ed.GateBegin(offP1) && ed.GateDragging, "click 2 m beside the fence picks the boundary point");
        (double drag, double want)[] widths = { (0.1, 3.0), (0.3, 1.0), (0.5, 1.0), (1.13, 2.5), (1.0, 2.0), (2.6, 5.0) };
        foreach (var (drag, want) in widths)
        {
            ed.GateDrag(onP1 + new Vector3((float)(tangent[0].GetDouble() * drag), 0, (float)(-tangent[1].GetDouble() * drag)));
            Check(Math.Abs(ed.GateWidth - want) < 1e-9, $"drag {drag} m from the picked point gives width {want} (min 1, snap 0.5, twice the distance): {ed.GateWidth}");
        }
        ed.GateDrag(onP1 + new Vector3((float)(tangent[0].GetDouble() * 1.0), 0, (float)(-tangent[1].GetDouble() * 1.0)));
        Check(_editor!.MarkerRoot.GetNodeOrNull("EditorGatePreview")?.GetChildCount() == 3, "a live preview marker (ribbon and two posts) is drawn while dragging");
        Check(ed.GateEnd() && !ed.GateArmed && ed.CurrentFence.Gaps.Count == 1 && ed.CurrentFence.Gaps[0].Width == 2.0 && ed.TargetDirty,
            "release adds the gate (2 m) as a pending change, the tool disarms");
        await Frame();
        ed.RefreshNow();
        Check(ed.GateMarkerCount == 1 && ed.SelectedGate == 0 && ed.GapRowCount == 1, "the new gate is drawn as a marker and listed (selected)");
        // edit the gate list, then revert
        Check(ed.SetGapWidth(0, 4.0) && ed.CurrentFence.Gaps[0].Width == 4.0, "gate width spin changes the pending gap");
        Check(ed.ArmGate() && ed.GateBegin(offP1) && ed.GateEnd() && ed.CurrentFence.Gaps.Count == 2, "a second gate (click without drag)");
        Check(Math.Abs(ed.CurrentFence.Gaps[1].Width - 3.0) < 1e-9, "a click without drag gives the 3 m default");
        Check(ed.RemoveGap(1) && ed.CurrentFence.Gaps.Count == 1, "delete removes a gate from the list");
        ed.Revert();
        Check(ed.PendingCount == 0 && ed.CurrentFence.Gaps.Count == 0 && File.ReadAllText(editsPath) == Expect("t1"), "Revert discards the pending gates, file untouched");
        ed.RefreshNow();
        // Esc cancels
        Check(ed.ArmGate(), "arm again");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Frame(3);
        Check(!ed.GateArmed && ed.Selection.Contains(C), "Esc cancels the armed tool and keeps the selection");
        // the real gate
        ed.ArmGate();
        ed.GateBegin(offP1);
        ed.GateDrag(onP1 + new Vector3((float)(tangent[0].GetDouble() * 1.0), 0, (float)(-tangent[1].GetDouble() * 1.0)));
        ed.GateEnd();
        var gap = ed.CurrentFence.Gaps[0];
        Check(Math.Abs(gap.Width - 2.0) < 1e-9, $"gate of width 2.0 at ({gap.X:F3}, {gap.Y:F3}) in S-JTSK");
        await ApplyAndWait("t2");
        Check(!ProbeHits(p1) && ProbeHits(p2), "the gate cut a gap into the fence collider (probe 1 free, probe 2 still blocked)");
        Check(ed.GateMarkerCount == 1, "the saved gate stays drawn");

        // ---- 3. fence none: colliders go away through the watcher ----
        Check(ed.SetFenceMode("none") && !ed.CurrentFence.HasCustomKeys && ed.CurrentFence.Gaps.Count == 0,
            "switching to none clears the custom-only keys (also the gaps)");
        await Frame();
        ed.RefreshNow();
        Check(!ed.SetFenceHeight(1.0) && !ed.FenceFieldsEnabled, "custom-only fields are disabled and refused under none");
        Check(!ed.GateButtonEnabled && !ed.ArmGate(), "gates are not available under none");
        await ApplyAndWait("t3");
        FileIs("t3", "fence none");
        var shapes = _map.PropsShapes.GetValueOrDefault(C) ?? new();
        var dynColliders = _map.DynamicProps.TryGetValue(C, out var dyn) ? CountOfType<CollisionShape3D>(dyn) : 0;
        Check(!ProbeHits(p1) && !ProbeHits(p2) && dynColliders == 0 && shapes.All(s => s.Disabled),
            $"fence none removed the colliders (dynamic={dynColliders}, baked disabled={shapes.Count})");

        // ---- 4. trees: density, reroll ----
        Check(ed.SetTreesKind("density") && ed.SetTreesValue(20) && ed.Reroll() && ed.CurrentTrees.Seed == 1, "density 20 per 1000 m2, reroll -> seed offset 1");
        await ApplyAndWait("t4a");
        FileIs("t4a", "density + seed");
        var node = _map.DynamicProps.GetValueOrDefault(C);
        var hash1 = node == null ? 0UL : MeshHash(node);
        var hasTrees = node != null && HasMesh(node);
        Check(hasTrees, "the regenerated parcel has tree meshes");
        Check(ed.Reroll() && ed.CurrentTrees.Seed == 2, "Reroll again increments the seed offset");
        await ApplyAndWait("t4b");
        FileIs("t4b", "density + seed 2");
        var hash2 = _map.DynamicProps.TryGetValue(C, out var node2) ? MeshHash(node2) : 0UL;
        Check(hash1 != 0 && hash2 != 0 && hash1 != hash2, $"reroll changed the regenerated trees (mesh hash {hash1:x} -> {hash2:x})");
        Check(ed.SetTreesKind("count") && Math.Abs(ed.CurrentTrees.CountValue - 20 * plan.GetProperty("c_area").GetDouble() / 1000.0) <= 1 && ed.SetTreesValue(-5) && ed.CurrentTrees.CountValue == 0,
            "density -> count converts by the area; negative values are clamped to 0");
        ed.Revert();

        // ---- 5. reset removes the entries, the file is back to its original bytes ----
        Check(ed.ResetTrees() && ed.ResetFence(), "Reset to rules (trees) and Reset (fence)");
        await ApplyAndWait("t5");
        FileIs("seed", "reset");
        Check(File.ReadAllText(editsPath) == seedText, "reset: edits.json is byte for byte the original again (the parcel entry is gone)");

        // ---- 6. pending state across selection changes ----
        await Sel(C);
        ed.SetFenceMode("custom");
        await Sel(F);
        ed.SetFenceMode("none");
        await Frame();
        Check(ed.PendingCount == 2 && ed.PanelFooterText.Contains("2 unsaved") && ed.PanelDirtyShown && ed.PanelHeaderText == F,
            $"two pending targets, dirty on {F} ('{ed.PanelFooterText}')");
        await Sel(C);
        Check(ed.PanelDirtyShown && ed.CurrentFence.EffectiveMode == "custom" && File.ReadAllText(editsPath) == seedText,
            "back on the first parcel: its pending change was kept, file untouched");
        await Sel(A);
        Check(!ed.PanelDirtyShown && ed.PendingCount == 2, "an untouched target is not marked dirty");
        await Sel(C);
        ed.Revert();
        Check(ed.PendingCount == 1 && !ed.TargetDirty, "Revert discards only the current target's changes");
        ed.SetFenceMode("custom");
        var saves0 = ed.SaveCount;
        await ApplyAndWait("t6");
        Check(ed.SaveCount == saves0 + 1 && ed.PendingCount == 0, "Apply wrote both targets in one save");
        FileIs("t6", "two targets");
        await Sel(C); ed.ResetFence();
        await Sel(F); ed.ResetFence();
        await ApplyAndWait("t6r");
        FileIs("seed", "two targets reset");

        // ---- 7. multi-parcel apply ----
        await Sel(C, F);
        Check(ed.PanelHeaderText == "2 parcels" && ed.PanelTreesNote.Contains("applies to 2 parcels") && ed.Target is { IsGroup: false },
            $"multi selection: header '{ed.PanelHeaderText}', note '{ed.PanelTreesNote}'");
        Check(!ed.GateButtonEnabled, "no gates for several loose parcels");
        Check(ed.SetFenceMode("none") && ed.SetTreesKind("count") && ed.SetTreesValue(2), "fence none and 2 trees for both");
        await ApplyAndWait("t7");
        FileIs("t7", "multi-parcel apply");
        await Sel(F);
        Check(ed.CurrentTrees.Kind == "count" && ed.CurrentTrees.CountValue == 2 && ed.CurrentFence.EffectiveMode == "none", "each parcel now shows the shared settings");
        await Sel(C, F);
        ed.ResetTrees(); ed.ResetFence();
        await ApplyAndWait("t7r");
        FileIs("seed", "multi reset");

        // ---- reroll from the rules ----
        await Sel(F);
        Check(ed.Reroll() && ed.CurrentTrees.Kind == "density" && Math.Abs(ed.CurrentTrees.DensityValue - ed.RulesDensity) < 1e-9 && ed.CurrentTrees.Seed == 1,
            "Reroll from the rules switches to the rules' density with seed offset 1");
        ed.Revert();

        // ---- 8. property group ----
        await Sel(A, B);
        Check(ed.Target is { IsGroup: true } && ed.PanelHeaderText == $"{gid} (2 parcels)" && !ed.ScopeRowShown,
            $"group selection: header '{ed.PanelHeaderText}'");
        Check(ed.SetFenceMode("custom") && ed.SetFenceType("wire") && ed.SetFenceColour("#AABBCC") && ed.SetTreesKind("count") && ed.SetTreesValue(4),
            "custom wire fence, colour #AABBCC, 4 trees per parcel");
        Check(ed.PanelTreesNote.Contains("applies to 2 parcels"), $"trees note: '{ed.PanelTreesNote}'");
        Check(ed.ArmGate() && ed.GateBegin(Ground("group_gate_pt", plan.GetProperty("group_gate_n")[0].GetDouble(), plan.GetProperty("group_gate_n")[1].GetDouble())), "gate on the group's outer perimeter");
        var gp = Ground("group_gate_pt");
        var gt = plan.GetProperty("group_gate_t");
        ed.GateDrag(gp + new Vector3((float)(gt[0].GetDouble() * 1.5), 0, (float)(-gt[1].GetDouble() * 1.5)));
        Check(ed.GateEnd() && Math.Abs(ed.CurrentFence.Gaps[0].Width - 3.0) < 1e-9, "group gate, width 3.0");
        await ApplyAndWait("t8");
        Check(ed.SaveCount > 0, "group saved");
        // member parcels' fence entries must not exist; the group entry holds the fence
        var fd = EditsFile.Load(editsPath);
        Check(EditModel.ParcelEntry(fd, A)?.ContainsKey("fence") != true && EditModel.ParcelEntry(fd, B)?.ContainsKey("fence") != true
              && EditsFile.Groups(fd)[gid] is SortedDictionary<string, object?> ge && ge.ContainsKey("fence"),
            "group edit went to the group entry, not to the member parcels");
        // scope toggle: one member
        await Sel(A);
        Check(ed.ScopeRowShown && ed.Target is { IsGroup: true } && ed.GroupScope, "one member selected: scope defaults to the whole group");
        ed.SetGroupScope(false);
        await Frame();
        ed.RefreshNow();
        Check(ed.Target is { IsGroup: false } && ed.PanelHeaderText == A, "scope 'this parcel' targets the member itself");
        Check(ed.SetFenceMode("none"), "own fence override: none");
        await ApplyAndWait("t8b");
        Check(EditModel.ParcelEntry(EditsFile.Load(editsPath), A)?["fence"] is SortedDictionary<string, object?> af && af.Count == 1 && af["mode"] as string == "none",
            "the member's own fence override is written to the parcel entry");
        ed.ResetFence();
        await ApplyAndWait("t8c");
        ed.SetGroupScope(true);
        await Sel(A, B);
        ed.ResetFence(); ed.ResetTrees();
        await ApplyAndWait("t8d");
        FileIs("seed", "group reset");

        // ---- Enter applies when no text field has focus ----
        await Sel(F);
        ed.SetFenceMode("none");
        var n1 = ed.SaveCount;
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        await Frame(3);
        Check(ed.SaveCount == n1 + 1 && ed.PendingCount == 0, "Enter applies the pending changes");
        await WaitIdle(30);
        ed.ResetFence();
        await ApplyAndWait("t9");
        FileIs("seed", "back to the seed");

        Check(await WaitIdle(60) && _watcher!.FailedCount == 0, $"watcher settled, no failed parcel-run ({_watcher!.FailedCount})");
    }
}
