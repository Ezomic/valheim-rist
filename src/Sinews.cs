using System;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The two numbers a card changes on the player's own body rather than through SE_Stats:
    /// how soon breath starts coming back, and how hard you push off the ground.
    ///
    /// Neither has a field on SE_Stats, so neither can be a plain catalogue line. Both are
    /// public fields on the character the game reads on its own schedule - m_staminaRegenDelay
    /// from RPC_UseStamina, m_jumpForce from Character.Jump - which is the same shape as the
    /// three numbers Horizon keeps, and the reason both are written here instead of patched.
    /// Patching either would mean running mod code on every point of stamina spent or every
    /// jump, to hand back a number that changes only when a hand of cards does.
    ///
    /// Written to the live Player, never to the prefab. A respawn builds a new Player carrying
    /// the prefab's values again, and Effects.Apply re-runs on a player change for exactly that
    /// reason, so the capture below re-reads the untouched numbers each time.
    /// </summary>
    internal static class Sinews
    {
        /// <summary>Seconds off the pause before stamina starts returning.</summary>
        internal const string StaminaDelay = "*staminadelay";

        /// <summary>Fraction added to jump force, so 0.2 is a fifth higher.</summary>
        internal const string JumpForce = "*jumpforce";

        /// <summary>
        /// The pause can be shortened and never removed, and this is not tidiness.
        ///
        /// Regen runs only while m_staminaRegenTimer has run out, and every point of stamina
        /// spent puts the timer back to m_staminaRegenDelay. Sprinting spends every frame, so
        /// any delay above zero is reset before it can elapse and stamina cannot come back
        /// while you run. At exactly zero that stops being true: regen would run during the
        /// sprint, out-pace the drain at any reasonable Long wind, and hand back the free
        /// sprinting that MinRunStaminaCost was added to stop - through a different door, and
        /// this time with no clamp in the way. A mis-typed catalogue line is all it would take,
        /// so the floor is here rather than in the catalogue's good intentions.
        /// </summary>
        private const float MinDelay = 0.25f;

        internal static float DelayCut;
        internal static float JumpBonus;

        // The player these were captured from, and the values it had before Rist touched them.
        private static Player _player;
        private static float _vanillaDelay;
        private static float _vanillaJump;

        /// <summary>
        /// What the character had before Rist wrote to it, for the readout to print a ratio
        /// against. Falls back to the live values when nothing has been captured yet, so a
        /// character holding no cards reads x1.00 rather than dividing by zero.
        /// </summary>
        internal static float VanillaJump(Player player)
        {
            if (ReferenceEquals(player, _player) && _vanillaJump > 0f) return _vanillaJump;
            return player == null ? 0f : player.m_jumpForce;
        }

        internal static float VanillaDelay(Player player)
        {
            if (ReferenceEquals(player, _player) && _vanillaDelay > 0f) return _vanillaDelay;
            return player == null ? 0f : player.m_staminaRegenDelay;
        }

        /// <summary>
        /// Your own jump never lands harder than a vanilla one.
        ///
        /// Fall damage is measured from the highest point since you last touched ground:
        /// Character.UpdateGroundContact takes m_maxAirAltitude less where you land, and anything
        /// over 4m hurts, (h - 4) / 16 of your health up to all of it at 20m. Vanilla's own
        /// Jump 100 already comes close to that line, so raising jump force by a fifth crossed
        /// it on flat ground and every jump hurt. No bonus size is safe for certain, because
        /// how close vanilla sits is Unity's project gravity and not readable from the assembly.
        ///
        /// So the landing is measured from the apex a vanilla jump would have reached. A jump's
        /// rise goes with the square of its launch speed, and launch speed is m_jumpForce times
        /// the Jump skill's factor, so the rise above takeoff is divided by the square of the
        /// ratio the jump was actually launched with. Only the rise is scaled: jump off a ledge
        /// and everything below where you left the ground counts in full, exactly as in vanilla.
        /// If air drag makes the real rise grow slower than the square, this forgives slightly
        /// more than the capstone added, never less, so the error lands in the player's favour.
        ///
        /// Three seams, because no one method knows both ends of a jump. Jump marks that the
        /// launch is a real jump rather than any other ForceJump caller, ForceJump records where
        /// and how hard it left the ground, and UpdateGroundContact corrects the altitude before
        /// the damage check reads it. That last one runs every physics step, in the air too, and
        /// acts only on a frame with ground contact - lowering the altitude mid-flight would be
        /// undone by the next Max() and forgive nothing.
        /// </summary>
        internal static class Landing
        {
            /// <summary>
            /// Longer than any jump stays in the air. A mark older than this belongs to a jump that
            /// ended somewhere with no ground contact - water, a ladder, a ship's rail - and must
            /// not be spent on the next unrelated fall.
            /// </summary>
            private const float Expiry = 6f;

            private static bool _inJump;
            private static Character _jumper;
            private static float _takeoffY;
            private static float _takeoffTime;
            private static float _ratio;

            private static AccessTools.FieldRef<Character, float> _maxAir;
            private static AccessTools.FieldRef<Character, bool> _groundContact;

            /// <summary>
            /// True once Harmony has confirmed both ends are attached and both fields bound. The
            /// capstone is withheld until then - see Sinews.Apply.
            /// </summary>
            internal static bool Guarded { get; private set; }

            internal static void Clear()
            {
                _inJump = false;
                _jumper = null;
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
            private static void JumpStart(Character __instance)
            {
                _inJump = ReferenceEquals(__instance, Player.m_localPlayer);
            }

            [HarmonyFinalizer]
            [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
            private static Exception JumpEnd(Exception __exception)
            {
                // A finalizer rather than a postfix, so a Jump that throws cannot leave the mark
                // on for whatever calls ForceJump next. Returning it rethrows it unchanged.
                _inJump = false;
                return __exception;
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Character), nameof(Character.ForceJump))]
            private static void Launched(Character __instance)
            {
                if (!_inJump || !(__instance is Player player)) return;

                // The ratio the jump was actually launched with, read off the character rather
                // than the hand, so the correction always matches what was really applied.
                var vanilla = VanillaJump(player);
                var ratio = vanilla > 0f ? player.m_jumpForce / vanilla : 1f;
                if (ratio <= 1.0001f) { _jumper = null; return; }

                _jumper = player;
                _takeoffY = player.transform.position.y;
                _takeoffTime = Time.time;
                _ratio = ratio;
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
            private static void Landed(Character __instance)
            {
                if (_jumper == null || !ReferenceEquals(__instance, _jumper)) return;

                if (Time.time - _takeoffTime > Expiry) { _jumper = null; return; }
                if (_maxAir == null || _groundContact == null) { _jumper = null; return; }

                // Still in the air. This runs every physics step and returns at once without
                // ground contact, and so must this.
                if (!_groundContact(__instance)) return;

                // This is the landing, however it goes.
                _jumper = null;

                ref var apex = ref _maxAir(__instance);
                var rise = apex - _takeoffY;
                if (rise <= 0.05f) return;

                var counted = rise / (_ratio * _ratio);
                apex = _takeoffY + counted;

                // Once a session at Info, so a test has the real heights to read without Verbose,
                // and every landing after that only when asked for.
                if (!_saidOnce || RistConfig.Verbose.Value)
                {
                    _saidOnce = true;
                    RistPlugin.Log.LogInfo("Long stride: jumped " + rise.ToString("0.00")
                        + "m above takeoff, landing measured from " + counted.ToString("0.00")
                        + "m as a vanilla jump would have reached. Fall damage starts at 4m.");
                }
            }

            private static bool _saidOnce;

            /// <summary>
            /// Ask Harmony whether both ends really are attached, and bind the two fields. Called
            /// by the plugin straight after it patches this class, the way OwnInventoryRows
            /// confirms its load guard: PatchAll returning is not proof, and a guard that is
            /// silently missing turns the capstone back into the bug.
            /// </summary>
            internal static void ConfirmGuard(string harmonyId)
            {
                try
                {
                    _maxAir = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
                    _groundContact = AccessTools.FieldRefAccess<Character, bool>("m_groundContact");

                    var landing = AccessTools.Method(typeof(Character), "UpdateGroundContact");
                    var launch = AccessTools.Method(typeof(Character), nameof(Character.ForceJump));

                    if (Attached(landing, harmonyId, nameof(Landed), prefix: true)
                        && Attached(launch, harmonyId, nameof(Launched), prefix: false))
                    {
                        Guarded = true;
                        RistPlugin.Log.LogInfo("Long stride: the landing guard is in place.");
                        return;
                    }

                    RistPlugin.Log.LogError("Long stride: the landing guard is not attached, so its "
                        + "jump bonus is withheld this session. A higher jump measured from its full "
                        + "height hurts on landing at high Jump skill.");
                }
                catch (Exception e)
                {
                    // Lazily and inside a try/catch, never in a static initialiser: a throwing one
                    // poisons every patch the class carries.
                    RistPlugin.Log.LogError("Long stride: could not confirm the landing guard ("
                        + e.Message + "), so its jump bonus is withheld this session.");
                }
            }

            private static bool Attached(System.Reflection.MethodBase target, string owner, string name, bool prefix)
            {
                if (target == null) return false;

                var info = Harmony.GetPatchInfo(target);
                if (info == null) return false;

                var list = prefix ? info.Prefixes : info.Postfixes;
                if (list == null) return false;

                foreach (var patch in list)
                {
                    // Ours specifically. Another mod patching the same method guards nothing here.
                    if (patch == null || patch.owner != owner || patch.PatchMethod == null) continue;
                    if (patch.PatchMethod.Name == name) return true;
                }

                return false;
            }
        }

        internal static void Reset()
        {
            _player = null;
            DelayCut = 0f;
            JumpBonus = 0f;
            Landing.Clear();
        }

        /// <summary>
        /// Put the current hand on the player. Called from Effects.Apply, which already returns
        /// early unless the cards or the player changed.
        /// </summary>
        internal static void Apply(Player player)
        {
            if (player == null) return;

            // Capture on a player change only. Capturing every call would read back a value
            // this class had already written and drift a little further every time a card was
            // taken - the delay walking toward its floor and the jump climbing without end.
            if (!ReferenceEquals(player, _player))
            {
                _player = player;
                _vanillaDelay = player.m_staminaRegenDelay;
                _vanillaJump = player.m_jumpForce;
            }

            player.m_staminaRegenDelay = Mathf.Max(MinDelay, _vanillaDelay - Mathf.Max(0f, DelayCut));

            // Multiplied, not added, because the Jump skill already multiplies the same field by
            // up to 1.4 at level 100 and the card should read as a share of your jump rather
            // than a flat push that matters less the better you get.
            //
            // The Player prefab carries 8, not the 10 Character's field initialiser suggests -
            // measured in game, and the reason `rist show` prints this as a ratio. A scenario
            // asserting the absolute number off the decompiled default would have failed.
            //
            // Only while the landing guard is attached. Without it a raised jump is measured from
            // its full height, and at Jump 100 that is fall damage on every jump on flat ground -
            // which is what the first build of this capstone did, found in testing on 2026-09-24.
            // No bonus is better than a bonus that hurts.
            var bonus = Landing.Guarded ? Mathf.Max(0f, JumpBonus) : 0f;
            player.m_jumpForce = _vanillaJump * (1f + bonus);
        }
    }
}
