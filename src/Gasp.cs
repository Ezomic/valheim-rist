using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Tide-borne's capstone: after your stamina runs out in water, drowning damage waits six seconds
    /// before its first tick instead of one.
    ///
    /// The sea's real hazard is the empty bar, and a swimmer who gasps reaches a boat or the shore. It
    /// rescues and never extends range: the bar still empties exactly as fast, and the ticks that follow
    /// the first are the game's own. It replaces -10% swim stamina, which was the stone's own stat.
    ///
    /// Player.OnSwimming adds the frame time to m_drownDamageTimer while the bar is empty and ticks once
    /// it passes one second, then zeroes it. A postfix zeroes it again for the first five seconds of an
    /// empty bar, so the original's own addition never reaches one, and from the fifth second lets it
    /// run, which puts the first tick at the sixth. The count of empty seconds is this class's own and
    /// restarts whenever stamina is back or the swim was broken for half a second, since OnSwimming is
    /// only called while swimming. Only the local player: drowning is worked out where the player is owned.
    /// </summary>
    internal static class Gasp
    {
        internal const string Key = "*swim:gasp";

        private const float Wait = 6f;
        private const float Broken = 0.5f;

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
            return Effects.Cached(Key) > 0f
                ? "gasp: carved, drowning waits " + Wait.ToString("0", CultureInfo.InvariantCulture)
                  + "s, held back " + _held.ToString("0.0", CultureInfo.InvariantCulture) + "s here"
                : "gasp: not carved";
        }
    }
}
