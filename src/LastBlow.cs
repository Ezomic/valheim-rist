using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Steady arm's capstone: the swing that kills gives half its stamina back (LastBlowShare, 50% by default).
    ///
    /// The stone is about nothing you swing costing what it did, and the swing you most want free is
    /// the one that ends the fight. It replaces a +20% parry bonus, a number on a stat the stone has
    /// nothing to do with.
    ///
    /// Three seams, because the game knows none of this at one moment. The price of the swing is read
    /// where Attack.GetAttackStamina works it out, after the Dodge-style discounts and Steady arm's
    /// own ranks, so the refund is exactly what left the bar. The target is taken in the prefix on
    /// Character.Damage, where Rist already edits a hit on the attacker's side. And the kill is read by
    /// asking the target afterwards, for a second and a half: a creature's health lives in its ZDO,
    /// so the answer is right whichever client owns it, which a postfix on Character.OnDeath is not
    /// (CLAUDE.md, the OnDeath ownership trap: that postfix runs on a non-owner too, and on the owner
    /// the ZDO is already gone).
    ///
    /// Read as dead when the creature says so, reads zero health, or is gone altogether: the owner's
    /// OnDeath ends in ZNetScene.Destroy for most creatures, and the next frame has nothing to ask.
    /// A kill that someone else lands inside the window would refund as well. That is a small cost
    /// and it is the one place this can be wrong in the player's favour.
    ///
    /// One refund per swing. The price is spent when it is paid out, so a sweep that kills three
    /// creatures gives it back once, and a swing that kills nothing gives nothing.
    /// </summary>
    internal static class LastBlow
    {
        internal const string Key = "*kill:refund";

        private const float Window = 1.5f;
        private const int MostTargets = 8;

        private static readonly List<Character> _targets = new List<Character>();
        private static float _until;
        private static float _cost;

        private static int _refunds;
        private static float _lastRefund;

        private static AccessTools.FieldRef<Attack, Humanoid> _owner;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _owner = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Last blow could not reach the game's attack owner and is off for "
                                        + "this session: " + e.Message);
            }

            return _bound;
        }

        private static bool Carved => RistConfig.Enabled.Value && Effects.Cached(Key) > 0f;

        [HarmonyPatch(typeof(Attack), "GetAttackStamina")]
        internal static class Cost
        {
            [HarmonyPostfix]
            private static void Paid(Attack __instance, float __result)
            {
                if (!Carved || !Bind()) return;
                if (!ReferenceEquals(_owner(__instance), Player.m_localPlayer)) return;

                // A swing that costs nothing has nothing to give back, and must not inherit the last one's price.
                if (__result <= 0f)
                {
                    _cost = 0f;
                    _targets.Clear();
                    return;
                }

                // A new swing starts a new account: whatever is still being watched belongs to the last one.
                if (!Mathf.Approximately(_cost, __result) || Time.time > _until) _targets.Clear();
                _cost = __result;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        internal static class Hit
        {
            [HarmonyPrefix]
            private static void Struck(Character __instance, HitData hit)
            {
                if (_cost <= 0f || hit == null || __instance == null || !Carved) return;
                if (hit.m_hitType != HitData.HitType.PlayerHit) return;

                var player = Player.m_localPlayer;
                if (player == null || !ReferenceEquals(hit.GetAttacker(), player)) return;
                if (ReferenceEquals(__instance, player) || __instance.IsDead() || !BaseAI.IsEnemy(player, __instance)) return;

                if (Time.time > _until) _targets.Clear();
                _until = Time.time + Window;

                if (_targets.Count < MostTargets && !_targets.Contains(__instance)) _targets.Add(__instance);
            }
        }

        /// <summary>From the plugin's Update. Pays the stamina back once, to the first of the watched creatures that is gone.</summary>
        internal static void Tick(Player player)
        {
            if (_targets.Count == 0 || player == null) return;

            if (Time.time > _until || !Carved)
            {
                _targets.Clear();
                return;
            }

            foreach (var target in _targets)
            {
                if (target != null && !target.IsDead() && target.GetHealth() > 0f) continue;

                var back = _cost * Mathf.Clamp01(RistConfig.LastBlowShare.Value);
                player.AddStamina(back);
                _refunds++;
                _lastRefund = back;
                _cost = 0f;
                _targets.Clear();
                return;
            }
        }

        internal static void Forget()
        {
            _targets.Clear();
            _cost = 0f;
            _until = -1f;
        }

        internal static string Probe()
        {
            if (Effects.Cached(Key) <= 0f) return "last blow: not carved";

            return "last blow: carved, refunded " + _refunds + (_refunds == 0
                ? " times"
                : " times, last " + _lastRefund.ToString("0.0", CultureInfo.InvariantCulture) + " stamina");
        }
    }
}
