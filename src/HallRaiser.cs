using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Hall-raiser: a crafting station covers more ground, and at rank five your own arm reaches
    /// twice as far with the hammer.
    ///
    /// The station half. A station's reach is CraftingStation.m_rangeBuild plus a share per
    /// extension, worked out in GetStationBuildRange and tested by HaveBuildStationInRange: is a
    /// station of the piece's kind within that radius of the point, measured flat. That one test
    /// is what the build menu greys on, what PlacePiece refuses on, and what Skaft asks before it
    /// repairs a piece. It is the only thing the reach is for in the game's code; the distance you
    /// stand from a bench to craft at it is a separate field, m_useDistance, and is not changed.
    ///
    /// The bonus belongs to whoever is asking, not to the station. Every caller passes a point on
    /// the player's own machine, so the answer is worked out from the local hand and a second
    /// player standing at the same bench gets their own. The station's radius is never written:
    /// GetExtensions copies m_buildRange onto the station's effect-area collider, and that collider
    /// is the circle that keeps enemies from spawning in a base. Widening the field would widen the
    /// circle, which is the one thing this must not do. So the vanilla test is left to run and, only
    /// when it finds nothing, asked again with the radius scaled.
    ///
    /// The marker circle that shows the range while you build is drawn from the same private field,
    /// so it is redrawn at the widened radius on the frame it is shown, or it would point at a
    /// station and outline ground you are not inside.
    ///
    /// The hammer half is Player.m_maxPlaceDistance, the one number placing, removing, copying,
    /// hovering a piece and repairing all measure against. It is scaled only while the hammer's
    /// piece table is the tool in hand: the hoe and the cultivator read the same field, and Jafna's
    /// hoe reach is its own setting and must not double on the way. Scaled by a ratio on the field
    /// as it stands, not written from a stored original, so another mod's change to it survives.
    ///
    /// Skaft is untouched in what it does. Its sweep radius comes from the Crafting skill and is
    /// measured around the piece you are hovering, so a longer arm starts the same sweep from further
    /// away. Its station test goes through HaveBuildStationInRange and so follows the wider reach,
    /// the same as vanilla's own repair does.
    /// </summary>
    internal static class HallRaiser
    {
        internal const string Station = "*reach:station";
        internal const string Hammer = "*reach:hammer";

        private const string HammerTable = "_HammerPieceTable";

        private static System.Reflection.FieldInfo _stations;
        private static AccessTools.FieldRef<Player, PieceTable> _pieces;
        private static bool _bound, _bindFailed;

        private static Player _scaled;
        private static float _factor = 1f;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _stations = AccessTools.Field(typeof(CraftingStation), "m_allStations");
                if (_stations == null) throw new System.MissingFieldException("CraftingStation.m_allStations");
                _pieces = AccessTools.FieldRefAccess<Player, PieceTable>("m_buildPieces");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Hall-raiser could not reach the game's station list or build tool and is off " +
                                        "for this session: " + e.Message);
            }

            return _bound;
        }

        private static float StationBonus()
        {
            return RistConfig.Enabled.Value && Player.m_localPlayer != null ? Mathf.Max(0f, Effects.Cached(Station)) : 0f;
        }

        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HaveBuildStationInRange))]
        [HarmonyPostfix]
        private static void Wider(string name, Vector3 point, ref CraftingStation __result)
        {
            if (__result != null) return;

            var bonus = StationBonus();
            if (bonus <= 0f || !Bind()) return;

            foreach (var station in (List<CraftingStation>)_stations.GetValue(null))
            {
                if (station == null || station.m_name != name) continue;

                // The game's own test, flat, with the radius it would have used scaled.
                var flat = point;
                flat.y = station.transform.position.y;
                if (Vector3.Distance(station.transform.position, flat) < station.GetStationBuildRange() * (1f + bonus))
                {
                    __result = station;
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.ShowAreaMarker))]
        [HarmonyPostfix]
        private static void Marker(CircleProjector ___m_areaMarkerCircle, float ___m_buildRange)
        {
            if (___m_areaMarkerCircle == null) return;

            var bonus = StationBonus();
            if (bonus > 0f) ___m_areaMarkerCircle.m_radius = ___m_buildRange * (1f + bonus);
        }

        /// <summary>
        /// Scales the player's placing distance while the hammer is the tool in hand. From the
        /// plugin's Update, because what is in hand changes without any rank changing.
        /// </summary>
        internal static void Tick(Player player)
        {
            if (player == null || !Bind()) return;

            // A new Player carries the prefab's number again, so what was applied to the last one
            // no longer means anything.
            if (!ReferenceEquals(player, _scaled))
            {
                _scaled = player;
                _factor = 1f;
            }

            var want = 1f;
            if (RistConfig.Enabled.Value)
            {
                var bonus = Effects.Cached(Hammer);
                if (bonus > 0f && HammerInHand(player)) want = 1f + bonus;
            }

            if (Mathf.Approximately(want, _factor)) return;

            player.m_maxPlaceDistance = player.m_maxPlaceDistance / _factor * want;
            _factor = want;
        }

        private static bool HammerInHand(Player player)
        {
            var table = _pieces(player);
            return table != null && table.name == HammerTable;
        }

        /// <summary>Forget what was scaled. A rank reset writes nothing back to a Player that is gone.</summary>
        internal static void Forget()
        {
            _scaled = null;
            _factor = 1f;
        }

        /// <summary>
        /// What `rist show` prints. Everything is asked of the game's own functions, never worked
        /// out from the hand: the station reach is found by asking HaveBuildStationInRange at
        /// growing distances from the nearest station until it stops answering, and the circle
        /// that keeps enemies out is read off the station's own collider, which nothing here writes.
        /// </summary>
        internal static string Probe(Player player)
        {
            var near = Nearest(player);
            var line = "station reach: ";

            if (near == null)
            {
                line += "no station within 100 m";
            }
            else
            {
                var bare = near.GetStationBuildRange();
                var edge = Edge(near, bare * 4f);
                var ratio = bare > 0f ? edge / bare : 1f;

                line += Plain(near.m_name) + " " + bare.ToString("0.0", CultureInfo.InvariantCulture) + " m, reaches "
                        + edge.ToString("0.0", CultureInfo.InvariantCulture) + " m (x"
                        + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ")";

                var collider = near.m_effectAreaCollider as SphereCollider;
                line += collider == null
                    ? "  no-spawn circle: no sphere collider on this station"
                    : "  no-spawn circle x" + (bare > 0f ? collider.radius / bare : 1f).ToString("0.00", CultureInfo.InvariantCulture);
            }

            var reach = player.m_maxPlaceDistance;
            var vanilla = _factor > 0f ? reach / _factor : reach;
            var table = _bound ? _pieces(player) : null;
            var held = table == null ? "no build tool" : table.name;

            return line + "  hammer reach x" + _factor.ToString("0.00", CultureInfo.InvariantCulture) + " ("
                   + reach.ToString("0.0", CultureInfo.InvariantCulture) + " m of "
                   + vanilla.ToString("0.0", CultureInfo.InvariantCulture) + " m, " + held + ")";
        }

        private static string Plain(string name)
        {
            return string.IsNullOrEmpty(name) ? "station" : name.TrimStart('$');
        }

        private static CraftingStation Nearest(Player player)
        {
            CraftingStation best = null;
            var bestDistance = 100f;

            foreach (var station in Object.FindObjectsOfType<CraftingStation>())
            {
                if (station == null) continue;

                var d = Vector3.Distance(station.transform.position, player.transform.position);
                if (d >= bestDistance) continue;

                best = station;
                bestDistance = d;
            }

            return best;
        }

        /// <summary>
        /// The furthest flat distance from the station at which the game still says this station
        /// is in range, by bisection. Asked of the public function, so it includes every patch on it.
        /// </summary>
        private static float Edge(CraftingStation station, float limit)
        {
            var low = 0f;
            var high = limit;
            var origin = station.transform.position;

            for (var i = 0; i < 14; i++)
            {
                var mid = (low + high) * 0.5f;
                var found = CraftingStation.HaveBuildStationInRange(station.m_name, origin + new Vector3(mid, 0f, 0f));

                if (ReferenceEquals(found, station)) low = mid;
                else high = mid;
            }

            return low;
        }
    }
}
