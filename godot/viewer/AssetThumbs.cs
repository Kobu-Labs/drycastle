using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Small renders of assets for the palette: each asset is put in an off-screen SubViewport with a fixed camera and light
/// and read back as a texture. Requests are rendered one after the other, one per frame, so the editor never stalls.
/// </summary>
public partial class AssetThumbs : Node
{
    public const int Pixels = 128;
    public Func<string, Node3D?> Source { get; init; } = _ => null;
    public event Action<string, Texture2D>? Ready;

    readonly Dictionary<string, Texture2D> _done = new();
    readonly Queue<string> _queue = new();
    SubViewport _vp = null!;
    Camera3D _cam = null!;
    Node3D _holder = null!;
    bool _running;

    public Texture2D? Get(string id) => _done.GetValueOrDefault(id);

    public override void _Ready()
    {
        _vp = new SubViewport
        {
            Size = new Vector2I(Pixels, Pixels), TransparentBg = true, OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Msaa3D = Viewport.Msaa.Msaa4X,
        };
        AddChild(_vp);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Canvas,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.8f, 0.85f, 0.95f), AmbientLightEnergy = 0.7f,
        };
        _vp.AddChild(new WorldEnvironment { Environment = env });
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, -35, 0), LightEnergy = 1.1f });
        _cam = new Camera3D { Fov = 28f, Near = 0.05f, Far = 500f };
        _vp.AddChild(_cam);
        _holder = new Node3D();
        _vp.AddChild(_holder);
    }

    public void Request(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (!_done.ContainsKey(id) && !_queue.Contains(id)) _queue.Enqueue(id);
        if (!_running) _ = Run();
    }

    async Task Run()
    {
        _running = true;
        try
        {
            while (_queue.Count > 0)
            {
                var id = _queue.Dequeue();
                var node = Source(id);
                if (node == null) continue;
                _holder.AddChild(node);
                Fit(node);
                _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var img = _vp.GetTexture().GetImage();
                _holder.RemoveChild(node);
                node.QueueFree();
                if (img == null) continue;
                var tex = ImageTexture.CreateFromImage(img);
                _done[id] = tex;
                Ready?.Invoke(id, tex);
            }
        }
        catch (Exception e) { GD.PushWarning($"asset thumbnails stopped: {e.Message}"); }
        finally { _running = false; }
    }

    static void Collect(Node n, ref Aabb box, ref bool any)
    {
        if (n is MeshInstance3D mi && mi.Mesh != null)
        {
            var b = mi.GlobalTransform * mi.GetAabb();
            box = any ? box.Merge(b) : b;
            any = true;
        }
        foreach (var c in n.GetChildren()) Collect(c, ref box, ref any);
    }

    void Fit(Node3D node)
    {
        var box = new Aabb(); var any = false;
        Collect(node, ref box, ref any);
        if (!any) box = new Aabb(new Vector3(-0.5f, 0, -0.5f), Vector3.One);
        var centre = box.GetCenter();
        var radius = Mathf.Max(box.Size.Length() * 0.5f, 0.05f);
        var dist = radius / Mathf.Sin(Mathf.DegToRad(_cam.Fov) / 2f) * 1.05f;
        var dir = new Vector3(0.62f, 0.5f, 0.78f).Normalized();
        _cam.Position = centre + dir * dist;
        _cam.LookAt(centre, Vector3.Up);
    }
}
