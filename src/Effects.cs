using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ezomic.Core;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Turns the cards a player holds into actual effect.
    ///
    /// Everything except the inventory row rides SE_Stats, the game's own stat-modifier
    /// status effect. That is the whole reason the catalogue can name fields directly: the
    /// game already sums these across active status effects for carry weight, armour,
    /// stamina costs and the rest, so a card needs no stat plumbing of its own - only a
    /// number written into a field the game was already reading.
    /// </summary>
    internal static class Effects
    {
        private const string EffectName = "Rist";

        // StatusEffect.NameHash() hashes the UnityEngine.Object name, so this can be computed
        // once without instantiating anything to ask.
        private static readonly int EffectHash = EffectName.GetStableHashCode();

        private static SE_Stats _applied;
        private static string _appliedSignature;
        private static Player _appliedTo;

        /// <summary>Rows added over vanilla, for the window backdrop to grow by.</summary>
        internal static int ExtraRows { get; private set; }

        internal static void Reset()
        {
            if (_applied != null) Object.Destroy(_applied);

            _applied = null;
            _appliedSignature = null;
            _appliedTo = null;
        }

        /// <summary>
        /// Bring the local player in line with <paramref name="ranks"/>. Cheap to call every
        /// frame: it returns immediately unless the cards or the player actually changed.
        /// </summary>
        internal static void Apply(Player player, Dictionary<string, int> ranks)
        {
            if (player == null) return;

            var signature = Signature(ranks);

            // Both halves matter. Comparing only the signature would miss a respawn, which
            // builds a fresh Player with a fresh SEMan holding none of this. An earlier
            // version also tested "_applied != null", which is null exactly when you hold no
            // cards - so with an empty hand it re-applied every frame, leaking a
            // ScriptableObject per frame and filling the log.
            if (ReferenceEquals(player, _appliedTo) && signature == _appliedSignature) return;

            _appliedTo = player;
            _appliedSignature = signature;

            ApplyStats(player, ranks);
            ApplyInventoryRows(player, ranks);
            ApplyHorizon(ranks);

            if (RistConfig.Verbose.Value)
                RistPlugin.Log.LogInfo("Applied cards: " + (signature.Length == 0 ? "(none)" : signature));
        }

        /// <summary>
        /// Everything the current hand adds up to, keyed by effect name.
        ///
        /// One pass over the cards covers base effects and capstones together, which is what
        /// lets a capstone name anything a card can name - another stat field, an inventory
        /// row, an attack-speed category - without each consumer knowing capstones exist.
        /// </summary>
        private static Dictionary<string, float> Totals(Dictionary<string, int> ranks)
        {
            var totals = new Dictionary<string, float>();
            if (ranks == null) return totals;

            foreach (var kv in ranks)
            {
                if (kv.Value <= 0) continue;

                var card = Cards.Get(kv.Key);
                if (card == null) continue;

                Add(totals, card.Effect, card.PerRank * kv.Value);

                if (!card.HasBonus) continue;

                // Once per BonusEvery ranks, so it lands once at the default MaxRank of 5 and
                // twice if the ceiling is ever raised to 10 - "every fifth upgrade" read
                // literally rather than "the last one".
                var times = Card.BonusTimes(kv.Value);
                if (times > 0) Add(totals, card.BonusEffect, card.BonusPerRank * times);
            }

            return totals;
        }

        /// <summary>
        /// The stamina specials, spread over the fields they cover.
        ///
        /// Seven cards each shaving a few percent off one verb were seven cards nobody could
        /// feel and which crowded out every pick that mattered. Two cards covering a set of
        /// verbs each are perceptible, and the choice between moving and fighting is an
        /// actual choice where "jump costs 6% less" never was.
        ///
        /// Spread here rather than in ApplyStats so a card still describes itself as one
        /// thing on the panel, and so a capstone can name a special exactly as it names a
        /// field.
        ///
        /// Running is m_runStaminaDrainModifier, not m_runStaminaUseModifier. The second is the
        /// obvious name beside four other *StaminaUseModifier fields, and it is dead: SE_Stats
        /// declares it and prints it in a tooltip, and nothing in the game ever reads it.
        /// Player.CheckRun charges sprinting through SEMan.ModifyRunStaminaDrain, which reads only
        /// the drain field. Tireless's running share used to write the dead one, so for every
        /// release up to 1.3.1 the tile said running was cheaper and it cost exactly what it
        /// always had. Card.DeadFields lists it, and Cards.WarnAboutMissingLabels warns at load if a
        /// catalogue names it.
        /// </summary>
        private static readonly Dictionary<string, string[]> Spread = new Dictionary<string, string[]>
        {
            {
                "*stamina:move", new[]
                {
                    "m_runStaminaDrainModifier", "m_jumpStaminaUseModifier", "m_dodgeStaminaUseModifier",
                    "m_swimStaminaUseModifier", "m_sneakStaminaUseModifier",
                }
            },
            {
                "*stamina:fight", new[] { "m_attackStaminaUseModifier", "m_blockStaminaUseModifier" }
            },
        };

        private static void Add(Dictionary<string, float> totals, string effect, float amount)
        {
            if (string.IsNullOrEmpty(effect)) return;

            if (Spread.TryGetValue(effect, out var fields))
            {
                foreach (var field in fields) Add(totals, field, amount);
                return;
            }

            // Several cards may target the same effect, so accumulate rather than assign.
            totals.TryGetValue(effect, out var running);
            totals[effect] = running + amount;
        }

        /// <summary>
        /// What the local hand adds up to for one effect. Read by AttackSpeed, which cannot
        /// go through SE_Stats because the game has no field for what it does.
        /// </summary>
        internal static float TotalFor(string effect)
        {
            if (!ClientState.Known || string.IsNullOrEmpty(effect)) return 0f;
            return Totals(ClientState.Ranks).TryGetValue(effect, out var total) ? total : 0f;
        }

        private static void ApplyStats(Player player, Dictionary<string, int> ranks)
        {
            var seman = player.GetSEMan();
            if (seman == null) return;

            // Work out what is owed before building anything, so an empty hand costs no
            // allocation at all.
            var totals = new Dictionary<FieldInfo, float>();
            foreach (var kv in Totals(ranks))
            {
                if (Card.IsSpecialEffect(kv.Key)) continue;

                var field = typeof(SE_Stats).GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.FieldType != typeof(float)) continue;

                totals[field] = kv.Value;
            }

            // Replace wholesale rather than editing in place: SEMan keys on the name hash, so
            // adding a second effect with the same name is a no-op and the old values would
            // simply persist.
            seman.RemoveStatusEffect(EffectHash, quiet: true);

            if (_applied != null)
            {
                Object.Destroy(_applied);
                _applied = null;
            }

            if (totals.Count == 0) return;

            var stats = ScriptableObject.CreateInstance<SE_Stats>();
            stats.name = EffectName;
            stats.m_name = EffectName;
            stats.m_ttl = 0f;   // 0 is permanent; anything else expires mid-session.

            // Two of the game's modifiers are gated behind a skill rather than applying to
            // everything, and a fresh SE_Stats has both as SkillType.None:
            //
            //   ModifyRaiseSkill  runs only if m_raiseSkill != 0
            //   ModifyAttack      runs m_damageModifier only if it matches m_modifyAttackSkill
            //
            // Without these two lines Quick study did nothing whatsoever, at any rank, and a
            // damage card could never have worked. Set unconditionally because both are inert
            // when no card targets them - m_raiseSkillModifier is 0, and m_damageModifier is
            // left at its neutral 1.
            stats.m_raiseSkill = Skills.SkillType.All;
            stats.m_modifyAttackSkill = Skills.SkillType.All;

            // ModifySkillLevel is gated the same way, and a capstone grants levels rather than
            // a faster climb toward them - which is felt immediately where a gain rate is not.
            stats.m_skillLevel = Skills.SkillType.All;

            foreach (var kv in totals)
            {
                // Some fields count from 1 rather than 0 - see Card.IsOneBased. A card still
                // carries the plain fraction, because that is what reads correctly both in the
                // catalogue and on the tile; the conversion belongs here, once.
                var value = Card.IsOneBased(kv.Key.Name) ? 1f + kv.Value : kv.Value;
                kv.Key.SetValue(stats, value);
            }

            seman.AddStatusEffect(stats);
            _applied = stats;
        }

        /// <summary>
        /// The one card that is not a stat.
        ///
        /// The rows are claimed rather than written. Inventory.m_height is one private int
        /// and any other mod wanting rows would be writing the same one - last writer wins,
        /// and a mod that writes only when its own state changes loses silently to one that
        /// writes every frame. Core adds every claim up and owns the write, so two mods stack
        /// instead of fighting, and the baseline capture and the window backdrop live in one
        /// place rather than being re-implemented per mod.
        /// </summary>
        private static void ApplyInventoryRows(Player player, Dictionary<string, int> ranks)
        {
            // Through Totals rather than over the cards directly, so a capstone that grants a
            // row counts too.
            Totals(ranks).TryGetValue("*inventoryrow", out var rows);

            ExtraRows = Mathf.Max(0, Mathf.RoundToInt(rows));

            // Through Core when it is here, through Rist's own owner when it is not. The two
            // never both run: OwnInventoryRows is only patched in when Core is absent.
            if (RistPlugin.CorePresent) ClaimThroughCore(ExtraRows);
            else OwnInventoryRows.Claimed = ExtraRows;
        }

        /// <summary>
        /// The map radius and the two sailing numbers.
        ///
        /// Set as plain fields rather than applied here, because both are read by the game on
        /// its own schedule - Minimap.UpdateExplore on a timer, Ship.GetWindAngleFactor every
        /// physics step - so the patches read the current value and there is nothing to write
        /// per frame and nothing to restore when a hand changes.
        ///
        /// Through Totals like everything else, so a capstone naming one of these counts
        /// without this method knowing capstones exist. That is what lets the wind card carry
        /// its rowing bonus as a capstone rather than needing a card of its own.
        /// </summary>
        private static void ApplyHorizon(Dictionary<string, int> ranks)
        {
            var totals = Totals(ranks);

            float explore, cone, row;
            totals.TryGetValue(Horizon.ExploreRadius, out explore);
            totals.TryGetValue(Horizon.WindCone, out cone);
            totals.TryGetValue(Horizon.RowSpeed, out row);

            Horizon.ExtraExplore = Mathf.Max(0f, explore);
            Horizon.ConeNarrowing = Mathf.Clamp01(cone);
            Horizon.RowBonus = Mathf.Max(0f, row);
        }

        /// <summary>
        /// Never inlined, for the same reason as RistPlugin.RegisterWithCore: the JIT resolves
        /// a method's assemblies when it first compiles that method, so this call sitting
        /// inline above would drag Ezomic.Core in on a machine that has no Core - and it sits
        /// on the path that runs whenever a rank changes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClaimThroughCore(int rows)
        {
            InventoryRows.Claim(RistPlugin.PluginGuid, rows);
        }

        private static string Signature(Dictionary<string, int> ranks)
        {
            if (ranks == null || ranks.Count == 0) return "";

            var ids = new List<string>(ranks.Keys);
            ids.Sort();

            var sb = new System.Text.StringBuilder();
            foreach (var id in ids)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(id).Append(':').Append(ranks[id]);
            }

            return sb.ToString();
        }
    }
}
