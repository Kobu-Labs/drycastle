using Godot;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>The flat test arena the weapon, rocket and vehicle smokes play in, far from the map: a floor and a wall, four spawns.</summary>
public static class SmokeArena
{
    public const float X = 20000f, Z = 20000f;

    /// <summary>Flat floor (top at y 0) and a 14 x 4 m wall in the middle of nowhere, so the weapons smoke does not depend on the map layout.</summary>
    public static void Build(ServerConfig config)
    {
        config.KillY = -1000f; // the arena lies far below the map spawns the host derived killY from
        AddColliders(config.World);
        config.Spawns = new List<System.Numerics.Vector3>
        {
            new(X - 3, 0, Z + 5), new(X + 3, 0, Z + 5), new(X - 3, 0, Z + 9), new(X + 3, 0, Z + 9),
        };
    }

    /// <summary>The arena geometry; the weapons smoke clients build the same so their prediction stands on it too.</summary>
    public static void AddColliders(Node3D world)
    {
        void Box(string name, Vector3 center, Vector3 size)
        {
            var b = new StaticBody3D { Name = name, Position = center };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            world.AddChild(b);
        }
        Box("ArenaFloor", new Vector3(X, -1, Z), new Vector3(400, 2, 400));
        Box("ArenaWall", new Vector3(X + 20, 2, Z - 3), new Vector3(14, 4, 0.5f));
    }
}
