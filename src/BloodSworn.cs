using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Blood-sworn: blood magic levels, a blood shield that absorbs more, and at rank five one
    /// more summon than the staff allows and a shield that recasting refills.
    ///
    /// Four specials, because the game has one place for each and they are not the same place:
    ///
    ///   *blood:levels   SEMan.ModifySkillLevel, the call Skills.GetSkillLevel makes. Gated to
    ///                   BloodMagic, so the levels raise nothing else. They count wherever the
    ///                   game reads the skill: a staff's damage, the shield's absorb, and the
    ///                   thresholds in a summon's level-up table.
    ///   *blood:absorb   SE_Shield.SetLevel, a fraction on top of the figure the game worked out
    ///                   itself (m_absorbDamage + m_absorbDamagePerSkillLevel * skill, plus the
    ///                   world level's share). Nothing is typed here that the game already knows.
    ///   *blood:refill   the same postfix, zeroing the damage the shield has soaked. Vanilla's
    ///                   recast calls ResetTime and SetLevel on the running shield and leaves the
    ///                   private damage counter alone, so a recast on a half-spent shield is a
    ///                   fresh timer over the same damage: it never refilled, whatever it looks like.
    ///   *blood:summon   Tameable.UnsummonMaxInstances, the per-player cap, plus the global count
    ///                   SpawnAbility checks first.
    ///
    /// "A blood magic shield" is read off the shield, not off a name list: SE_Shield names the
    /// skill it levels when it breaks, and only a shield that names BloodMagic is touched. A staff
    /// of the other school keeps its shield exactly as it was.
    ///
    /// The shield is only touched on a running copy in the local player's own status effects. The
    /// tooltip calls SetLevel on the shared prefab with no character, and widening that would put
    /// a number on a tooltip that no shield was ever cast at.
    /// </summary>
    internal static class BloodSworn
    {
        internal const string Levels = "*blood:levels";
        internal const string Absorb = "*blood:absorb";
        internal const string Summon = "*blood:summon";
        internal const string Refill = "*blood:refill";

        private const string SummonKey = "rist_summons";

        private static Player _publishedTo;
        private static int _published = -1;

        private static bool Mine(Character character)
        {
            return character != null && RistConfig.Enabled.Value
                   && ReferenceEquals(character, Player.m_localPlayer);
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifySkillLevel))]
        [HarmonyPostfix]
        private static void Levelled(SEMan __instance, Skills.SkillType skill, ref float level)
        {
            if (skill != Skills.SkillType.BloodMagic) return;

            var bonus = Effects.Cached(Levels);
            if (bonus <= 0f) return;

            var player = Player.m_localPlayer;
            if (player == null || !RistConfig.Enabled.Value) return;
            if (!ReferenceEquals(__instance, player.GetSEMan())) return;

            level += bonus;
        }

        [HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.SetLevel))]
        [HarmonyPostfix]
        private static void Shielded(SE_Shield __instance, ref float ___m_totalAbsorbDamage, ref float ___m_damage)
        {
            if (__instance.m_levelUpSkillOnBreak != Skills.SkillType.BloodMagic) return;
            if (!Mine(__instance.m_character)) return;

            var more = Effects.Cached(Absorb);
            if (more > 0f) ___m_totalAbsorbDamage *= 1f + more;

            if (Effects.Cached(Refill) > 0f) ___m_damage = 0f;

            if (RistConfig.Verbose.Value)
                RistPlugin.Log.LogInfo("Blood shield set: absorbs " +
                                       ___m_totalAbsorbDamage.ToString("0.#", CultureInfo.InvariantCulture) +
                                       ", soaked " + ___m_damage.ToString("0.#", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The per-player cap. Tameable.Command calls this when a summon starts following, and
        /// drops the oldest of that player's followers of the same kind down to the number it is
        /// given - the number SpawnAbility wrote onto the creature at spawn, from the staff.
        ///
        /// The follower is read off the creature, and the bonus off that player rather than off
        /// this machine, because the creature's owner is whoever is nearest and need not be the
        /// caster. For anyone but the local player it is the one number they publish.
        /// </summary>
        [HarmonyPatch(typeof(Tameable), "UnsummonMaxInstances")]
        [HarmonyPrefix]
        private static void Capped(ref int maxInstances, MonsterAI ___m_monsterAI)
        {
            if (maxInstances <= 0 || ___m_monsterAI == null) return;

            var target = ___m_monsterAI.GetFollowTarget();
            if (target == null || !target.TryGetComponent<Player>(out var follower)) return;

            maxInstances += ExtraSummons(follower);
        }

        /// <summary>
        /// The count SpawnAbility checks before it will spawn at all. m_maxSpawned counts every
        /// loaded creature of the prefab, anyone's, and only a staff that sets it is affected -
        /// whether Dead Raiser sets it is prefab data not readable outside the running game, so
        /// this hands back one short for a tameable prefab counted over the whole world (maxRange
        /// zero, which is how SpawnAbility asks), and leaves every ranged count alone.
        /// </summary>
        [HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.GetNrOfInstances),
            typeof(GameObject), typeof(Vector3), typeof(float), typeof(bool), typeof(bool))]
        [HarmonyPostfix]
        private static void Counted(GameObject prefab, float maxRange, ref int __result)
        {
            if (!_inSpawn || __result <= 0 || maxRange > 0f || prefab == null) return;
            if (Effects.Cached(Summon) <= 0f || !Mine(Player.m_localPlayer)) return;
            if (!prefab.TryGetComponent<Tameable>(out _)) return;

            __result--;
        }

        private static bool _inSpawn;

        /// <summary>
        /// Marks the stretches of SpawnAbility.Spawn that run, so the discount above is given to the
        /// ability's own check and not to any other caller that counts a prefab with no range. Spawn
        /// is a coroutine, so it is its compiler-made MoveNext that is patched. Its pauses are
        /// yields, so the flag is up only inside a slice and never across a frame, and a finalizer
        /// clears it if a slice throws.
        /// </summary>
        internal static class Spawning
        {
            private static System.Reflection.MethodBase Target()
            {
                var machine = AccessTools.FirstInner(typeof(SpawnAbility), t => t.Name.StartsWith("<Spawn>"));
                return machine == null ? null : AccessTools.Method(machine, "MoveNext");
            }

            [HarmonyTargetMethod]
            private static System.Reflection.MethodBase Find()
            {
                var move = Target();
                if (move == null)
                    RistPlugin.Log.LogError("Blood-sworn could not find SpawnAbility's spawn routine: the extra " +
                                            "summon is off for this session.");
                return move;
            }

            [HarmonyPrefix]
            private static void Enter()
            {
                _inSpawn = true;
            }

            [HarmonyFinalizer]
            private static void Leave()
            {
                _inSpawn = false;
            }
        }

        internal static int ExtraSummons(Player player)
        {
            if (player == null || !RistConfig.Enabled.Value) return 0;

            if (ReferenceEquals(player, Player.m_localPlayer))
                return Effects.Cached(Summon) > 0f ? 1 : 0;

            if (!player.TryGetComponent<ZNetView>(out var nview) || !nview.IsValid()) return 0;

            var zdo = nview.GetZDO();
            return zdo != null && zdo.GetInt(SummonKey, 0) > 0 ? 1 : 0;
        }

        /// <summary>
        /// Puts the one flag other clients need on this character's own ZDO, when it changes.
        /// Not behind ShowPlate: the plate is decoration, and this decides whose summons are
        /// culled. From the plugin's Update.
        /// </summary>
        internal static void Publish(Player player)
        {
            if (player == null) return;

            if (!ReferenceEquals(player, _publishedTo))
            {
                _publishedTo = player;
                _published = -1;
            }

            var want = Effects.Cached(Summon) > 0f ? 1 : 0;
            if (want == _published) return;

            if (!player.TryGetComponent<ZNetView>(out var nview) || !nview.IsValid() || !nview.IsOwner()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            zdo.Set(SummonKey, want);
            _published = want;
        }

        internal static void Forget()
        {
            _publishedTo = null;
            _published = -1;
        }

        /// <summary>
        /// What `rist show` prints: the blood magic level the game reads against the one on the
        /// character, and what a blood shield would be set to, worked on a copy of the game's own
        /// shield. The shield lines are ratios, because the absorb figure is prefab data nobody
        /// here has measured, and "x1.50" is true whatever it is.
        /// </summary>
        internal static string Probe(Player player)
        {
            var skills = player.GetSkills();
            var raw = 0f;
            foreach (var skill in skills.GetSkillList())
                if (skill.m_info != null && skill.m_info.m_skill == Skills.SkillType.BloodMagic) raw = skill.m_level;

            var read = skills.GetSkillLevel(Skills.SkillType.BloodMagic);
            var line = "blood magic " + read.ToString("0", CultureInfo.InvariantCulture) + " (character "
                       + Mathf.Floor(raw).ToString("0", CultureInfo.InvariantCulture) + ", +"
                       + (read - Mathf.Floor(raw)).ToString("0", CultureInfo.InvariantCulture) + ")";

            line += "  summons +" + ExtraSummons(player);

            SE_Shield shield = null;
            if (ObjectDB.instance != null)
                foreach (var effect in ObjectDB.instance.m_StatusEffects)
                    if (effect is SE_Shield s && s.m_levelUpSkillOnBreak == Skills.SkillType.BloodMagic) { shield = s; break; }

            if (shield == null) return line + "  blood shield: none in ObjectDB";

            var absorb = AccessTools.Field(typeof(SE_Shield), "m_totalAbsorbDamage");
            var soaked = AccessTools.Field(typeof(SE_Shield), "m_damage");
            if (absorb == null || soaked == null) return line + "  blood shield: fields not found";

            var bare = Object.Instantiate(shield);
            var mine = Object.Instantiate(shield);
            bare.m_character = null;
            mine.m_character = player;
            soaked.SetValue(mine, 50f);
            bare.SetLevel(1, read);
            mine.SetLevel(1, read);

            var ratio = (float)absorb.GetValue(bare) > 0f
                ? (float)absorb.GetValue(mine) / (float)absorb.GetValue(bare)
                : 1f;
            line += "  blood shield absorbs x" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                    + " (" + shield.name + "), soaked after a recast " + ((float)soaked.GetValue(mine)).ToString("0", CultureInfo.InvariantCulture)
                    + " of 50";

            Object.DestroyImmediate(bare);
            Object.DestroyImmediate(mine);
            return line;
        }
    }
}
