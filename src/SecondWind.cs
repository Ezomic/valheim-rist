using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Long wind's capstone: the first time your stamina runs out, a quarter of the bar comes back at
    /// once, and it cannot happen again for 90 seconds.
    ///
    /// Breath is the stone, and this is the gasp that buys one more roll or one more swing. It is not
    /// regen speed, so it does not stack on its own stat, and it replaces another 12% of regen, which was
    /// the same number twice. It shares a shape with Brimming's last cast, an emergency button that comes
    /// back with time, and the balance note says so. The 90 seconds is the tuning knob.
    ///
    /// Player.UseStamina is the one place stamina is spent from, for an owner it ends in RPC_UseStamina
    /// that floors the bar at zero, and the postfix sees the bar after that. A spend that leaves the bar
    /// empty while the cooldown is up is the moment. Sprinting spends every frame, so running dry on a
    /// sprint triggers it as well, which is the point: it is the same dry bar. A spend of nothing is
    /// ignored, since UseStamina returns early on zero and the bar did not just run out.
    ///
    /// Local player only, and only the owner's call counts, since that is the machine that holds the bar.
    /// </summary>
    internal static class SecondWind
    {
        internal const string Key = "*stamina:secondwind";

        private const float Back = 0.25f;
        private const float Cooldown = 90f;

        private static float _last = -1000f;
        private static int _used;

        [HarmonyPatch(typeof(Player), nameof(Player.UseStamina))]
        internal static class Spent
        {
            [HarmonyPostfix]
            private static void Gasp(Player __instance, float v)
            {
                if (v <= 0f || !RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (__instance.GetStamina() > 0.01f || Time.time - _last < Cooldown) return;
                if (Effects.Cached(Key) <= 0f) return;

                _last = Time.time;
                _used++;
                __instance.AddStamina(__instance.GetMaxStamina() * Back);
                __instance.Message(MessageHud.MessageType.TopLeft, "Second wind");
            }
        }

        internal static void Forget()
        {
            _last = -1000f;
        }

        /// <summary>Spends stamina the way the game does, for `rist spend`.</summary>
        internal static string Spend(Player player, float amount)
        {
            player.UseStamina(amount);
            return "rist: spent " + amount.ToString("0.#", CultureInfo.InvariantCulture) + " stamina.";
        }

        internal static string Probe(Player player)
        {
            var percent = player.GetMaxStamina() > 0f
                ? Mathf.RoundToInt(player.GetStamina() / player.GetMaxStamina() * 100f)
                : 0;

            if (Effects.Cached(Key) <= 0f) return "second wind: not carved, stamina " + percent + "%";

            var wait = _last + Cooldown - Time.time;
            return "second wind: carved, " + (wait > 0f
                       ? "ready in " + Mathf.CeilToInt(wait) + "s"
                       : "ready")
                   + ", used " + _used + " times, stamina " + percent + "%";
        }
    }
}
