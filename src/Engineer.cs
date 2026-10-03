using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Engineer: traps and siege weapons you built do more damage, and at rank five your ballistae
    /// never fire on a player.
    ///
    /// The difficulty the ticket named is real. A ballista fires on whichever machine owns it, which
    /// is usually whoever stands nearest, and a trap's spikes are worked out on the machine that
    /// stepped on it. Neither runs on the builder's machine, so "built by this player" is not
    /// something the game can answer at the moment a piece does harm. The rank is therefore stamped
    /// onto the piece as it is placed, as two values on the piece's own ZDO, and read back by
    /// whichever machine happens to be doing the shooting. Piece.SetCreator is the one call
    /// Player.PlacePiece makes on a piece it has just made, with the placing player's id, so that is
    /// where the stamp goes.
    ///
    /// Consequences, both meant. Carving the stone later does not re-arm anything already standing:
    /// the stamp is what was true when the piece was set down. And a piece set down by a player
    /// without the stone, or before it, carries no stamp and is exactly vanilla, so another player's
    /// traps are never touched.
    ///
    /// What counts as a trap or a siege weapon is decided by what the piece has, not by a list of
    /// names: a Turret (the ballista), a Trap, a Catapult, a SiegeMachine (the ram), or any damaging
    /// Aoe in its hierarchy (the sharp stakes keep theirs on a child). A fire, a smelter or a
    /// cooking station can carry an Aoe for other reasons and is left out.
    ///
    /// Where the damage is set, and so where it is scaled:
    ///   Aoe.GetDamage        the stakes, a trap's spikes and the ram's punch, read from the Aoe
    ///                        itself at the moment it hits, through the piece above it.
    ///   Projectile.Setup     the ballista bolt and the catapult load. Both build a HitData in
    ///   Aoe.Setup            ShootProjectile and hand it to Setup, and a catapult load spawns
    ///                        children that inherit that same HitData, so it is scaled in Setup
    ///                        while one of those two methods is running. A flag set around
    ///                        ShootProjectile tells Setup the shot is an engineer's.
    ///
    /// The capstone is a ballista that skips every player, not only the one who built it. The turret
    /// asks BaseAI.FindClosestCreature for a target with its m_targetPlayers field as the "include
    /// players" argument, and that field is per instance, so clearing it on a stamped turret is the
    /// whole of "does not fire on a player". Done in a prefix on UpdateTarget, on whichever machine
    /// owns the turret.
    /// </summary>
    internal static class Engineer
    {
        internal const string Damage = "*engineer:damage";
        internal const string Calibrated = "*engineer:calibrated";

        private const string KeyDamage = "rist_engineer";
        private const string KeyCalibrated = "rist_calibrated";

        private static readonly int HashDamage = KeyDamage.GetStableHashCode();
        private static readonly int HashCalibrated = KeyCalibrated.GetStableHashCode();

        /// <summary>The damage share of the shot being made right now, or zero outside ShootProjectile.</summary>
        private static float _shooting;

        /// <summary>
        /// A trap or siege weapon by what it carries. See the class comment for why not by name.
        /// </summary>
        internal static bool IsEngineered(GameObject piece)
        {
            if (piece == null) return false;

            if (piece.GetComponentInChildren<Turret>(true) != null) return true;
            if (piece.GetComponentInChildren<Trap>(true) != null) return true;
            if (piece.GetComponentInChildren<Catapult>(true) != null) return true;
            if (piece.GetComponentInChildren<SiegeMachine>(true) != null) return true;

            if (piece.GetComponentInChildren<Fireplace>(true) != null) return false;
            if (piece.GetComponentInChildren<Smelter>(true) != null) return false;
            if (piece.GetComponentInChildren<CookingStation>(true) != null) return false;

            foreach (var aoe in piece.GetComponentsInChildren<Aoe>(true))
                if (aoe != null && aoe.m_hitCharacters && aoe.m_damage.GetTotalDamage() > 0f) return true;

            return false;
        }

        private static ZDO Zdo(Piece piece)
        {
            if (piece == null || !piece.TryGetComponent<ZNetView>(out var nview) || !nview.IsValid()) return null;
            return nview.GetZDO();
        }

        // Aoe.GetDamage runs on every area hit and Calibration on every fixed update of every owned
        // ballista, and the parent walk is the part that costs. A part never changes its piece, so
        // the answer is kept per instance id, a miss included. Cleared past a cap, since the ids of
        // destroyed bolts and areas are never asked about again, and on logout.
        private const int PieceCacheCap = 4096;
        private static readonly Dictionary<int, Piece> PieceOf = new Dictionary<int, Piece>();

        private static Piece PieceAbove(Component part)
        {
            var id = part.GetInstanceID();
            if (PieceOf.TryGetValue(id, out var cached) && (cached != null || ReferenceEquals(cached, null))) return cached;

            if (PieceOf.Count >= PieceCacheCap) PieceOf.Clear();

            var piece = part.GetComponentInParent<Piece>();
            PieceOf[id] = piece;
            return piece;
        }

        internal static void Forget()
        {
            PieceOf.Clear();
        }

        /// <summary>The damage share stamped on the piece this component sits under, or zero.</summary>
        private static float Stamped(Component part)
        {
            if (part == null) return 0f;

            var zdo = Zdo(PieceAbove(part));
            return zdo == null ? 0f : Mathf.Max(0f, zdo.GetFloat(HashDamage, 0f));
        }

        internal static class Stamp
        {
            [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
            [HarmonyPostfix]
            private static void Placed(Piece __instance, long uid)
            {
                if (!RistConfig.Enabled.Value || Player.m_localPlayer == null) return;

                var damage = Effects.Cached(Damage);
                if (damage <= 0f) return;

                var zdo = Zdo(__instance);
                if (zdo == null || zdo.GetLong(ZDOVars.s_creator, 0L) != uid) return;
                if (!IsEngineered(__instance.gameObject)) return;

                zdo.Set(HashDamage, damage);
                if (Effects.Cached(Calibrated) > 0f) zdo.Set(HashCalibrated, 1);

                if (RistConfig.Verbose.Value)
                    RistPlugin.Log.LogInfo("Engineer stamped " + Utils.GetPrefabName(__instance.gameObject) + " at +"
                                           + Mathf.RoundToInt(damage * 100f) + "%.");
            }
        }

        internal static class Hits
        {
            [HarmonyPatch(typeof(Aoe), "GetDamage", typeof(int))]
            [HarmonyPostfix]
            private static void Scaled(Aoe __instance, ref HitData.DamageTypes __result)
            {
                var bonus = Stamped(__instance);
                if (bonus > 0f) __result.Modify(1f + bonus);
            }
        }

        internal static class Shots
        {
            [HarmonyPatch(typeof(Turret), nameof(Turret.ShootProjectile))]
            [HarmonyPrefix]
            private static void TurretFiring(Turret __instance)
            {
                _shooting = Stamped(__instance);
            }

            [HarmonyPatch(typeof(Catapult), "ShootProjectile")]
            [HarmonyPrefix]
            private static void CatapultFiring(Catapult __instance)
            {
                _shooting = Stamped(__instance);
            }

            // A finalizer, not a postfix: a throw inside ShootProjectile must not leave the next
            // shot of somebody else's turret scaled.
            [HarmonyPatch(typeof(Turret), nameof(Turret.ShootProjectile))]
            [HarmonyFinalizer]
            private static void TurretDone()
            {
                _shooting = 0f;
            }

            [HarmonyPatch(typeof(Catapult), "ShootProjectile")]
            [HarmonyFinalizer]
            private static void CatapultDone()
            {
                _shooting = 0f;
            }

            [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
            [HarmonyPrefix]
            private static void ProjectileSetup(HitData hitData)
            {
                if (_shooting > 0f && hitData != null) hitData.m_damage.Modify(1f + _shooting);
            }

            [HarmonyPatch(typeof(Aoe), nameof(Aoe.Setup))]
            [HarmonyPrefix]
            private static void AoeSetup(HitData hitData)
            {
                if (_shooting > 0f && hitData != null) hitData.m_damage.Modify(1f + _shooting);
            }
        }

        internal static class Calibration
        {
            [HarmonyPatch(typeof(Turret), "UpdateTarget")]
            [HarmonyPrefix]
            private static void Spare(Turret __instance)
            {
                if (!__instance.m_targetPlayers) return;

                var zdo = Zdo(PieceAbove(__instance));
                if (zdo != null && zdo.GetInt(HashCalibrated, 0) > 0) __instance.m_targetPlayers = false;
            }
        }

        /// <summary>
        /// What `rist show` prints: every trap and siege weapon within 40 m, with what is stamped
        /// on it and what the game reads from it. A stake, a trap or a ram shows the damage ratio
        /// its Aoe reports through the same call a hit uses, so a stamp that did not reach the hit
        /// shows as x1.00 beside a +40%. A ballista shows whether it still targets players.
        /// </summary>
        internal static string Probe(Player player)
        {
            var found = new List<string>();

            foreach (var piece in Object.FindObjectsOfType<Piece>())
            {
                if (piece == null) continue;
                if (Vector3.Distance(piece.transform.position, player.transform.position) > 40f) continue;
                if (!IsEngineered(piece.gameObject)) continue;

                var zdo = Zdo(piece);
                var bonus = zdo == null ? 0f : zdo.GetFloat(HashDamage, 0f);

                var sb = new StringBuilder(Utils.GetPrefabName(piece.gameObject));
                sb.Append(bonus > 0f ? " +" + Mathf.RoundToInt(bonus * 100f) + "%" : " unstamped");

                var turret = piece.GetComponentInChildren<Turret>(true);
                if (turret != null)
                {
                    sb.Append(zdo != null && zdo.GetInt(HashCalibrated, 0) > 0 ? " calibrated" : "");
                    sb.Append(turret.m_targetPlayers ? ", targets players" : ", does not target players");
                }

                var aoe = piece.GetComponentInChildren<Aoe>(true);
                if (aoe != null && turret == null)
                {
                    var bare = aoe.m_damage.GetTotalDamage();
                    var read = AoeDamage(aoe);
                    if (bare > 0f) sb.Append(", hits x" + (read / bare).ToString("0.00", CultureInfo.InvariantCulture));
                }

                found.Add(sb.ToString());
            }

            return found.Count == 0
                ? "engineer: no trap or siege weapon within 40 m"
                : "engineer: " + string.Join("; ", found.ToArray());
        }

        private static float AoeDamage(Aoe aoe)
        {
            var method = AccessTools.Method(typeof(Aoe), "GetDamage", new[] { typeof(int) });
            if (method == null) return 0f;

            var damage = (HitData.DamageTypes)method.Invoke(aoe, new object[] { 1 });
            return damage.GetTotalDamage();
        }
    }
}
