using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Flocks;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace FlocksTest
{
    /// <summary>
    /// Lag and memory checks. Every Flocks server tick (the four bird behaviours' OnGameTick, the landing-flock
    /// scheduler, the passing flocks and their schedule) is timed through Harmony, so the cost shows up as CPU time rather than hiding inside the server's 33 ms
    /// tick budget; each step logs a [perf] line with the total, the cost per server tick and the worst single call.
    /// The server's own tick gaps are watched too (spikes from any source, world generation included). Then a load step
    /// (about 80 birds of four kinds, flushed every 10 s) and spawn / kill cycles that watch managed memory and the mod's
    /// static caches for growth.
    /// </summary>
    public partial class FlocksTestSystem
    {
        // ------------------------------------------------------------------------------------------------ profiler

        private class Cost { public long Calls, Ticks, MaxTicks, Bytes; public string MaxWho; }
        private static readonly Dictionary<string, Cost> stepCost = new(), runCost = new();
        private static readonly object costLock = new();
        private static long stepTickGapMax; private static double stepServerTicks;
        private static int stepGen2 = GC.CollectionCount(2), stepGen0 = GC.CollectionCount(0);
        private long lastTickStamp;
        private static string worstRunCall = "-"; private static double worstRunMs;
        private static int mainThread;
        /// <summary>Flocks time on the main thread since the last server tick, and the biggest single call in it.</summary>
        private static long sinceTick, sinceTickMax, sinceTickInner; private static string sinceTickMaxWho, sinceTickInnerWho;
        private static int spikes;
        private Harmony harmony;
        private static ILogger perfLog;

        /// <summary>Timed methods that run inside another timed one (a bird tick, a spawn check): listed with a ~ and left out
        /// of the totals, so nothing is counted twice.</summary>
        private static readonly (Type type, string method)[] Inner =
        {
            (typeof(Scarecrows), "Protects"), (typeof(Scarecrows), "Nearest"), (typeof(Scarecrows), "Around"),
            (typeof(BiomesCompat), "Allows"), (typeof(LakeSearch), "FindSpots"), (typeof(LakeSearch), "FindWadeSpots"),
            (typeof(EntityBehaviorCorvid), "GroundSpots"), (typeof(ResidentGeese), "LakeSize"), (typeof(LandingFlocks), "Arrive"),
        };

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private void StartProfiler()
        {
            mainThread = Environment.CurrentManagedThreadId; perfLog = sapi.Logger;
            harmony = new Harmony("flockstest.perf");
            var pre = new HarmonyMethod(typeof(FlocksTestSystem).GetMethod(nameof(TimePre), BindingFlags.Static | BindingFlags.NonPublic));
            var post = new HarmonyMethod(typeof(FlocksTestSystem).GetMethod(nameof(TimePost), BindingFlags.Static | BindingFlags.NonPublic));
            var targets = new[] { typeof(EntityBehaviorWaterfowl), typeof(EntityBehaviorCorvid), typeof(EntityBehaviorRaptor), typeof(EntityBehaviorOwl) }
                .Select(t => (MethodBase)t.GetMethod("OnGameTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Append(AccessTools.Method(typeof(LandingFlocks), "Tick"))
                .Append(AccessTools.Method(typeof(SkyFlocks), "Tick"))
                .Append(AccessTools.Method(typeof(MigrationScheduler), "Tick"))
                .Append(AccessTools.Method(typeof(ResidentGeese), "TrySpawn"))
                .Concat(Inner.SelectMany(i => AccessTools.GetDeclaredMethods(i.type).Where(m => m.Name == i.method).Cast<MethodBase>().DefaultIfEmpty(null)))
                .ToList();
            foreach (var m in targets)
            {
                if (m == null) { Check(false, "profiler: a method to time is missing"); continue; }
                harmony.Patch(m, prefix: pre, postfix: post);
            }
            Log($"profiler: timing {targets.Count(m => m != null)} methods");
            lastTickStamp = Stopwatch.GetTimestamp();
            sapi.Event.RegisterGameTickListener(_ =>
            {
                long now = Stopwatch.GetTimestamp(), gap = now - lastTickStamp;
                lastTickStamp = now;
                lock (costLock)
                {
                    stepTickGapMax = Math.Max(stepTickGapMax, gap);
                    stepServerTicks++;
                    // a hitch: say how much of it was Flocks (on the main thread)
                    if (Ms(gap) > 100 && spikes++ < 60)
                        Log($"[spike] server tick gap {Ms(gap):0} ms in step '{stepName}': Flocks {Ms(sinceTick):0.0} ms of it" +
                            (sinceTickMaxWho != null ? $", biggest call {Ms(sinceTickMax):0.00} ms ({sinceTickMaxWho})" : "") +
                            (sinceTickInnerWho != null ? $", biggest inner call {Ms(sinceTickInner):0.00} ms ({sinceTickInnerWho})" : ""));
                    sinceTick = 0; sinceTickMax = 0; sinceTickMaxWho = null; sinceTickInner = 0; sinceTickInnerWho = null;
                }
            }, 0);
        }

        private static void TimePre(out (long t, long b) __state) => __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

        private static void TimePost((long t, long b) __state, object __instance, MethodBase __originalMethod)
        {
            long t = Stopwatch.GetTimestamp() - __state.t, bytes = GC.GetAllocatedBytesForCurrentThread() - __state.b;
            string type = __originalMethod.DeclaringType?.Name, name = __originalMethod.Name;
            bool inner = Inner.Any(i => i.type.Name == type && i.method == name);
            bool main = Environment.CurrentManagedThreadId == mainThread;
            string who = __instance is EntityBehavior b && name == "OnGameTick" ? b.entity?.Code?.Path ?? "?"
                : name == "TrySpawn" ? "spawncheck" : name == "Tick" ? "landings" : $"~{type}.{name}";
            if (!main) who += "@thread";
            lock (costLock)
            {
                foreach (var d in new[] { stepCost, runCost })
                {
                    if (!d.TryGetValue(who, out var c)) d[who] = c = new Cost();
                    c.Calls++; c.Ticks += t; c.Bytes += bytes;
                    if (t > c.MaxTicks) { c.MaxTicks = t; c.MaxWho = who; }
                }
                if (inner && main && t > sinceTickInner) { sinceTickInner = t; sinceTickInnerWho = who; }
                if (inner && main && Ms(t) > 50) perfLog?.Notification($"[FlocksTest] [slow] {who} took {Ms(t):0.0} ms");
                if (inner || !main) return;
                sinceTick += t;
                if (t > sinceTickMax) { sinceTickMax = t; sinceTickMaxWho = who; }
                if (Ms(t) > worstRunMs) { worstRunMs = Ms(t); worstRunCall = who; }
            }
        }

        private static bool Top(string who) => !who.StartsWith("~") && !who.EndsWith("@thread");

        /// <summary>Logs the finished step's cost and resets the counters (called from NextStep).</summary>
        private void ReportStepCost(string name, double seconds)
        {
            if (name == null) return;
            lock (costLock)
            {
                var top = stepCost.Where(kv => Top(kv.Key)).Select(kv => kv.Value).ToList();
                if (stepCost.Count > 0)
                {
                    long total = top.Sum(c => c.Ticks);
                    var worst = top.OrderByDescending(c => c.MaxTicks).FirstOrDefault() ?? new Cost { MaxWho = "-" };
                    string per = string.Join(", ", stepCost.OrderByDescending(kv => kv.Value.Ticks)
                        .Select(kv => $"{kv.Key} {kv.Value.Calls}x avg {Ms(kv.Value.Ticks) / kv.Value.Calls * 1000:0}us max {Ms(kv.Value.MaxTicks):0.00}ms alloc {kv.Value.Bytes / kv.Value.Calls}B/call"));
                    long bytes = top.Sum(c => c.Bytes);
                    Log($"[perf] {name}: {Ms(total):0.0} ms over {seconds:0.0} s = {Ms(total) / Math.Max(1, stepServerTicks):0.000} ms per server tick; " +
                        $"worst call {Ms(worst.MaxTicks):0.00} ms ({worst.MaxWho}); longest server tick gap {Ms(stepTickGapMax):0} ms; " +
                        $"Flocks allocated {bytes / 1048576.0:0.00} MB ({bytes / 1048576.0 / Math.Max(0.1, seconds):0.00} MB/s); GCs gen0 {GC.CollectionCount(0) - stepGen0}, gen2 {GC.CollectionCount(2) - stepGen2}; {per}");
                }
                else Log($"[perf] {name}: no Flocks ticks; longest server tick gap {Ms(stepTickGapMax):0} ms");
                stepCost.Clear(); stepTickGapMax = 0; stepServerTicks = 0; stepGen0 = GC.CollectionCount(0); stepGen2 = GC.CollectionCount(2);
            }
        }

        private void ReportRunCost()
        {
            lock (costLock)
            {
                foreach (var kv in runCost.OrderByDescending(kv => kv.Value.Ticks))
                    Log($"[perf] whole run, {kv.Key}: {kv.Value.Calls} calls, {Ms(kv.Value.Ticks):0} ms, avg {Ms(kv.Value.Ticks) / kv.Value.Calls * 1000:0} us, max {Ms(kv.Value.MaxTicks):0.00} ms, allocated {kv.Value.Bytes / 1048576.0:0.0} MB ({kv.Value.Bytes / kv.Value.Calls} B/call)");
                Log($"[perf] whole run worst single main-thread call: {worstRunMs:0.00} ms ({worstRunCall}); {spikes} server tick gaps over 100 ms");
            }
        }

        // ------------------------------------------------------------------------------------------------ load and leaks

        private readonly List<Entity> loadBirds = new();
        private readonly List<FlockRecord> loadFlocks = new();
        private readonly List<(string what, long mem, int entities, string caches)> leakRuns = new();

        /// <summary>Four goose flocks of 8, three mallard flocks of 8, four crow flocks of 5 and 2 herons, around pond A and the
        /// crop field; the harness runs their physics. Returns how many were spawned.</summary>
        private int SpawnLoad()
        {
            loadBirds.Clear(); loadFlocks.Clear();
            FlockRegistry.Instance.Calm();
            void Flock(string species, string entity, int count, System.Func<int, Vec3d> at)
            {
                var rec = FlockRegistry.Instance.Create(species, count, c); loadFlocks.Add(rec);
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", entity));
                for (int i = 0; i < count; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, rec.Id);
                    e.Pos.SetPos(at(i));
                    sapi.World.SpawnEntity(e); loadBirds.Add(e); extraPhysics.Add(e);
                }
            }
            for (int f = 0; f < 4; f++) { int ff = f; Flock("goose", "greylag", 8, i => new Vec3d(c.X - 46 + (i % 4) * 2.5 + ff % 2 * 10, y0 + 0.75, c.Z - 6 + (i / 4) * 2.5 + ff / 2 * 6)); }
            for (int f = 0; f < 3; f++) { int ff = f; Flock("mallard", "mallard", 8, i => new Vec3d(c.X - 44 + (i % 4) * 1.5 + ff * 6, y0 + 0.75, c.Z + 8 + (i / 4) * 1.5)); }
            for (int f = 0; f < 4; f++) { int ff = f; Flock("crow", "crow", 5, i => new Vec3d(c.X - 10 + ff * 6 + (i % 3) * 1.5, y0 + 1, c.Z + 72 + (i / 3) * 1.5)); }
            Flock("heron", "heron", 2, i => new Vec3d(c.X - 40 + 16.5, y0 + 0.05, c.Z + i * 3 - 1));
            return loadBirds.Count;
        }

        /// <summary>Startles one bird of every load flock (each flock's own flush logic then moves the rest).</summary>
        private void FlushLoad()
        {
            FlockRegistry.Instance.Calm();
            foreach (var rec in loadFlocks)
            {
                var e = loadBirds.FirstOrDefault(b => b.Alive && b.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, -1) == rec.Id);
                e?.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 0.01f);
            }
        }

        private void KillLoad()
        {
            foreach (var e in loadBirds) { extraPhysics.Remove(e); if (e.Alive) e.Die(EnumDespawnReason.Expire); }
            loadBirds.Clear();
        }

        /// <summary>Sizes of the mod's static and registry state that could grow without bound.</summary>
        private string Caches()
        {
            int Count(Type t, string field, object inst = null)
            {
                var f = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                return f?.GetValue(inst) is ICollection col ? col.Count : -1;
            }
            int calls = -1;
            if (typeof(BirdCalls).GetField("recent", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is IDictionary rec)
                calls = rec.Values.Cast<ICollection>().Sum(l => l.Count);
            return $"patches {Count(typeof(EntityBehaviorCorvid), "patches")}, cropBusy {Count(typeof(EntityBehaviorCorvid), "cropBusy")}, " +
                   $"birdcalls {calls}, registry {Count(typeof(FlockRegistry), "flocks", FlockRegistry.Instance)}, " +
                   $"hitListFields {Count(typeof(EntityBehaviorBird), "hitListFields")}";
        }

        private void QueuePerfSteps()
        {
            int spawned = 0;
            steps.Enqueue(("load: idle", () => { spawned = SpawnLoad(); }, () => Elapsed > 20, () =>
            {
                int alive = loadBirds.Count(e => e.Alive);
                Check(alive == spawned, $"{spawned} birds spawned for the load test, {alive} alive");
            }, 30));
            int flushes = 0;
            double quietPerTick = 0;
            steps.Enqueue(("load: flushing", () => { flushes = 0; }, () =>
            {
                if (Elapsed > flushes * 10) { flushes++; FlushLoad(); }
                return Elapsed > 60;
            }, () =>
            {
                Log($"load: {flushes} flushes, {loadBirds.Count(e => e.Alive)} alive, {Caches()}");
                double perTick = Ms(stepCost.Where(kv => Top(kv.Key)).Sum(kv => kv.Value.Ticks)) / Math.Max(1, stepServerTicks);
                long worst = stepCost.Where(kv => Top(kv.Key)).Select(kv => kv.Value.MaxTicks).DefaultIfEmpty(0).Max();
                // the server's tick budget is 33 ms; the birds' AI should take a small slice of it, without spikes
                Check(perTick < 3, $"{spawned} birds flushing every 10 s cost {perTick:0.000} ms of AI per server tick");
                quietPerTick = perTick;
                Check(Ms(worst) < 50, $"worst single bird tick under load: {Ms(worst):0.00} ms");
            }, 70));
            // the same birds among 150 other animals (as other mods' fish and wildlife would be): every bird scans the
            // entities around it many times a second, so their number shouldn't multiply its cost
            var crowd = new List<Entity>();
            steps.Enqueue(("load: crowded", () =>
            {
                flushes = 0;
                var hen = sapi.World.GetEntityType(new AssetLocation("game", "chicken-hen"));
                for (int i = 0; i < 150; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(hen);
                    e.Pos.SetPos(c.X - 60 + (i % 15) * 4 + 0.5, y0 + 1, c.Z - 20 + (i / 15) * 4 + 0.5);
                    sapi.World.SpawnEntity(e); crowd.Add(e);
                }
            }, () =>
            {
                if (Elapsed > flushes * 10) { flushes++; FlushLoad(); }
                return Elapsed > 60;
            }, () =>
            {
                double perTick = Ms(stepCost.Where(kv => Top(kv.Key)).Sum(kv => kv.Value.Ticks)) / Math.Max(1, stepServerTicks);
                Log($"crowded: {crowd.Count(e => e.Alive)} hens, {sapi.World.LoadedEntities.Count} entities loaded; {perTick:0.000} ms of AI per tick vs {quietPerTick:0.000} without them");
                Check(perTick < 3, $"{loadBirds.Count(e => e.Alive)} birds among 150 hens cost {perTick:0.000} ms of AI per server tick");
                foreach (var e in crowd) if (e.Alive) e.Die(EnumDespawnReason.Expire);
                crowd.Clear();
            }, 70));
            steps.Enqueue(("load: clear", () => KillLoad(), () => Elapsed > 5, () => { }, 10));

            for (int cycle = 1; cycle <= 5; cycle++)
            {
                int n = cycle;
                steps.Enqueue(($"leak cycle {n}", () => { SpawnLoad(); }, () =>
                {
                    if (Elapsed > 3 && flushes != -n) { flushes = -n; FlushLoad(); }
                    if (Elapsed > 25 && loadBirds.Count > 0) KillLoad();
                    return Elapsed > 30;
                }, () =>
                {
                    long mem = GC.GetTotalMemory(true) / (1024 * 1024);
                    int ents = sapi.World.LoadedEntities.Count;
                    string caches = Caches();
                    leakRuns.Add(($"cycle {n}", mem, ents, caches));
                    Log($"leak cycle {n}: managed memory {mem} MB, {ents} loaded entities, {caches}");
                }, 45));
            }
            steps.Enqueue(("leaks", () => { }, () => true, () =>
            {
                var m = leakRuns.Select(r => r.mem).ToList();
                var e = leakRuns.Select(r => r.entities).ToList();
                // cycle 1 warms caches; from cycle 2 on, five more loads of 78 birds shouldn't keep raising the floor
                Check(m.Count == 5 && m[4] - m[1] < 24, $"managed memory after each spawn/kill cycle (MB): {string.Join(", ", m)}");
                Check(e.Count == 5 && e[4] - e[1] <= 5, $"loaded entities after each cycle: {string.Join(", ", e)}");
                foreach (var r in leakRuns) Log($"  {r.what}: {r.caches}");
                Check(runCost.Count > 0 && worstRunMs < 50, $"worst single Flocks tick call over the whole run: {worstRunMs:0.00} ms ({worstRunCall})");
            }, 5));
        }
    }
}
