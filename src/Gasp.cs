using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Tide-borne's capstone: after your stamina runs out in water, drowning damage waits twelve seconds
    /// before its first tick instead of one.
    ///
    /// The sea's real hazard is the empty bar, and a swimmer who gasps reaches a boat or the shore. The wait
    /// rescues rather than extends: the bar empties as fast as the stone's ranks leave it, and the ticks that follow
    /// the first are the game's own. The capstone review added a second bite, 25% less swim stamina at rank
    /// five, as a companion (Card.CompanionsOf), so a swimmer also lasts longer, which the first draft refused.
    ///
    /// Player.OnSwimming adds the frame time to m_drownDamageTimer while the bar is empty and ticks once
    /// it passes one second, then zeroes it. A postfix zeroes it again until the empty bar is one second short of the
    /// wait, so the original's own addition never reaches one, and then lets it
    /// run, which puts the first tick at the wait (12 s by default). The count of empty seconds is this class's own and
    /// restarts whenever stamina is back or the swim was broken for half a second, since OnSwimming is
    /// only called while swimming. Only the local player: drowning is worked out where the player is owned.
    /// </summary>
    internal static class Gasp
    {
        internal const string Key = "*swim:gasp";

        private const float Broken = 0.5f;

        private static float Wait => Mathf.Max(1f, RistConfig.GaspSeconds.Value);

        private static AccessTools.FieldRef<Player, float> _timer;
        private static bool _bound, _bindFailed;

        private static float _dry;
        private static float _lastSwim = -10f;
        private static float _held;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _timer = AccessTools.FieldRefAccess<Player, float>("m_drownDamageTimer");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Gasp could not reach the game's drowning timer and is off for this "
                                        + "session: " + e.Message);
            }

            return _bound;
        }

        [HarmonyPatch(typeof(Player), "OnSwimming")]
        internal static class Swimming
        {
            [HarmonyPostfix]
            private static void Breathless(Player __instance, float dt)
            {
                if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (Effects.Cached(Key) <= 0f || !Bind()) return;

                if (Time.time - _lastSwim > Broken) _dry = 0f;
                _lastSwim = Time.time;

                if (__instance.HaveStamina())
                {
                    _dry = 0f;
                    return;
                }

                _dry += dt;
                if (_dry >= Wait - 1f) return;

                _timer(__instance) = 0f;
                _held += dt;
            }
        }

        internal static void Forget()
        {
            _dry = 0f;
            _lastSwim = -10f;
        }

        internal static string Probe()
        {
            var swim = "";
            var player = Player.m_localPlayer;
            if (player != null && player.GetSEMan() != null)
            {
                var factor = 1f;
                player.GetSEMan().ModifySwimStaminaUsage(1f, ref factor, minZero: false);
                swim = ", swim stamina x" + factor.ToString("0.00", CultureInfo.InvariantCulture);
            }

            return Effects.Cached(Key) > 0f
                ? "gasp: carved, drowning waits " + Wait.ToString("0", CultureInfo.InvariantCulture)
                  + "s, held back " + _held.ToString("0.0", CultureInfo.InvariantCulture) + "s here" + swim
                : "gasp: not carved" + swim;
        }
    }
}
