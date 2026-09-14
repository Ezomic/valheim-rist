using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The one test, shared by every card that stretches a status effect, for "this changes how
    /// you move". Move speed has a single source in this catalogue, and a longer speed buff from
    /// a mead or a forsaken power would be a second one wearing a hat.
    /// </summary>
    internal static class Movement
    {
        internal static bool Changes(SE_Stats se)
        {
            return se != null &&
                   (se.m_speedModifier != 0f || se.m_windMovementModifier != 0f || se.m_jumpModifier != Vector3.zero);
        }
    }

    /// <summary>
    /// Deep draught: buff meads last longer, and at rank five one in four is not used up.
    ///
    /// "Buff mead" is decided from the item rather than from a list of names, so a mead a game
    /// update or another mod adds is covered by the same rule. It is a consumable that is not
    /// food, whose effect is exactly SE_Stats, and that restores nothing - no health, stamina or
    /// eitr up front or over time, since stretching a healing potion's tick would make it heal
    /// more. Anything that changes movement - speed, wind or jump - is left alone too: move
    /// speed has one source in this catalogue, and a longer speed tonic would be a second.
    ///
    /// Both halves hang off Player.ConsumeItem, which drinking from the inventory or the hotbar
    /// goes through. Its own CanConsumeItem already refuses a mead whose effect is running, so
    /// the effect it adds is always fresh and scaling that instance's m_ttl cannot compound. A
    /// consumable placed in the world as a piece is eaten through ItemDrop.Eat instead and is
    /// not covered; no vanilla buff mead is known to be placeable.
    ///
    /// The capstone skips the RemoveOneItem that ConsumeItem calls last, only for the item being
    /// drunk and only inside that call. Refusing earlier would destroy the mead without its
    /// effect: ConsumeItem removes the item whatever EatFood returned.
    /// </summary>
    internal static class DeepDraught
    {
        internal const string Duration = "*mead:duration";
        internal const string FullCask = "*mead:fullcask";

        private const float KeepChance = 0.25f;

        private static ItemDrop.ItemData _keep;

        private static bool IsBuffMead(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_food > 0f) return false;

            var se = item.m_shared.m_consumeStatusEffect as SE_Stats;
            if (se == null || se.GetType() != typeof(SE_Stats) || se.m_ttl <= 0f) return false;

            if (se.m_healthUpFront != 0f || se.m_healthOverTime != 0f || se.m_healthPerTick != 0f) return false;
            if (se.m_staminaUpFront != 0f || se.m_staminaOverTime != 0f) return false;
            if (se.m_eitrUpFront != 0f || se.m_eitrOverTime != 0f) return false;
            if (Movement.Changes(se)) return false;

            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        [HarmonyPrefix]
        private static void Before(Player __instance, ItemDrop.ItemData item)
        {
            _keep = null;
            if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            if (!IsBuffMead(item) || Effects.TotalFor(FullCask) <= 0f) return;

            if (Random.value < KeepChance) _keep = item;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        [HarmonyPostfix]
        private static void After(Player __instance, ItemDrop.ItemData item, bool __result)
        {
            var kept = _keep != null;
            _keep = null;

            if (!__result || !RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            if (!IsBuffMead(item)) return;

            var bonus = Effects.TotalFor(Duration);
            if (bonus > 0f)
            {
                var running = __instance.GetSEMan().GetStatusEffect(item.m_shared.m_consumeStatusEffect.NameHash());
                if (running != null) running.m_ttl *= 1f + bonus;
            }

            if (kept) __instance.Message(MessageHud.MessageType.TopLeft, "The cask is still full");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        [HarmonyFinalizer]
        private static void Cleanup()
        {
            _keep = null;
        }

        internal static class Keep
        {
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem))]
            [HarmonyPrefix]
            private static bool Removing(ItemDrop.ItemData item)
            {
                // Only the one item, only while ConsumeItem is running for it.
                return _keep == null || !ReferenceEquals(item, _keep);
            }
        }
    }
}
