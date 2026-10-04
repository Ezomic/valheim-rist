using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Steady footing's capstone: three seconds after you recover from a stagger, nothing staggers you again.
    ///
    /// The ranks make a stagger harder to start. This is for the stagger that happened anyway, and it
    /// ends the chain: a Troll's blow, the second blow while you are still getting your feet, the
    /// third. It replaces another 5% of armour, which was Thick-hided's stat on a stone about balance.
    ///
    /// This is the alternative the design pass offered for Rooted (no knockback from a creature's hits),
    /// taken on purpose and not by accident. Rooted as written would have made Steady footing the obvious
    /// pick: no shove from a Troll or a Lox at all is a lot more than a stagger bar, and it would have
    /// made knockback, which is how a fight is escaped as well as how it is lost, a thing the player
    /// never meets. The guard keeps the first stagger exactly as dangerous as it was and only refuses
    /// the chain behind it.
    ///
    /// "Recover" is the moment IsStaggering goes from true to false, seen from the plugin's Update, and
    /// the guard is a clock from that moment. The refusal is in RPC_Stagger, the same place Quick chant's
    /// guard and Answering blow's secondary guard sit, because the owner's own Stagger calls it directly
    /// and a remote attacker's arrives through it. The stagger bar still fills and still flashes on a hit,
    /// and the stagger damage the hit carried is still worked out; only the stagger itself is refused.
    /// A stagger that began before the guard stands and runs its course. Only the local player.
    /// </summary>
    internal static class FootingBack
    {
        internal const string Key = "*stagger:guard";

        private const float Guard = 3f;

        private static bool _was;
        private static float _until = -1f;
        private static int _refused;

        private static bool Carved => RistConfig.Enabled.Value && Effects.Cached(Key) > 0f;

        [HarmonyPatch(typeof(Character), "RPC_Stagger")]
        internal static class Refuse
        {
            [HarmonyPrefix]
            private static bool Staggering(Character __instance)
            {
                if (_until < 0f || Time.time >= _until) return true;
                if (!ReferenceEquals(__instance, Player.m_localPlayer) || !Carved) return true;

                _refused++;
                return false;
            }
        }

        /// <summary>From the plugin's Update: arms the guard on the frame the stagger ends.</summary>
        internal static void Tick(Player player)
        {
            if (player == null) return;

            var now = player.IsStaggering();
            if (_was && !now && Carved)
            {
                _until = Time.time + Guard;
                player.Message(MessageHud.MessageType.TopLeft, "Footing back");
            }

            _was = now;
        }

        internal static void Forget()
        {
            _was = false;
            _until = -1f;
        }

        /// <summary>A stagger the way an attacker sends one, for `rist stagger`.</summary>
        internal static string Stagger(Player player)
        {
            player.Stagger(-player.transform.forward);
            return "rist: stagger sent.";
        }

        internal static string Probe()
        {
            if (Effects.Cached(Key) <= 0f) return "footing back: not carved";

            var left = _until - Time.time;
            return "footing back: carved, " + (left > 0f
                       ? "guard armed " + left.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                       : "guard not armed")
                   + ", refused " + _refused + " staggers";
        }
    }
}
