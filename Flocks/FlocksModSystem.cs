using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using ProtoBuf;

namespace Flocks
{
    /// <summary>Server -> client: run a /flocks subcommand on the caller's client, which owns the birds.</summary>
    [ProtoContract]
    public class SendFlockPacket
    {
        [ProtoMember(1)] public int Count;
        [ProtoMember(2)] public string Species;
        [ProtoMember(3)] public string Command; // null or "send" = send a flock, "schedule" = show today's plan
        /// <summary>schedule: the server's part (landed flocks leaving, landing flocks coming in), shown after the sky's.</summary>
        [ProtoMember(4)] public string Local;
        /// <summary>send: follow the flock with the camera (the server has made the player a spectator already).</summary>
        [ProtoMember(5)] public bool Watch;
    }

    /// <summary>Server -> client: the species whose passing flocks aren't drawn where the player is, because the Biomes mod
    /// puts that spot outside their realms (BiomesCompat; Biomes runs on the server only). Sent when it changes.</summary>
    [ProtoContract]
    public class SkyRealmsPacket
    {
        [ProtoMember(1)] public string[] Absent;
    }

    /// <summary>Client-side migrating flocks: a renderer, the species, and a scheduler that sends them in season.</summary>
    public class FlocksModSystem : ModSystem
    {
        private ICoreClientAPI capi;
        /// <summary>This side's species (config/flocks-species/), filled when assets load; the same dictionary is handed
        /// to everything that needs it, so it is complete before the world runs.</summary>
        private readonly Dictionary<string, Species> species = new();
        public Dictionary<string, Species> SpeciesByCode => species;
        private FlockRenderer renderer;
        private MigrationScheduler scheduler;
        private FlocksConfig config;
        public const string DefaultSpecies = "goose";
        public const string ConfigFile = "flocks.json";

        private bool rendererRegistered;

        public override void Start(ICoreAPI api)
        {
            api.Network.RegisterChannel("flocks").RegisterMessageType<SendFlockPacket>().RegisterMessageType<SkyRealmsPacket>()
                .RegisterMessageType<FlockCamPacket>();
            api.RegisterEntityBehaviorClass(EntityBehaviorWaterfowl.Code, typeof(EntityBehaviorWaterfowl));
            api.RegisterEntityBehaviorClass(EntityBehaviorRaptor.Code, typeof(EntityBehaviorRaptor));
            api.RegisterEntityBehaviorClass(EntityBehaviorCorvid.Code, typeof(EntityBehaviorCorvid));
            api.RegisterEntityBehaviorClass(EntityBehaviorOwl.Code, typeof(EntityBehaviorOwl));
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            species.Clear();
            foreach (var kv in Species.LoadAll(api)) species[kv.Key] = kv.Value;
        }

        public override void AssetsFinalize(ICoreAPI api) => Scarecrows.HandbookText(api);

        /// <summary>/flocks (typed with a slash) goes to the server; it only relays the subcommand to the caller's client.</summary>
        private ResidentGeese residents;
        private FlockRegistry registry;
        private LandingFlocks landings;
        private FlockCamera camera;
        private FlockJourney journeys;
        /// <summary>The server's landing planner (null on the client).</summary>
        public LandingFlocks Landings => landings;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            residents = new ResidentGeese(sapi, LoadServerConfig(sapi), species);
            ScaleNumbers(sapi, residents.Cfg.Numbers);
            var ch = sapi.Network.GetChannel("flocks");
            camera = new FlockCamera(sapi, ch);
            journeys = new FlockJourney(sapi, camera, (sp, spring, at) =>
                MoveClockToSeason(sapi, sp, spring, at, 0, out double moved) ? $"{moved:0.0} days on, {sapi.World.Calendar.PrettyDate()}" : sapi.World.Calendar.PrettyDate());
            registry = new FlockRegistry(sapi, residents.Cfg);
            landings = new LandingFlocks(sapi, species);
            // tell each client which species don't fly over its player: switched off in the server config (Animals), or,
            // with the Biomes mod, not of the realm the player stands in
            var realmsSent = new Dictionary<string, string>();
            sapi.Event.RegisterGameTickListener(_ =>
            {
                foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
                {
                    if (plr.Entity == null || plr.ConnectionState != EnumClientState.Playing) continue;
                    var pos = plr.Entity.Pos.AsBlockPos;
                    var absent = species.Values.Where(sp => !residents.Cfg.SpeciesEnabled(sp) || !BiomesCompat.Allows(sapi, sp, pos))
                        .Select(sp => sp.Code).OrderBy(c => c).ToArray();
                    string key = string.Join(",", absent);
                    if (realmsSent.TryGetValue(plr.PlayerUID, out var last) && last == key) continue;
                    realmsSent[plr.PlayerUID] = key;
                    sapi.Network.GetChannel("flocks").SendPacket(new SkyRealmsPacket { Absent = absent }, plr);
                }
            }, 20000, 3000);
            sapi.Event.PlayerDisconnect += plr => realmsSent.Remove(plr.PlayerUID);
            var p = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.Create("flocks").WithDescription(Lang.Get("flocks:cmd-flocks")).RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("send").WithDescription(Lang.Get("flocks:cmd-send")).RequiresPlayer()
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"), p.OptionalWord("watch"))
                .HandleWith(args => SendRelay((IServerPlayer)args.Caller.Player, args, pkt => ch.SendPacket(pkt, (IServerPlayer)args.Caller.Player)))
                .EndSubCommand()
                .BeginSubCommand("schedule").WithDescription(Lang.Get("flocks:cmd-schedule")).RequiresPlayer()
                .HandleWith(args =>
                {
                    ch.SendPacket(new SendFlockPacket { Command = "schedule", Local = LocalSchedule(sapi, (IServerPlayer)args.Caller.Player) }, (IServerPlayer)args.Caller.Player);
                    return TextCommandResult.Success();
                })
                .EndSubCommand()
                .BeginSubCommand("season").WithDescription(Lang.Get("flocks:cmd-season")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.Word("season"), p.OptionalWord("species"), p.OptionalDouble("days", 0))
                .HandleWith(args => JumpToSeason(sapi, args.Caller.Entity, args[0] as string, args[1] as string, (double)args[2]))
                .EndSubCommand()
                .BeginSubCommand("depart").WithDescription(Lang.Get("flocks:cmd-depart")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("species"), p.OptionalWord("watch"))
                .HandleWith(args =>
                {
                    string code = SpeciesAndWatch(args, out bool watch);
                    var res = DepartNearest(sapi, args.Caller.Entity, code, out long id, out Vec3d at);
                    return watch && res.Status == EnumCommandStatus.Success ? Watch(res, (IServerPlayer)args.Caller.Player, id, at) : res;
                })
                .EndSubCommand()
                .BeginSubCommand("journey").WithDescription(Lang.Get("flocks:cmd-journey")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("species"))
                .HandleWith(args => StartJourney(sapi, (IServerPlayer)args.Caller.Player, args[0] as string))
                .EndSubCommand()
                .BeginSubCommand("unwatch").WithDescription(Lang.Get("flocks:cmd-unwatch")).RequiresPlayer()
                .HandleWith(args => camera.Stop((IServerPlayer)args.Caller.Player)
                    ? TextCommandResult.Success(Lang.Get("flocks:watch-stopped")) : TextCommandResult.Error(Lang.Get("flocks:watch-none")))
                .EndSubCommand()
                .BeginSubCommand("land").WithDescription(Lang.Get("flocks:cmd-land")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalInt("count", 0), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    var sp = LandingSpecies(args[1] as string, out string err);
                    return sp == null ? TextCommandResult.Error(err) : Landing.Land(sapi, args.Caller.Entity, (int)args[0], sp);
                })
                .EndSubCommand()
                .BeginSubCommand("arrive").WithDescription(Lang.Get("flocks:cmd-arrive")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("species"), p.OptionalWord("watch"))
                .HandleWith(args =>
                {
                    var sp = LandingSpecies(SpeciesAndWatch(args, out bool watch), out string err);
                    if (sp == null) return TextCommandResult.Error(err);
                    var plr = (IServerPlayer)args.Caller.Player;
                    bool ok = landings.Arrive(plr, sp, true, out string msg);
                    if (!ok) return TextCommandResult.Error(msg);
                    return watch ? Watch(TextCommandResult.Success(msg), plr, landings.LastFlockId, landings.LastEntry) : TextCommandResult.Success(msg);
                })
                .EndSubCommand()
                .BeginSubCommand("calm").WithDescription(Lang.Get("flocks:cmd-calm"))
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => { registry.Calm(); return TextCommandResult.Success(Lang.Get("flocks:cmd-calm-done")); })
                .EndSubCommand()
                .BeginSubCommand("near").WithDescription(Lang.Get("flocks:cmd-near")).RequiresPlayer()
                .HandleWith(args =>
                {
                    var me = args.Caller.Entity;
                    var lines = new List<string>();
                    foreach (var e in sapi.World.GetEntitiesAround(me.Pos.XYZ, 48, 48, e => e.Alive && e.GetBehavior<EntityBehaviorBird>() != null))
                    {
                        if (lines.Count >= 12) { lines.Add("..."); break; }
                        lines.Add(e.GetBehavior<EntityBehaviorBird>().Diagnose(args.Caller.Player));
                    }
                    return TextCommandResult.Success(lines.Count == 0 ? Lang.Get("flocks:cmd-near-none") : string.Join("\n", lines));
                })
                .EndSubCommand()
                .BeginSubCommand("lineup").WithDescription(Lang.Get("flocks:cmd-lineup")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("pose"), p.OptionalDouble("spacing", 2.5))
                .HandleWith(args => Lineup(sapi, args.Caller.Entity, args[0] as string ?? "stand", (double)args[1]))
                .EndSubCommand()
                .BeginSubCommand("residents").WithDescription(Lang.Get("flocks:cmd-residents"))
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => TextCommandResult.Success(residents.Report() + "\n" + registry.Report()))
                .EndSubCommand();
        }

        /// <summary>
        /// /flocks lineup [stand|fly|clear] [spacing]: one of each Flocks animal in a row across in front of the player,
        /// biggest first, frozen facing them (EntityBehaviorBird.FrozenKey): standing on the ground, or held in the air
        /// gliding with the wings spread. For pictures. "clear" removes them. The mouse runs on vanilla's AI, which is
        /// switched off for this one.
        /// </summary>
        public TextCommandResult Lineup(ICoreServerAPI sapi, Entity caller, string pose, double spacing)
        {
            var world = sapi.World;
            bool Frozen(Entity e) => e.WatchedAttributes.HasAttribute(EntityBehaviorBird.FrozenKey) || e.Attributes.GetBool("flocksLineup");
            if (pose == "clear")
            {
                int n = 0;
                foreach (var e in world.LoadedEntities.Values.Where(e => e.Alive && e.Code.Domain == "flocks" && Frozen(e)).ToList()) { e.Die(EnumDespawnReason.Removed); n++; }
                return TextCommandResult.Success(Lang.Get("flocks:lineup-cleared", n));
            }
            if (pose != "stand" && pose != "fly") return TextCommandResult.Error(Lang.Get("flocks:lineup-usage"));
            spacing = GameMath.Clamp(spacing, 1, 8);
            var order = new[] { "eagle", "heron", "greylag", "gull", "mallard", "crow", "owl", "mouse" };
            var types = order.Select(c => world.GetEntityType(new AssetLocation("flocks", c))).Where(t => t != null).ToList();
            // the row: 6 blocks ahead of the player, across their view
            double look = caller.Pos.Yaw, fx = -Math.Sin(look), fz = -Math.Cos(look);   // the player looks along (-sin, -cos)
            double ax = -fz, az = fx;
            double cx = caller.Pos.X + fx * 6, cz = caller.Pos.Z + fz * 6;
            for (int i = 0; i < types.Count; i++)
            {
                double off = (i - (types.Count - 1) / 2.0) * spacing;
                double x = cx + ax * off, z = cz + az * off;
                int top = world.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z));
                double y = top + 1 + (pose == "fly" ? 3 : 0);
                var e = world.ClassRegistry.CreateEntity(types[i]);
                e.Pos.SetPos(x, y, z);
                double yaw = Math.Atan2(caller.Pos.X - x, caller.Pos.Z - z);
                e.Pos.Yaw = (float)yaw;
                bool bird = e.Code.Path != "mouse";
                if (bird)
                {
                    var t = new Vintagestory.API.Datastructures.TreeAttribute();
                    t.SetDouble("x", x); t.SetDouble("y", y); t.SetDouble("z", z); t.SetDouble("yaw", yaw);
                    t.SetString("anim", pose == "fly" ? "fly-soar" : "idle-stand");
                    e.WatchedAttributes[EntityBehaviorBird.FrozenKey] = t;
                }
                world.SpawnEntity(e);
                if (!bird)
                {
                    // vanilla's AI would walk it off: switch it off for this mouse only
                    e.SidedProperties.Behaviors.RemoveAll(b => b is Vintagestory.GameContent.EntityBehaviorTaskAI);
                    e.Attributes.SetBool("flocksLineup", true);
                }
            }
            return TextCommandResult.Success(Lang.Get("flocks:lineup-done", types.Count, pose == "fly" ? Lang.Get("flocks:lineup-flying") : Lang.Get("flocks:lineup-standing")));
        }

        /// <summary>
        /// /flocks season spring|autumn [species] [days]: moves the calendar forward to the first morning (an hour before
        /// the species' first flying hour) of its spring or autumn migration where the player stands, plus 'days' further
        /// in. Latitude moves the windows as everywhere else (MigrationScheduler.SeasonStart). Run again within two days
        /// of the start, it stays in this window instead of skipping a year; time never goes back.
        /// </summary>
        private TextCommandResult JumpToSeason(ICoreServerAPI sapi, Entity caller, string which, string code, double days)
        {
            which = which?.ToLowerInvariant();
            bool spring = which == "spring";
            if (!spring && which != "autumn" && which != "fall") return TextCommandResult.Error(Lang.Get("flocks:season-usage"));
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            if (!species.TryGetValue(code, out var sp)) return TextCommandResult.Error(Lang.Get("flocks:send-unknown", code, string.Join(", ", species.Keys)));
            if (sp.YearRound) return TextCommandResult.Error(Lang.Get("flocks:season-year-round", sp.DisplayName));
            var cal = sapi.World.Calendar;
            var pos = caller.Pos.AsBlockPos;
            double lat = cal.OnGetLatitude(pos.Z);
            if (MigrationScheduler.Tropical(lat)) return TextCommandResult.Error(Lang.Get("flocks:season-tropics"));
            if (!MoveClockToSeason(sapi, sp, spring, pos, days, out double moved)) return TextCommandResult.Error(Lang.Get("flocks:season-past"));
            float start = MigrationScheduler.SeasonStart(sp, spring, lat), relNow = cal.GetSeasonRel(pos);
            double into = MigrationScheduler.DaysUntil(relNow, start, cal.DaysPerYear);
            double strength = MigrationScheduler.Strength(sp, relNow, lat, out _);
            return TextCommandResult.Success(Lang.Get("flocks:season-done", moved.ToString("0.0"), cal.PrettyDate(),
                Lang.Get(spring ? "flocks:season-spring" : "flocks:season-autumn"), sp.DisplayName, into.ToString("0.0"), strength.ToString("0.00"),
                Lang.Get(spring ? "flocks:season-spring-hint" : "flocks:season-autumn-hint", sp.DisplayName)));
        }

        /// <summary>
        /// Moves the calendar forward to the first morning (an hour before the species' first flying hour) of its spring
        /// or autumn migration at pos, plus 'days' further in. Within two days past the start it stays in this window
        /// rather than skipping a year. False when that would mean going back in time.
        /// </summary>
        private static bool MoveClockToSeason(ICoreServerAPI sapi, Species sp, bool spring, BlockPos pos, double days, out double moved)
        {
            var cal = sapi.World.Calendar;
            double lat = cal.OnGetLatitude(pos.Z);
            float rel = cal.GetSeasonRel(pos), start = MigrationScheduler.SeasonStart(sp, spring, lat);
            double hpd = cal.HoursPerDay, ahead = MigrationScheduler.DaysUntil(start, rel, cal.DaysPerYear);
            if (cal.DaysPerYear - ahead < 2) ahead -= cal.DaysPerYear;   // just past the start: this window, not next year's
            double target = cal.TotalHours + (ahead + days) * hpd;
            double at = Math.Floor(target / hpd) * hpd + Math.Max(0, sp.Hours[0] - 1);
            if (at < target) at += hpd;
            moved = (at - cal.TotalHours) / hpd;
            if (at <= cal.TotalHours) return false;
            cal.Add((float)(at - cal.TotalHours));
            return true;
        }

        /// <summary>The nearest group of waterbirds on the water within 256 blocks (of a species, if given); null if none.</summary>
        private Entity NearestOnWater(ICoreServerAPI sapi, Entity caller, Species only)
        {
            var me = caller.Pos.XYZ;
            return sapi.World.GetEntitiesAround(me, 256, 128, e => e.Alive && (only == null || e.Code.Equals(only.LandedEntityCode))
                    && !e.WatchedAttributes.HasAttribute(EntityBehaviorBird.FrozenKey) && e.GetBehavior<EntityBehaviorWaterfowl>()?.OnWater == true)
                .OrderBy(e => e.Pos.SquareDistanceTo(me)).FirstOrDefault();
        }

        /// <summary>/flocks journey [species]: the nearest flock on the water makes its whole migration, followed (FlockJourney).</summary>
        private TextCommandResult StartJourney(ICoreServerAPI sapi, IServerPlayer plr, string code)
        {
            Species only = null;
            if (code != null && (only = LandingSpecies(code, out string err)) == null) return TextCommandResult.Error(err);
            var bird = NearestOnWater(sapi, plr.Entity, only);
            if (bird == null) return TextCommandResult.Error(only == null ? Lang.Get("flocks:depart-none-any") : Lang.Get("flocks:depart-none", only.DisplayName));
            return journeys.Start(plr, ResidentGeese.ByEntity(species, bird.Code), bird);
        }

        /// <summary>/flocks depart [species]: the nearest group of waterbirds on the water (within 256 blocks) leaves now
        /// and rejoins the migration, exactly as at the end of its season.</summary>
        private TextCommandResult DepartNearest(ICoreServerAPI sapi, Entity caller, string code, out long id, out Vec3d at)
        {
            id = 0; at = null;
            Species only = null;
            if (code != null && (only = LandingSpecies(code, out string err)) == null) return TextCommandResult.Error(err);
            var me = caller.Pos.XYZ;
            var bird = sapi.World.GetEntitiesAround(me, 256, 128, e => e.Alive && (only == null || e.Code.Equals(only.LandedEntityCode))
                    && !e.WatchedAttributes.HasAttribute(EntityBehaviorBird.FrozenKey) && e.GetBehavior<EntityBehaviorWaterfowl>()?.OnWater == true)
                .OrderBy(e => e.Pos.SquareDistanceTo(me)).FirstOrDefault();
            if (bird == null || !bird.GetBehavior<EntityBehaviorWaterfowl>().Depart())
                return TextCommandResult.Error(only == null ? Lang.Get("flocks:depart-none-any") : Lang.Get("flocks:depart-none", only.DisplayName));
            id = bird.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0);
            at = bird.Pos.XYZ;
            int n = FlockRegistry.Instance?.Get(id)?.Count ?? 1;
            string name = ResidentGeese.ByEntity(species, bird.Code)?.DisplayName ?? bird.Code.Path;
            return TextCommandResult.Success(Lang.Get("flocks:depart-done", id, n, name, bird.Pos.DistanceTo(me).ToString("0")));
        }

        /// <summary>
        /// The server's part of /flocks schedule, for the landed waterbirds near the player: per species, whether their
        /// season here keeps them or sends them off (and how many days until that changes), today's landing flocks, and
        /// the flocks on the water nearby with any that are leaving now.
        /// </summary>
        private string LocalSchedule(ICoreServerAPI sapi, IServerPlayer plr)
        {
            if (plr?.Entity == null) return null;
            var cal = sapi.World.Calendar;
            var pos = plr.Entity.Pos.AsBlockPos;
            float rel = cal.GetSeasonRel(pos);
            double lat = cal.OnGetLatitude(pos.Z);
            double Until(Species sp, bool spring) => MigrationScheduler.DaysUntil(MigrationScheduler.SeasonStart(sp, spring, lat), rel, cal.DaysPerYear);
            var sb = new StringBuilder("Local flocks (on the water, within 256 blocks):");
            foreach (var sp in species.Values.Where(s => s.Lands && !s.Ground && !s.Soars && !s.Woodland))
            {
                string season;
                if (!residents.Cfg.SpeciesEnabled(sp)) season = "off in flocks-server.json";
                else if (sp.YearRound) season = "here all year (they leave only a lake disturbed too often)";
                else if (MigrationScheduler.Tropical(lat)) season = "no migration this near the equator: here all year";
                else if (ResidentGeese.InSeason(sapi.World, pos, sp)) season = $"in season: they stay until the autumn migration, in {Until(sp, false):0.0} days";
                else season = $"season over: any still here leave (each group checks every 30 s); arrivals start with the spring migration, in {Until(sp, true):0.0} days";
                sb.Append($"\n{sp.Code}: {season}");
                string today = landings.TodayFor(plr, sp);
                if (today != null) sb.Append($"\n  {today}");
                var local = registry.LocalFlocks(sp.Code, plr.Entity.Pos.XYZ, 256);
                foreach (var line in local.Take(6)) sb.Append($"\n  {line}");
                if (local.Count > 6) sb.Append($"\n  ... and {local.Count - 6} more");
            }
            return sb.ToString();
        }

        /// <summary>depart and arrive take a species and/or the word "watch", in either order.</summary>
        private static string SpeciesAndWatch(TextCommandCallingArgs args, out bool watch)
        {
            string a = args[0] as string, b = args[1] as string;
            watch = a == "watch" || b == "watch";
            return a == "watch" ? b : a;
        }

        /// <summary>A started depart or arrive, now watched from above the flock (FlockCamera).</summary>
        private TextCommandResult Watch(TextCommandResult started, IServerPlayer plr, long flockId, Vec3d at)
            => TextCommandResult.Success(started.StatusMessage + " " + camera.Start(plr, flockId, at));

        /// <summary>A species that lands (has a landed entity), by code; default goose.</summary>
        private Species LandingSpecies(string code, out string err)
        {
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            err = null;
            if (species.TryGetValue(code, out var sp) && sp.Lands) return sp;
            err = Lang.Get("flocks:cmd-no-landing-species", code, string.Join(", ", species.Values.Where(s => s.Lands).Select(s => s.Code)));
            return null;
        }

        private string TrySend(int count, string code, out bool ok, bool watch = false)
        {
            ok = false;
            if (watch)
            {
                // a watch the server began: follow the flock if one went up, else put the player back
                string msg = TrySend(count, code, out ok);
                if (ok) camClient.WatchSky(lastSent); else camClient.Cancel();
                return ok ? msg + " " + Lang.Get("flocks:watch-started") : msg;
            }
            int n = GameMath.Clamp(count, 1, 60);
            ok = false;
            if (species == null || renderer == null) return Lang.Get("flocks:send-not-running");
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            if (!species.TryGetValue(code, out var sp)) return Lang.Get("flocks:send-unknown", code, string.Join(", ", species.Keys));
            if (sp.Meshes?.Ok != true) return Lang.Get("flocks:send-no-meshes", sp.DisplayName);
            lastSent = renderer.Send(sp, n); ok = true;
            return Lang.Get("flocks:send-sent", n, sp.DisplayName, renderer.VisibleRange(sp).ToString("0"), renderer.Count);
        }

        private Flock lastSent;
        private FlockCameraClient camClient;

        /// <summary>/flocks send [count] [species] [watch] on the server: "watch" (admin) makes the player a spectator
        /// here, then the client sends the flock and follows it.</summary>
        private TextCommandResult SendRelay(IServerPlayer plr, TextCommandCallingArgs args, Action<SendFlockPacket> relay)
        {
            string code = args[1] as string, w = args[2] as string;
            bool watch = code == "watch" || w == "watch";
            if (code == "watch") code = null;
            if (watch)
            {
                if (!args.Caller.HasPrivilege(Privilege.controlserver)) return TextCommandResult.Error(Lang.Get("flocks:watch-privilege"));
                string err = camera.Begin(plr);
                if (err != null) return TextCommandResult.Error(err);
            }
            relay(new SendFlockPacket { Command = "send", Count = (int)args[0], Species = code, Watch = watch });
            return TextCommandResult.Success();
        }

        private string Schedule(string local = null) => scheduler?.Report(local) ?? "Flocks client part not running.";

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            config = LoadConfig(api);
            renderer = new FlockRenderer(api) { CallVolume = Math.Max(0, config.FlockCallVolume) };
            scheduler = new MigrationScheduler(api, renderer, species, config);
            api.Event.RegisterGameTickListener(scheduler.Tick, 1000);
            api.Event.LevelFinalize += () =>
            {
                Scarecrows.HandbookText(api);
                foreach (var sp in species.Values)
                {
                    if (sp.Meshes == null) sp.CreateMeshes(api);
                    sp.Meshes.Load(); sp.FitScale();
                    // one size for drawn and landed birds: the entity's client.size, so changing it there resizes both
                    float size = string.IsNullOrEmpty(sp.SizeFromEntity) ? 0 : api.World.GetEntityType(new AssetLocation(sp.SizeFromEntity))?.Client?.Size ?? 0;
                    if (size > 0) sp.Scale = size;
                }
                if (!rendererRegistered) { api.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "flocks"); rendererRegistered = true; }
            };
            camClient = new FlockCameraClient(api, api.Network.GetChannel("flocks"), renderer);
            api.Network.GetChannel("flocks").SetMessageHandler<SkyRealmsPacket>(pkt => scheduler.SetAbsent(pkt.Absent));
            api.Network.GetChannel("flocks").SetMessageHandler<SendFlockPacket>(pkt =>
            {
                string msg = pkt.Command == "schedule" ? Schedule(pkt.Local) : TrySend(pkt.Count, pkt.Species, out _, pkt.Watch);
                api.ShowChatMessage("[Flocks] " + msg);
            });
            var p = api.ChatCommands.Parsers;
            api.ChatCommands.Create("flocks").WithDescription(Lang.Get("flocks:cmd-flocks"))
                .BeginSubCommand("send").WithDescription(Lang.Get("flocks:cmd-send"))
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    string msg = TrySend((int)args[0], args[1] as string, out bool ok);
                    return ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
                })
                .EndSubCommand()
                .BeginSubCommand("schedule").WithDescription(Lang.Get("flocks:cmd-schedule"))
                .HandleWith(args => TextCommandResult.Success(Schedule()))
                .EndSubCommand();
            api.Logger.Notification("[Flocks] client loaded; automatic migration {0}", config.AutoMigration ? "on" : "off");
        }

        public const string ServerConfigFile = "flocks-server.json";

        /// <summary>Server config Numbers: scales every flocks animal's spawn cap and worldgen tries (the spawner reads both
        /// live from the entity types). The tries are replaced, not changed in place: the default is the shared NatFloat.Zero.</summary>
        private static void ScaleNumbers(ICoreServerAPI sapi, double k)
        {
            if (k == 1 || k < 0) return;
            foreach (var type in sapi.World.EntityTypes)
            {
                var sc = type.Code.Domain == "flocks" ? type.Server?.SpawnConditions : null;
                if (sc == null) continue;
                if (sc.Runtime != null) sc.Runtime.MaxQuantity = (int)Math.Round(sc.Runtime.MaxQuantity * k);
                var tries = sc.Worldgen?.TriesPerChunk;
                if (tries != null) sc.Worldgen.TriesPerChunk = new NatFloat(tries.avg * (float)k, tries.var * (float)k, tries.dist) { offset = tries.offset };
            }
            sapi.Logger.Notification("[Flocks] Numbers {0}: spawn caps and worldgen spawning scaled", k);
        }

        private static FlocksServerConfig LoadServerConfig(ICoreServerAPI api)
        {
            FlocksServerConfig cfg;
            try { cfg = api.LoadModConfig<FlocksServerConfig>(ServerConfigFile); }
            catch (Exception ex)
            {
                api.Logger.Error("[Flocks] {0} is unreadable, using defaults: {1}", ServerConfigFile, ex.Message);
                return new FlocksServerConfig();
            }
            cfg ??= new FlocksServerConfig();
            try { api.StoreModConfig(cfg, ServerConfigFile); }
            catch (Exception ex) { api.Logger.Warning("[Flocks] could not write {0}: {1}", ServerConfigFile, ex.Message); }
            return cfg;
        }

        private static FlocksConfig LoadConfig(ICoreClientAPI api)
        {
            FlocksConfig cfg;
            try { cfg = api.LoadModConfig<FlocksConfig>(ConfigFile); }
            catch (Exception ex)
            {
                // leave a broken file alone so the player's edits aren't lost; run on defaults this session
                api.Logger.Error("[Flocks] {0} is unreadable, using defaults: {1}", ConfigFile, ex.Message);
                return new FlocksConfig();
            }
            cfg ??= new FlocksConfig();
            try { api.StoreModConfig(cfg, ConfigFile); } // writes defaults, and adds new fields to an older file
            catch (Exception ex) { api.Logger.Warning("[Flocks] could not write {0}: {1}", ConfigFile, ex.Message); }
            return cfg;
        }

        public override void Dispose()   // no meshes on the server
        {
            foreach (var sp in species.Values) sp.Meshes?.Dispose();
        }
    }
}
