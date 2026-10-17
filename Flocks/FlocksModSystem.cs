using System;
using System.Collections.Generic;
using System.Linq;
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
            api.Network.RegisterChannel("flocks").RegisterMessageType<SendFlockPacket>().RegisterMessageType<SkyRealmsPacket>();
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
        /// <summary>The server's landing planner (null on the client).</summary>
        public LandingFlocks Landings => landings;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            residents = new ResidentGeese(sapi, LoadServerConfig(sapi), species);
            var ch = sapi.Network.GetChannel("flocks");
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
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    ch.SendPacket(new SendFlockPacket { Command = "send", Count = (int)args[0], Species = args[1] as string }, (IServerPlayer)args.Caller.Player);
                    return TextCommandResult.Success();
                })
                .EndSubCommand()
                .BeginSubCommand("schedule").WithDescription(Lang.Get("flocks:cmd-schedule")).RequiresPlayer()
                .HandleWith(args =>
                {
                    ch.SendPacket(new SendFlockPacket { Command = "schedule" }, (IServerPlayer)args.Caller.Player);
                    return TextCommandResult.Success();
                })
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
                .WithArgs(p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    var sp = LandingSpecies(args[0] as string, out string err);
                    if (sp == null) return TextCommandResult.Error(err);
                    bool ok = landings.Arrive((IServerPlayer)args.Caller.Player, sp, true, out string msg);
                    return ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
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

        /// <summary>A species that lands (has a landed entity), by code; default goose.</summary>
        private Species LandingSpecies(string code, out string err)
        {
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            err = null;
            if (species.TryGetValue(code, out var sp) && sp.Lands) return sp;
            err = Lang.Get("flocks:cmd-no-landing-species", code, string.Join(", ", species.Values.Where(s => s.Lands).Select(s => s.Code)));
            return null;
        }

        private string TrySend(int count, string code, out bool ok)
        {
            int n = GameMath.Clamp(count, 1, 60);
            ok = false;
            if (species == null || renderer == null) return Lang.Get("flocks:send-not-running");
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            if (!species.TryGetValue(code, out var sp)) return Lang.Get("flocks:send-unknown", code, string.Join(", ", species.Keys));
            if (sp.Meshes?.Ok != true) return Lang.Get("flocks:send-no-meshes", sp.DisplayName);
            renderer.Send(sp, n); ok = true;
            return Lang.Get("flocks:send-sent", n, sp.DisplayName, renderer.VisibleRange(sp).ToString("0"), renderer.Count);
        }

        private string Schedule() => scheduler?.Report() ?? "Flocks client part not running.";

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
            api.Network.GetChannel("flocks").SetMessageHandler<SkyRealmsPacket>(pkt => scheduler.SetAbsent(pkt.Absent));
            api.Network.GetChannel("flocks").SetMessageHandler<SendFlockPacket>(pkt =>
            {
                string msg = pkt.Command == "schedule" ? Schedule() : TrySend(pkt.Count, pkt.Species, out _);
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

        public override void Dispose() { foreach (var sp in species.Values) sp.Meshes?.Dispose(); } // no meshes on the server
    }
}
