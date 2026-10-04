using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Keen hand's capstone: a weapon combo forgives a late swing.
    ///
    /// Attack.Start drops a combo back to its first hit when the next swing begins more than 0.2 s after
    /// the last one ended (timeSinceLastAttack > 0.2f against m_attackChainLevels). That is the whole
    /// of a combo's rhythm in the game, and the stone is the swing-rhythm stone, so the capstone is
    /// the forgiveness of rhythm instead of more speed: the argument is read 0.4 s less, so a swing up
    /// to 0.6 s late still lands the chain's second or third hit. It replaces +5% damage on every
    /// weapon, which sat next to Keen edge's own effect.
    ///
    /// Melee weapons only, by the same category the swing-speed stone uses, so the axe counts as a
    /// tool and not a weapon here exactly as it does there. Only the local player's own swings: another
    /// player's combo is decided by their game.
    /// </summary>
    internal static class ComboHold
    {
        internal const string Key = "*combo:hold";

        private const float Slack = 0.4f;
        private const float Vanilla = 0.2f;

        private static int _held;

        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        internal static class Chain
        {
            [HarmonyPrefix]
            private static void Forgive(Attack __instance, Humanoid character, ItemDrop.ItemData weapon,
                                        ref float timeSinceLastAttack)
            {
                if (timeSinceLastAttack <= Vanilla || __instance.m_attackChainLevels <= 1) return;
                if (!RistConfig.Enabled.Value || !ReferenceEquals(character, Player.m_localPlayer)) return;
                if (AttackSpeed.CategoryOf(weapon) != AttackSpeed.Melee) return;
                if (Effects.Cached(Key) <= 0f) return;

                var forgiven = Mathf.Max(0f, timeSinceLastAttack - Slack);
                if (forgiven <= Vanilla) _held++;
                timeSinceLastAttack = forgiven;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "combo hold: carved, " + (Vanilla + Slack).ToString("0.0", CultureInfo.InvariantCulture)
                  + "s to chain, held " + _held + " chains"
                : "combo hold: not carved";
        }
    }
}
