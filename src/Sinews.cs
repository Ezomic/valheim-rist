using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The two numbers a card changes on the player's own body rather than through SE_Stats:
    /// how soon breath starts coming back, and how hard you push off the ground.
    ///
    /// Neither has a field on SE_Stats, so neither can be a plain catalogue line. Both are
    /// public fields on the character the game reads on its own schedule - m_staminaRegenDelay
    /// from RPC_UseStamina, m_jumpForce from Character.Jump - which is the same shape as the
    /// three numbers Horizon keeps, and the reason both are written here instead of patched.
    /// Patching either would mean running mod code on every point of stamina spent or every
    /// jump, to hand back a number that changes only when a hand of cards does.
    ///
    /// Written to the live Player, never to the prefab. A respawn builds a new Player carrying
    /// the prefab's values again, and Effects.Apply re-runs on a player change for exactly that
    /// reason, so the capture below re-reads the untouched numbers each time.
    /// </summary>
    internal static class Sinews
    {
        /// <summary>Seconds off the pause before stamina starts returning.</summary>
        internal const string StaminaDelay = "*staminadelay";

        /// <summary>Fraction added to jump force, so 0.2 is a fifth higher.</summary>
        internal const string JumpForce = "*jumpforce";

        /// <summary>
        /// The pause can be shortened and never removed, and this is not tidiness.
        ///
        /// Regen runs only while m_staminaRegenTimer has run out, and every point of stamina
        /// spent puts the timer back to m_staminaRegenDelay. Sprinting spends every frame, so
        /// any delay above zero is reset before it can elapse and stamina cannot come back
        /// while you run. At exactly zero that stops being true: regen would run during the
        /// sprint, out-pace the drain at any reasonable Long wind, and hand back the free
        /// sprinting that MinRunStaminaCost was added to stop - through a different door, and
        /// this time with no clamp in the way. A mis-typed catalogue line is all it would take,
        /// so the floor is here rather than in the catalogue's good intentions.
        /// </summary>
        private const float MinDelay = 0.25f;

        internal static float DelayCut;
        internal static float JumpBonus;

        // The player these were captured from, and the values it had before Rist touched them.
        private static Player _player;
        private static float _vanillaDelay;
        private static float _vanillaJump;

        /// <summary>
        /// What the character had before Rist wrote to it, for the readout to print a ratio
        /// against. Falls back to the live values when nothing has been captured yet, so a
        /// character holding no cards reads x1.00 rather than dividing by zero.
        /// </summary>
        internal static float VanillaJump(Player player)
        {
            if (ReferenceEquals(player, _player) && _vanillaJump > 0f) return _vanillaJump;
            return player == null ? 0f : player.m_jumpForce;
        }

        internal static float VanillaDelay(Player player)
        {
            if (ReferenceEquals(player, _player) && _vanillaDelay > 0f) return _vanillaDelay;
            return player == null ? 0f : player.m_staminaRegenDelay;
        }

        internal static void Reset()
        {
            _player = null;
            DelayCut = 0f;
            JumpBonus = 0f;
        }

        /// <summary>
        /// Put the current hand on the player. Called from Effects.Apply, which already returns
        /// early unless the cards or the player changed.
        /// </summary>
        internal static void Apply(Player player)
        {
            if (player == null) return;

            // Capture on a player change only. Capturing every call would read back a value
            // this class had already written and drift a little further every time a card was
            // taken - the delay walking toward its floor and the jump climbing without end.
            if (!ReferenceEquals(player, _player))
            {
                _player = player;
                _vanillaDelay = player.m_staminaRegenDelay;
                _vanillaJump = player.m_jumpForce;
            }

            player.m_staminaRegenDelay = Mathf.Max(MinDelay, _vanillaDelay - Mathf.Max(0f, DelayCut));

            // Multiplied, not added, because the Jump skill already multiplies the same field by
            // up to 1.4 at level 100 and the card should read as a share of your jump rather
            // than a flat push that matters less the better you get.
            player.m_jumpForce = _vanillaJump * (1f + Mathf.Max(0f, JumpBonus));
        }
    }
}
