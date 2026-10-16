using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
            api.Network.RegisterChannel("flocks").RegisterMessageType<SendFlockPacket>();
            api.RegisterEntityBehaviorClass(EntityBehaviorWaterfowl.Code, typeof(EntityBehaviorWaterfowl));
            api.RegisterEntityBehaviorClass(EntityBehaviorRaptor.Code, typeof(EntityBehaviorRaptor));
            api.RegisterEntityBehaviorClass(EntityBehaviorCorvid.Code, typeof(EntityBehaviorCorvid));
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            species.Clear();
            foreach (var kv in Species.LoadAll(api)) species[kv.Key] = kv.Value;
        }

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
            var p = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.Create("flocks").WithDescription("Flocks (client-side birds)").RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("send").WithDescription("Send a visual flock over you: /flocks send [count] [species, e.g. goose, crane, hawk]").RequiresPlayer()
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    ch.SendPacket(new SendFlockPacket { Command = "send", Count = (int)args[0], Species = args[1] as string }, (IServerPlayer)args.Caller.Player);
                    return TextCommandResult.Success();
                })
                .EndSubCommand()
                .BeginSubCommand("schedule").WithDescription("Show today's migration plan and why").RequiresPlayer()
                .HandleWith(args =>
                {
                    ch.SendPacket(new SendFlockPacket { Command = "schedule" }, (IServerPlayer)args.Caller.Player);
                    return TextCommandResult.Success();
                })
                .EndSubCommand()
                .BeginSubCommand("land").WithDescription("Debug: put a group of birds on open water (crows: open ground) near you: /flocks land [count] [species, default goose]").RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalInt("count", 0), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    var sp = LandingSpecies(args[1] as string, out string err);
                    return sp == null ? TextCommandResult.Error(err) : Landing.Land(sapi, args.Caller.Entity, (int)args[0], sp);
                })
                .EndSubCommand()
                .BeginSubCommand("arrive").WithDescription("Debug: send a landing flock to a lake near you now (any season): /flocks arrive [species, default goose]").RequiresPlayer()
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
                .BeginSubCommand("calm").WithDescription("Admin: calm every flock and forget abandoned lakes (lake wariness)")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => { registry.Calm(); return TextCommandResult.Success("All flocks calm; no lakes abandoned."); })
                .EndSubCommand()
                .BeginSubCommand("near").WithDescription("Debug: the birds within 48 blocks of you, what they are doing, and whether they sense you").RequiresPlayer()
                .HandleWith(args =>
                {
                    var me = args.Caller.Entity;
                    var lines = new List<string>();
                    foreach (var e in sapi.World.GetEntitiesAround(me.Pos.XYZ, 48, 48, e => e.Alive && e.GetBehavior<EntityBehaviorBird>() != null))
                    {
                        if (lines.Count >= 12) { lines.Add("..."); break; }
                        lines.Add(e.GetBehavior<EntityBehaviorBird>().Diagnose(args.Caller.Player));
                    }
                    return TextCommandResult.Success(lines.Count == 0 ? "No Flocks birds within 48 blocks." : string.Join("\n", lines));
                })
                .EndSubCommand()
                .BeginSubCommand("residents").WithDescription("Debug: resident geese state (spawn checks and vetoes, geese near each player, season)")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => TextCommandResult.Success(residents.Report() + "\n" + registry.Report()))
                .EndSubCommand();
        }

        /// <summary>A species that lands (has a landed entity), by code; default goose.</summary>
        private Species LandingSpecies(string code, out string err)
        {
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            err = null;
            if (species.TryGetValue(code, out var sp) && sp.Lands) return sp;
            err = $"No landing species '{code}'. Landing species: {string.Join(", ", species.Values.Where(s => s.Lands).Select(s => s.Code))}.";
            return null;
        }

        private string TrySend(int count, string code, out bool ok)
        {
            int n = GameMath.Clamp(count, 1, 60);
            ok = false;
            if (species == null || renderer == null) return "Flocks client part not running.";
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            if (!species.TryGetValue(code, out var sp)) return $"Unknown species '{code}'. Try: {string.Join(", ", species.Keys)}.";
            if (sp.Meshes?.Ok != true) return $"No {code} meshes loaded; see [Flocks] lines in client-main.log.";
            renderer.Send(sp, n); ok = true;
            return $"Sent {n} {code} ({sp.Meshes.Loaded}) overhead from ~{renderer.VisibleRange(sp):0} blocks out ({renderer.Count} flock(s) in the sky).";
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
            api.Network.GetChannel("flocks").SetMessageHandler<SendFlockPacket>(pkt =>
            {
                string msg = pkt.Command == "schedule" ? Schedule() : TrySend(pkt.Count, pkt.Species, out _);
                api.ShowChatMessage("[Flocks] " + msg);
            });
            var p = api.ChatCommands.Parsers;
            api.ChatCommands.Create("flocks").WithDescription("Flocks (client-only birds)")
                .BeginSubCommand("send").WithDescription("Send a visual flock over you: /flocks send [count] [species, e.g. goose, crane, hawk]")
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    string msg = TrySend((int)args[0], args[1] as string, out bool ok);
                    return ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
                })
                .EndSubCommand()
                .BeginSubCommand("schedule").WithDescription("Show today's migration plan and why")
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
