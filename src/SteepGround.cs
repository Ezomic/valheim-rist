using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Long stride's capstone: you hold your footing on slopes up to 46 degrees instead of 38.
    ///
    /// "Ground passes under you" without a second speed source: the 10% cap and the one-source rule on
    /// movement speed are untouched, because this is about whether you stay on your feet, not how fast.
    /// It replaces +15% jump height, whose landing guard and special are gone with it.
    ///
    /// Character.ApplySlide starts a slide when the ground's angle passes GetSlideAngle, which returns
    /// 38 for a player and is read by ApplySlide alone. A postfix raises it for the local player. A
    /// player already runs up steep slopes in 1.0 while sprinting, by wall-running, so this is the
    /// walking half of that. It is always on, which the move-speed note counts against stones like this
    /// one, and it only matters on mountains, so the balance note files it with the strong ones and
    /// the narrow ones at once.
    ///
    /// GetSlideAngle is small, and a method the JIT inlines into its caller is a method a patch does not
    /// reach. The readout below asks the game through the patched method, which proves the patch and
    /// not the caller, so if the slope still slides in play with the capstone carved, the inlining is the
    /// first thing to suspect and a transpiler on ApplySlide the fix.
    /// </summary>
    internal static class SteepGround
    {
        internal const string Key = "*slope:steep";

        private const float Extra = 8f;

        private static MethodInfo _angle;

        [HarmonyPatch(typeof(Character), "GetSlideAngle")]
        internal static class Slide
        {
            [HarmonyPostfix]
            private static void Steeper(Character __instance, ref float __result)
            {
                if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (Effects.Cached(Key) > 0f) __result += Extra;
            }
        }

        internal static string Probe(Player player)
        {
            if (_angle == null) _angle = AccessTools.Method(typeof(Character), "GetSlideAngle");

            var angle = _angle == null ? -1f : (float)_angle.Invoke(player, null);
            return "steep ground: " + (Effects.Cached(Key) > 0f ? "carved" : "not carved")
                   + ", slide angle " + angle.ToString("0", CultureInfo.InvariantCulture) + " degrees";
        }
    }
}
