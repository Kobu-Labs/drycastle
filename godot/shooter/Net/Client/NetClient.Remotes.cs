using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Bunkers;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Glass;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Scenery;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The other players as drawn: made on their first snapshot, placed about 100 ms in the past from their interpolation buffers, tumbling when thrown, animated from their motion.</summary>
public partial class NetClient
{
    RemotePlayer MakeRemote(ushort id, bool target = false)
    {
        var node = new Node3D { Name = $"Remote{id}" };
        var view = CharacterView.Make();
        Camo.Dress(view, id); // the look may arrive before the first snapshot (#140)
        DressPlayer(view, id); // what they wear (#266), likewise
        node.AddChild(view);
        node.AddChild(new Label3D
        {
            Text = Ctx.Names.TryGetValue(id, out var n) ? Sandbox.NameTag(id, n) : $"#{id}", // [SANDBOX] tag for sandbox players (#156)
            Modulate = target ? TargetTint : Colors.White, // orange name tag for target dummies (#148)
            Position = new Vector3(0, MoveConsts.CapsuleHeight + 0.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.005f, NoDepthTest = true,
        });
        World.AddChild(node);
        return new RemotePlayer { Node = node, View = view, Binoculars = _binocularsBy.GetValueOrDefault(id) };
    }

    /// <summary>The fractional server tick the remote entities are drawn at this frame (see <see cref="RenderClock"/>).</summary>
    double RenderTickNow() => Ctx.RenderTick;

    void RenderRemotes()
    {
        if (Ctx.LastTick == 0) return;
        var rt = RenderTickNow();
        var dt = (float)GetProcessDeltaTime();
        var drawn = 0;
        foreach (var (id, r) in Ctx.Remotes)
            if (r.Buf.At(rt) is { } s)
            {
                drawn++;
                var at = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                var dead = r.State == EntityState.Dead;
                at = GuessRunOver(id, r, at, dead, dt);
                var guessed = r.Guess.Active; // the body the driver's vehicle has just hit: thrown on the guessed arc, ahead of the server
                r.GuessRagdoll = guessed && (r.GuessRagdoll || dead); // the corpse's ragdoll only once the server says dead; before that the thrown body tumbles as one that may get up
                r.ShownYaw = s.Yaw; r.ShownPitch = s.Pitch;
                if (dead) EndFling(id);
                TumbleRemote(id, r, at, s.Yaw, dead && TargetDummies.IsDummyId(id) || r.GuessRagdoll, dt);
                PounceLieRemote(id, r, at); // the one the demon has pinned lies on its back
                // a killed player's thrown corpse is its own entity: the body standing at the death spot would be a ghost behind it
                r.Node.Visible = guessed || !(dead && !TargetDummies.IsDummyId(id) && (CorpseOf(id) || HarvestVictimHidden(id, dead))); // the harvester's victim is in the machine, then inside its bale: no body at the spot where it died // riders are drawn in their seat (the server puts the body there), seated rather than standing
                r.View?.SetStance(r.State == EntityState.InVehicle ? (r.RideStanding ? Stance.Stand : Stance.Crouch) : r.Stance); // a rider sits; the gunner in a ring stands
                if (r.State == EntityState.InVehicle) _lastRode[id] = Ctx.Now;
                AnimateRemote(r, s.Pos);
                UpdateNameTag(id, r);
            }
        _rub.AddRender(rt, Ctx.LastTick, drawn);
    }

    /// <summary>
    /// The driver's picture of a run-over (#411, as <see cref="ThrowGuess"/> does for animals): the vehicle this player drives is drawn ahead of the remote bodies (about the
    /// interpolation delay and a round trip), so a body it hits is seen inside the hull until the server's throw arrives. The moment the predicted vehicle's box touches a living
    /// body as drawn, at any speed that moves a body, the body is shown thrown on the arc the server will give it. The server throws every player a vehicle touches, killed or
    /// not, and one who lives through a hit that hurts is drawn tumbling by everybody (PlayerFlung, <see cref="BodyFlingTracker"/>): so a hit that hurts tumbles here at once,
    /// which is right whether the server then says dead (the corpse's ragdoll takes over in flight) or alive (it lands, lies and gets up). A slow push is an upright shove.
    /// Only a touch the server did not see (a graze, within the lag) is taken back: the body glides to where the server has it. Returns where the body is drawn.
    /// </summary>
    Vector3 GuessRunOver(ushort id, RemotePlayer r, Vector3 at, bool dead, float dt)
    {
        var g = r.Guess;
        var feet = new NVec3(at.X, at.Y, at.Z);
        if (g.Ready && r.State == EntityState.Alive && Vehicles != null && ThrowGuessCar() is { } c
            && RunOverFootprint.OfCapsule(feet).Hits(c.State.Position, c.State.Position, c.State.Yaw, c.Def.HalfWidth, c.Def.HalfLength))
        {
            var launch = CorpseRules.ThrowFromCar(new NVec3(-MathF.Sin(c.State.Yaw), 0, -MathF.Cos(c.State.Yaw)), c.State.Speed);
            if (g.Start(feet, launch))
            {
                r.GuessLaunch = new NVec3(launch.X, 0, launch.Z).Length(); r.GuessConfirmed = false;
                r.GuessFlung = RunOver.Damage(Vehicles, c.State.Speed, Vehicles.RunOverScale(c.Def)) > 0; // a hit that hurts knocks the body off its feet
                if (r.GuessFlung) FlingOf(id).Thrown();
            }
        }
        // the server's own throw of a living body: its event says it threw, its speed says when its body has come down; the guessed arc is kept until both have, as for a corpse
        var moved = at - r.GuessServerLast; moved.Y = 0; r.GuessServerLast = at;
        var flies = !dead && r.GuessConfirmed && dt > 0 && ThrowGuess.ServerStillFlies(moved.Length() / dt, r.GuessLaunch);
        var was = g.Phase;
        var p = g.Step(dt, feet, dead || r.GuessConfirmed, flies, g.Active ? GroundUnder(g.Position) : null);
        if (was == ThrowGuessPhase.Flying && g.Phase == ThrowGuessPhase.Settling && r.GuessFlung && !dead) EndFling(id); // the server saw no hit: the tumble is taken back with the arc
        if (!g.Active) r.GuessFlung = false;
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>The server's word that a vehicle threw a player alive (<see cref="EventKind.PlayerFlung"/>, #411): the body tumbles and gets up, on every client; the driver's own guess of it is confirmed.</summary>
    void OnPlayerFlung(ushort id)
    {
        FlingOf(id).Thrown();
        if (Ctx.Remotes.TryGetValue(id, out var r) && r.Guess.Active) r.GuessConfirmed = true;
    }

    /// <summary>
    /// The ragdoll of a killed dummy: while it is thrown (dead and moving fast) it spins about its middle with its limbs flailing, then lies flat with
    /// them loose. A dummy that fell where it stood plays the death clip; everything else stands upright at its feet. Corpses do the same in <see cref="RenderCorpses"/>.
    /// </summary>
    void TumbleRemote(ushort id, RemotePlayer r, Vector3 at, float yaw, bool dummyDown, float dt)
    {
        var t = r.Tumble;
        if (_flings.TryGetValue(id, out var fling) && fling.Active && !dummyDown) { FlingRemote(id, r, fling, at, yaw, dt); return; } // in the Bigfoot's fist or flying from it (#360)
        if (!dummyDown) { if (t.Thrown) { t.Reset(); r.View?.SetRagdoll(false); } }
        else
        {
            var speed = dt > 0 ? (at - r.TumbleLast).Length() / dt : 0;
            t.Update(at, speed > 2.5f && speed < 60f, dt);
            if (t.Thrown) r.View?.SetRagdoll(true, t.Amount, t.AtRest);
        }
        r.TumbleLast = at;
        if (r.View != null) r.View.Position = t.Thrown ? new Vector3(0, -RagdollTumble.Half, 0) : Vector3.Zero;
        r.Node.GlobalPosition = t.Thrown ? at + Vector3.Up * t.PivotHeight : at;
        r.Node.Basis = t.Thrown ? t.BasisFor(yaw) : new Basis(Vector3.Up, yaw);
    }

    /// <summary>True while a corpse of player <paramref name="owner"/> is on the map.</summary>
    bool CorpseOf(ushort owner)
    {
        foreach (var id in _corpses.Keys)
            if (WorldEntities.Entities.TryGetValue(id, out var e) && e.State.Owner == owner) return true;
        return false;
    }

    /// <summary>Drives the remote character's animation from snapshot state: horizontal speed of the interpolated position, a recent shot event, and the dead state.</summary>
    void AnimateRemote(RemotePlayer r, NVec3 pos)
    {
        var now = Ctx.Now;
        var p = new Vector3(pos.X, pos.Y, pos.Z);
        if (r.LastT > 0 && now > r.LastT + 0.001)
        {
            var d = p - r.LastPos; d.Y = 0;
            var sp = d.Length() / (float)(now - r.LastT);
            if (sp > 30f) sp = 0; // a teleport (respawn), not running
            // a rider sits: the seat moving (the car driving, a flak turret turning the gunner about its ring) is not the body walking
            if (r.State == EntityState.InVehicle) sp = 0;
            r.Speed = Mathf.Lerp(r.Speed, sp, 0.3f);
            if (r.State != EntityState.InVehicle) SfxRemoteStep(r, p, (float)(now - r.LastT));
        }
        r.LastPos = p; r.LastT = now;
        var weaponId = Weapons != null && r.Slot >= 1 && r.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[r.Slot - 1].Id : "";
        r.View?.SetBinoculars(r.Binoculars);
        r.View?.Apply(r.Speed, now < r.FireUntil, r.State == EntityState.Dead || r.GuessRagdoll, weaponId);
    }
}
