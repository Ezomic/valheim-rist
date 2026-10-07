using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Quiet wake's capstone: crouched and no faster than a crouch-walk, you make no noise at all.
    ///
    /// "You leave less behind you" taken to its end. The stone's ranks already shrink every noise by 8% a rank
    /// (the game's own m_noiseModifier, applied where a noise is recorded); the capstone removes the noise
    /// altogether while you are sneaking at the pace of a creep or slower, standing included. It replaces
    /// Sleepers sleep on, which kept sleeping creatures asleep and mattered only where creatures sleep, and
    /// that code, with the flag it published for the creature's owner, is gone.
    ///
    /// How the game makes your noise. Every sound a character makes goes through Character.AddNoise(range),
    /// which on the machine that owns the character is RPC_AddNoise: it raises m_noiseRange to the range if
    /// it is larger, runs it through the status effects' ModifyNoise, and the value decays at 4 a second. A
    /// creature hears you when you are inside that range (BaseAI.CanHearTarget, through GetNoiseRange). The
    /// callers are walking (15, or 30 running, and none at all when crouched: a vanilla crouch-walk is already
    /// silent on foot), jumping (30), rolling (5), swimming (15), every swing (the attack's own start and
    /// hit noise), and building or breaking things (50 and more, the hit noise of a tree or a rock sent to
    /// the nearest player). So the capstone is not about footsteps, which a crouch already removes; it is about
    /// everything else a creeper does, a swing or a plank, at the pace where they are sneaking.
    ///
    /// The rule, exactly. For the local player, while the stone is carved and SilentStep is on: when the
    /// character is crouching (the game's own IsCrouching, the animator state) and its flat speed is at most
    /// the crouch-walk speed (Character.m_crouchSpeed, 2 m/s, with ten percent of slack for the speed
    /// ramping), a noise is dropped before it is recorded. Faster than that, or standing up, it is the
    /// game's. Both entry points are patched, AddNoise for the owner's own calls and RPC_AddNoise for the
    /// ones other machines send (a tree you hit is reported by the machine that owns the tree), so one rule
    /// holds for both. The noise already recorded decays in a couple of seconds, as it always does.
    /// A bow drawn from a crouch stands you up for the animation, so a shot is not silenced by this; Low draw
    /// is the stone for that.
    /// </summary>
    internal static class SilentStep
    {
        internal const string Key = "*noise:silent";

        private const float Slack = 1.1f;

        private static int _silenced;
        private static float _lastRange;

        private static AccessTools.FieldRef<Character, float> _noise;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _noise = AccessTools.FieldRefAccess<Character, float>("m_noiseRange");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Silent step could not reach the game's noise field, so `rist noise` is off: " + e.Message);
            }

            return _bound;
        }

        /// <summary>True when this character's noise is to be dropped.</summary>
        internal static bool Silent(Character character)
        {
            if (!RistConfig.Enabled.Value || !RistConfig.SilentStep.Value) return false;
            if (!ReferenceEquals(character, Player.m_localPlayer) || Effects.Cached(Key) <= 0f) return false;
            if (!character.IsCrouching()) return false;

            var velocity = character.GetVelocity();
            velocity.y = 0f;
            return velocity.magnitude <= character.m_crouchSpeed * Slack;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.AddNoise))]
        internal static class Made
        {
            [HarmonyPrefix]
            private static bool Quietly(Character __instance, float range)
            {
                if (!Silent(__instance)) return true;

                _silenced++;
                _lastRange = range;
                return false;
            }
        }

        [HarmonyPatch(typeof(Character), "RPC_AddNoise")]
        internal static class Reported
        {
            [HarmonyPrefix]
            private static bool Quietly(Character __instance, float range)
            {
                if (!Silent(__instance)) return true;

                _silenced++;
                _lastRange = range;
                return false;
            }
        }

        /// <summary>
        /// Makes a noise of range 30, the size of a jump, from a cleared slate, and reads what the game recorded.
        /// For `rist noise`.
        /// </summary>
        internal static string Make(Player player)
        {
            if (!Bind()) return "rist: the game's noise field was not found.";

            _noise(player) = 0f;
            player.AddNoise(30f);
            var heard = player.GetNoiseRange();

            var velocity = player.GetVelocity();
            velocity.y = 0f;
            return "rist: noise asked 30.0, emitted " + heard.ToString("0.0", CultureInfo.InvariantCulture)
                   + " (crouching " + (player.IsCrouching() ? "yes" : "no")
                   + ", speed " + velocity.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s)";
        }

        /// <summary>Crouches or stands the character, as the key does. For `rist crouch`.</summary>
        internal static string Crouch(Player player, bool on)
        {
            var set = AccessTools.Method(typeof(Player), "SetCrouch");
            if (set == null) return "rist: the game's SetCrouch was not found.";

            set.Invoke(player, new object[] { on });
            return "rist: crouch " + (on ? "on" : "off") + ", give the animation a second.";
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "silent step: carved" + (RistConfig.SilentStep.Value ? "" : " but switched off")
                  + ", silenced " + _silenced + " noises here"
                  + (_silenced == 0 ? "" : ", last asked " + _lastRange.ToString("0", CultureInfo.InvariantCulture))
                : "silent step: not carved";
        }
    }
}
