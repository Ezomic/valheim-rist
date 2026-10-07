using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Long stride's capstone: run in a straight line for five seconds and you gain a little speed, which
    /// you keep until you stop or turn sharply.
    ///
    /// "Ground passes under you" as something earned by the run, not worn all the time. It replaces Steep
    /// ground (the slide angle), and the jump height the stone gave before that stays removed, with the
    /// landing guard that went with it. The bonus is 5% movement speed (MomentumBonus), reached over one
    /// second (MomentumRamp) after the five seconds (MomentumSeconds) so it does not pop.
    ///
    /// The move-speed note in cards.txt says the stone's ranks are the one speed source, capped at 10%, and
    /// the reason it gives is that a speed bonus which is always on is never noticed and its absence is
    /// what gets felt. Momentum is the exception that argument allows for: it exists only while running in
    /// a line, it is gone the moment you stop, and the stone's ranks (+2% a rank) are untouched, so a full
    /// run at rank five is 15% over a walking jog and 10% without the streak. That is 5 points over the
    /// documented cap, on purpose, and the balance note says so.
    ///
    /// What counts. Running in the game's sense: Character.IsRunning, the sprint that drains stamina and is
    /// refused while you crouch, so sneaking never counts. Swimming, being attached (a ship's helm or a
    /// chair), standing on a ship's deck, riding and rolling do not count. The speed must stay at or above
    /// the walking speed on the ground, so running into a wall or up a cliff ends it. A jump does not end it,
    /// because the sprint input is still held in the air and the game's run flag does not look at the
    /// ground. A sharp turn does: the heading of the run is the direction of travel, a reference direction
    /// follows it at 45 degrees a second, and the streak restarts when the two come 40 degrees apart. A
    /// gradual curve stays inside that, a corner does not.
    ///
    /// The speed is applied where the game works out a runner's speed, Player.GetRunSpeedFactor, which the
    /// game reads only when it is running, so a walk, a sneak or a swim cannot get it by accident. It
    /// multiplies the factor, as the stone's own SE_Stats speed does at its place. Local player only.
    /// </summary>
    internal static class Momentum
    {
        internal const string Key = "*run:momentum";

        private const float TurnLimit = 40f;
        private const float TurnRate = 45f;

        private static bool _tracking;
        private static float _streak;
        private static Vector3 _ref;

        private static bool _probing;
        private static float _driveUntil = -1f;
        private static float _plateau, _after;

        private static float Seconds => Mathf.Max(0f, RistConfig.MomentumSeconds.Value);
        private static float RampTime => Mathf.Max(0.01f, RistConfig.MomentumRamp.Value);
        private static float Bonus => Mathf.Max(0f, RistConfig.MomentumBonus.Value);

        private static bool Carved => RistConfig.Enabled.Value && Effects.Cached(Key) > 0f;

        private static float Ramp(float streak)
        {
            return Mathf.Clamp01((streak - Seconds) / RampTime);
        }

        internal static float Multiplier => Carved ? 1f + Bonus * Ramp(_streak) : 1f;

        private static void Reset()
        {
            _tracking = false;
            _streak = 0f;
        }

        private static bool Counts(Player player)
        {
            if (!player.IsRunning() || player.IsSwimming() || player.IsAttached() || player.IsRiding()) return false;
            if (player.InDodge() || player.GetStandingOnShip() != null) return false;
            return true;
        }

        /// <summary>From the plugin's Update, once a frame.</summary>
        internal static void Tick(Player player, float dt)
        {
            if (!Carved || !Counts(player))
            {
                Reset();
                return;
            }

            var velocity = player.GetVelocity();
            velocity.y = 0f;
            if (velocity.magnitude < player.m_walkSpeed)
            {
                Reset();
                return;
            }

            var heading = velocity.normalized;
            if (!_tracking)
            {
                _tracking = true;
                _streak = 0f;
                _ref = heading;
                return;
            }

            if (Vector3.Angle(_ref, heading) > TurnLimit)
            {
                _streak = 0f;
                _ref = heading;
                return;
            }

            _ref = Vector3.RotateTowards(_ref, heading, TurnRate * Mathf.Deg2Rad * dt, 0f);
            _streak += dt;

            if (_driveUntil > Time.time)
            {
                var speed = velocity.magnitude;
                if (_streak > 2f && _streak < Seconds) _plateau = Mathf.Max(_plateau, speed);
                if (Ramp(_streak) >= 1f) _after = Mathf.Max(_after, speed);
            }
        }

        [HarmonyPatch(typeof(Player), "GetRunSpeedFactor")]
        internal static class Speed
        {
            [HarmonyPostfix]
            private static void Faster(Player __instance, ref float __result)
            {
                if (_probing || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                __result *= Multiplier;
            }
        }

        /// <summary>Holds the run key and walks forward for a test drive, in place of the player's own input.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        internal static class Drive
        {
            [HarmonyPrefix]
            private static void Forward(Player __instance, ref Vector3 movedir, ref bool run)
            {
                if (_driveUntil <= Time.time || !ReferenceEquals(__instance, Player.m_localPlayer)) return;

                movedir = Vector3.forward;
                run = true;
            }
        }

        internal static void Forget()
        {
            Reset();
            _driveUntil = -1f;
        }

        /// <summary>
        /// Pretends the run has gone on for <paramref name="seconds"/> and reads the game's own run speed factor
        /// with and without Momentum. For `rist momentum`.
        /// </summary>
        internal static string Simulate(Player player, float seconds)
        {
            if (!Carved) return "rist: Momentum is not carved, so a run gains nothing.";

            var factor = AccessTools.Method(typeof(Player), "GetRunSpeedFactor");
            if (factor == null) return "rist: the game's GetRunSpeedFactor was not found.";

            _tracking = true;
            _streak = seconds;

            var with = (float)factor.Invoke(player, null);
            _probing = true;
            float without;
            try
            {
                without = (float)factor.Invoke(player, null);
            }
            finally
            {
                _probing = false;
            }

            return "rist: after " + seconds.ToString("0.0#", CultureInfo.InvariantCulture)
                   + " s of running the game's run speed factor is " + with.ToString("0.000", CultureInfo.InvariantCulture)
                   + " against " + without.ToString("0.000", CultureInfo.InvariantCulture)
                   + " without Momentum, x" + (without > 0f ? with / without : 0f).ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>Runs straight ahead for a number of seconds, in place of input, and keeps the speeds it saw. For `rist run`.</summary>
        internal static string Run(Player player, float seconds)
        {
            _driveUntil = Time.time + seconds;
            _plateau = 0f;
            _after = 0f;
            return "rist: running straight ahead for " + seconds.ToString("0.#", CultureInfo.InvariantCulture)
                   + " s. Needs open flat ground and the look direction as it is.";
        }

        internal static string Probe()
        {
            if (!Carved) return "momentum: not carved";

            var line = "momentum: carved, streak " + _streak.ToString("0.0", CultureInfo.InvariantCulture)
                       + " s, bonus +" + (Bonus * Ramp(_streak) * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";

            if (_plateau > 0f && _after > 0f)
                line += ", drive speed " + _plateau.ToString("0.00", CultureInfo.InvariantCulture) + " m/s before and "
                        + _after.ToString("0.00", CultureInfo.InvariantCulture) + " after (x"
                        + (_after / _plateau).ToString("0.000", CultureInfo.InvariantCulture) + ")";

            return line;
        }
    }
}
