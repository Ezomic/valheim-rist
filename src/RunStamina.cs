using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Sprinting always costs something.
    ///
    /// The game sums run-stamina discounts additively and clamps only at zero. SE_Stats does
    /// <c>drain += baseDrain * m_runStaminaDrainModifier</c> once per active status effect, and
    /// <c>SEMan.ModifyRunStaminaDrain</c> finishes with <c>if (drain &lt; 0) drain = 0</c>. Vanilla
    /// never has two large sources at once, so the sum never passes -1 and the clamp never
    /// matters. Rist is a second source, and that is what broke it.
    ///
    /// Counted off the shipped catalogue at MaxRank 5: Tireless gives -0.25 through
    /// *stamina:move plus -0.15 at its capstone, Long wind's capstone -0.05 and Long stride's
    /// -0.06, so a hand holding all three is -0.51 before anything else. Eikthyr's power is the
    /// other half: exactly -0.60, measured in game on 2026-09-24 by rist-sprinting-is-never-free,
    /// which reads it alone as x0.40. It is asset data and not readable from the assembly, which
    /// is why it had to be measured rather than looked up. Together that is -1.11, the clamp
    /// read zero, and AllHailPidgey reported sprinting as free on the live server the same day.
    ///
    /// The fix is a floor rather than smaller cards. Trimming the three stones would only move
    /// the number the stack has to reach, and a fourth card touching running later would walk
    /// into the same wall; the invariant worth stating is that running is never free, whatever
    /// is stacked. It also leaves the cards feeling like what their tiles say, which is the
    /// same argument the catalogue makes for capping move speed at 10% rather than shaving
    /// every source of it.
    ///
    /// Nothing vanilla-only changes at the default. Eikthyr alone is -0.60 and a full hand of
    /// Rist alone is -0.51, and both sit above a 0.2 floor untouched. Only the combination is
    /// caught, which is the thing that was reported.
    /// </summary>
    internal static class RunStamina
    {
        /// <summary>
        /// Raises the drain back to the floor after every status effect has had its say.
        ///
        /// A postfix on SEMan rather than on Player.CheckRun, because SEMan is where the sum
        /// exists. Both of the game's callers are Player's - CheckRun charges the sprint, and
        /// GetStaminaUse asks with a base of 1 for the cost readout - so clamping here keeps
        /// what a player is shown and what a player is charged the same number. Creatures never
        /// reach this: Character.CheckRun decides whether you are running, and only Player's
        /// override pays for it.
        ///
        /// The floor is a fraction of <paramref name="baseDrain"/>, not of the raw
        /// m_runStaminaDrain, so it is already through the Run skill's halving and through
        /// armour weight before this sees it. A cap measured against the raw number would
        /// tighten as a character's own Run skill made sprinting cheaper, which is backwards.
        ///
        /// Only ever raises. A wind penalty multiplies drain upward inside SE_Stats and has to
        /// survive.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyRunStaminaDrain))]
        private static void Floor(float baseDrain, ref float drain)
        {
            var keep = RistConfig.MinRunStaminaCost.Value;
            if (keep <= 0f) return;

            // Negative base would invert the comparison. It cannot happen from CheckRun, but
            // GetStaminaUse hands in a literal and a future caller need not.
            if (baseDrain <= 0f) return;

            var floor = baseDrain * keep;
            if (drain < floor) drain = floor;
        }
    }
}
