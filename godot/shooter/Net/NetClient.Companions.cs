using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Companions on the client (GLOSSARY "Companion", #488): the world entities of type <see cref="CompanionRules.TypeId"/>. Each is drawn as the model of its species' look
/// (companions.json names the species and the asset; animals.json the model) and plays the clip of the <see cref="CompanionPose"/> the server sends. The client knows no kind:
/// a new kind of Companion needs nothing here unless it needs a clip no pose names. A Companion appears and goes in a puff. The server owns it; the client only shows it.
/// </summary>
public partial class NetClient
{
    /// <summary>companions.json: set by the host (the index of a Companion in it is what the wire carries).</summary>
    public CompanionsFile CompanionData = new();

    sealed class CompanionView
    {
        public Node3D Node = null!;
        public AnimationPlayer? Anim;
        public string?[] Clips = Array.Empty<string?>();
        public float Height = 0.5f;
        /// <summary>The pose whose clip is showing; 255 = none yet.</summary>
        public byte Shown = 255;
    }

    readonly Dictionary<ushort, CompanionView> _companions = new();

    // ---- the picker of the Equipment screen: what the local player can pick and what is out (the server's word, EventKind.Companions) ----

    CompanionRoster _companionRoster = CompanionRoster.None;

    /// <summary>The roster changed (a pick was answered, a Companion was unlocked or given): the Equipment screen redraws its picker.</summary>
    public event Action? CompanionsChanged;

    /// <summary>The Companions the local player can pick (indices of companions.json, in its order). Stable between <see cref="CompanionsChanged"/> events.</summary>
    public IReadOnlyList<int> CompanionsOwned => _companionRoster.Owned;

    /// <summary>The Companion that is out (an index of companions.json), or -1.</summary>
    public int CompanionOut => _companionRoster.Out;

    /// <summary>The display name of a Companion: the translation "companion.&lt;id&gt;" when there is one, else the name in companions.json.</summary>
    public string CompanionName(int def)
    {
        if (def < 0 || def >= CompanionData.Companions.Count) return "";
        var entry = CompanionData.Companions[def];
        var k = "companion." + entry.Id;
        var t = Strings.T(k);
        return t == k ? entry.Name : t;
    }

    /// <summary>The picture of a Companion's species (the Hunter's Log's flat silhouette: white with alpha, tinted by the drawer); null when the species has none.</summary>
    public Texture2D? CompanionIcon(int def)
    {
        if (def < 0 || def >= CompanionData.Companions.Count) return null;
        var entry = CompanionData.Companions[def];
        // its own look's picture ("<species>/<variant>" in the shapes) when it is not the species' base model
        var look = entry.Asset != "" && entry.Asset != AnimalVariant.BaseAssetId ? HuntArtOf.ForSpecies($"{entry.Species}/{entry.Asset}").Picture : null;
        return (look ?? HuntArtOf.ForSpecies(entry.Species).Picture)?.Fill;
    }

    /// <summary>A wish: have this Companion out (an index of <see cref="CompanionsOwned"/>'s values). The server answers with the roster.</summary>
    public void RequestCompanion(int def) => SendItem(EventKind.CompanionPick, CompanionRoster.EncodePick(def));

    void OnCompanionEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.Companions || !CompanionRoster.TryDecode(payload, out var roster)) return;
        _companionRoster = roster;
        CompanionsChanged?.Invoke();
    }

    /// <summary>Called once from <see cref="_Ready"/>: registers the Companion type and follows its entities.</summary>
    void InitCompanions()
    {
        CompanionRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnCompanionSpawned;
        WorldEntities.DespawnedFor += OnCompanionDespawnedFor;
        WorldEntities.Despawned += OnCompanionDespawned;
        WorldEntities.DormancyChanged += e => { if (e.Type == CompanionRules.TypeId && _companions.TryGetValue(e.Id, out var v)) SetCompanionDormant(v, e); };
    }

    /// <summary>One outside the interest range (ADR 0025) is hidden and its animation stops; one that comes back starts its clip afresh.</summary>
    static void SetCompanionDormant(CompanionView v, WorldReplica.Entity e)
    {
        v.Node.Visible = !e.Dormant;
        v.Node.ProcessMode = e.Dormant ? Node.ProcessModeEnum.Disabled : Node.ProcessModeEnum.Inherit;
        if (!e.Dormant) v.Shown = 255;
    }

    void OnCompanionSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CompanionRules.TypeId) return;
        var index = e.State.Owner;
        if (index >= CompanionData.Companions.Count || AnimalData.Find(CompanionData.Companions[index].Species) is not { } def)
        {
            GD.PrintErr($"client: companion {e.Id} is entry {index} of companions.json, which has {CompanionData.Companions.Count}; not drawn (data files differ from the server's?)");
            return;
        }
        var entry = CompanionData.Companions[index];
        var variant = Math.Max(0, entry.VariantIn(def));
        var box = def.HitBoxOf(variant).Select(x => x * entry.Scale).ToArray();
        var node = new Node3D { Name = $"Companion{e.Id}" };
        var (_, anim, clips) = AddAnimalModel(node, def, variant, entry.Scale, box);
        Owned.AddChild(node);
        var at = e.State.Position;
        node.GlobalPosition = new Vector3(at.X, at.Y, at.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
        var view = new CompanionView { Node = node, Anim = anim, Clips = clips, Height = box.Length == 3 ? box[1] : 0.5f };
        _companions[e.Id] = view;
        if (e.Dormant) SetCompanionDormant(view, e);
        else PetFx.Puff(Owned, node.GlobalPosition + Vector3.Up * (view.Height / 2), view.Height); // it appears in a puff, as it goes
    }

    /// <summary>A Companion whose owner died or took a seat goes in a puff; one removed with the round just leaves. Runs before <see cref="OnCompanionDespawned"/> frees the node.</summary>
    void OnCompanionDespawnedFor(ushort id, WorldDespawnReason reason)
    {
        if (reason != WorldDespawnReason.Destroyed || !_companions.TryGetValue(id, out var v) || !v.Node.Visible) return;
        PetFx.Puff(Owned, v.Node.GlobalPosition + Vector3.Up * (v.Height / 2), v.Height);
    }

    void OnCompanionDespawned(ushort id)
    {
        if (_companions.Remove(id, out var v)) v.Node.QueueFree();
    }

    /// <summary>Places the Companions (interpolated, a little in the past like every remote body) and plays the clip of their pose.</summary>
    void RenderCompanions()
    {
        if (_companions.Count == 0 || _lastTick == 0) return;
        var rt = RenderTickNow();
        foreach (var (id, v) in _companions)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant) continue;
            if (e.Buf.At(rt) is { } s)
            {
                v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                v.Node.Rotation = new Vector3(s.Pitch, s.Yaw, 0);
            }
            var pose = e.State.State;
            if (v.Shown != pose) ShowCompanionPose(v, CompanionRules.PoseOf(pose));
        }
    }

    /// <summary>The clip of a pose. Sitting is sit-down then the held pose (one already sitting when first seen just sits), and standing up plays before what follows.</summary>
    static void ShowCompanionPose(CompanionView v, CompanionPose pose)
    {
        var was = v.Shown;
        v.Shown = (byte)pose;
        if (v.Anim == null || v.Clips.Length == 0) return;
        string? Clip(AnimalClip c) => v.Clips[(int)c];
        if (pose == CompanionPose.Sit)
        {
            if (Clip(AnimalClip.SitIdle) is not { } hold) return;
            if (was != 255 && Clip(AnimalClip.SitStart) is { } down) { v.Anim.Play(down, 0.2); v.Anim.Queue(hold); }
            else v.Anim.Play(hold, 0.2);
            return;
        }
        var clip = pose switch
        {
            CompanionPose.Walk => Clip(AnimalClip.Walk),
            CompanionPose.Run => Clip(AnimalClip.Run) ?? Clip(AnimalClip.Walk),
            CompanionPose.Fly => Clip(AnimalClip.Cruise) ?? Clip(AnimalClip.Idle),
            _ => Clip(AnimalClip.Idle), // Idle, and a flyer's Hover
        };
        if (clip == null) return;
        if (was == (byte)CompanionPose.Sit && Clip(AnimalClip.SitUp) is { } up) { v.Anim.Play(up, 0.2); v.Anim.Queue(clip); return; }
        v.Anim.Play(clip, 0.2);
    }
}
