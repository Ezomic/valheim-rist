using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Deep draught: buff meads last longer, and at rank five each has a 25% chance not to be
    /// used up - a roll per drink, not every fourth one.
    ///
    /// "Buff mead" is decided from the item rather than from a list of names, so a mead a game
    /// update or another mod adds is covered by the same rule. It is a consumable that is not
    /// food, whose effect is exactly SE_Stats, and that restores nothing - no health, stamina or
    /// eitr up front or over time, since stretching a healing potion's tick would make it heal
    /// more.
    ///
    /// Two exclusions this used to make were taken out on Robbin's call, 2026-09-15, and are
    /// deliberate. Meads that change movement are in: Tonic of Ratatosk's speed and Lightfoot's
    /// jump are the game's own numbers, only lasting longer, so the one-move-speed-source rule
    /// the catalogue holds its own cards to does not apply to a mead the player brewed. And the
    /// Lingering meads are in, although each shares a lockout category with its potions, so a
    /// longer Lingering Healing Mead also keeps the healing potions locked out for longer.
    ///
    /// Against the vanilla assets that is fifteen meads: Fire Resistance Barley Wine, Frost and
    /// Poison Resistance Mead, Berserkir Mead, Mead of Troll Endurance, Draught of Vananidir,
    /// Brew of Animal Whispers, Tasty Mead, Anti-Sting Concoction, Love Potion, the Lingering
    /// Healing, Stamina and Eitr Meads, Tonic of Ratatosk and Lightfoot Mead. Out: the Minor,
    /// Medium and Major Healing Meads, the Minor and Medium Stamina Meads and the Minor Eitr
    /// Mead, which restore.
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

        private static ItemDrop.ItemData _keep;

        private static bool IsBuffMead(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_food > 0f) return false;

            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;

            var se = item.m_shared.m_consumeStatusEffect as SE_Stats;
            if (se == null || se.GetType() != typeof(SE_Stats) || se.m_ttl <= 0f) return false;

            if (se.m_healthUpFront != 0f || se.m_healthOverTime != 0f || se.m_healthPerTick != 0f) return false;
            if (se.m_staminaUpFront != 0f || se.m_staminaOverTime != 0f) return false;
            if (se.m_eitrUpFront != 0f || se.m_eitrOverTime != 0f) return false;

            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        [HarmonyPrefix]
        private static void Before(Player __instance, ItemDrop.ItemData item)
        {
            _keep = null;
            if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            // The capstone's catalogue value is the chance itself, 0.25 by default.
            var chance = Mathf.Clamp01(Effects.TotalFor(FullCask));
            if (chance <= 0f || !IsBuffMead(item)) return;

            if (Random.value < chance) _keep = item;
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
