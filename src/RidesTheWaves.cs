using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Weatherly's capstone: while you hold the helm, slams into waves do no hull damage, and a collision with
    /// rocks, ice or the shore costs the hull half as much.
    ///
    /// The rank bends the sail to windward; this keeps the hull in weather, the other half of staying
    /// afloat. It replaces 20% rowing speed, which sat on the sail's own job as a second kind of speed. The
    /// collision half was added by the capstone review, because a hull that survives the sea and not the
    /// reef is half a promise.
    ///
    /// Ship.UpdateWaterForce damages the hull with m_waterImpactDamage (10 blunt) when the ship drops
    /// onto the water faster than m_minWaterImpactForce, at most every couple of seconds, and only with
    /// players aboard. Upside-down damage and the Ashlands' are other methods and stay. The ship's physics
    /// run on whichever client owns the ship, and that is not always the helmsman, so the helmsman is found
    /// from ShipControlls.GetUser and the stone is read from the flag word on their player's ZDO
    /// (Carried), which every client can read. While that helmsman carries it the damage is set to zero for
    /// the call and put back in a finalizer, so a throw cannot leave a ship that never takes a wave again.
    /// A passenger's stone does nothing; the helm has to be held.
    ///
    /// Collisions are ImpactEffect.OnCollisionEnter on the ship, whose m_triggerMask picks the layers it reacts
    /// to and which damages the ship through WearNTear.Damage in the same call. So the prefix notes which ship
    /// is colliding when its helmsman carries the stone, and a prefix on WearNTear.Damage takes the share off
    /// the hit that same ship is handed while that note stands, and nothing else: an enemy's blow on the hull
    /// arrives outside the collision handler and is untouched. The share is RidesTheWavesCollision, 0.50.
    /// </summary>
    internal static class RidesTheWaves
    {
        internal const string Key = Carried.WavesKey;

        private static Ship _colliding;
        private static int _softened;
        private static float _lastCut;

        private static bool Helm(Ship ship)
        {
            if (!RistConfig.Enabled.Value || ship == null || ship.m_shipControlls == null) return false;

            var helm = ship.m_shipControlls.GetUser();
            return helm != 0L && Carried.Has(Player.GetPlayer(helm), Carried.RidesTheWaves);
        }

        [HarmonyPatch(typeof(Ship), "UpdateWaterForce")]
        internal static class Slam
        {
            [HarmonyPrefix]
            private static void Spare(Ship __instance, out float __state)
            {
                __state = -1f;
                if (!Helm(__instance)) return;

                __state = __instance.m_waterImpactDamage;
                __instance.m_waterImpactDamage = 0f;
            }

            [HarmonyFinalizer]
            private static void Restore(Ship __instance, float __state)
            {
                if (__state >= 0f) __instance.m_waterImpactDamage = __state;
            }
        }

        [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
        internal static class Collide
        {
            [HarmonyPrefix]
            private static void Note(ImpactEffect __instance)
            {
                var ship = __instance.GetComponentInParent<Ship>();
                _colliding = Helm(ship) ? ship : null;
            }

            [HarmonyFinalizer]
            private static void Forget()
            {
                _colliding = null;
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage))]
        internal static class Soften
        {
            [HarmonyPrefix]
            private static void Cushion(WearNTear __instance, HitData hit)
            {
                if (_colliding == null || hit == null) return;
                if (!ReferenceEquals(__instance.GetComponent<Ship>(), _colliding)) return;

                var cut = Mathf.Clamp01(RistConfig.RidesTheWavesCollision.Value);
                if (cut <= 0f) return;

                hit.ApplyModifier(1f - cut);
                _softened++;
                _lastCut = cut;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "rides the waves: carved, hull slams do nothing while you hold the helm, collisions cost "
                  + ((int)((1f - Mathf.Clamp01(RistConfig.RidesTheWavesCollision.Value)) * 100f)).ToString(CultureInfo.InvariantCulture)
                  + "% of the damage, softened " + _softened + " here"
                  + (_softened == 0 ? "" : ", last " + ((int)(_lastCut * 100f)).ToString(CultureInfo.InvariantCulture) + "% off")
                : "rides the waves: not carved";
        }
    }
}
