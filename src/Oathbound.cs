using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Oath-bound: the forsaken power comes back sooner, and at rank five its blessing lasts a
    /// minute longer - for everyone it lands on.
    ///
    /// Player.ActivateGuardianPower applies the effect to every player within ten metres first
    /// and sets m_guardianPowerCooldown from the power's own m_cooldown last. So the cooldown is
    /// cut in a postfix, and only when this call is the one that started it - a prefix records
    /// whether the power was ready, because the method returns false either way and a postfix
    /// alone cannot tell a cast from a refusal.
    ///
    /// The extra minute follows the caster, not the receiver: carved by whoever activates the
    /// power, it reaches every player the power reached; activated by someone without it,
    /// everyone gets the game's duration, the carver included. Robbin's rule. A power is a
    /// thing you share at a boss stone, and a longer one should be longer for the group.
    ///
    /// The other players' copies live in their own SEMan on their own machines - the game adds
    /// them there by RPC - so the caster cannot lengthen them directly. It sends each of those
    /// players a Rist RPC naming the power and the seconds, and their Rist sets the effect it just
    /// received to the power's own duration plus those seconds. The receiver checks the effect really is a forsaken power and
    /// caps the seconds, so a changed client cannot hand out hour-long blessings.
    ///
    /// Every power gets the minute, Moder's included, although Moder's carries +10% move speed
    /// (read from the game's assets, 2026-09-15; it is the only one of the seven that does).
    /// It skipped movement powers under the one-move-speed-source rule for a day, and Robbin
    /// took that out: the speed is the power's own, only lasting longer.
    /// </summary>
    internal static class Oathbound
    {
        internal const string Cooldown = "*power:cooldown";
        internal const string Duration = "*power:duration";

        internal const string Rpc = "Rist_PowerDuration";

        // The same ten metres ActivateGuardianPower uses, so the extra time reaches exactly the
        // players the power did.
        private const float Range = 10f;

        // A floor on the cut. A mis-typed catalogue line should cost a slightly-too-short
        // cooldown, not a forsaken power on a two-second loop.
        private const float MinCooldownFactor = 0.5f;

        // What a receiver accepts from anyone. Well above the shipped 60, well below abuse.
        private const float MaxSharedSeconds = 300f;

        // The RPC can arrive before the game's own effect RPC has been applied; hold it briefly
        // rather than lose it.
        private const float PendingWindow = 3f;

        private struct Pending
        {
            internal int Hash;
            internal float Seconds;
            internal float Until;
        }

        private static readonly List<Pending> _pending = new List<Pending>();

        [HarmonyPatch(typeof(Player), nameof(Player.ActivateGuardianPower))]
        [HarmonyPrefix]
        private static void Before(Player __instance, out bool __state)
        {
            __state = RistConfig.Enabled.Value && ReferenceEquals(__instance, Player.m_localPlayer) &&
                      __instance.m_guardianPowerCooldown <= 0f;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ActivateGuardianPower))]
        [HarmonyPostfix]
        private static void After(Player __instance, bool __state)
        {
            if (!__state || __instance.m_guardianPowerCooldown <= 0f) return;

            var cut = Effects.TotalFor(Cooldown);
            if (cut < 0f)
                __instance.m_guardianPowerCooldown *= Mathf.Max(MinCooldownFactor, 1f + cut);

            var extra = Effects.TotalFor(Duration);
            if (extra <= 0f) return;

            __instance.GetGuardianPowerHUD(out var power, out _);
            if (power == null) return;

            var hash = power.NameHash();
            var players = new List<Player>();
            Player.GetPlayersInRange(__instance.transform.position, Range, players);

            foreach (var p in players)
            {
                if (ReferenceEquals(p, __instance))
                {
                    Lengthen(p, hash, extra);
                    continue;
                }

                if (ZRoutedRpc.instance == null || !p.TryGetComponent<ZNetView>(out var nview)) continue;
                var zdo = nview.GetZDO();
                if (zdo == null) continue;

                ZRoutedRpc.instance.InvokeRoutedRPC(zdo.GetOwner(), Rpc, hash, extra);
            }
        }

        /// <summary>Registered by Net.EnsureRegistered alongside Rist's other RPCs.</summary>
        internal static void OnShared(long sender, int hash, float seconds)
        {
            if (!RistConfig.Enabled.Value || seconds <= 0f) return;

            seconds = Mathf.Min(seconds, MaxSharedSeconds);

            var player = Player.m_localPlayer;
            if (player != null && Lengthen(player, hash, seconds)) return;

            _pending.Add(new Pending { Hash = hash, Seconds = seconds, Until = Time.time + PendingWindow });
        }

        /// <summary>Retries a shared minute that arrived before its effect. From the plugin's Update.</summary>
        internal static void Tick()
        {
            if (_pending.Count == 0) return;

            var player = Player.m_localPlayer;
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if ((player != null && Lengthen(player, p.Hash, p.Seconds)) || Time.time > p.Until)
                    _pending.RemoveAt(i);
            }
        }

        /// <summary>
        /// Sets the player's running copy to base plus the seconds. False only when the effect is not
        /// there yet, so a refusal ends the retry: there is nothing to wait for.
        /// </summary>
        private static bool Lengthen(Player player, int hash, float seconds)
        {
            var running = player.GetSEMan().GetStatusEffect(hash);
            if (running == null) return false;

            // A forsaken power by the game's own naming, read off ObjectDB's prefab rather than
            // the running copy, and only one with a duration.
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(hash) : null;
            if (prefab == null || !prefab.name.StartsWith("GP_") || running.m_ttl <= 0f) return true;

            // Set, never added. The game refreshes a running power by zeroing its timer and
            // leaving m_ttl alone, so adding would stack a minute per carver per cast - and let a
            // changed client send the RPC over and over. Base plus the capped extra is the whole
            // duration, however many times it arrives.
            running.m_ttl = prefab.m_ttl + Mathf.Min(seconds, MaxSharedSeconds);
            return true;
        }

        /// <summary>
        /// Puts a forsaken power back to its own duration whenever the game adds or refreshes it.
        ///
        /// Needed for the half of Robbin's rule that says a caster without the capstone gives
        /// everyone the normal time. That caster sends no Rist message, and the game's refresh
        /// only zeroes the timer - so anyone still carrying a carver's longer copy would keep it.
        /// Internal_AddStatusEffect is where every add lands on the machine that owns the effect,
        /// the caster's own and the ones that arrive by RPC, and it runs before the carver's
        /// message does, so a carver's extra minute is applied on top of the reset.
        /// </summary>
        internal static class Reset
        {
            [HarmonyPatch(typeof(SEMan), "Internal_AddStatusEffect")]
            [HarmonyPostfix]
            private static void Added(SEMan __instance, int nameHash, bool resetTime)
            {
                if (!resetTime || ObjectDB.instance == null) return;

                var prefab = ObjectDB.instance.GetStatusEffect(nameHash);
                if (prefab == null || !prefab.name.StartsWith("GP_")) return;

                var running = __instance.GetStatusEffect(nameHash);
                if (running != null) running.m_ttl = prefab.m_ttl;
            }
        }
    }
}
