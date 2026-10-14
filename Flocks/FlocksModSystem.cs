using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>Step 1 skeleton: client logs season, latitude and migration heading once per in-game day.</summary>
    public class FlocksModSystem : ModSystem
    {
        private ICoreClientAPI capi;
        private int lastDay = int.MinValue;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            api.Event.RegisterGameTickListener(OnTick, 5000);
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
    }
}
