using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Far sight's capstone: the map also uncovers a second circle ahead of the way you walk, sail or ride.
    ///
    /// "The land gives up its shape" before you reach it. It replaces another 10% of map radius, which was
    /// the stone's own stat, and it is still a map-width stone at heart, so the balance note files it with
    /// the weakest: what it buys is ground uncovered in the direction of travel, not a bigger circle.
    ///
    /// Minimap.Explore(Vector3, float) has one caller, UpdateExplore, every two seconds, with the player's
    /// position and the radius Rist has already scaled. A postfix calls it once more with the centre moved
    /// ahead by that radius along the heading and a radius of 60% of it, so the new circle starts at the
    /// edge of the old one and overlaps it. Heading is the velocity of what carries you: the ship you steer
    /// or the mount you ride when there is one, else yourself, flattened to the ground and ignored below
    /// one metre a second, so standing still reveals nothing extra.
    ///
    /// The call goes through the patched method, so the radius prefix has to stand aside for it or it
    /// would scale the second circle a second time; Inside is how it knows. A re-entrance guard also stops
    /// the second call from asking for a third.
    /// </summary>
    internal static class Lookahead
    {
        internal const string Key = "*explore:ahead";

        private const float Share = 0.6f;
        private const float Still = 1f;

        /// <summary>True while the extra circle is being revealed, for the radius prefix to read.</summary>
        internal static bool Inside;

        private static MethodInfo _explore;
        private static int _circles;
        private static float _lastAhead;
        private static float _lastRadius;

        private static Vector3 Heading(Player player)
        {
            Rigidbody body = null;

            var controller = player.GetDoodadController();
            if (controller != null && controller.IsValid())
            {
                var carrier = controller.GetControlledComponent();
                if (carrier != null) body = carrier.GetComponent<Rigidbody>();
            }

            var velocity = body != null ? body.linearVelocity : player.GetVelocity();
            velocity.y = 0f;
            return velocity.magnitude < Still ? Vector3.zero : velocity.normalized;
        }

        [HarmonyPatch(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) })]
        internal static class Ahead
        {
            [HarmonyPostfix]
            private static void Reveal(Minimap __instance, Vector3 p, float radius)
            {
                if (Inside || !RistConfig.Enabled.Value || Effects.Cached(Key) <= 0f) return;

                var player = Player.m_localPlayer;
                if (player == null) return;

                var heading = Heading(player);
                if (heading == Vector3.zero) return;

                if (_explore == null)
                    _explore = AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) });
                if (_explore == null) return;

                Inside = true;
                try
                {
                    _explore.Invoke(__instance, new object[] { p + heading * radius, radius * Share });
                    _circles++;
                    _lastAhead = radius;
                    _lastRadius = radius * Share;
                }
                finally
                {
                    Inside = false;
                }
            }
        }

        internal static string Probe()
        {
            if (Effects.Cached(Key) <= 0f) return "lookahead: not carved";

            return "lookahead: carved, uncovered " + _circles + " extra circles" + (_circles == 0
                ? ""
                : ", last " + _lastAhead.ToString("0", CultureInfo.InvariantCulture) + " m ahead, radius "
                  + _lastRadius.ToString("0", CultureInfo.InvariantCulture) + " m");
        }
    }
}
