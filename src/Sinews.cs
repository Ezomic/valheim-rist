using System;
using System.Globalization;
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

        /// <summary>
        /// Fraction added to how HIGH you jump, so 0.15 is 15% higher. Not jump force: the rise
        /// goes with the square of the push, so a fifth more force was 44% more height, while the
        /// tile and the changelog both said "a fifth higher" about a jump that went from 3.0m to
        /// 4.4m. Robbin caught it by doing the sum on 2026-09-24. The card now names the thing a
        /// player sees, and the code works out the push.
        /// </summary>
        internal const string JumpHeight = "*jumpheight";

        /// <summary>
        /// Stamina walking costs while over your carry limit, negative like the other stamina
        /// modifiers: -0.5 is half. Vanilla drains Player.m_encumberedStaminaDrain, 10 a second,
        /// whenever an overloaded character moves, and stops regen outright while it is over -
        /// so this is how far you can shuffle before you have to drop something.
        /// </summary>
        internal const string Overloaded = "*overloaded";

        /// <summary>Sure-footed's capstone: a dodge pressed as you land makes the fall shorter. A flag, written 1.</summary>
        internal const string LandingRoll = "*landing:roll";

        /// <summary>
        /// Never below a quarter of vanilla, whatever the catalogue asks. Being over the limit
        /// also stops regen and takes away running, jumping and dodging, and a drain of zero
        /// would leave crouch speed as the only price of carrying anything - the limit switched
        /// off rather than eased. Same reasoning as MinDelay: a typo should not reach it.
        /// </summary>
        private const float MinOverloadDrain = 0.25f;

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

        /// <summary>Fraction of extra jump HEIGHT the hand asks for. See JumpHeight.</summary>
        internal static float JumpBonus;

        /// <summary>Fraction off the overloaded drain, zero or negative. See Overloaded.</summary>
        internal static float OverloadMod;

        // The player these were captured from, and the values it had before Rist touched them.
        private static Player _player;
        private static float _vanillaDelay;
        private static float _vanillaJump;
        private static float _vanillaOverload;

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

        internal static float VanillaOverload(Player player)
        {
            if (ReferenceEquals(player, _player) && _vanillaOverload > 0f) return _vanillaOverload;
            return player == null ? 0f : player.m_encumberedStaminaDrain;
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
        /// Jump 100 rises about 3.04m, and the first build of this capstone added a fifth to
        /// jump force, which is 44% more height: 4.38m, over the line on flat ground, and every
        /// jump hurt. The card is 15% of HEIGHT now, about 3.5m, which clears the line on its
        /// own - but that margin is Unity's project gravity and the Jump skill's curve, neither
        /// of which this mod owns, so the guard stays. No bonus size is safe for certain, because
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

            /// <summary>
            /// Metres a landing roll takes off a fall. Eight is 50% of the 16 m between the first
            /// hurt (4 m) and a lethal fall (20 m), so a roll turns a fatal 24 m drop into a
            /// 16 m one and a 12 m drop into one that does not hurt at all, and it comes off the
            /// height before Sure-footed's own percentage, which then applies to what is left.
            /// </summary>
            private const float RollMetres = 8f;

            /// <summary>
            /// How long before touchdown the dodge may be pressed. The game queues a press for
            /// 0.5 s (Player.Dodge sets m_queuedDodgeTimer), which would let a roll pressed
            /// halfway down count. A third of a second is a press made as the ground arrives.
            /// </summary>
            private const float RollWindow = 0.3f;

            private static float _pressedAt = -1f;
            private static bool _rollSaidOnce;

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
                _pressedAt = -1f;
            }

            /// <summary>
            /// Its own class so that a game update that moves Player.Dodge costs the landing roll
            /// and not the jump guard beside it: PatchAll(type) throws for the whole class.
            /// </summary>
            internal static class Press
            {
                /// <summary>
                /// The moment a dodge is asked for, which is the only moment that can be told from the
                /// roll itself: Player.Dodge only queues it, and UpdateDodge runs it once the character
                /// is on the ground, which is the same physics step as the landing and may come before
                /// or after the prefix below. The queue timer is therefore not safe to read at the
                /// landing, but the press is.
                ///
                /// Recorded only when the roll can happen. Dodge queues even with an empty bar and
                /// UpdateDodge then refuses it with a flash, and a landing roll for a roll that never
                /// took place would be a discount for nothing. Encumbered is refused by Dodge itself.
                /// </summary>
                [HarmonyPrefix]
                [HarmonyPatch(typeof(Player), "Dodge")]
                private static void Pressed(Player __instance)
                {
                    if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                    if (Effects.Cached(LandingRoll) <= 0f || __instance.IsEncumbered()) return;

                    // The rest of what UpdateDodge asks before it starts a roll. Dodge queues for
                    // half a second whatever the state, and a press the game then refuses must not
                    // count. Ground is left out: the press is made in the air by design.
                    if (__instance.IsDead() || __instance.InAttack() || __instance.IsStaggering()
                        || __instance.InDodge()) return;

                    // Eel-slick's armed free roll prices the dodge at nothing inside UpdateDodge, so
                    // the bar is not asked for it here either.
                    if (!EelSlick.FreeArmed(__instance))
                    {
                        try
                        {
                            var cost = AccessTools.Method(typeof(Player), "GetDodgeStaminaUse");
                            if (cost != null && !__instance.HaveStamina((float)cost.Invoke(__instance, null))) return;
                        }
                        catch (Exception e)
                        {
                            RistPlugin.Log.LogWarning("Landing roll could not price the dodge, so it does not check "
                                + "the bar: " + e.Message);
                        }
                    }

                    _pressedAt = Time.time;
                }
            }

            /// <summary>
            /// Takes the fall's height down by RollMetres when a roll was pressed in time. Runs on
            /// the landing step, before the game measures the fall, and always spends the press: a
            /// dodge made before a ledge must not be saved for the next drop.
            ///
            /// Also remembers every damaging fall of the local player and what the game will charge
            /// for it, for `rist show`. Fall damage is not readable afterwards in god mode, which is
            /// how a scenario has to run, so the figure is worked out here through the same
            /// SEMan.ModifyFallDamage the game calls.
            /// </summary>
            private static void Roll(Character landing)
            {
                if (!RistConfig.Enabled.Value || !ReferenceEquals(landing, Player.m_localPlayer)) return;
                if (_maxAir == null || _groundContact == null || !_groundContact(landing)) return;

                var pressed = _pressedAt;
                _pressedAt = -1f;

                ref var apex = ref _maxAir(landing);
                var y = landing.transform.position.y;
                var fall = apex - y;
                if (fall <= 4f) return;

                var rolled = pressed >= 0f && Time.time - pressed <= RollWindow && Effects.Cached(LandingRoll) > 0f;
                if (rolled)
                {
                    apex = y + Mathf.Max(0f, fall - RollMetres);

                    if (landing is Player player)
                        player.Message(MessageHud.MessageType.TopLeft, "Landing roll");
                }

                var counted = apex - y;
                var damage = Mathf.Clamp01((counted - 4f) / 16f) * 100f;
                var seman = landing.GetSEMan();
                if (seman != null) seman.ModifyFallDamage(damage, ref damage);

                _lastFall = fall;
                _lastCounted = counted;
                _lastRolled = rolled;
                _lastDamage = damage;

                if (rolled && (!_rollSaidOnce || RistConfig.Verbose.Value))
                {
                    _rollSaidOnce = true;
                    RistPlugin.Log.LogInfo("Landing roll: fell " + fall.ToString("0.00")
                        + "m, measured as " + counted.ToString("0.00") + "m. Fall damage starts at 4m.");
                }
            }

            private static float _lastFall = -1f, _lastCounted, _lastDamage;
            private static bool _lastRolled;

            /// <summary>What `rist show` prints about the last damaging fall. Whole numbers, so it asserts.</summary>
            internal static string Probe()
            {
                if (_lastFall < 0f) return "no fall measured yet";

                return "last fall " + _lastFall.ToString("0", CultureInfo.InvariantCulture)
                    + "m, counted " + _lastCounted.ToString("0", CultureInfo.InvariantCulture)
                    + "m, landing roll " + (_lastRolled ? "yes" : "no")
                    + ", fall damage " + _lastDamage.ToString("0", CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// A fall without a drop, for `rist fall`: raises the record of how high the player has
            /// been to <paramref name="metres"/> above where they stand. The next physics step with
            /// ground contact is a landing from that height, so the whole path runs, the game's
            /// own measurement and damage included, with nobody needing to climb anything. With
            /// <paramref name="roll"/> a dodge is pressed in the same call, which is inside the
            /// window by construction.
            /// </summary>
            internal static string Fall(Player player, float metres, bool roll)
            {
                if (_maxAir == null) return "rist: the landing guard is not bound, so a fall cannot be staged.";
                if (!player.IsOnGround()) return "rist: stand on the ground first.";

                if (roll)
                {
                    var dodge = AccessTools.Method(typeof(Player), "Dodge");
                    if (dodge == null) return "rist: the game's Dodge method was not found.";
                    dodge.Invoke(player, new object[] { player.transform.forward });
                }

                _maxAir(player) = player.transform.position.y + metres;
                return "rist: falling " + metres.ToString("0.#", CultureInfo.InvariantCulture) + "m" + (roll ? ", with a roll" : "") + ".";
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
                JumpGuard(__instance);
                SafeRoll(__instance);
            }

            private static void JumpGuard(Character __instance)
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
            private static bool _rollFailedOnce;

            /// <summary>
            /// Runs after the jump guard, never before: Long stride's correction brings a raised
            /// jump back down to what a vanilla jump would have fallen, and the roll then takes its
            /// eight metres off that. In the other order the roll came first and the correction,
            /// which only ever lowers the apex it is given, worked on a figure the roll had already
            /// changed. Caught so that a throw costs the roll and never the guard or the physics step.
            /// </summary>
            private static void SafeRoll(Character landing)
            {
                try
                {
                    Roll(landing);
                }
                catch (Exception e)
                {
                    if (_rollFailedOnce) return;
                    _rollFailedOnce = true;
                    RistPlugin.Log.LogError("Landing roll failed and is skipped: " + e);
                }
            }

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
                    }
                    else
                    {
                        RistPlugin.Log.LogError("Long stride: the landing guard is not attached, so its "
                            + "jump bonus is withheld this session. A higher jump measured from its full "
                            + "height hurts on landing at high Jump skill.");
                    }
                }
                catch (Exception e)
                {
                    // Lazily and inside a try/catch, never in a static initialiser: a throwing one
                    // poisons every patch the class carries.
                    RistPlugin.Log.LogError("Long stride: could not confirm the landing guard ("
                        + e.Message + "), so its jump bonus is withheld this session.");
                }

                try
                {
                    if (!Attached(AccessTools.Method(typeof(Player), "Dodge"), harmonyId, "Pressed", prefix: true))
                        RistPlugin.Log.LogError("Landing roll: the dodge press is not attached, so Sure-footed's "
                            + "capstone never rolls this session. The game's Player.Dodge probably moved.");
                }
                catch (Exception e)
                {
                    RistPlugin.Log.LogError("Landing roll: could not confirm the dodge press (" + e.Message + ").");
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
            OverloadMod = 0f;
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
                _vanillaOverload = player.m_encumberedStaminaDrain;
            }

            player.m_staminaRegenDelay = Mathf.Max(MinDelay, _vanillaDelay - Mathf.Max(0f, DelayCut));

            // Multiplied, not added, because the Jump skill already multiplies the same field by
            // up to 1.4 at level 100 and the card should read as a share of your jump rather
            // than a flat push that matters less the better you get.
            //
            // The square root is the height-to-push conversion. How high a jump rises goes with
            // the square of the speed it leaves the ground at, and that speed is m_jumpForce
            // times the Jump skill's factor, so asking for 15% more height means sqrt(1.15),
            // about 7% more push. Measured at Jump 100: 20% more push rose 4.38m where vanilla
            // rises about 3.04m, which is 1.44 - the square of 1.2 - to two decimals.
            //
            // The Player prefab carries 8, not the 10 Character's field initialiser suggests -
            // measured in game, and the reason `rist show` prints this as a ratio. A scenario
            // asserting the absolute number off the decompiled default would have failed.
            //
            // Only while the landing guard is attached. Without it a raised jump is measured from
            // its full height, and at Jump 100 that is fall damage on every jump on flat ground -
            // which is what the first build of this capstone did, found in testing on 2026-09-24.
            // No bonus is better than a bonus that hurts.
            var height = Landing.Guarded ? Mathf.Max(0f, JumpBonus) : 0f;
            player.m_jumpForce = _vanillaJump * Mathf.Sqrt(1f + height);

            player.m_encumberedStaminaDrain = _vanillaOverload
                * Mathf.Max(MinOverloadDrain, 1f + Mathf.Min(0f, OverloadMod));
        }
    }
}
