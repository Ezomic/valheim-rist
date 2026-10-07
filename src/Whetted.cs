using System.Globalization;
using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Sure hand's capstone: axes and pickaxes lose 40% less durability.
    ///
    /// The stone is chopping and mining without waiting, and a tool that outlasts the vein is the other half
    /// of that. It replaces another 12% stamina regen, which belonged to Long wind. A tool a mod can reach is
    /// one with m_useDurability; the hammer and the hoe are not covered, since the stone's flavour is axe and
    /// pick and the wear on a building tool is a different bill.
    ///
    /// The cost of a connecting swing is a flat 1 durability inline in Attack.DoMeleeAttack, with no method
    /// of its own to patch, and the other attack types spend theirs inline too. So the durability is read
    /// before Attack.OnAttackTrigger, which every attack type goes through, and after it, and 40% of
    /// whatever left is put back. The refund never exceeds what was spent, so it cannot mend a tool.
    ///
    /// The axe counts as a tool here as it does for the swing speed, because it is one item: an axe swung at
    /// a greydwarf wears 40% less as well. Local player only.
    /// </summary>
    internal static class Whetted
    {
        internal const string Key = "*tool:whetted";

        private const float Share = 0.4f;

        private static AccessTools.FieldRef<Attack, Humanoid> _owner;
        private static AccessTools.FieldRef<Attack, ItemDrop.ItemData> _weapon;
        private static bool _bound, _bindFailed;

        private static float _saved;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _owner = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
                _weapon = AccessTools.FieldRefAccess<Attack, ItemDrop.ItemData>("m_weapon");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Whetted could not reach the game's attack fields and is off for this "
                                        + "session: " + e.Message);
            }

            return _bound;
        }

        private static bool IsPickOrAxe(ItemDrop.ItemData weapon)
        {
            switch (weapon.m_shared.m_skillType)
            {
                case Skills.SkillType.Axes:
                case Skills.SkillType.Pickaxes:
                case Skills.SkillType.WoodCutting:
                    return true;
                default:
                    return false;
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        internal static class Swing
        {
            [HarmonyPrefix]
            private static void Before(Attack __instance, out float __state)
            {
                __state = -1f;
                if (!RistConfig.Enabled.Value || Effects.Cached(Key) <= 0f || !Bind()) return;
                if (!ReferenceEquals(_owner(__instance), Player.m_localPlayer)) return;

                var weapon = _weapon(__instance);
                if (weapon == null || weapon.m_shared == null || !weapon.m_shared.m_useDurability) return;
                if (!IsPickOrAxe(weapon)) return;

                __state = weapon.m_durability;
            }

            [HarmonyPostfix]
            private static void After(Attack __instance, float __state)
            {
                if (__state < 0f) return;

                var weapon = _weapon(__instance);
                if (weapon == null) return;

                var lost = __state - weapon.m_durability;
                if (lost <= 0f) return;

                weapon.m_durability += lost * Share;
                _saved += lost * Share;
            }
        }

        internal static string Probe()
        {
            var speed = ", tool swing speed +" + ((int)System.Math.Round(Effects.Cached(AttackSpeed.Tools) * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
            return Effects.Cached(Key) > 0f
                ? "whetted: carved, " + ((int)(Share * 100f)).ToString(CultureInfo.InvariantCulture)
                  + "% less wear on axes and picks, saved " + _saved.ToString("0.0", CultureInfo.InvariantCulture)
                  + " durability here" + speed
                : "whetted: not carved" + speed;
        }
    }
}
