using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Two cards that widen what the world gives back rather than what you do to it.
    ///
    /// Both ride numbers the game already owns and already recomputes, so neither needs a
    /// restore path and neither can leak: the explore radius is read fresh on every explore
    /// tick, and the wind factor is recomputed every physics step from the ship's heading.
    ///
    /// They are here together because they answer the same design problem. Rist's move-speed
    /// card had to be capped at 10% - a permanent speed buff is always on, never noticed, and
    /// a player told us it had made his unmodded playthroughs feel broken. These two cannot do
    /// that. A bigger map radius is felt only while exploring ground you have not seen, and a
    /// narrower dead cone is felt only at sea with a sail up. Neither follows you home.
    /// </summary>
    internal static class Horizon
    {
        internal const string ExploreRadius = "*exploreradius";
        internal const string WindCone = "*windcone";
        internal const string TackSpeed = "*tackspeed";

        /// <summary>Fraction above vanilla. 0.25 means a quarter further. Set by Effects.</summary>
        internal static float ExtraExplore;

        /// <summary>How far the dead-upwind cone is narrowed, 0 to 1. Set by Effects.</summary>
        internal static float ConeNarrowing;

        /// <summary>Extra sail force while tacking, as a fraction. Set by Effects.</summary>
        internal static float TackBonus;

        /// <summary>
        /// Vanilla's own radius, captured once so the card is a multiplier on the real value
        /// rather than on whatever it was last set to.
        ///
        /// Re-read rather than assumed 100: it is a public field on a MonoBehaviour, so a game
        /// update or another mod can have moved it, and multiplying our own previous answer is
        /// how a value walks away over a session.
        /// </summary>
        private static float _vanillaRadius = -1f;

        /// <summary>
        /// The map's explore radius, applied where the game reads it.
        ///
        /// Minimap.UpdateExplore calls Explore(position, m_exploreRadius) on a timer, so the
        /// field is consulted fresh every interval and setting it is enough - there is no
        /// per-frame write to fight and nothing to undo when the cards change.
        ///
        /// A prefix on UpdateExplore rather than a one-time write at login, because the field
        /// belongs to a Minimap that is rebuilt per world and would otherwise keep a value from
        /// the last one.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "UpdateExplore")]
        internal static class Radius
        {
            [HarmonyPrefix]
            private static void Widen(Minimap __instance)
            {
                if (__instance == null) return;

                // Captured on the first pass of each Minimap, before anything has been written.
                if (_vanillaRadius < 0f) _vanillaRadius = __instance.m_exploreRadius;

                var wanted = _vanillaRadius * (1f + Mathf.Max(0f, ExtraExplore));

                // Compared before writing: this runs on a timer, not every frame, but an
                // unconditional write would still hide a value another mod had set between
                // ticks, and the comparison costs nothing.
                if (!Mathf.Approximately(__instance.m_exploreRadius, wanted))
                    __instance.m_exploreRadius = wanted;
            }
        }

        /// <summary>
        /// Sailing closer to the wind.
        ///
        /// Ship.GetWindAngleFactor is the whole sailing model:
        ///
        ///     float num  = Dot(windDir, -transform.forward);
        ///     float num2 = Lerp(0.7f, 1f, 1f - Abs(num));      // across the wind is best
        ///     float num3 = 1f - LerpStep(0.75f, 0.8f, num);    // dead upwind is nothing
        ///     return num2 * num3;
        ///
        /// So the usable range is 0.7 to 1.0, and the reason tacking exists at all is num3 -
        /// the cone where the sail simply stops working.
        ///
        /// This card lifts num3 and leaves num2 alone, which is the whole point. Raising num2
        /// would be a flat speed bonus wearing a nautical hat, and that is the mistake the
        /// move-speed card already made. Narrowing the dead cone changes what a course can be,
        /// not how fast the boat is - at full rank you can point nearer the wind before the
        /// sail dies, and on every other heading nothing changes at all.
        ///
        /// Clamped to the vanilla ceiling so the factor can never exceed what a perfect beam
        /// reach gives you. A card that made upwind the fastest point of sail would be absurd.
        /// </summary>
        [HarmonyPatch(typeof(Ship), nameof(Ship.GetWindAngleFactor))]
        internal static class WindAngle
        {
            [HarmonyPostfix]
            private static void Narrow(Ship __instance, ref float __result)
            {
                if (__instance == null || EnvMan.instance == null) return;
                if (ConeNarrowing <= 0f && TackBonus <= 0f) return;

                var into = Vector3.Dot(EnvMan.instance.GetWindDir(), -__instance.transform.forward);

                // Nothing downwind or on a beam reach. Both cards are about beating into the
                // wind, and a bonus that also helped a following wind would be a flat speed
                // buff wearing a nautical hat - the mistake the move-speed card already made.
                if (into <= 0f) return;

                var across = Mathf.Lerp(0.7f, 1f, 1f - Mathf.Abs(into));
                var vanillaCone = 1f - Mathf.Clamp01((into - 0.75f) / 0.05f);

                // A floor under the cone rather than a shift of its edges: simpler to reason
                // about, and it cannot open the cone so far that sailing straight into the
                // wind becomes free.
                var cone = Mathf.Max(vanillaCone, Mathf.Clamp01(ConeNarrowing));

                var factor = across * cone;
                if (TackBonus > 0f) factor *= 1f + TackBonus;

                // Never below vanilla's own answer, and never above a perfect beam reach plus
                // whatever the tack capstone is worth. Clamped in one place at the end, which
                // is the only way this stays readable once two cards feed it.
                __result = Mathf.Clamp(factor, __result, across * (1f + Mathf.Max(0f, TackBonus)));
            }
        }
    }
}
