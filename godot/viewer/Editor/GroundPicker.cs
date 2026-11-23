using Drycastle.MapLoader;
using Godot;
using System;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor;

/// <summary>Points of the ground and the screen: the ground under a ray or the mouse, the parcel there, the nearest corner of a ring, and the ground segments of a ring.</summary>
public sealed class GroundPicker
{
    const float CornerPickPx = EditorTheme.Space * 2.5f;

    readonly IEditorHost _host;
    // the last ground ray: reused within the same process frame while the map's physics has not changed
    ulong _rayFrame = ulong.MaxValue;
    int _rayVersion = -1;
    Vector3 _rayOrigin, _rayDir, _rayPoint;
    bool _rayHit;

    public GroundPicker(IEditorHost host) => _host = host;

    /// <summary>
    /// Ground point under a ray: the first crossing of the terrain height (ray march, buildings and props are
    /// ignored), else a horizontal plane at the mean terrain height. False when the ray hits none.
    /// </summary>
    public bool RayToGround(Vector3 origin, Vector3 dir, out Vector3 point)
    {
        // hover, ghost, gate drag and the shape tools cast the same mouse ray several times a frame
        var frame = Engine.GetProcessFrames();
        var map = _host.Map;
        if (frame == _rayFrame && _rayVersion == map.GeometryVersion && origin == _rayOrigin && dir == _rayDir)
        {
            point = _rayPoint;
            return _rayHit;
        }
        var hit = CastGroundRay(origin, dir, out point);
        (_rayFrame, _rayVersion, _rayOrigin, _rayDir, _rayPoint, _rayHit) = (frame, map.GeometryVersion, origin, dir, point, hit);
        return hit;
    }

    bool CastGroundRay(Vector3 origin, Vector3 dir, out Vector3 point)
    {
        var map = _host.Map;
        // march the ray against the terrain height (roofs, eaves, fence tops and props must not count as ground)
        var prevT = 0f;
        var h0 = map.GroundHeight(origin.X, origin.Z);
        var above = float.IsNaN(h0) || origin.Y > h0;
        for (var t = 1f; t <= 3000f; t += Mathf.Max(1f, t * 0.01f))
        {
            var p = origin + dir * t;
            var h = map.GroundHeight(p.X, p.Z);
            if (float.IsNaN(h)) { above = true; prevT = t; continue; }
            if (p.Y > h) { above = true; prevT = t; continue; }
            if (!above) { prevT = t; continue; } // started below the terrain: wait until the ray comes out of it
            var lo = prevT; var hi = t; // above at lo, below at hi
            for (var i = 0; i < 12; i++)
            {
                var mid = (lo + hi) / 2;
                var m = origin + dir * mid;
                var mh = map.GroundHeight(m.X, m.Z);
                if (!float.IsNaN(mh) && m.Y <= mh) hi = mid; else lo = mid;
            }
            point = origin + dir * hi;
            return true;
        }
        var plane = map.MeanTerrainHeight;
        if (dir.Y < -1e-4f)
        {
            var t = (plane - origin.Y) / dir.Y;
            if (t > 0) { point = origin + dir * t; return true; }
        }
        point = default;
        return false;
    }

    /// <summary>Parcel under a screen position of the main viewport (null: none); point is the ground point found.</summary>
    public ParcelInfo? PickScreen(Vector2 screen, out Vector3 point)
        => RayToGround(_host.FlyCam.ProjectRayOrigin(screen), _host.FlyCam.ProjectRayNormal(screen), out point) ? _host.Map.ParcelAt(point.X, point.Z) : null;

    /// <summary>Screen position (px) to the nearest corner of a ring (absolute metres); -1 when none is within reach.</summary>
    public int PickCorner(Vector2 screen, IReadOnlyList<(double x, double y)> ring)
    {
        var cam = _host.FlyCam;
        var best = -1; var bestD = CornerPickPx * (float)Math.Max(1.0, _host.GetViewport().GetVisibleRect().Size.X / 1280.0);
        for (var i = 0; i < ring.Count; i++)
        {
            var w = GroundPoint(ring[i]);
            if (cam.IsPositionBehind(w)) continue;
            var d = cam.UnprojectPosition(w).DistanceTo(screen);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>An absolute-metres point on the terrain, a little above it.</summary>
    public Vector3 GroundPoint((double x, double y) p)
    {
        var map = _host.Map;
        var l = map.FromAbsolute(p.x, p.y);
        return new Vector3(l.X, map.GroundHeightOrMean(l.X, l.Y) + 0.3f, l.Y);
    }

    /// <summary>The ground segments of a ring or a line (absolute metres).</summary>
    public List<(Vector2, Vector2)> RingSegs(IReadOnlyList<(double x, double y)> pts, bool closed)
    {
        var map = _host.Map;
        var s = new List<(Vector2, Vector2)>();
        var n = closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = map.FromAbsolute(pts[i].x, pts[i].y); var b = map.FromAbsolute(pts[(i + 1) % pts.Count].x, pts[(i + 1) % pts.Count].y);
            if (a.DistanceSquaredTo(b) > 1e-8f) s.Add((a, b));
        }
        return s;
    }
}
