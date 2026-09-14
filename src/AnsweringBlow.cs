using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Answering blow: a perfect dodge arms your next hit.
    ///
    /// Vanilla pays a perfect dodge in stamina and adrenaline and nothing else, while a perfect
    /// block staggers the attacker. This gives a dodge build the payoff a shield build already
    /// has, limited by the rules a parry follows.
    ///
    /// Armed in Player.RPC_HitWhileDodging, which is where the game itself decides a dodge was
    /// perfect, and only on the call that flips m_beenHitWhileDodging - so one roll arms one
    /// blow however many attacks it slipped. A second perfect dodge while armed neither stacks
    /// nor extends the window.
    ///
    /// Spent in Character.Damage on the attacker's side, before the hit is sent, and applied to
    /// that hit's damage directly. Never as an SE_Stats damage field: Attack writes those into
    /// the HitData when the attack starts, so a sweep or an arrow fired inside the window would
    /// carry the bonus to every target it touched.
    /// </summary>
    internal static class AnsweringBlow
    {
        internal const string Bonus = "*answer";
        internal const string Stagger = "*answer:stagger";

        private const float Window = 4f;

        private static float _armedUntil = -1f;

        private static AccessTools.FieldRef<Player, bool> _beenHit;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _beenHit = AccessTools.FieldRefAccess<Player, bool>("m_beenHitWhileDodging");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Answering blow could not reach the game's dodge field and is off " +
                                        "for this session: " + e.Message);
            }

            return _bound;
        }

        internal static class Arm
        {
            [HarmonyPatch(typeof(Player), "RPC_HitWhileDodging")]
            [HarmonyPrefix]
            private static void Before(Player __instance, out bool __state)
            {
                __state = RistConfig.Enabled.Value && ReferenceEquals(__instance, Player.m_localPlayer) &&
                          Bind() && !_beenHit(__instance);
            }

            [HarmonyPatch(typeof(Player), "RPC_HitWhileDodging")]
            [HarmonyPostfix]
            private static void After(Player __instance, bool __state)
            {
                // Only the call that actually turned the flag on counts as the perfect dodge.
                if (!__state || !_beenHit(__instance)) return;
                if (Time.time <= _armedUntil) return;
                if (Effects.TotalFor(Bonus) <= 0f) return;

                // The RPC carries no attacker, and the game counts a friend with PvP on swinging
                // through you as a perfect dodge too. Without this, two players could roll
                // through each other's swings to arm a bonus hit on demand. Requiring an
                // enemy close by keeps every real dodge and ends the trick outside a fight.
                if (!EnemyNear(__instance)) return;

                _armedUntil = Time.time + Window;

                // Said on screen, because a bonus with a four-second fuse that nobody can see is
                // a bonus nobody plays around.
                __instance.Message(MessageHud.MessageType.TopLeft, "Answering blow ready");
            }

            private const float EnemyRange = 8f;

            private static bool EnemyNear(Player player)
            {
                var here = player.transform.position;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsDead() || ReferenceEquals(c, player)) continue;
                    if (!BaseAI.IsEnemy(player, c)) continue;
                    if (Vector3.Distance(here, c.transform.position) <= EnemyRange) return true;
                }

                return false;
            }
        }

        internal static class Spend
        {
            [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
            [HarmonyPrefix]
            private static void Damaging(Character __instance, HitData hit)
            {
                if (_armedUntil < 0f || hit == null || __instance == null) return;
                if (Time.time > _armedUntil)
                {
                    _armedUntil = -1f;
                    return;
                }

                var player = Player.m_localPlayer;
                if (player == null || !ReferenceEquals(hit.GetAttacker(), player)) return;
                if (hit.m_hitType != HitData.HitType.PlayerHit || !BaseAI.IsEnemy(player, __instance)) return;

                _armedUntil = -1f;

                var bonus = Effects.TotalFor(Bonus);
                if (bonus > 0f) hit.m_damage.Modify(1f + bonus);

                // A chance, not a certainty: the capstone's value in the catalogue is the chance,
                // 0.30 by default, Robbin's number. It was a guaranteed stagger for one commit.
                //
                // Staggers only what a parry would. Humanoid.BlockAttack staggers a blocked
                // attacker on m_staggerWhenBlocked alone, so that is the whole test - plus never a
                // boss, which a parry does not exclude and this card does.
                var chance = Mathf.Clamp01(Effects.TotalFor(Stagger));
                if (chance > 0f && __instance.m_staggerWhenBlocked && !Bosses.Is(__instance) &&
                    Random.value < chance)
                    hit.m_staggerMultiplier = Mathf.Max(hit.m_staggerMultiplier, 100f);
            }
        }
    }

    /// <summary>
    /// What counts as a boss, in the one place two cards need it.
    ///
    /// m_boss is false on Hildir's mini-bosses, and a defeat key on its own matches every bat in
    /// a frost cave - Bat, Troll and Surtling all set one that is a GlobalKeys member. So a boss
    /// is m_boss, or a defeat key the game does not know as a GlobalKey, which is what the
    /// custom quest keys like BossHildir2 are.
    /// </summary>
    internal static class Bosses
    {
        internal static bool Is(Character c)
        {
            if (c == null) return false;
            if (c.IsBoss()) return true;

            var key = c.m_defeatSetGlobalKey;
            if (string.IsNullOrEmpty(key)) return false;

            try
            {
                System.Enum.Parse(typeof(GlobalKeys), key, true);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
