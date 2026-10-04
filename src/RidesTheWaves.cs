using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Weatherly's capstone: slams into waves do no hull damage while you hold the helm.
    ///
    /// The rank bends the sail to windward; this keeps the hull in weather, the other half of staying
    /// afloat. It replaces 20% rowing speed, which sat on the sail's own job as a second kind of speed.
    ///
    /// Ship.UpdateWaterForce damages the hull with m_waterImpactDamage (10 blunt) when the ship drops
    /// onto the water faster than m_minWaterImpactForce, at most every couple of seconds, and only with
    /// players aboard. Upside-down damage and the Ashlands' are other methods and stay. The ship's physics
    /// run on whichever client owns the ship, and that is not always the helmsman, so the helmsman is found
    /// from ShipControlls.GetUser and the stone is read from the flag word on their player's ZDO
    /// (Carried), which every client can read. While that helmsman carries it the damage is set to zero for
    /// the call and put back in a finalizer, so a throw cannot leave a ship that never takes a wave again.
    /// A passenger's stone does nothing; the helm has to be held.
    /// </summary>
    internal static class RidesTheWaves
    {
        internal const string Key = Carried.WavesKey;

        [HarmonyPatch(typeof(Ship), "UpdateWaterForce")]
        internal static class Slam
        {
            [HarmonyPrefix]
            private static void Spare(Ship __instance, out float __state)
            {
                __state = -1f;
                if (!RistConfig.Enabled.Value || __instance.m_shipControlls == null) return;

                var helm = __instance.m_shipControlls.GetUser();
                if (helm == 0L || !Carried.Has(Player.GetPlayer(helm), Carried.RidesTheWaves)) return;

                __state = __instance.m_waterImpactDamage;
                __instance.m_waterImpactDamage = 0f;
            }

            [HarmonyFinalizer]
            private static void Restore(Ship __instance, float __state)
            {
                if (__state >= 0f) __instance.m_waterImpactDamage = __state;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "rides the waves: carved, hull slams do nothing while you hold the helm"
                : "rides the waves: not carved";
        }
    }
}
