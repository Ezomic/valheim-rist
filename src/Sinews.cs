using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The numbers a card changes on the player's own body rather than through SE_Stats: how soon breath
    /// starts coming back, and how much you can carry before it costs stamina to walk. Also the landing
    /// roll, which is the one thing here that is a patch and not a number.
    ///
    /// Neither number has a field on SE_Stats, so neither can be a plain catalogue line. Both are
    /// public fields on the character the game reads on its own schedule - m_staminaRegenDelay from
    /// RPC_UseStamina, m_encumberedStaminaDrain from the encumbered update - which is the same shape as
    /// the three numbers Horizon keeps, and the reason both are written here instead of patched. Patching
    /// either would mean running mod code on every point of stamina spent or every step taken, to hand
    /// back a number that changes only when a hand of cards does.
    ///
    /// Written to the live Player, never to the prefab. A respawn builds a new Player carrying the
    /// prefab's values again, and Effects.Apply re-runs on a player change for exactly that reason, so
    /// the capture below re-reads the untouched numbers each time.
    ///
    /// A jump height special lived here until LHM-44, with a guard that measured a raised jump's landing
    /// from the height a vanilla jump would have reached. Long stride's capstone was its only user, and
    /// the capstone is steep ground now, so the special, the guard and the jump-force write are gone.
    /// </summary>
    internal static class Sinews
    {
        /// <summary>Seconds off the pause before stamina starts returning.</summary>
        internal const string StaminaDelay = "*staminadelay";

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

        /// <summary>Fraction off the overloaded drain, zero or negative. See Overloaded.</summary>
        internal static float OverloadMod;

        // The player these were captured from, and the values it had before Rist touched them.
        private static Player _player;
        private static float _vanillaDelay;
        private static float _vanillaOverload;

        /// <summary>
        /// What the character had before Rist wrote to it, for the readout to print a ratio
        /// against. Falls back to the live values when nothing has been captured yet, so a
        /// character holding no cards reads x1.00 rather than dividing by zero.
        /// </summary>
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
        /// Sure-footed's landing roll.
        ///
        /// Fall damage is measured from the highest point since you last touched ground:
        /// Character.UpdateGroundContact takes m_maxAirAltitude less where you land, and anything over 4m
        /// hurts, (h - 4) / 16 of your health up to all of it at 20m. The roll takes eight metres off that
        /// height before the stone's own percentage is taken.
        ///
        /// Two seams. Player.Dodge marks the moment a dodge is asked for, and the prefix on
        /// UpdateGroundContact corrects the altitude before the damage check reads it. That last one runs
        /// every physics step, in the air too, and acts only on a frame with ground contact - lowering the
        /// altitude mid-flight would be undone by the next Max() and forgive nothing.
        /// </summary>
        internal static class Landing
        {
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
            private static bool _rollFailedOnce;

            private static AccessTools.FieldRef<Character, float> _maxAir;
            private static AccessTools.FieldRef<Character, bool> _groundContact;

            internal static void Clear()
            {
                _pressedAt = -1f;
            }

            /// <summary>
            /// Its own class so that a game update that moves Player.Dodge costs the landing roll
            /// and not the landing correction beside it: PatchAll(type) throws for the whole class.
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
                    if (Effects.TotalFor(LandingRoll) <= 0f || __instance.IsEncumbered()) return;

                    // The rest of what UpdateDodge asks before it starts a roll. Dodge queues for
                    // half a second whatever the state, and a press the game then refuses must not
                    // count. Ground is left out: the press is made in the air by design.
                    if (__instance.IsDead() || __instance.InAttack() || __instance.IsStaggering()
                        || __instance.InDodge()) return;

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

                var rolled = pressed >= 0f && Time.time - pressed <= RollWindow && Effects.TotalFor(LandingRoll) > 0f;
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
                if (_maxAir == null) return "rist: the landing correction is not bound, so a fall cannot be staged.";
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
            [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
            private static void Landed(Character __instance)
            {
                SafeRoll(__instance);
            }

            /// <summary>
            /// Caught so that a throw costs the roll and never the physics step it runs inside.
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
            /// Bind the two fields the roll reads and ask Harmony whether both of its ends really are
            /// attached. Called by the plugin straight after it patches this class, the way
            /// OwnInventoryRows confirms its load guard: PatchAll returning is not proof, and an end that
            /// is silently missing turns the capstone into nothing at all.
            /// </summary>
            internal static void Confirm(string harmonyId)
            {
                try
                {
                    _maxAir = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
                    _groundContact = AccessTools.FieldRefAccess<Character, bool>("m_groundContact");

                    var landing = AccessTools.Method(typeof(Character), "UpdateGroundContact");
                    if (!Attached(landing, harmonyId, nameof(Landed), prefix: true))
                        RistPlugin.Log.LogError("Landing roll: the landing correction is not attached, so "
                            + "Sure-footed's capstone never rolls this session. The game's "
                            + "Character.UpdateGroundContact probably moved.");
                }
                catch (Exception e)
                {
                    // Lazily and inside a try/catch, never in a static initialiser: a throwing one
                    // poisons every patch the class carries.
                    RistPlugin.Log.LogError("Landing roll: could not bind the game's fall fields ("
                        + e.Message + "), so Sure-footed's capstone never rolls this session.");
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
            // taken - the delay walking toward its floor without end.
            if (!ReferenceEquals(player, _player))
            {
                _player = player;
                _vanillaDelay = player.m_staminaRegenDelay;
                _vanillaOverload = player.m_encumberedStaminaDrain;
            }

            player.m_staminaRegenDelay = Mathf.Max(MinDelay, _vanillaDelay - Mathf.Max(0f, DelayCut));

            player.m_encumberedStaminaDrain = _vanillaOverload
                * Mathf.Max(MinOverloadDrain, 1f + Mathf.Min(0f, OverloadMod));
        }
    }
}
