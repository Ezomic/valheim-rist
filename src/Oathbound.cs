using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Oath-bound: the forsaken power comes back sooner, and at rank five its blessing lasts a
    /// minute longer.
    ///
    /// Player.ActivateGuardianPower applies the effect to everyone within ten metres first and
    /// sets m_guardianPowerCooldown from the power's own m_cooldown last. So the cooldown is cut
    /// in a postfix, and only when this call is the one that started it - a prefix records
    /// whether the power was ready, because the method returns false either way and a postfix
    /// alone cannot tell a cast from a refusal.
    ///
    /// The extra minute goes on the caster's own copy of the effect, and not at all on a power
    /// that changes movement speed, wind or jump - move speed has one source in this catalogue.
    /// The players standing beside you receive theirs through their own SEMan - by RPC when you
    /// do not own them - and a caster's rank has no business rewriting another player's status
    /// effects, so they get the game's duration.
    /// </summary>
    internal static class Oathbound
    {
        internal const string Cooldown = "*power:cooldown";
        internal const string Duration = "*power:duration";

        // A floor on the cut. A mis-typed catalogue line should cost a slightly-too-short
        // cooldown, not a forsaken power on a two-second loop.
        private const float MinCooldownFactor = 0.5f;

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

            var running = __instance.GetSEMan().GetStatusEffect(power.NameHash());
            if (running == null || running.m_ttl <= 0f) return;

            // Which powers change movement is asset data this code cannot see, so it is tested
            // on the running effect rather than assumed. A power that does is not stretched.
            if (Movement.Changes(running as SE_Stats))
            {
                if (RistConfig.Verbose.Value)
                    RistPlugin.Log.LogInfo("Oath-bound left " + power.name + " at its own duration: it changes movement.");
                return;
            }

            running.m_ttl += extra;
        }
    }
}
