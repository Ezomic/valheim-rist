using System.Globalization;
using HarmonyLib;

namespace Rist
{
    /// <summary>
    /// Thick-hided's capstone: a hit worth under 4% of your maximum health does nothing.
    ///
    /// A thick hide ignores the small stuff, which is the swarm case (Deathsquitos, ticks, Seekers) that
    /// a percentage of armour cannot touch, and armour keeps its own job. It replaces another 5% of
    /// armour, which was the stone's own stat again.
    ///
    /// Creature hits only. The game tags every hit an attack works out as EnemyHit when the attacker is
    /// not a player (Attack.DoMeleeAttack, DoAreaAttack and the projectile path all do), and tags a fall,
    /// fire, poison, drowning and the rest with their own types, so the rule is one comparison and
    /// needs no list of creatures. The hit is judged where Character.ApplyDamage receives it, after the
    /// resistances and armour have been taken off, because "worth" means what would have been lost:
    /// a hit that armour already reduced under the line is shrugged, and a Troll's blow is not.
    /// Fire, poison and spirit are split off before ApplyDamage in RPC_Damage and arrive as damage over
    /// time, so they are untouched, which is what a hide should not stop.
    ///
    /// The threshold is a share of maximum health, so it grows with food and with the Plains and
    /// beyond, and it grows with armour too: both lift what counts as small. That is the thing to
    /// watch in play and the balance note says so. The hit is zeroed in place, and ApplyDamage returns
    /// early on a hit under a tenth of a point, which also skips the stagger it would have added.
    /// Only the local player is judged: another player's hide is their own game's business.
    /// </summary>
    internal static class Shrug
    {
        internal const string Key = "*shrug";

        internal const float Share = 0.04f;

        private static int _shrugged;
        private static float _soaked;

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        internal static class Small
        {
            [HarmonyPrefix]
            private static void Hit(Character __instance, HitData hit)
            {
                if (hit == null || hit.m_hitType != HitData.HitType.EnemyHit) return;
                if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (Effects.Cached(Key) <= 0f) return;

                var total = hit.GetTotalDamage();
                if (total <= 0f || total >= __instance.GetMaxHealth() * Share) return;

                hit.m_damage = default(HitData.DamageTypes);
                _shrugged++;
                _soaked += total;
            }
        }

        internal static string Probe()
        {
            return Effects.Cached(Key) > 0f
                ? "shrug: carved, hits under " + (Share * 100f).ToString("0", CultureInfo.InvariantCulture)
                  + "% of max health do nothing, shrugged " + _shrugged + " hits ("
                  + _soaked.ToString("0.0", CultureInfo.InvariantCulture) + " damage)"
                : "shrug: not carved";
        }
    }
}
