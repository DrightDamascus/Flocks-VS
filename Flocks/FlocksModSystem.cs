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
    /// <summary>
    /// Flocks' mod system. The server runs the birds: residents, landing flocks, the registry, and the passing flocks
    /// (SkyFlocks, sent over in season by the MigrationScheduler). The client draws the passing flocks the server reports
    /// (FlockRenderer) and runs the flock camera.
    /// </summary>
    public class FlocksModSystem : ModSystem
    {
        /// <summary>This side's species (config/flocks-species/), filled when assets load; the same dictionary is handed
        /// to everything that needs it, so it is complete before the world runs.</summary>
        private readonly Dictionary<string, Species> species = new();
        public Dictionary<string, Species> SpeciesByCode => species;
        private FlockRenderer renderer;
        private MigrationScheduler scheduler;
        private SkyFlocks sky;
        public const string DefaultSpecies = "goose";
        public const string ConfigFile = "flocks.json";

        private bool rendererRegistered;

        public override void Start(ICoreAPI api)
        {
            api.Network.RegisterChannel("flocks").RegisterMessageType<SkyFlockPacket>().RegisterMessageType<FlockCamPacket>();
            // the birds' behaviours, by the codes the entity files name them with
            foreach (var (code, type) in new[] {
                (EntityBehaviorWaterfowl.Code, typeof(EntityBehaviorWaterfowl)), (EntityBehaviorRaptor.Code, typeof(EntityBehaviorRaptor)),
                (EntityBehaviorCorvid.Code, typeof(EntityBehaviorCorvid)), (EntityBehaviorOwl.Code, typeof(EntityBehaviorOwl)) })
                api.RegisterEntityBehaviorClass(code, type);
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            species.Clear();
            foreach (var kv in Species.LoadAll(api)) species[kv.Key] = kv.Value;
        }

        public override void AssetsFinalize(ICoreAPI api) => Scarecrows.HandbookText(api);

        private ResidentGeese residents;
        private FlockRegistry registry;
        private LandingFlocks landings;
        private FlockCamera camera;
        private FlockJourney journeys;
        /// <summary>The server's landing planner (null on the client).</summary>
        public LandingFlocks Landings => landings;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            residents = new ResidentGeese(sapi, ReadConfig<FlocksServerConfig>(sapi, ServerConfigFile), species);
            ScaleNumbers(sapi, residents.Cfg.Numbers);
            IServerNetworkChannel channel = sapi.Network.GetChannel("flocks");
            camera = new FlockCamera(sapi, channel);
            journeys = new FlockJourney(sapi, camera, (sp, spring, at) =>
                MoveClockToSeason(sapi, sp, spring, at, 0, out double moved) ? $"{moved:0.0} days on, {sapi.World.Calendar.PrettyDate()}" : sapi.World.Calendar.PrettyDate());
            registry = new FlockRegistry(sapi, residents.Cfg);
            landings = new LandingFlocks(sapi, species);
            sky = new SkyFlocks(sapi, channel, species);
            // flocks.json: the passing flocks' schedule settings (in single player the same file the client reads)
            scheduler = new MigrationScheduler(sapi, sky, species, ReadConfig<FlocksConfig>(sapi, ConfigFile), residents.Cfg);
            var parsers = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.Create("flocks").WithDescription(Lang.Get("flocks:cmd-flocks")).RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("send").WithDescription(Lang.Get("flocks:cmd-send")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalInt("count", 12), parsers.OptionalWord("species"), parsers.OptionalWord("watch"))
                .HandleWith(args => Send((IServerPlayer)args.Caller.Player, (int)args[0], args[1] as string, args[2] as string))
                .EndSubCommand()
                .BeginSubCommand("schedule").WithDescription(Lang.Get("flocks:cmd-schedule")).RequiresPlayer()
                .HandleWith(args =>
                {
                    var plr = (IServerPlayer)args.Caller.Player;
                    return TextCommandResult.Success(scheduler.Report(plr, LocalSchedule(sapi, plr)));
                })
                .EndSubCommand()
                .BeginSubCommand("season").WithDescription(Lang.Get("flocks:cmd-season")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("season"), parsers.OptionalWord("species"), parsers.OptionalDouble("days", 0))
                .HandleWith(args => JumpToSeason(sapi, args.Caller.Entity, args[0] as string, args[1] as string, (double)args[2]))
                .EndSubCommand()
                .BeginSubCommand("depart").WithDescription(Lang.Get("flocks:cmd-depart")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalWord("species"), parsers.OptionalWord("watch"))
                .HandleWith(args =>
                {
                    string code = SpeciesAndWatch(args, out bool watch);
                    var res = DepartNearest(sapi, args.Caller.Entity, code, out long id, out Vec3d at);
                    return watch && res.Status == EnumCommandStatus.Success ? Watch(res, (IServerPlayer)args.Caller.Player, id, at) : res;
                })
                .EndSubCommand()
                .BeginSubCommand("journey").WithDescription(Lang.Get("flocks:cmd-journey")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalWord("species"))
                .HandleWith(args => StartJourney(sapi, (IServerPlayer)args.Caller.Player, args[0] as string))
                .EndSubCommand()
                .BeginSubCommand("unwatch").WithDescription(Lang.Get("flocks:cmd-unwatch")).RequiresPlayer()
                .HandleWith(args => camera.Stop((IServerPlayer)args.Caller.Player)
                    ? TextCommandResult.Success(Lang.Get("flocks:watch-stopped")) : TextCommandResult.Error(Lang.Get("flocks:watch-none")))
                .EndSubCommand()
                .BeginSubCommand("land").WithDescription(Lang.Get("flocks:cmd-land")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalInt("count", 0), parsers.OptionalWord("species"))
                .HandleWith(args =>
                {
                    var sp = LandingSpecies(args[1] as string, out string err);
                    return sp == null ? TextCommandResult.Error(err) : Landing.Land(sapi, args.Caller.Entity, (int)args[0], sp);
                })
                .EndSubCommand()
                .BeginSubCommand("arrive").WithDescription(Lang.Get("flocks:cmd-arrive")).RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalWord("species"), parsers.OptionalWord("watch"))
                .HandleWith(args =>
                {
                    var sp = LandingSpecies(SpeciesAndWatch(args, out bool watch), out string err);
                    if (sp == null) return TextCommandResult.Error(err);
                    var plr = (IServerPlayer)args.Caller.Player;
                    if (!landings.Arrive(plr, sp, true, out string msg)) return TextCommandResult.Error(msg);
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
                .WithArgs(parsers.OptionalWord("pose"), parsers.OptionalDouble("spacing", 2.5))
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

        /// <summary>A command's answer: shown as a success or as an error.</summary>
        private static TextCommandResult Reply(bool ok, string msg) => ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);

        /// <summary>
        /// /flocks send [count] [species] [watch] (admin: the birds are real near players, so huntable): a passing flock of
        /// 1-60 birds of the species (default goose) sets off toward the player now, from where it is out of sight. With
        /// "watch" the player becomes a spectator and follows it.
        /// </summary>
        private TextCommandResult Send(IServerPlayer plr, int count, string code, string w)
        {
            bool watch = code == "watch" || w == "watch";
            if (code == "watch") code = w == "watch" ? null : w;
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            if (!species.TryGetValue(code, out var sp)) return TextCommandResult.Error(Lang.Get("flocks:send-unknown", code, string.Join(", ", species.Keys)));
            if (!residents.Cfg.SpeciesEnabled(sp)) return TextCommandResult.Error(Lang.Get("flocks:animal-off", sp.DisplayName));
            if (watch)
            {
                string err = camera.Begin(plr);
                if (err != null) return TextCommandResult.Error(err);
            }
            var rand = new Random();
            int birds = GameMath.Clamp(count, 1, 60);
            var f = sky.Launch(sp, birds, plr.Entity.Pos.XYZ, rand.NextDouble() * Math.PI * 2, sp.RandomAltitude(rand));
            string msg = Lang.Get("flocks:send-sent", birds, sp.DisplayName, SkyFlocks.StartRange.ToString("0"), sky.All.Count);
            if (!watch) return TextCommandResult.Success(msg);
            camera.FollowSky(plr, f.Id, f.Centre);
            return TextCommandResult.Success(msg + " " + Lang.Get("flocks:watch-started"));
        }

        private FlockCameraClient camClient;

        public override void StartClientSide(ICoreClientAPI api)
        {
            var config = ReadConfig<FlocksConfig>(api, ConfigFile);
            renderer = new FlockRenderer(api, species, config.FlockCallVolume);
            api.Event.LevelFinalize += () => OnLevelReady(api);
            var channel = api.Network.GetChannel("flocks");
            camClient = new FlockCameraClient(api, channel, renderer);
            channel.SetMessageHandler<SkyFlockPacket>(renderer.Apply);
            api.Logger.Notification("[Flocks] client loaded");
        }

        /// <summary>Once the world is loaded on the client: the handbook text, each species' drawn-flock meshes and size,
        /// and (the first time) the renderer.</summary>
        private void OnLevelReady(ICoreClientAPI api)
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
            if (rendererRegistered) return;
            rendererRegistered = true;
            api.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "flocks");
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

        /// <summary>
        /// A config file from ModConfig, or the defaults when there is none yet. It is written back straight away, which
        /// creates it on the first run and adds any settings newer than the file. A file that can't be read is left as
        /// it is, so a typo doesn't cost the player their edits, and this session runs on the defaults.
        /// </summary>
        private static T ReadConfig<T>(ICoreAPI api, string file) where T : class, new()
        {
            T settings;
            try { settings = api.LoadModConfig<T>(file) ?? new T(); }
            catch (Exception ex)
            {
                api.Logger.Error("[Flocks] {0} is unreadable, using defaults: {1}", file, ex.Message);
                return new T();
            }
            try { api.StoreModConfig(settings, file); }
            catch (Exception ex) { api.Logger.Warning("[Flocks] could not write {0}: {1}", file, ex.Message); }
            return settings;
        }

        public override void Dispose() { foreach (var sp in species.Values) sp.Meshes?.Dispose(); } // no meshes on the server
    }
}
