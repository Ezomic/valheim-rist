using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Quick draw's capstone: loose a shot and the next draw you begin within a second and a half
    /// starts 15% full.
    ///
    /// "The string is back before you are" is the follow-up shot, and the bonus only exists inside a
    /// rhythm of shots, so a lone arrow is drawn exactly as before. It replaces +5% damage from bows and
    /// crossbows. It is still a draw-speed effect on a draw-speed stone, closer to the stat than the
    /// other capstones in this round, and the balance note says so; the difference is that it rewards
    /// the second arrow and not the first.
    ///
    /// Two seams. Attack.Start runs when the arrow is released, so a postfix on it notes the moment of
    /// a bow shot. Attack.StartDraw runs once when the string is first pulled, while the draw timer
    /// Player.UpdateAttackBowDraw counts is still zero, and adds Time.fixedDeltaTime to it right after,
    /// so a postfix there sets the timer to a share (15% by default, SecondNockShare) of the bow's own draw time and the game carries on from
    /// it. That duration is worked out the way Humanoid.GetAttackDrawPercentage does, from the bow's
    /// m_drawDurationMin and the Bows skill, so the head start is 15% of what this player's draw
    /// really takes. Quick draw's own ranks scale the percentage where it is read and compose with it.
    ///
    /// Bows only. A crossbow's wait is a reload on another path and is not touched. A head start is
    /// spent once: it is armed by a shot and used by the next draw, however long that draw is held.
    /// </summary>
    internal static class SecondNock
    {
        internal const string Key = "*draw:second";

        private const float Window = 1.5f;
        private static float _shot = -100f;
        private static int _nocked;

        private static AccessTools.FieldRef<Humanoid, float> _drawTime;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _drawTime = AccessTools.FieldRefAccess<Humanoid, float>("m_attackDrawTime");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Second nock could not reach the game's draw timer and is off for "
                                        + "this session: " + e.Message);
            }

            return _bound;
        }

        private static bool IsBow(ItemDrop.ItemData weapon)
        {
            return weapon != null && weapon.m_shared != null && weapon.m_shared.m_skillType == Skills.SkillType.Bows;
        }

        private static bool Mine(Humanoid character)
        {
            return RistConfig.Enabled.Value && ReferenceEquals(character, Player.m_localPlayer);
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        internal static class Loose
        {
            [HarmonyPostfix]
            private static void Shot(bool __result, Humanoid character, ItemDrop.ItemData weapon)
            {
                if (!__result || !Mine(character) || !IsBow(weapon) || Effects.Cached(Key) <= 0f) return;
                _shot = Time.time;
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.StartDraw))]
        internal static class Draw
        {
            [HarmonyPostfix]
            private static void Pulled(bool __result, Humanoid character, ItemDrop.ItemData weapon)
            {
                if (!__result || !Mine(character) || !IsBow(weapon) || !Bind()) return;
                if (Time.time - _shot > Window || Effects.Cached(Key) <= 0f) return;

                _shot = -100f;

                var attack = weapon.m_shared.m_attack;
                var skill = character.GetSkillFactor(weapon.m_shared.m_skillType);
                var duration = Mathf.Lerp(attack.m_drawDurationMin, attack.m_drawDurationMin * 0.2f, skill);
                if (duration <= 0f) return;

                _drawTime(character) += duration * Mathf.Clamp01(RistConfig.SecondNockShare.Value);
                _nocked++;
            }
        }

        internal static void Forget()
        {
            _shot = -100f;
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "second nock: carved, " + (Mathf.Clamp01(RistConfig.SecondNockShare.Value) * 100f).ToString("0", CultureInfo.InvariantCulture)
                  + "% head start, given " + _nocked + " times"
                : "second nock: not carved";
        }
    }
}
