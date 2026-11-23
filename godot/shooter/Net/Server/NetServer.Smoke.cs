using Godot;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using Drycastle.ShooterServer.Persistence;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>The smoke tests' side of the server: the flat test arena the weapon, rocket and vehicle smokes play in, and the verdict that ends a run.</summary>
public partial class NetServer
{
    public bool RpgSmoke;
    /// <summary>Car smoke: flat arena, three cars at fixed spots, debug commands, judges the vehicle scenario.</summary>
    public bool CarSmoke;
    /// <summary>Blast smoke (ticket #103): flat arena with three cars (two side by side, one far), judges vehicle damage, the blast and the chain reaction.</summary>
    public bool BlastSmoke;
    bool ArenaMode => WeaponsSmoke || RpgSmoke || CarSmoke || BlastSmoke;
    public const float ArenaX = 20000f, ArenaZ = 20000f;

    /// <summary>A test run ends the server with its verdict, when its deadline or its own finish time has come.</summary>
    void FinishSmokeIfDue()
    {
        var quit = QuitAfter > 0 && _elapsed >= QuitAfter;
        var due = _smoke.FinishAt > 0 && _elapsed >= _smoke.FinishAt || quit;
        if (RpgSmoke) { if (due) EndSmoke(_smoke.Rpg()); }
        else if (BlastSmoke) { if (due) FinishBlast(); }
        else if (CarSmoke) { if (due) FinishCar(); }
        else if (WeaponsSmoke) { if (due) EndSmoke(_smoke.Weapons()); }
        else if (MatchSmoke) { if (due) EndSmoke(_smoke.Match(_match, MatchConfig)); }
        else if (LossTest > 0) { if (quit) LossEnd(false); }
        else if (quit) EndSmoke(_smoke.Net());
    }

    /// <summary>Flat floor (top at y 0) and a 14 x 4 m wall in the middle of nowhere, so the weapons smoke does not depend on the map layout.</summary>
    void BuildArena()
    {
        KillY = -1000f; // the arena lies far below the map spawns the host derived killY from
        AddArenaColliders(World);
        Spawns = new List<System.Numerics.Vector3>
        {
            new(ArenaX - 3, 0, ArenaZ + 5), new(ArenaX + 3, 0, ArenaZ + 5), new(ArenaX - 3, 0, ArenaZ + 9), new(ArenaX + 3, 0, ArenaZ + 9),
        };
    }

    /// <summary>The arena geometry; the weapons smoke clients build the same so their prediction stands on it too.</summary>
    public static void AddArenaColliders(Node3D world)
    {
        void Box(string name, Vector3 center, Vector3 size)
        {
            var b = new StaticBody3D { Name = name, Position = center };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            world.AddChild(b);
        }
        Box("ArenaFloor", new Vector3(ArenaX, -1, ArenaZ), new Vector3(400, 2, 400));
        Box("ArenaWall", new Vector3(ArenaX + 20, 2, ArenaZ - 3), new Vector3(14, 4, 0.5f));
    }

    /// <summary>Ends the server with the smoke test's verdict.</summary>
    void EndSmoke(bool ok)
    {
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }
}
