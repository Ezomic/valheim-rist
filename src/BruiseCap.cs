using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Thick-hided's capstone: no single hit can take more than half of your maximum health.
    ///
    /// You cannot be one-shot from full health, and nothing else about being hit changes: a boss still wears
    /// you down, and a long fight is still lost. It replaces Shrug (a hit under 4% of your health does
    /// nothing), which was the swarm case; this is the opposite end, the one big blow. The stone's armour
    /// from its ranks is unchanged.
    ///
    /// Character.RPC_Damage works a hit out in this order: resistances, then armour, then it strips the fire,
    /// poison and spirit parts off the hit (they arrive later as damage over time), and only then calls
    /// ApplyDamage, which for a player multiplies the hit by Game.m_localDamgeTakenRate (the world's damage
    /// taken setting) and subtracts it from the health. So ApplyDamage is the place where "the final damage of
    /// one hit" exists, and a prefix on it sees the hit after armour and resistances. The cap is judged on the
    /// hit as the health bar will see it, that is times the damage-taken rate, and the whole hit is scaled
    /// down by the one factor that brings it to the cap, so the types keep their proportions and the stagger
    /// that follows (worked out from the same damage) shrinks with it.
    ///
    /// One hit means one HitData handed to ApplyDamage once. Damage over time is not capped: burning, poison,
    /// smoke and the cold each call ApplyDamage again for every tick, typed Burning, Poisoned, Smoke and
    /// Freezing, and the drowning, water, lava and ocean-heat ticks are typed the same way. Those, the edge of
    /// the world (lethal by design) and the player's own Self hits (a staff that costs health, which is a
    /// price and not a blow) are left alone by type. Everything else is a hit and is capped: a creature's
    /// blow, another player's, a tree or a collapsing structure, a boat, a trap, and a fall.
    ///
    /// A fall is capped on purpose. It is one HitData typed Fall, delivered through the same path, and the
    /// fall system only decides how much damage there is; it never depends on the health bar surviving at
    /// full, so capping the result breaks nothing, including Sure-footed's landing roll, which shortens the
    /// fall before the damage is worked out. The cost is that a drop that used to kill a full-health player
    /// now leaves them at half, which is what "cannot be one-shot" says.
    ///
    /// The cap is a share of maximum health, so it is only a ceiling: a player already below it dies to a
    /// hit under it as always. Only the local player; the hit is applied on the machine that owns the player.
    /// </summary>
    internal static class BruiseCap
    {
        internal const string Key = "*hit:cap";

        private const float LeastShare = 0.05f;

        private static int _capped;
        private static float _lost;
        private static float _lastCap;

        internal static float Share => Mathf.Clamp(RistConfig.BruiseCapShare.Value, LeastShare, 1f);

        private static bool IsAHit(HitData.HitType type)
        {
            switch (type)
            {
                case HitData.HitType.Burning:
                case HitData.HitType.Poisoned:
                case HitData.HitType.Smoke:
                case HitData.HitType.Freezing:
                case HitData.HitType.Drowning:
                case HitData.HitType.Water:
                case HitData.HitType.AshlandsLava:
                case HitData.HitType.AshlandsOcean:
                case HitData.HitType.CinderFire:
                case HitData.HitType.Incinerator:
                case HitData.HitType.EdgeOfWorld:
                case HitData.HitType.Self:
                    return false;
                default:
                    return true;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        internal static class Cap
        {
            [HarmonyPrefix]
            private static void Hit(Character __instance, HitData hit)
            {
                if (hit == null || !RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (Effects.Cached(Key) <= 0f || !IsAHit(hit.m_hitType)) return;

                var rate = Game.m_localDamgeTakenRate;
                var total = hit.GetTotalDamage() * rate;
                var cap = __instance.GetMaxHealth() * Share;
                if (total <= cap || total <= 0f) return;

                hit.ApplyModifier(cap / total);
                _capped++;
                _lost += total - cap;
                _lastCap = cap;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "bruise cap: carved, no hit takes more than " + (Share * 100f).ToString("0", CultureInfo.InvariantCulture)
                  + "% of max health, capped " + _capped + " hits ("
                  + _lost.ToString("0.0", CultureInfo.InvariantCulture) + " damage"
                  + (_capped == 0 ? "" : ", last cap " + _lastCap.ToString("0.0", CultureInfo.InvariantCulture))
                  + ")"
                : "bruise cap: not carved";
        }
    }
}
