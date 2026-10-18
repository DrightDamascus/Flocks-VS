using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace FlocksClientBench
{
    /// <summary>
    /// Frame-time benchmark in the real client (tools/run_client_bench.ps1). Once the world is in, it waits a warm-up
    /// (chunks streaming in), then records every frame for a set time while the player stands still, sending a few
    /// Flocks flyovers along the way (when Flocks is loaded). Every frame's Flocks time is measured: the flyover renderer
    /// and migration scheduler on the client thread, and the birds' AI on the singleplayer server's thread (same
    /// process), along with garbage collections. Writes flocksbench-results.txt to the data folder and quits the game.
    /// Settings come from environment variables: FLOCKSBENCH_WARMUP, FLOCKSBENCH_SECONDS (seconds), FLOCKSBENCH_LABEL.
    /// </summary>
    public class FlocksClientBench : ModSystem
    {
        private const string HarmonyId = "flocksbench";
        private static readonly object costLock = new();
        /// <summary>Flocks time since the last frame (Stopwatch ticks): client thread, other threads (the server).</summary>
        private static long clientSince, serverSince, serverSinceMax;
        private static string serverSinceMaxWho;
        private static readonly Dictionary<string, (long calls, long ticks, long max, long bytes)> costs = new();

        public override bool ShouldLoad(EnumAppSide side) => true;
        public override double ExecuteOrder() => 1;   // after the other mods are started

        public override void StartServerSide(ICoreServerAPI api) => Patch(api.Logger);

        // ------------------------------------------------------------------------------------------------ timing

        private static void Patch(ILogger log)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Flocks").ToList();
            if (asm.Count == 0) { log.Notification("[FlocksBench] Flocks isn't loaded: frames only"); return; }
            var harmony = new Harmony(HarmonyId);
            var pre = new HarmonyMethod(typeof(FlocksClientBench).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var post = new HarmonyMethod(typeof(FlocksClientBench).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic));
            var wanted = new (string type, string method)[]
            {
                ("Flocks.FlockRenderer", "OnRenderFrame"), ("Flocks.MigrationScheduler", "Tick"),
                ("Flocks.EntityBehaviorWaterfowl", "OnGameTick"), ("Flocks.EntityBehaviorCorvid", "OnGameTick"),
                ("Flocks.EntityBehaviorRaptor", "OnGameTick"), ("Flocks.EntityBehaviorOwl", "OnGameTick"),
                ("Flocks.ResidentGeese", "TrySpawn"), ("Flocks.LandingFlocks", "Tick"),
            };
            int n = 0;
            foreach (var a in asm)
                foreach (var (type, method) in wanted)
                {
                    var m = a.GetType(type) is Type t ? AccessTools.DeclaredMethod(t, method) : null;
                    if (m == null) continue;
                    var info = Harmony.GetPatchInfo(m);
                    if (info != null && info.Owners.Contains(HarmonyId)) continue;   // client and server share the process
                    harmony.Patch(m, prefix: pre, postfix: post); n++;
                }
            log.Notification($"[FlocksBench] timing {n} Flocks methods in {asm.Count} assembly(ies)");
        }

        private static int clientThread = -1;

        private static void Pre(out (long t, long b) __state) => __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

        private static void Post((long t, long b) __state, MethodBase __originalMethod)
        {
            long t = Stopwatch.GetTimestamp() - __state.t, bytes = GC.GetAllocatedBytesForCurrentThread() - __state.b;
            string who = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            lock (costLock)
            {
                costs.TryGetValue(who, out var c);
                costs[who] = (c.calls + 1, c.ticks + t, Math.Max(c.max, t), c.bytes + bytes);
                if (Environment.CurrentManagedThreadId == clientThread) clientSince += t;
                else
                {
                    serverSince += t;
                    if (t > serverSinceMax) { serverSinceMax = t; serverSinceMaxWho = who; }
                }
            }
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // ------------------------------------------------------------------------------------------------ frames

        private ICoreClientAPI capi;
        private double warmup, seconds;
        private string label;

        private struct Frame { public double At, Ms, FlocksClientMs, FlocksServerMs, ServerMaxMs; public int Gen0, Gen1, Gen2; public int InSky; public string ServerMaxWho; }
        private readonly List<Frame> frames = new();
        private long last, start; private bool measuring, done;
        private int g0, g1, g2; private TimeSpan pauseAtStart;
        private long memStart;
        private readonly List<string> events = new();

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            clientThread = Environment.CurrentManagedThreadId;
            warmup = Env("FLOCKSBENCH_WARMUP", 90); seconds = Env("FLOCKSBENCH_SECONDS", 120);
            label = Environment.GetEnvironmentVariable("FLOCKSBENCH_LABEL") ?? "run";
            api.Event.LevelFinalize += () =>
            {
                Patch(api.Logger);
                api.Logger.Notification($"[FlocksBench] world in; warming up {warmup} s, then measuring {seconds} s");
                // chat commands as the player once it is in (FLOCKSBENCH_COMMANDS, ';'-separated), so runs start alike
                string cmds = Environment.GetEnvironmentVariable("FLOCKSBENCH_COMMANDS") ?? "/time set day";
                api.Event.RegisterCallback(_ =>
                {
                    foreach (var c in cmds.Split(';').Select(c => c.Trim()).Where(c => c.Length > 0))
                    {
                        api.SendChatMessage(c);
                        api.Logger.Notification("[FlocksBench] command: " + c);
                    }
                }, 5000);
                api.Event.RegisterCallback(_ => Begin(), (int)(warmup * 1000));
                api.Event.RegisterRenderer(new FrameHook(this), EnumRenderStage.Before, "flocksbench");
            };
        }

        private static double Env(string name, double def) => double.TryParse(Environment.GetEnvironmentVariable(name), out double v) ? v : def;

        private void Begin()
        {
            lock (costLock) { costs.Clear(); clientSince = serverSince = serverSinceMax = 0; }
            g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
            pauseAtStart = GC.GetTotalPauseDuration(); memStart = GC.GetTotalMemory(false);
            start = last = Stopwatch.GetTimestamp(); measuring = true;
            capi.Logger.Notification("[FlocksBench] measuring");
            // flyovers along the way, so the renderer is measured with birds up (Flocks only)
            foreach (var (at, n, sp) in new[] { (0.15, 14, "goose"), (0.4, 20, "crane"), (0.65, 12, "crow") })
                capi.Event.RegisterCallback(_ => Send(n, sp), (int)(seconds * at * 1000));
            capi.Event.RegisterCallback(_ => Finish(), (int)(seconds * 1000));
        }

        private void Send(int count, string species)
        {
            var sys = capi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "Flocks.FlocksModSystem");
            var m = sys?.GetType().GetMethod("TrySend", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) return;
            var args = new object[] { count, species, false };
            string msg = m.Invoke(sys, args) as string;
            events.Add($"{Elapsed():0.0}s send {count} {species}: {msg}");
        }

        private double Elapsed() => Ms(Stopwatch.GetTimestamp() - start) / 1000;

        private int InSky()
        {
            var sys = capi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "Flocks.FlocksModSystem");
            var r = sys?.GetType().GetField("renderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sys);
            return r?.GetType().GetProperty("Count")?.GetValue(r) is int n ? n : -1;
        }

        private class FrameHook : IRenderer
        {
            private readonly FlocksClientBench b;
            public FrameHook(FlocksClientBench b) { this.b = b; }
            public double RenderOrder => 0;
            public int RenderRange => 0;
            public void OnRenderFrame(float dt, EnumRenderStage stage) => b.OnFrame();
            public void Dispose() { }
        }

        private int lastG0, lastG1, lastG2;

        private void OnFrame()
        {
            if (!measuring || done) return;
            long now = Stopwatch.GetTimestamp();
            int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
            Frame f;
            lock (costLock)
            {
                f = new Frame
                {
                    At = Ms(now - start) / 1000, Ms = Ms(now - last), FlocksClientMs = Ms(clientSince), FlocksServerMs = Ms(serverSince),
                    ServerMaxMs = Ms(serverSinceMax), ServerMaxWho = serverSinceMaxWho,
                    Gen0 = c0 - lastG0, Gen1 = c1 - lastG1, Gen2 = c2 - lastG2,
                };
                clientSince = serverSince = serverSinceMax = 0; serverSinceMaxWho = null;
            }
            f.InSky = frames.Count % 30 == 0 ? InSky() : (frames.Count > 0 ? frames[^1].InSky : InSky());
            if (frames.Count > 0) frames.Add(f);
            else frames.Add(new Frame { At = f.At, Ms = 0, InSky = f.InSky });   // the first sample only starts the clock
            lastG0 = c0; lastG1 = c1; lastG2 = c2; last = now;
        }

        private void Finish()
        {
            done = true; measuring = false;
            var sb = new StringBuilder();
            var fr = frames.Skip(1).ToList();
            var ms = fr.Select(f => f.Ms).OrderBy(x => x).ToList();
            double P(double q) => ms.Count == 0 ? 0 : ms[Math.Min(ms.Count - 1, (int)(q * ms.Count))];
            double total = fr.Sum(f => f.Ms) / 1000;
            sb.AppendLine($"label: {label}");
            sb.AppendLine($"flocks loaded: {AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Flocks")}");
            sb.AppendLine($"mods: {string.Join(", ", capi.ModLoader.Mods.Select(m => m.Info.ModID + "@" + m.Info.Version))}");
            sb.AppendLine($"frames: {fr.Count} over {total:0.0} s; avg fps {fr.Count / Math.Max(0.001, total):0.0}");
            sb.AppendLine($"frame ms: median {P(0.5):0.0}, p90 {P(0.9):0.0}, p99 {P(0.99):0.0}, p99.9 {P(0.999):0.0}, max {(ms.Count > 0 ? ms[^1] : 0):0.0}");
            sb.AppendLine($"1% low fps: {1000 / Math.Max(0.001, P(0.99)):0.0}; frames over 50 ms: {fr.Count(f => f.Ms > 50)}, over 100 ms: {fr.Count(f => f.Ms > 100)}, over 250 ms: {fr.Count(f => f.Ms > 250)}");
            sb.AppendLine($"GCs: gen0 {GC.CollectionCount(0) - g0}, gen1 {GC.CollectionCount(1) - g1}, gen2 {GC.CollectionCount(2) - g2}; total GC pause {(GC.GetTotalPauseDuration() - pauseAtStart).TotalMilliseconds:0} ms; managed heap {memStart / 1048576} -> {GC.GetTotalMemory(false) / 1048576} MB");
            double fc = fr.Sum(f => f.FlocksClientMs), fs = fr.Sum(f => f.FlocksServerMs);
            sb.AppendLine($"Flocks client-thread time: {fc:0.0} ms total ({fc / Math.Max(1, fr.Count):0.000} ms per frame, max in one frame {(fr.Count > 0 ? fr.Max(f => f.FlocksClientMs) : 0):0.00} ms)");
            sb.AppendLine($"Flocks server-thread time: {fs:0.0} ms total ({fs / Math.Max(0.001, total):0.0} ms per second)");
            var sky = fr.Where(f => f.InSky > 0).ToList(); var clear = fr.Where(f => f.InSky == 0).ToList();
            string Stat(List<Frame> l) { if (l.Count == 0) return "none"; var s = l.Select(f => f.Ms).OrderBy(x => x).ToList(); return $"{l.Count} frames, avg fps {l.Count / (l.Sum(f => f.Ms) / 1000):0.0}, p99 {s[Math.Min(s.Count - 1, (int)(0.99 * s.Count))]:0.0} ms, max {s[^1]:0.0} ms"; }
            sb.AppendLine($"frames with a flyover in the sky: {Stat(sky)}");
            sb.AppendLine($"frames with the sky clear: {Stat(clear)}");
            lock (costLock)
                foreach (var kv in costs.OrderByDescending(kv => kv.Value.ticks))
                    sb.AppendLine($"  {kv.Key}: {kv.Value.calls} calls, {Ms(kv.Value.ticks):0.0} ms, avg {Ms(kv.Value.ticks) / kv.Value.calls * 1000:0} us, max {Ms(kv.Value.max):0.00} ms, {kv.Value.bytes / Math.Max(1, kv.Value.calls)} B/call");
            foreach (var e in events) sb.AppendLine("event: " + e);
            sb.AppendLine("slowest frames (at s: frame ms | Flocks client ms | Flocks server ms (biggest call) | GCs gen0/1/2 | flocks in sky):");
            foreach (var f in fr.OrderByDescending(f => f.Ms).Take(25).OrderBy(f => f.At))
                sb.AppendLine($"  {f.At,6:0.00}: {f.Ms,7:0.0} | {f.FlocksClientMs,6:0.00} | {f.FlocksServerMs,6:0.00} ({f.ServerMaxMs:0.00} {f.ServerMaxWho ?? "-"}) | {f.Gen0}/{f.Gen1}/{f.Gen2} | {f.InSky}");
            string path = Path.Combine(GamePaths.DataPath, "flocksbench-results.txt");
            File.WriteAllText(path, sb.ToString());
            capi.Logger.Notification("[FlocksBench] results written to " + path);
            capi.Event.RegisterCallback(_ => Environment.Exit(0), 500);
        }
    }
}
