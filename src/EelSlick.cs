using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Eel-slick: a dodge roll's invulnerable window runs longer, and at rank five a perfect
    /// roll makes the next roll cost nothing.
    ///
    /// How the window works in the game, read from Player.cs. Invulnerability is
    /// m_dodgeInvincible, set true when the roll starts in UpdateDodge and cleared by one
    /// animation event, DodgeMortal, which lands part way through the roll. The roll's animation
    /// carries on for a while after it, and that tail is the part of a roll where you can be hit.
    /// The flag is also what is written to the character's ZDO as dodgeinv, and that, not the
    /// owner's field, is what an attacker on another machine reads before deciding a swing was
    /// dodged. So the window has to be lengthened in the field itself and never in
    /// IsDodgeInvincible, or a monster owned by another client would hit straight through it.
    ///
    /// The window therefore ENDS LATER and never starts earlier: the roll begins at the same
    /// instant, the animation is not stretched or sped, and the event that closes the window is
    /// held back by a share of the time it took to arrive. The extension is cut short by the end
    /// of the roll's own animation, because a flag that outlived the animation would read as
    /// invulnerable only while the animator says the character is rolling.
    ///
    /// A perfect roll is the game's own: RPC_HitWhileDodging, sent by an attack whose hit landed
    /// inside the window, and handled on the machine that owns the character. That is the local
    /// machine for the player rolling, with no second player involved, because the attacker's
    /// side sends it and the owner's side is the one that runs it. Armed exactly the way
    /// Answering blow is, and with the same rule that an enemy must be close.
    ///
    /// The free roll is the dodge's stamina price read as zero, and only while UpdateDodge is
    /// the caller. GetDodgeStaminaUse is also what the perfect-dodge stamina return multiplies,
    /// and zeroing it there would take the refund off a paid roll too. The refund is zeroed only for a roll that
    /// was itself free, so chained free rolls cannot gain stamina. The plain
    /// discount was refused on the ideas board: the Dodge skill already halves the price at
    /// skill 100 and Tireless discounts it too, and the three multiply.
    /// </summary>
    internal static class EelSlick
    {
        internal const string Window = "*roll:window";
        internal const string Free = "*roll:free";

        /// <summary>
        /// How long a free roll waits to be used. Long enough to take one more swing and roll
        /// again, which is the shape of the fights this is for, and short enough that it cannot
        /// be carried out of a fight and banked. Answering blow's own fuse is four seconds; a
        /// roll is spent less often than a hit, so this one is twice that.
        /// </summary>
        private const float FreeFor = 8f;

        // A roll that began longer ago than this is not the one an event belongs to.
        private const float LongestWindow = 3f;

        private static AccessTools.FieldRef<Player, bool> _invincible, _inDodge, _beenHit;
        private static bool _bound, _bindFailed;

        private static float _start = -1f;
        private static bool _holding;
        private static float _until;

        private static float _armedUntil = -1f;
        private static bool _inUpdate, _inPerfect;
        private static float _staminaBefore;

        private static float _vanilla = -1f;
        private static float _asked;
        private static float _got = -1f;
        private static float _lastCost = -1f;
        private static bool _lastFree;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _invincible = AccessTools.FieldRefAccess<Player, bool>("m_dodgeInvincible");
                _inDodge = AccessTools.FieldRefAccess<Player, bool>("m_inDodge");
                _beenHit = AccessTools.FieldRefAccess<Player, bool>("m_beenHitWhileDodging");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Eel-slick could not reach the game's dodge fields and is off " +
                                        "for this session: " + e.Message);
            }

            return _bound;
        }

        private static bool Mine(Player player)
        {
            return RistConfig.Enabled.Value && ReferenceEquals(player, Player.m_localPlayer) && Bind();
        }

        internal static class Roll
        {
            [HarmonyPatch(typeof(Player), "UpdateDodge")]
            [HarmonyPrefix]
            private static void Before(Player __instance, out bool __state)
            {
                __state = false;
                if (!Mine(__instance)) return;

                // The held window ends on the clock, or when the roll's animation does, whichever
                // is first. Cleared before the game reads the flag, so the frame it ends is a
                // vulnerable one and not the one after.
                if (_holding && (Time.time >= _until || !_inDodge(__instance)))
                {
                    _invincible(__instance) = false;
                    _holding = false;
                    _got = Time.time - _start;
                }

                __state = _invincible(__instance);
                _staminaBefore = __instance.GetStamina();
                _inUpdate = true;
            }

            // A throwing UpdateDodge skips the postfix, and a flag left up would zero the price of
            // every later GetDodgeStaminaUse caller.
            [HarmonyPatch(typeof(Player), "UpdateDodge")]
            [HarmonyFinalizer]
            private static void Done()
            {
                _inUpdate = false;
            }

            [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
            [HarmonyPostfix]
            private static void Died(Player __instance)
            {
                if (ReferenceEquals(__instance, Player.m_localPlayer)) Forget();
            }

            [HarmonyPatch(typeof(Player), "UpdateDodge")]
            [HarmonyPostfix]
            private static void After(Player __instance, bool __state)
            {
                _inUpdate = false;
                if (!Mine(__instance) || __state || !_invincible(__instance)) return;

                // The flag went from false to true inside this call, which is the one place the
                // game starts a roll.
                _start = Time.time;
                _holding = false;
                _vanilla = -1f;
                _got = -1f;
                _lastCost = Mathf.Max(0f, _staminaBefore - __instance.GetStamina());

                _lastFree = _armedUntil > Time.time;
                if (_lastFree) _armedUntil = -1f;
            }
        }

        internal static class Mortal
        {
            [HarmonyPatch(typeof(Player), nameof(Player.OnDodgeMortal))]
            [HarmonyPrefix]
            private static bool Closing(Player __instance)
            {
                if (!Mine(__instance) || _start < 0f || Time.time - _start > LongestWindow) return true;

                _vanilla = Time.time - _start;

                var bonus = Effects.Cached(Window);
                _asked = Mathf.Max(0f, bonus);
                if (bonus <= 0f)
                {
                    _got = _vanilla;
                    return true;
                }

                _holding = true;
                _until = Time.time + _vanilla * bonus;
                return false;
            }
        }

        internal static class Perfect
        {
            [HarmonyPatch(typeof(Player), "RPC_HitWhileDodging")]
            [HarmonyPrefix]
            private static void Before(Player __instance, out bool __state)
            {
                __state = Mine(__instance) && !_beenHit(__instance);
                _inPerfect = __state;
            }

            [HarmonyPatch(typeof(Player), "RPC_HitWhileDodging")]
            [HarmonyFinalizer]
            private static void Done()
            {
                _inPerfect = false;
            }

            [HarmonyPatch(typeof(Player), "RPC_HitWhileDodging")]
            [HarmonyPostfix]
            private static void After(Player __instance, bool __state)
            {
                if (!__state || !_beenHit(__instance)) return;
                if (Effects.Cached(Free) <= 0f) return;

                // The game counts a friend swinging through you with PvP on as a perfect roll as
                // well, which would let two players arm each other for nothing. Same rule, same
                // eight metres, as Answering blow.
                if (!AnsweringBlow.Arm.EnemyNear(__instance)) return;

                _armedUntil = Time.time + FreeFor;
                __instance.Message(MessageHud.MessageType.TopLeft, "Next roll is free");
            }
        }

        internal static class Price
        {
            [HarmonyPatch(typeof(Player), "GetDodgeStaminaUse")]
            [HarmonyPostfix]
            private static void Cost(Player __instance, ref float __result)
            {
                // The perfect-roll refund multiplies this same price, so a roll that cost nothing
                // refunds nothing. Without that a free perfect roll paid stamina out and a chain
                // of them gained it.
                var free = _inUpdate ? _armedUntil > Time.time : _inPerfect && _lastFree;
                if (!free || !Mine(__instance)) return;
                __result = 0f;
            }
        }

        /// <summary>Logout and death: an armed roll, a held window and the call flags belong to one life.</summary>
        internal static void Forget()
        {
            _armedUntil = -1f;
            _inUpdate = false;
            _inPerfect = false;
            _holding = false;
            _start = -1f;
            _lastFree = false;
        }

        /// <summary>Starts a roll the way a keypress does. For `rist roll`.</summary>
        internal static string Start(Player player)
        {
            var dodge = AccessTools.Method(typeof(Player), "Dodge");
            if (dodge == null) return "rist: the game's Dodge method was not found.";

            dodge.Invoke(player, new object[] { player.transform.forward });
            return "rist: roll queued.";
        }

        /// <summary>A hit inside the window, sent the way an attack sends it. For `rist perfect`.</summary>
        internal static string HitInWindow(Player player)
        {
            if (!player.InDodge()) return "rist: not mid-roll, so there is no window to hit. Run `rist roll` first.";

            player.HitWhileDodging();
            return "rist: a hit was sent into the roll.";
        }

        /// <summary>
        /// What `rist show` prints. "asked" is the share of the vanilla window the hand asked for,
        /// which is the thing to assert on, and "got" is what the roll actually held, in seconds,
        /// which the end of the animation can cut short and which is therefore read, not asserted.
        /// </summary>
        internal static string Probe()
        {
            var window = _vanilla < 0f
                ? "roll window: no roll measured yet"
                : "roll window " + _vanilla.ToString("0.00", CultureInfo.InvariantCulture) + "s, asked +"
                  + Mathf.RoundToInt(_asked * 100f) + "%, got "
                  + (_got < 0f ? "nothing yet" : _got.ToString("0.00", CultureInfo.InvariantCulture) + "s");

            var armed = _armedUntil > Time.time
                ? "free roll armed " + (_armedUntil - Time.time).ToString("0.0", CultureInfo.InvariantCulture) + "s"
                : "free roll not armed";

            var cost = _lastCost < 0f
                ? "no roll yet"
                : "last roll cost " + _lastCost.ToString("0.0", CultureInfo.InvariantCulture) + " stamina"
                  + (_lastFree ? " (free)" : "");

            return window + "  " + armed + "  " + cost;
        }
    }
}
