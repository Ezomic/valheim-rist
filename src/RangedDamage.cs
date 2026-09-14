using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Quick draw's capstone: more damage from a bow or a crossbow, and from nothing else.
    ///
    /// It used to be m_damageModifier, the same field Keen edge carries, and that field is not
    /// what it looks like here. Rist builds one SE_Stats for the whole hand with
    /// m_modifyAttackSkill set to All - it has to be, or Keen edge would only ever apply to one
    /// skill - so any card writing m_damageModifier raises every weapon at once. Quick draw's
    /// capstone was quietly a second Keen edge: a fully carved Quick draw added 5% to a sword
    /// as much as to a bow.
    ///
    /// SEMan.ModifyAttack is where the game hands each attack's HitData to the status effects,
    /// with the attacking skill beside it, so the skill test is exact and the multiplier lands
    /// on the same numbers the game's own modifiers do. For a projectile that happens when the
    /// shot is fired and the HitData rides with the arrow or bolt, so the bonus is the one that
    /// was carved when it left the string.
    /// </summary>
    internal static class RangedDamage
    {
        internal const string Key = "*damage:ranged";

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyAttack))]
        [HarmonyPostfix]
        private static void Modified(Skills.SkillType skill, ref HitData hitData, Character ___m_character)
        {
            if (skill != Skills.SkillType.Bows && skill != Skills.SkillType.Crossbows) return;
            if (!RistConfig.Enabled.Value || hitData == null) return;

            // Local player only: another player's hand is theirs to apply, and a creature's
            // SEMan runs this for every arrow a skeleton looses.
            if (___m_character == null || !ReferenceEquals(___m_character, Player.m_localPlayer)) return;

            var bonus = Effects.TotalFor(Key);
            if (bonus <= 0f) return;

            hitData.m_damage.Modify(1f + bonus);
        }
    }
}
