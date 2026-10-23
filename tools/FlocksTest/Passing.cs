using System;
using System.Collections.Generic;
using System.Linq;
using Flocks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FlocksTest
{
    /// <summary>
    /// Passing flocks (SkyFlocks): a flock sent over a stand-in player turns into real birds as it comes within
    /// SkyFlocks.RealRange, flies on in formation, loses a bird to a shot (the bird falls and stays; the flock climbs and
    /// flies on a bird short), goes back to being drawn beyond DrawnRange, and leaves the sky at the end of its way. A
    /// passing bird no flock owns stays as an ordinary bird if it was spawned (let out of a cage), and is removed if it
    /// was loaded from a save.
    /// </summary>
    public partial class FlocksTestSystem
    {
        /// <summary>Bodies the harness steps physics for although dead (a shot bird falling).</summary>
        private readonly List<Entity> bodies = new();

        private void QueuePassingSteps()
        {
            Entity viewer = null;
            Flock f = null;
            var sp = sapi.ModLoader.GetModSystem<FlocksModSystem>().SpeciesByCode;
            List<long> realIds = null;
            double maxOff = 0, sumOff = 0; int offSamples = 0, hits = 0;
            Entity shot = null; Vec3d shotAt = null; double cruiseBefore = 0; int birdsBefore = 0;
            List<Entity> Real() => f == null ? new List<Entity>() : f.Birds.Where(b => b.EntityId != 0)
                .Select(b => sapi.World.GetEntityById(b.EntityId)).Where(e => e != null && e.Alive).ToList();
            void Physics(IEnumerable<Entity> es) { foreach (var e in es) if (!extraPhysics.Contains(e)) extraPhysics.Add(e); }

            steps.Enqueue(("passing flock: real near a player", () =>
            {
                // a stand-in player in the middle of the slab; the geese come from 450 blocks north, heading south
                viewer = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "chicken-hen")));
                viewer.Pos.SetPos(c.X, y0 + 1, c.Z);
                sapi.World.SpawnEntity(viewer);
                EntityBehaviorBird.TestPlayers.Add(viewer);
                f = SkyFlocks.Instance.Launch(sp["goose"], 9, viewer.Pos.XYZ, 0, 40, 450);
                Log($"passing flock #{f.Id}: {f.Birds.Count} geese from {f.Pos.Z - c.Z:0} blocks north at {f.Pos.Y - y0:0} over the slab");
            }, () => { Physics(Real()); return f.Birds.All(b => b.EntityId != 0); }, () =>
            {
                double d = Math.Sqrt(f.Centre.HorizontalSquareDistanceTo(c.X, c.Z));
                var real = Real();
                realIds = real.Select(e => e.EntityId).ToList();
                Check(real.Count == 9 && real.All(e => e.GetBehavior<EntityBehaviorBird>()?.Passing == true),
                    $"all 9 geese real, passing birds ({real.Count}), the flock {d:0} blocks out (real within {SkyFlocks.RealRange:0})");
                Check(d <= SkyFlocks.RealRange + 5 && d > SkyFlocks.RealRange - 40, $"they turned real as it came within range ({d:0})");
                double worst = f.Birds.Max(b => sapi.World.GetEntityById(b.EntityId).Pos.DistanceTo(f.SlotInWorld(b.Offset)));
                Check(worst < 3, $"each appeared at its slot and keeps to it (furthest {worst:0.0} off, up to half a second later)");
            }, 60));

            steps.Enqueue(("passing flock: holds formation", () => { maxOff = sumOff = 0; offSamples = hits = 0; }, () =>
            {
                Physics(Real());
                foreach (var b in f.Birds)
                {
                    var e = b.EntityId == 0 ? null : sapi.World.GetEntityById(b.EntityId);
                    if (e == null) continue;
                    double off = e.Pos.DistanceTo(f.SlotInWorld(b.Offset));
                    maxOff = Math.Max(maxOff, off); sumOff += off; offSamples++;
                    if (e.CollidedHorizontally) hits++;
                }
                return Elapsed > 8;
            }, () =>
            {
                Check(offSamples > 0 && sumOff / offSamples < 2.5 && maxOff < 8, $"in their slots: {sumOff / Math.Max(1, offSamples):0.0} blocks off on average, at most {maxOff:0.0}");
                Check(hits == 0, $"nothing hit ({hits})");
                Check(Real().Count == 9, $"still 9 real geese ({Real().Count})");
            }, 30));

            // the game runs physics only for birds a player's client tracks; untracked ones move themselves
            steps.Enqueue(("passing flock: moves itself untracked", () =>
            {
                maxOff = sumOff = 0; offSamples = 0;
                EntityBehaviorBird.MoveUntracked = true;
                extraPhysics.RemoveAll(e => e.GetBehavior<EntityBehaviorBird>()?.Passing == true);
            }, () =>
            {
                foreach (var b in f.Birds)
                {
                    var e = b.EntityId == 0 ? null : sapi.World.GetEntityById(b.EntityId);
                    if (e == null) continue;
                    double off = e.Pos.DistanceTo(f.SlotInWorld(b.Offset));
                    maxOff = Math.Max(maxOff, off); sumOff += off; offSamples++;
                }
                return Elapsed > 5;
            }, () =>
            {
                EntityBehaviorBird.MoveUntracked = false;
                Physics(Real());
                Check(offSamples > 0 && sumOff / offSamples < 2.5 && maxOff < 8, $"with no physics run for them they keep up: {sumOff / Math.Max(1, offSamples):0.0} blocks off their slots on average, at most {maxOff:0.0}");
            }, 20));

            steps.Enqueue(("passing flock: a kill", () =>
            {
                shot = Real()[0];
                shotAt = shot.Pos.XYZ;
                cruiseBefore = f.CruiseY; birdsBefore = f.Birds.Count;
                var arrow = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "arrow-flint")));
                arrow.Pos.SetPos(shot.Pos.X, shot.Pos.Y - 1, shot.Pos.Z);
                sapi.World.SpawnEntity(arrow);
                shot.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = arrow, CauseEntity = viewer, Type = EnumDamageType.PiercingAttack }, 20);
                sapi.Event.RegisterCallback(_ => arrow.Die(EnumDespawnReason.Expire), 500);
                bodies.Add(shot);
            }, () => { Physics(Real()); return Elapsed > 6; }, () =>
            {
                Check(!shot.Alive && shot.State != EnumEntityState.Despawned, $"the goose shot is dead and stays (state {shot.State})");
                Check(f.Birds.Count == birdsBefore - 1 && f.Birds.All(b => b.EntityId != shot.EntityId), $"it left the flock: {f.Birds.Count} of {birdsBefore} fly on");
                Check(Real().Count == birdsBefore - 1, $"the rest are still real birds ({Real().Count})");
                Check(f.CruiseY >= cruiseBefore + 9, $"the flock climbed after the shot (cruise {cruiseBefore - y0:0} -> {f.CruiseY - y0:0} over the slab)");
                Check(shotAt.Y - shot.Pos.Y > 5, $"the dead goose fell ({shotAt.Y - shot.Pos.Y:0.0} blocks in 6 s)");
                Check(shot.SidedProperties.Behaviors.Any(b => b.PropertyName() == "harvestable"), "and can be harvested like any kill");
            }, 30));

            steps.Enqueue(("passing flock: drawn again further out", () => { }, () =>
            {
                Physics(Real());
                return f.Birds.All(b => b.EntityId == 0);
            }, () =>
            {
                double d = Math.Sqrt(f.Centre.HorizontalSquareDistanceTo(c.X, c.Z));
                Check(SkyFlocks.Instance.Get(f.Id) == f && f.Birds.Count == birdsBefore - 1, $"still in the sky, a bird short ({f.Birds.Count}), {d:0} blocks out");
                Check(realIds.All(id => sapi.World.GetEntityById(id) is not Entity e || !e.Alive), "its real birds are gone (the dead one excepted, it stays)");
                Check(shot.State != EnumEntityState.Despawned, "the dead goose is still where it fell");
            }, 120));

            steps.Enqueue(("passing flock: leaves the sky", () => { }, () => SkyFlocks.Instance.Get(f.Id) == null, () =>
            {
                Check(true, $"flock #{f.Id} has gone ({f.Travel:0} of {f.Length:0} blocks flown)");
                EntityBehaviorBird.TestPlayers.Remove(viewer);
                viewer.Die(EnumDespawnReason.Expire);
                bodies.Remove(shot); shot.Die(EnumDespawnReason.Expire);
                extraPhysics.RemoveAll(e => !e.Alive);
            }, 120));

            Entity caged = null, saved = null;
            steps.Enqueue(("passing bird without a flock", () =>
            {
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "mallard"));
                Entity Make(double dx)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    var t = new TreeAttribute(); t.SetLong("flock", 999999);
                    e.Attributes[EntityBehaviorBird.PassageKey] = t;
                    e.Pos.SetPos(c.X - 40 + dx, y0 + 0.9, c.Z);   // on pond A
                    return e;
                }
                // let out of a cage: spawned with the mark of a flock that is gone
                caged = Make(0); sapi.World.SpawnEntity(caged);
                // loaded from a save: the same, with the load hook run before its first tick
                saved = Make(4); sapi.World.SpawnEntity(saved);
                saved.GetBehavior<EntityBehaviorBird>().OnEntityLoaded();
            }, () => Elapsed > 2, () =>
            {
                Check(caged.Alive && !caged.Attributes.HasAttribute(EntityBehaviorBird.PassageKey) && caged.GetBehavior<EntityBehaviorBird>()?.Passing == false,
                    "a passing bird let out of a cage stays, as an ordinary bird");
                Check(!saved.Alive, "a passing bird loaded from a save (its flock long gone) is removed");
                caged.Die(EnumDespawnReason.Expire);
            }, 10));
        }
    }
}
