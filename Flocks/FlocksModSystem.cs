using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using ProtoBuf;

namespace Flocks
{
    [ProtoContract]
    public class SendFlockPacket
    {
        [ProtoMember(1)] public int Count;
        [ProtoMember(2)] public string Species;
    }

    /// <summary>Step 1 skeleton: client logs season, latitude and migration heading once per in-game day.</summary>
    public class FlocksModSystem : ModSystem
    {
        private ICoreClientAPI capi;
        private int lastDay = int.MinValue;
        private BirdMeshes meshes;
        private FlockRenderer renderer;

        private bool rendererRegistered;

        public override void Start(ICoreAPI api)
        {
            api.Network.RegisterChannel("flocks").RegisterMessageType<SendFlockPacket>();
        }

        /// <summary>/flocks (typed with a slash) goes to the server; it just tells the caller's client to spawn the flock.</summary>
        public override void StartServerSide(ICoreServerAPI sapi)
        {
            var ch = sapi.Network.GetChannel("flocks");
            var p = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.Create("flocks").WithDescription("Flocks (client-side birds)").RequiresPrivilege(Privilege.chat).RequiresPlayer()
                .BeginSubCommand("send").WithDescription("Send a visual flock over you: /flocks send [count] [species]")
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    // Server side only relays: the client owns meshes and renderer.
                    ch.SendPacket(new SendFlockPacket { Count = (int)args[0], Species = args[1] as string }, (IServerPlayer)args.Caller.Player);
                    return TextCommandResult.Success();
                })
                .EndSubCommand();
        }

        private string TrySend(int count, string species, out bool ok)
        {
            int n = GameMath.Clamp(count, 1, 60);
            string sp = species ?? "hawk";
            ok = false;
            if (sp != "hawk") return "Only 'hawk' exists so far.";
            if (meshes == null || renderer == null) return "Flocks client part not running.";
            if (!meshes.Ok) return "No bird meshes loaded; see [Flocks] lines in client-main.log.";
            renderer.Send(n); ok = true;
            return $"Sent {n} birds ({meshes.Loaded}) overhead ({renderer.Count} flock(s) in the sky).";
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            api.Event.RegisterGameTickListener(OnTick, 5000);
            meshes = new BirdMeshes(api);
            renderer = new FlockRenderer(api, meshes);
            api.Event.LevelFinalize += () =>
            {
                meshes.Load();
                if (!rendererRegistered) { api.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "flocks"); rendererRegistered = true; }
            };
            api.Network.GetChannel("flocks").SetMessageHandler<SendFlockPacket>(pkt =>
            {
                string msg = TrySend(pkt.Count, pkt.Species, out _);
                api.ShowChatMessage("[Flocks] " + msg);
            });
            var p = api.ChatCommands.Parsers;
            api.ChatCommands.Create("flocks").WithDescription("Flocks (client-only birds)")
                .BeginSubCommand("send").WithDescription("Send a visual flock over you: /flocks send [count] [species]")
                .WithArgs(p.OptionalInt("count", 12), p.OptionalWord("species"))
                .HandleWith(args =>
                {
                    string msg = TrySend((int)args[0], args[1] as string, out bool ok);
                    return ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
                })
                .EndSubCommand();
            api.Logger.Notification("[Flocks] client loaded");
        }

        private void OnTick(float dt)
        {
            var player = capi.World?.Player?.Entity;
            if (player == null) return;
            var cal = capi.World.Calendar;
            int day = (int)cal.TotalDays;
            if (day == lastDay) return;
            lastDay = day;

            BlockPos pos = player.Pos.AsBlockPos;
            EnumSeason season = cal.GetSeason(pos);
            double lat = cal.OnGetLatitude(pos.Z);
            float rel = cal.GetSeasonRel(pos);
            string dir = MigrationDirection(rel, lat);
            capi.Logger.Notification("[Flocks] day {0}: season={1} seasonRel={2:0.00} latitude={3:0.000} hemisphere={4} migration={5}",
                day, season, rel, lat, cal.GetHemisphere(pos), dir);
        }

        /// <summary>
        /// seasonRel: 0 = start of spring (hemisphere-local). Spring migration window 0.05-0.25 heads poleward,
        /// autumn window 0.55-0.80 heads equatorward. Latitude sign gives which way "pole" is (north = -Z in VS).
        /// </summary>
        public static string MigrationDirection(float seasonRel, double latitude)
        {
            bool spring = seasonRel >= 0.05f && seasonRel < 0.25f;
            bool autumn = seasonRel >= 0.55f && seasonRel < 0.80f;
            if (!spring && !autumn) return "none";
            bool north = latitude >= 0;
            bool poleward = spring;
            bool headNorth = poleward == north;
            return (headNorth ? "north" : "south") + (spring ? " (spring, poleward)" : " (autumn, equatorward)");
        }
        public override void Dispose() { meshes?.Dispose(); }
    }
}




