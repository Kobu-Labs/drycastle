using Drycastle.ShooterCore.Colliders;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>What one sweep of a run-over mover needs: where it was and is, its box, how hard it hits, and who is credited.</summary>
internal readonly record struct RunOverSweep(MoverKind Mover, NVec3 Prev, NVec3 Now, float Yaw, float HalfWidth, float HalfLength, NVec3 Forward, float Speed, float Damage, ushort Attacker, Dictionary<ushort, double> Cooldown);
