using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Swift-mending's capstone: ten seconds after your last swing or hit taken, you recover a quarter of
    /// the health you lost in that fight, over five seconds.
    ///
    /// "Flesh knits without asking" is regen that arrives when the fight ends. Vanilla regen is one
    /// small tick of food regen every ten seconds, too little for a bigger tick to be felt, which is
    /// also why the old capstone, +2 armour, was a number on another stat. It does nothing during a
    /// fight, so it saves potions and not lives, and it does not soften death mid-fight.
    ///
    /// It needs no patch. Health is read every frame from the player's own ZDO-backed GetHealth: a
    /// fall is a hit taken, a rise is healing, and the tally of what was lost shrinks by every
    /// rise that is not this capstone's own, so a mead or a regen tick in the middle of a fight takes
    /// its share off what is owed and nothing is healed twice. The last swing is read from
    /// Humanoid.m_lastCombatTimer, which StartAttack resets and Player.CanSwitchPVP reads at the same ten
    /// seconds. Blocking is not counted as a swing; a block that lets damage through is a hit taken.
    ///
    /// New damage or a new swing while the mend is running cancels what is left of it, since the fight
    /// is not over. The share that was not paid out stays out of the tally, which is why the capstone
    /// cannot be farmed: only damage taken is ever owed, a quarter of it at most, once.
    /// </summary>
    internal static class PatchUp
    {
        internal const string Key = "*mend:patchup";

        private const float Quiet = 10f;
        private const float Share = 0.25f;
        private const float Over = 5f;
        private const float Smallest = 0.5f;

        private static Player _for;
        private static float _previous = -1f;
        private static float _lost;
        private static float _lastActive;
        private static float _left;
        private static float _rate;

        private static int _mends;
        private static float _mended;

        private static AccessTools.FieldRef<Humanoid, float> _combat;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _combat = AccessTools.FieldRefAccess<Humanoid, float>("m_lastCombatTimer");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Patch-up could not reach the game's combat timer and is off for this "
                                        + "session: " + e.Message);
            }

            return _bound;
        }

        /// <summary>From the plugin's Update.</summary>
        internal static void Tick(Player player)
        {
            if (player == null) return;

            if (!ReferenceEquals(player, _for))
            {
                _for = player;
                Clear();
            }

            if (Effects.Cached(Key) <= 0f || player.IsDead() || !Bind())
            {
                Clear();
                return;
            }

            var now = Time.time;
            var health = player.GetHealth();

            if (_previous >= 0f)
            {
                var change = health - _previous;
                if (change < -0.01f)
                {
                    _lost -= change;
                    _lastActive = now;
                    _left = 0f;
                }
                else if (change > 0.01f)
                {
                    _lost = Mathf.Max(0f, _lost - change);
                }
            }

            var since = _combat(player);
            if (since < 0.5f)
            {
                _lastActive = now;
                _left = 0f;
            }
            else
            {
                _lastActive = Mathf.Max(_lastActive, now - since);
            }

            if (_left > 0f)
            {
                var amount = Mathf.Min(_left, _rate * Time.deltaTime);
                player.Heal(amount, false);
                _left -= amount;
                _mended += amount;
            }
            else if (_lost >= Smallest && now - _lastActive >= Quiet)
            {
                _left = _lost * Share;
                _rate = _left / Over;
                _lost = 0f;
                _mends++;
                player.Message(MessageHud.MessageType.TopLeft, "Patch-up");
            }

            _previous = player.GetHealth();
        }

        private static void Clear()
        {
            _previous = -1f;
            _lost = 0f;
            _left = 0f;
        }

        internal static string Probe()
        {
            if (Effects.Cached(Key) <= 0f) return "patch-up: not carved";

            return "patch-up: carved, lost " + _lost.ToString("0.0", CultureInfo.InvariantCulture)
                   + ", mending " + _left.ToString("0.0", CultureInfo.InvariantCulture)
                   + ", mended " + _mends + " times (" + _mended.ToString("0.0", CultureInfo.InvariantCulture)
                   + " health)";
        }
    }
}
