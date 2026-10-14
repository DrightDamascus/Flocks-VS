using System;
using System.Collections.Generic;
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
        private Dictionary<string, Species> species;
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
        }

        /// <summary>/flocks (typed with a slash) goes to the server; it only relays the subcommand to the caller's client.</summary>
        private ResidentGeese residents;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            residents = new ResidentGeese(sapi, LoadServerConfig(sapi));
            var ch = sapi.Network.GetChannel("flocks");
            var p = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.Create("flocks").WithDescription("Flocks (client-side birds)").RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("send").WithDescription("Send a visual flock over you: /flocks send [count] [goose|crane|hawk]").RequiresPlayer()
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
                .BeginSubCommand("land").WithDescription("Debug: spawn 5-12 greylag geese on open water near you: /flocks land [count]").RequiresPlayer()
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalInt("count", 0))
                .HandleWith(args => Landing.Land(sapi, args.Caller.Entity, (int)args[0]))
                .EndSubCommand()
                .BeginSubCommand("residents").WithDescription("Debug: resident geese state (spawn checks and vetoes, geese near each player, season)")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => TextCommandResult.Success(residents.Report()))
                .EndSubCommand();
        }

        private string TrySend(int count, string code, out bool ok)
        {
            int n = GameMath.Clamp(count, 1, 60);
            ok = false;
            if (species == null || renderer == null) return "Flocks client part not running.";
            code = (code ?? DefaultSpecies).ToLowerInvariant();
            if (!species.TryGetValue(code, out var sp)) return $"Unknown species '{code}'. Try: {string.Join(", ", species.Keys)}.";
            if (!sp.Meshes.Ok) return $"No {code} meshes loaded; see [Flocks] lines in client-main.log.";
            renderer.Send(sp, n); ok = true;
            return $"Sent {n} {code} ({sp.Meshes.Loaded}) overhead from ~{renderer.VisibleRange(sp):0} blocks out ({renderer.Count} flock(s) in the sky).";
        }

        private string Schedule() => scheduler?.Report() ?? "Flocks client part not running.";

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            config = LoadConfig(api);
            species = Species.Create(api);
            renderer = new FlockRenderer(api);
            scheduler = new MigrationScheduler(api, renderer, species, config);
            api.Event.RegisterGameTickListener(scheduler.Tick, 1000);
            api.Event.LevelFinalize += () =>
            {
                foreach (var sp in species.Values) sp.Meshes.Load();
                if (!rendererRegistered) { api.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "flocks"); rendererRegistered = true; }
            };
            api.Network.GetChannel("flocks").SetMessageHandler<SendFlockPacket>(pkt =>
            {
                string msg = pkt.Command == "schedule" ? Schedule() : TrySend(pkt.Count, pkt.Species, out _);
                api.ShowChatMessage("[Flocks] " + msg);
            });
            var p = api.ChatCommands.Parsers;
            api.ChatCommands.Create("flocks").WithDescription("Flocks (client-only birds)")
                .BeginSubCommand("send").WithDescription("Send a visual flock over you: /flocks send [count] [goose|crane|hawk]")
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

        public override void Dispose() { if (species != null) foreach (var sp in species.Values) sp.Meshes.Dispose(); }
    }
}
