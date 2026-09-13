using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The three attack-speed cards, split by what is in your hands.
    ///
    /// This is the first thing in Rist that patches a gameplay path rather than riding a stat
    /// the game already sums, and it is here because there is no other way: SE_Stats has no
    /// attack-speed field and SEMan has no ModifyAttackSpeed among its twenty-two hooks. Swing
    /// speed is the animator's speed, full stop.
    ///
    /// What makes it cheap anyway is that vanilla already owns that number and already puts it
    /// back. CharacterAnimEvent.CustomFixedUpdate runs
    ///
    ///     if (!InAttack() &amp;&amp; !InMinorAction() &amp;&amp; !InEmote() &amp;&amp; CanMove())
    ///         m_animator.speed = 1f;
    ///
    /// every fixed update, so a raised speed is reset the moment the swing ends. There is no
    /// restore to write and no timer to leak. Two more things fall out of the same design:
    /// ZSyncAnimation writes m_animator.speed into the ZDO on the owner and reads it on
    /// remotes, so other players see the faster swing without any network code of ours; and
    /// FreezeFrame captures the current speed into m_pauseSpeed before the hit-pause and
    /// restores it after, so the hit-stop still works.
    ///
    /// The speed is set through CharacterAnimEvent.Speed, which is public and does exactly
    /// this - no reflection, and the one seam the game itself offers.
    /// </summary>
    internal static class AttackSpeed
    {
        internal const string Melee = "*attackspeed:melee";
        internal const string Tools = "*attackspeed:tools";
        internal const string Ranged = "*attackspeed:ranged";

        /// <summary>
        /// Which card, if any, covers the thing being swung.
        ///
        /// Read off m_skillType rather than the item type, because that is what the game
        /// itself dispatches on and it separates a pickaxe from a sword without a name list.
        ///
        /// The axe sits with the tools, by choice. It is SkillType.Axes whether it is meeting a
        /// tree or a greydwarf - one item, one animation, no way to tell during the swing - so
        /// it has to be one card or the other, and a card called tool speed that did not cover
        /// the thing most people chop with was the wrong half to keep. The cost is that axe
        /// *combat* is sped by the tool card rather than the melee one.
        ///
        /// A staff is ElementalMagic or BloodMagic, which no card covers: casting stays at
        /// vanilla speed until there is a fourth card for it.
        /// </summary>
        private static string CategoryOf(ItemDrop.ItemData weapon)
        {
            if (weapon == null || weapon.m_shared == null) return null;

            switch (weapon.m_shared.m_skillType)
            {
                case Skills.SkillType.Bows:
                case Skills.SkillType.Crossbows:
                    return Ranged;

                case Skills.SkillType.Pickaxes:
                case Skills.SkillType.WoodCutting:
                case Skills.SkillType.Axes:
                    return Tools;

                case Skills.SkillType.Swords:
                case Skills.SkillType.Knives:
                case Skills.SkillType.Clubs:
                case Skills.SkillType.Polearms:
                case Skills.SkillType.Spears:
                case Skills.SkillType.Unarmed:
                    return Melee;
            }

            // The hammer, hoe and cultivator carry no skill at all, so they fall through to
            // the item type. They are still Attacks and still animate.
            return weapon.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool ? Tools : null;
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        [HarmonyPostfix]
        private static void Started(bool __result, Humanoid character, CharacterAnimEvent animEvent,
                                    ItemDrop.ItemData weapon)
        {
            if (!__result || !RistConfig.Enabled.Value) return;
            if (character == null || animEvent == null) return;

            // Local player only. The cards live in ClientState, which is this client's own
            // standing - another player's swing is driven by their game and arrives here
            // through the ZDO already at the right speed.
            if (!ReferenceEquals(character, Player.m_localPlayer)) return;

            var category = CategoryOf(weapon);
            if (category == null) return;

            var bonus = Effects.TotalFor(category);
            if (bonus <= 0f) return;

            // Clamped, because this multiplies an animation rather than a number in a table.
            // A mis-typed catalogue line could otherwise run the whole character at twenty
            // times speed, and animation events are what land the hit.
            bonus = Mathf.Min(bonus, Mathf.Max(0f, RistConfig.AttackSpeedMax.Value));

            animEvent.Speed(1f + bonus);
        }

        /// <summary>
        /// Quick draw on a bow, which the animator patch above never reached.
        ///
        /// Attack.Start runs when the arrow is released, not when the string is pulled, so on a
        /// bow the card sped up the release and nothing else. The draw itself is
        /// Player.UpdateAttackBowDraw adding Time.fixedDeltaTime to m_attackDrawTime, and
        /// Humanoid.GetAttackDrawPercentage dividing that by the bow's draw duration. No
        /// animation speed touches either. So the tile said "draw speed" and a fully carved card
        /// drew exactly as slowly as none - the same way Far sight and Weatherly failed, only
        /// quieter, because the release did get faster and looked like something.
        ///
        /// Scaling the percentage where it is read rather than the timer where it is counted is
        /// the choice that matters. Every consumer goes through this one method - the power
        /// Attack.Start is handed (damage, spread, arrow speed), the reticle in Hud, the bow's
        /// bend through the "drawpercent" animator float, and the halved stamina drain once the
        /// draw is full - so all of them move together and none can disagree about how drawn
        /// the bow is. Clamp01(p * (1 + bonus)) is exactly a draw duration divided by (1 +
        /// bonus), including once the vanilla clamp has already reached 1.
        ///
        /// Its own class so a game update that moves this method costs the bow draw and not
        /// every swing-speed card beside it.
        /// </summary>
        internal static class BowDraw
        {
            private static int _frame = -1;
            private static float _bonus;

            [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GetAttackDrawPercentage))]
            [HarmonyPostfix]
            private static void Drawn(Humanoid __instance, ref float __result)
            {
                // Before anything else: Hud asks this every frame whether or not a bow is up,
                // and nearly every one of those answers is 0.
                if (__result <= 0f || __result >= 1f) return;
                if (!RistConfig.Enabled.Value) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                // Bows by skill, not by m_bowDraw. A staff can carry the draw flag too, and a
                // crossbow's wait is its reload, which is a different number on a different path.
                var weapon = __instance.GetCurrentWeapon();
                if (weapon == null || weapon.m_shared == null) return;
                if (weapon.m_shared.m_skillType != Skills.SkillType.Bows) return;

                // Once per rendered frame. This is read every fixed update and every frame for
                // as long as the string is held, and TotalFor builds its totals from scratch.
                if (_frame != Time.frameCount)
                {
                    _frame = Time.frameCount;
                    _bonus = Mathf.Min(Effects.TotalFor(Ranged), Mathf.Max(0f, RistConfig.AttackSpeedMax.Value));
                }

                if (_bonus <= 0f) return;

                __result = Mathf.Clamp01(__result * (1f + _bonus));
            }
        }
    }
}
