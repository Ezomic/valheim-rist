using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Soft step's capstone: a creature that has lost you gives up the hunt after 12 seconds instead of 30.
    ///
    /// "The woods do not notice you" ends with the woods forgetting you. The ranks make you harder to spot;
    /// this makes you easy to lose, and it replaces -10% noise, which was Quiet wake's own effect: the
    /// two stones used to be each other's capstones.
    ///
    /// MonsterAI.UpdateTarget drops its alert and its target when m_timeSinceSensedTargetCreature passes a
    /// hard-coded 30 (there is no field to change), unless the creature is one that hunts players. A
    /// postfix performs the same drop at 12 for a creature whose target is a player carrying the stone.
    /// The drop is the game's own, line for line: not alerted, no creature or static target, the attack
    /// clock back to zero and the next target search five seconds off, so it does not go straight back to
    /// the same player on the next frame. Bosses and creatures that hunt players are left alone, and so
    /// is anything not yet 12 seconds out of sensing the player, which includes every creature that can
    /// still see or hear them.
    ///
    /// The AI runs on whichever client owns the creature, and that client holds only its own player's
    /// runestones, so the carver's stone is read from the flag word on the player's ZDO (Carried), which
    /// every client can read. The postfix is cheap by construction: it returns at the first field read
    /// for any creature with no player target, and reads the ZDO only once the 12 seconds have passed.
    /// </summary>
    internal static class LoseThem
    {
        internal const string Key = Carried.LoseThemKey;

        private const float GiveUpAfter = 12f;
        private const float RetargetAfter = 5f;

        private static AccessTools.FieldRef<MonsterAI, float> _sensed, _attacking, _updateTimer;
        private static AccessTools.FieldRef<MonsterAI, Character> _target;
        private static AccessTools.FieldRef<MonsterAI, StaticTarget> _static;
        private static MethodInfo _setAlerted;
        private static bool _bound, _bindFailed;

        private static int _lost;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _sensed = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceSensedTargetCreature");
                _attacking = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceAttacking");
                _updateTimer = AccessTools.FieldRefAccess<MonsterAI, float>("m_updateTargetTimer");
                _target = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
                _static = AccessTools.FieldRefAccess<MonsterAI, StaticTarget>("m_targetStatic");
                _setAlerted = AccessTools.Method(typeof(MonsterAI), "SetAlerted");
                if (_setAlerted == null) throw new System.MissingMethodException("MonsterAI.SetAlerted");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Lose them could not reach the game's creature AI and is off for this "
                                        + "session: " + e.Message);
            }

            return _bound;
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        internal static class Drop
        {
            [HarmonyPostfix]
            private static void Lost(MonsterAI __instance)
            {
                if (!Bind()) return;

                var target = _target(__instance) as Player;
                if (target == null || _sensed(__instance) <= GiveUpAfter) return;
                if (__instance.HuntPlayer() || !Carried.Has(target, Carried.LoseThem)) return;

                if (__instance.TryGetComponent<Character>(out var character) && Bosses.Is(character)) return;

                _setAlerted.Invoke(__instance, new object[] { false });
                _target(__instance) = null;
                _static(__instance) = null;
                _attacking(__instance) = 0f;
                _updateTimer(__instance) = RetargetAfter;
                _lost++;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "lose them: carved, gives up after " + GiveUpAfter.ToString("0", CultureInfo.InvariantCulture)
                  + "s, dropped " + _lost + " hunts here"
                : "lose them: not carved";
        }
    }
}
