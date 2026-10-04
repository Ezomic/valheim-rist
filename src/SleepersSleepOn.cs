using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Quiet wake's capstone: a sleeping creature is not woken by you walking close, only by real noise.
    ///
    /// A wake is what you leave behind, and sleepers are what you wake. It moves the noise stone from a
    /// number to what it is for, and it replaces +10% detection reduction, which was Soft step's effect.
    ///
    /// MonsterAI.UpdateSleep wakes a sleeper in three ways: it hunts players, a player is within
    /// m_wakeupRange (5 m by default), or m_noiseWakeup is set and a player's noise reaches it. Only the
    /// middle one is closeness, so this takes it away: when every player who would wake the sleeper by
    /// standing near carries the stone, UpdateSleep is replaced by a copy of itself without that branch,
    /// the sleep delay and the noise branch kept as the game has them. A player without the stone in range
    /// leaves the game's own method to run, so a friend who walks up still wakes it, and a sleeper that
    /// hunts players is left to the game too. Quiet wake's own ranks make noise smaller, so the noise
    /// branch is the one a Quiet wake carver is working against.
    ///
    /// Which prefabs sleep is prefab data this repository has not read. m_sleeping and m_wakeupRange are
    /// fields on the creature's MonsterAI, so a Devkit rip of one answers it; the balance note leaves
    /// this stone the weakest until that is known, since it only matters where sleepers are common.
    ///
    /// The AI runs on the owner of the creature, which holds only its own player's stones, so the stone
    /// is read from the flag word on each player's ZDO (Carried).
    /// </summary>
    internal static class SleepersSleepOn
    {
        internal const string Key = Carried.SleepersKey;

        private static readonly List<Player> _near = new List<Player>();
        private static readonly HashSet<int> _kept = new HashSet<int>();

        private static AccessTools.FieldRef<MonsterAI, float> _timer, _delay;
        private static MethodInfo _wake;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _timer = AccessTools.FieldRefAccess<MonsterAI, float>("m_sleepTimer");
                _delay = AccessTools.FieldRefAccess<MonsterAI, float>("m_sleepDelay");
                _wake = AccessTools.Method(typeof(MonsterAI), "Wakeup");
                if (_wake == null) throw new System.MissingMethodException("MonsterAI.Wakeup");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Sleepers sleep on could not reach the game's creature AI and is off for "
                                        + "this session: " + e.Message);
            }

            return _bound;
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateSleep")]
        internal static class Sleep
        {
            [HarmonyPrefix]
            private static bool Quietly(MonsterAI __instance, float dt)
            {
                if (!__instance.IsSleeping() || __instance.m_wakeupRange <= 0f || !Bind()) return true;
                if (__instance.HuntPlayer()) return true;

                _near.Clear();
                Player.GetPlayersInRange(__instance.transform.position, __instance.m_wakeupRange, _near);

                var carriers = 0;
                foreach (var player in _near)
                {
                    if (player == null || player.InGhostMode() || player.IsDebugFlying()) continue;
                    if (!Carried.Has(player, Carried.SleepersSleepOn)) return true;
                    carriers++;
                }

                if (carriers == 0) return true;

                _timer(__instance) += dt;
                if (_kept.Count < 256) _kept.Add(__instance.GetInstanceID());
                if (_timer(__instance) < _delay(__instance) || !__instance.m_noiseWakeup) return false;

                var noisy = Player.GetPlayerNoiseRange(__instance.transform.position, __instance.m_maxNoiseWakeupRange);
                if (noisy != null && !noisy.InGhostMode() && !noisy.IsDebugFlying())
                    _wake.Invoke(__instance, null);

                return false;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "sleepers sleep on: carved, kept " + _kept.Count + " sleepers asleep here"
                : "sleepers sleep on: not carved";
        }
    }
}
