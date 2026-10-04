using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Turned blade's capstone: the parry answers back.
    ///
    /// Vanilla gives a parry a stagger on the attacker and nothing else with teeth. The capstone throws
    /// a quarter of the damage the parry blocked back at the attacker, melee attackers only and never
    /// a boss. It is not Answering blow: that one is armed by a dodge and strengthens your own next
    /// swing, this one is armed by a block and is the blow itself.
    ///
    /// Humanoid.BlockAttack decides a parry from the blocker's m_timedBlockBonus above 1 and
    /// m_blockTimer under 0.25 (private), so the prefix reads exactly that, before the method runs,
    /// and keeps the blockable damage the hit carried. The postfix takes what the block actually
    /// removed, which is that figure less what the hit still carries: BlockAttack calls BlockDamage
    /// only when the stamina held and the block was not staggered through, so a failed parry leaves
    /// nothing to throw. The thrown damage keeps the hit's own types, so a fire hit is returned as
    /// fire and meets the creature's own resistances.
    ///
    /// Sent the way the game sends its own deflection push at the end of BlockAttack: a fresh HitData
    /// through attacker.Damage. It is typed Self rather than PlayerHit on purpose. Answering blow is
    /// spent by the next PlayerHit on an enemy and Last blow watches PlayerHits, and a riposte must
    /// neither use up an armed answering blow nor refund the stamina of a swing. The attacker is set,
    /// so a creature that dies of it is credited to the player.
    ///
    /// The prefix runs on the same method the Wide guard transpiler edits, whose companion this stone
    /// keeps, so a parry from behind that the arc now allows is a parry like any other.
    /// </summary>
    internal static class Riposte
    {
        internal const string Key = "*parry:riposte";

        private const float Share = 0.25f;
        private const float ParryWindow = 0.25f;

        private static AccessTools.FieldRef<Humanoid, float> _blockTimer;
        private static MethodInfo _blocker;
        private static bool _bound, _bindFailed;

        private static int _thrown;
        private static float _lastThrown;

        internal sealed class Block
        {
            internal HitData.DamageTypes Before;
            internal float Blockable;
        }

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _blockTimer = AccessTools.FieldRefAccess<Humanoid, float>("m_blockTimer");
                _blocker = AccessTools.Method(typeof(Humanoid), "GetCurrentBlocker");
                if (_blocker == null) throw new System.MissingMethodException("Humanoid.GetCurrentBlocker");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Riposte could not reach the game's block fields and is off for this "
                                        + "session: " + e.Message);
            }

            return _bound;
        }

        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        internal static class Parry
        {
            [HarmonyPrefix]
            private static void Watch(Humanoid __instance, HitData hit, out Block __state)
            {
                __state = null;
                if (hit == null || hit.m_ranged || !RistConfig.Enabled.Value) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer) || Effects.Cached(Key) <= 0f || !Bind()) return;

                var timer = _blockTimer(__instance);
                if (timer == -1f || timer >= ParryWindow) return;

                var blocker = _blocker.Invoke(__instance, null) as ItemDrop.ItemData;
                if (blocker == null || blocker.m_shared == null || blocker.m_shared.m_timedBlockBonus <= 1f) return;

                __state = new Block { Before = hit.m_damage.Clone(), Blockable = hit.GetTotalBlockableDamage() };
            }

            [HarmonyPostfix]
            private static void Answer(Humanoid __instance, HitData hit, Character attacker, bool __result, Block __state)
            {
                if (__state == null || !__result || __state.Blockable <= 0f) return;
                if (attacker == null || attacker.IsPlayer() || attacker.IsDead() || Bosses.Is(attacker)) return;
                if (!BaseAI.IsEnemy(__instance, attacker)) return;

                var blocked = __state.Blockable - hit.GetTotalBlockableDamage();
                if (blocked <= 0.01f) return;

                var back = __state.Before;
                back.m_nonPlayer = 0f;
                back.Modify(Share * blocked / __state.Blockable);

                var away = attacker.transform.position - __instance.transform.position;
                away.y = 0f;

                var riposte = new HitData
                {
                    m_damage = back,
                    m_point = attacker.GetCenterPoint(),
                    m_dir = away.sqrMagnitude > 0.0001f ? away.normalized : __instance.transform.forward,
                    m_hitType = HitData.HitType.Self,
                    m_blockable = false,
                    m_dodgeable = false,
                };
                riposte.SetAttacker(__instance);
                attacker.Damage(riposte);

                _thrown++;
                _lastThrown = back.GetTotalDamage();
            }
        }

        internal static string Probe()
        {
            if (Effects.Cached(Key) <= 0f) return "riposte: not carved";

            return "riposte: carved, thrown back " + _thrown + (_thrown == 0
                ? " times"
                : " times, last " + _lastThrown.ToString("0.0", CultureInfo.InvariantCulture) + " damage");
        }
    }
}
