using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Turned blade's capstone: a good parry makes your next swing cost no stamina.
    ///
    /// The parry answers back with a free blow instead of a thrown one. It replaces Riposte, which threw a
    /// quarter of the blocked damage back at the attacker, and the two things LHM-53 folded into this stone
    /// (the arc behind you and the cheaper block) stay exactly as they were, as companions in Card.Companions.
    ///
    /// A good parry is the game's own perfect block. Humanoid.BlockAttack counts one when the blocker has a
    /// timed block bonus above 1 and its block timer (private) is under a quarter of a second, and the
    /// parry only works if the stamina held and the block was not staggered through: then BlockAttack takes
    /// the blocked damage off the hit. So the prefix reads the timer and the bonus before the method runs
    /// and keeps the blockable damage the hit carried, and the postfix calls it a good parry when the hit
    /// carries less than it did. A block that stamina or stagger broke through arms nothing.
    ///
    /// The free swing is armed for ReturnBlowSeconds (5) and spent by one swing. Attack.Start asks
    /// GetAttackStamina to see whether the bar can pay, and Attack.Update asks it again a few frames later,
    /// when the animation really starts, to take the stamina. The first call to see an armed, eligible swing
    /// is the Start prefix, which names that Attack: the answer is zero for it from then on, so a character
    /// at an empty bar can still swing, and the arm is spent only when Start returns true. A swing the game
    /// refuses for another reason costs nothing and keeps the arm.
    ///
    /// Melee only, by the swing-speed stone's categories: swords, knives, clubs, polearms, spears and fists
    /// are melee, and an axe counts too, because an axe and a shield is the commonest hand that parries
    /// and the speed stone's "axes are tools" split has no reason to cost it the capstone. Pickaxes and the
    /// other tools, bows and staffs do not. A swing that was free anyway does not spend the arm.
    ///
    /// The zeroing runs ahead of Last blow's own postfix on the same method (Priority.First), so Last blow
    /// sees a price of nothing and refunds nothing for a swing that was not paid for.
    /// </summary>
    internal static class ReturnBlow
    {
        internal const string Key = "*parry:returnblow";

        private const float ParryWindow = 0.25f;

        private static AccessTools.FieldRef<Humanoid, float> _blockTimer;
        private static AccessTools.FieldRef<Attack, Humanoid> _owner;
        private static MethodInfo _blocker;
        private static bool _bound, _bindFailed;

        private static float _armedUntil = -1f;
        private static Attack _starting, _free, _updating;
        private static float _startPrice;

        private static int _given;
        private static float _lastPriced = -1f, _lastPaid = -1f;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _blockTimer = AccessTools.FieldRefAccess<Humanoid, float>("m_blockTimer");
                _owner = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
                _blocker = AccessTools.Method(typeof(Humanoid), "GetCurrentBlocker");
                if (_blocker == null) throw new System.MissingMethodException("Humanoid.GetCurrentBlocker");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Return blow could not reach the game's block and attack fields and is "
                                        + "off for this session: " + e.Message);
            }

            return _bound;
        }

        private static float Seconds => Mathf.Max(0.5f, RistConfig.ReturnBlowSeconds.Value);

        private static bool Carved => RistConfig.Enabled.Value && Effects.Cached(Key) > 0f;

        internal static bool Armed => Time.time < _armedUntil;

        private static bool Eligible(ItemDrop.ItemData weapon)
        {
            if (weapon == null || weapon.m_shared == null) return false;

            return AttackSpeed.CategoryOf(weapon) == AttackSpeed.Melee
                   || weapon.m_shared.m_skillType == Skills.SkillType.Axes;
        }

        internal static void Arm(Player player)
        {
            _armedUntil = Time.time + Seconds;
            player.Message(MessageHud.MessageType.TopLeft, "Next swing is free");
        }

        internal sealed class Block
        {
            internal float Blockable;
        }

        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        internal static class Parry
        {
            [HarmonyPrefix]
            private static void Watch(Humanoid __instance, HitData hit, out Block __state)
            {
                __state = null;
                if (hit == null || !Carved || !ReferenceEquals(__instance, Player.m_localPlayer) || !Bind()) return;

                var timer = _blockTimer(__instance);
                if (timer == -1f || timer >= ParryWindow) return;

                var blocker = _blocker.Invoke(__instance, null) as ItemDrop.ItemData;
                if (blocker == null || blocker.m_shared == null || blocker.m_shared.m_timedBlockBonus <= 1f) return;

                __state = new Block { Blockable = hit.GetTotalBlockableDamage() };
            }

            [HarmonyPostfix]
            private static void Answer(Humanoid __instance, HitData hit, Character attacker, bool __result, Block __state)
            {
                if (__state == null || !__result || __state.Blockable <= 0f || attacker == null) return;
                if (__state.Blockable - hit.GetTotalBlockableDamage() <= 0.01f) return;

                Arm((Player)__instance);
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        internal static class Begin
        {
            [HarmonyPrefix]
            private static void Check(Attack __instance, Humanoid character, ItemDrop.ItemData weapon)
            {
                _starting = null;
                _startPrice = 0f;
                if (!Armed || !Carved || !ReferenceEquals(character, Player.m_localPlayer) || !Eligible(weapon)) return;

                _starting = __instance;
            }

            [HarmonyPostfix]
            private static void Spent(bool __result)
            {
                if (_starting == null) return;

                if (__result && _startPrice > 0f)
                {
                    _free = _starting;
                    _armedUntil = -1f;
                    _given++;
                }

                _starting = null;
            }

            [HarmonyFinalizer]
            private static void Done()
            {
                _starting = null;
            }
        }

        [HarmonyPatch(typeof(Attack), "GetAttackStamina")]
        internal static class Price
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Free(Attack __instance, ref float __result)
            {
                if (!Bind() || !ReferenceEquals(_owner(__instance), Player.m_localPlayer)) return;

                var vanilla = __result;
                if (ReferenceEquals(__instance, _starting)) _startPrice = vanilla;

                if ((ReferenceEquals(__instance, _starting) || ReferenceEquals(__instance, _free)) && vanilla > 0f)
                    __result = 0f;

                if (ReferenceEquals(__instance, _updating))
                {
                    _lastPriced = vanilla;
                    _lastPaid = __result;
                }
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.Update))]
        internal static class Paying
        {
            [HarmonyPrefix]
            private static void Enter(Attack __instance)
            {
                _updating = __instance;
            }

            [HarmonyFinalizer]
            private static void Leave()
            {
                _updating = null;
            }
        }

        internal static void Forget()
        {
            _armedUntil = -1f;
            _starting = null;
            _free = null;
            _updating = null;
        }

        /// <summary>Arms the free swing the way a good parry does. For `rist parry`.</summary>
        internal static string Simulate(Player player)
        {
            if (!Carved) return "rist: Return blow is not carved, so a parry arms nothing.";

            Arm(player);
            return "rist: a good parry was simulated, the next melee swing within "
                   + Seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s is free.";
        }

        /// <summary>Starts a primary swing with the weapon in hand, as the attack key does. For `rist swing`.</summary>
        internal static string Swing(Player player)
        {
            return player.StartAttack(null, false)
                ? "rist: swing started."
                : "rist: the game refused the swing (nothing in hand, mid-action or not enough stamina).";
        }

        internal static string Probe()
        {
            if (!Carved) return "return blow: not carved";

            var state = Armed
                ? "armed for " + (_armedUntil - Time.time).ToString("0.0", CultureInfo.InvariantCulture) + " s"
                : "not armed";

            var last = _lastPriced < 0f
                ? ""
                : ", last swing priced " + _lastPriced.ToString("0.0", CultureInfo.InvariantCulture)
                  + " paid " + _lastPaid.ToString("0.0", CultureInfo.InvariantCulture);

            return "return blow: carved, " + state + ", free swings given " + _given + last;
        }
    }
}
