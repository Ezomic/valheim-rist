using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ezomic.Core;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// One card. The effect is the literal name of a public float field on the
    /// game's own SE_Stats, which is why the catalogue is a text file rather than a switch
    /// statement: the game already ships about forty-five of these modifiers, and naming
    /// one turns it into a card without any code here knowing it exists.
    /// </summary>
    internal sealed class Card
    {
        internal string Id;
        internal string Name;
        internal string Flavour;
        internal string Effect;
        internal float PerRank;

        /// <summary>
        /// The capstone: a second, different effect granted once per BonusEvery ranks. Empty
        /// on a card that has none, which is allowed - the last two fields of a line are
        /// optional so the fourteen cards written before this existed still parse.
        /// </summary>
        internal string BonusEffect = "";
        internal float BonusPerRank;

        /// <summary>
        /// The rune cut into the middle of this rist's stone. Optional eighth field: a card
        /// written before stones existed falls back to the first letter of its id, which is
        /// wrong-looking but never blank.
        /// </summary>
        internal string Sigil = "";

        /// <summary>Resolved SE_Stats fields. Null for specials, which Effects handles by hand.</summary>
        internal FieldInfo Field;
        internal FieldInfo BonusField;

        internal bool IsSpecial => IsSpecialEffect(Effect);
        internal bool HasBonus => BonusEffect.Length > 0;

        internal static bool IsSpecialEffect(string effect)
        {
            return effect != null && effect.Length > 0 && effect[0] == '*';
        }

        /// <summary>How many times the capstone has been earned at this rank.</summary>
        internal static int BonusTimes(int rank)
        {
            var every = Mathf.Max(1, RistConfig.BonusEvery.Value);
            return rank / every;
        }

        /// <summary>
        /// The green line under the card name. Generated rather than written in the
        /// catalogue so that a new card is one line and cannot drift out of step with the
        /// number it actually applies.
        /// </summary>
        internal string Describe(int rank)
        {
            return Format(Effect, PerRank * Mathf.Max(1, rank));
        }

        /// <summary>The capstone as it reads at <paramref name="times"/> grants of it.</summary>
        internal string DescribeBonus(int times)
        {
            return Format(BonusEffect, BonusPerRank * Mathf.Max(1, times));
        }

        private static string Format(string effect, float total)
        {
            if (!Labels.TryGetValue(effect, out var label)) label = effect;

            if (Percent.Contains(effect))
            {
                var pct = total * 100f;
                return (pct >= 0f ? "+" : "−") + Mathf.Abs(pct).ToString("0.#", CultureInfo.InvariantCulture) + "% " + label;
            }

            return (total >= 0f ? "+" : "−") + Mathf.Abs(total).ToString("0.#", CultureInfo.InvariantCulture) + " " + label;
        }

        /// <summary>
        /// Fields whose neutral value is 1 rather than 0.
        ///
        /// The game reads these as multipliers and several test them before use - the three
        /// regen ones with a literal `if (m_xRegenMultiplier > 1f)`, which silently skipped a
        /// card writing 0.08 and made Long wind and Swift-mending do nothing at all at any
        /// rank. m_damageModifier is worse than silent: it multiplies the hit, so a raw 0.03
        /// would have cut damage to 3% rather than adding it.
        ///
        /// The catalogue still carries the plain fraction and Effects adds the 1 on the way
        /// in, so a card line and its tile both read the way anyone would expect.
        /// </summary>
        internal static bool IsOneBased(string effect)
        {
            return Neutrals().Contains(effect);
        }

        private static HashSet<string> _oneBased;

        /// <summary>
        /// Which fields count from 1, asked of the game rather than remembered.
        ///
        /// A fresh SE_Stats carries its declared defaults, and "the neutral value is 1" is
        /// exactly what a `= 1f` initialiser means - so the set can be read instead of listed.
        /// Today that reproduces the four below exactly. The difference is what happens when
        /// the game changes: a balance pass that makes another modifier multiplicative, or that
        /// makes one of these additive, moves this set with it, where a hardcoded list would
        /// keep converting on the old rule and produce the one failure this whole mechanism
        /// exists to prevent - writing 0.03 into a multiplier and cutting damage to 3%.
        ///
        /// Falls back to the known four if the probe cannot be made, because being wrong about
        /// m_damageModifier is much worse than being out of date about a field no card targets.
        /// </summary>
        private static HashSet<string> Neutrals()
        {
            if (_oneBased != null) return _oneBased;

            try
            {
                var probe = ScriptableObject.CreateInstance<SE_Stats>();

                // Plain == null, never ?. - Unity overloads equality and the null-propagating
                // operators bypass the overload.
                if (probe == null)
                {
                    _oneBased = Fallback;
                    return _oneBased;
                }

                var found = new HashSet<string>();
                foreach (var field in typeof(SE_Stats).GetFields(BindingFlags.Public
                                                                 | BindingFlags.Instance))
                {
                    if (field.FieldType != typeof(float)) continue;
                    if (Mathf.Approximately((float)field.GetValue(probe), 1f))
                        found.Add(field.Name);
                }

                UnityEngine.Object.Destroy(probe);

                // Said once, and only when it disagrees with what this mod was written against.
                // A card converting on the wrong rule is silent in play, so the log is the only
                // place it can ever surface.
                foreach (var name in Fallback)
                    if (!found.Contains(name))
                        RistPlugin.Log.LogWarning("SE_Stats." + name + " no longer defaults to 1, "
                            + "so Rist has stopped treating it as a multiplier. Check the cards "
                            + "that target it - the game has changed what neutral means.");

                foreach (var name in found)
                    if (!Fallback.Contains(name))
                        RistPlugin.Log.LogInfo("SE_Stats." + name + " defaults to 1 and is being "
                            + "treated as a multiplier; it was not one when Rist was written.");

                _oneBased = found;
            }
            catch (Exception e)
            {
                RistPlugin.Log.LogWarning("Could not read SE_Stats' neutral values, so Rist is "
                    + "using the four multipliers it was written against. " + e.Message);
                _oneBased = Fallback;
            }

            return _oneBased;
        }

        /// <summary>
        /// The four that counted from 1 when this was written, kept as the fallback and as the
        /// baseline the probe is reported against.
        /// </summary>
        private static readonly HashSet<string> Fallback = new HashSet<string>
        {
            "m_healthRegenMultiplier", "m_staminaRegenMultiplier", "m_eitrRegenMultiplier",
            "m_damageModifier",
        };

        // Only for display. A field with no entry falls back to its own name, which is ugly
        // but never wrong, and is a visible prompt to add it here.
        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "m_addMaxCarryWeight", "carry weight" },
            { "m_addArmor", "armour" },
            { "*inventoryrow", "inventory row" },
            { "m_runStaminaUseModifier", "run stamina" },
            { "m_attackStaminaUseModifier", "attack stamina" },
            { "m_blockStaminaUseModifier", "block stamina" },
            { "m_swimStaminaUseModifier", "swim stamina" },
            { "m_jumpStaminaUseModifier", "jump stamina" },
            { "m_sneakStaminaUseModifier", "sneak stamina" },
            { "m_staminaRegenMultiplier", "stamina regen" },
            { "m_healthRegenMultiplier", "health regen" },
            { "m_eitrRegenMultiplier", "eitr regen" },
            { "m_fallDamageModifier", "fall damage" },
            { "m_stealthModifier", "stealth" },
            { "m_noiseModifier", "noise" },
            { "m_staggerModifier", "stagger taken" },
            { "m_raiseSkillModifier", "skill gain" },
            { "m_speedModifier", "movement speed" },
            { "m_damageModifier", "damage" },
            { "m_dodgeStaminaUseModifier", "dodge stamina" },
            { "m_swimSpeedModifier", "swim speed" },
            { "m_timedBlockBonus", "parry bonus" },
            { "*stamina:move", "movement stamina" },
            { "*stamina:fight", "combat stamina" },
            { AttackSpeed.Melee, "melee speed" },
            { AttackSpeed.Tools, "tool speed" },
            { AttackSpeed.Ranged, "draw speed" },
        };

        private static readonly HashSet<string> Percent = new HashSet<string>
        {
            "m_runStaminaUseModifier", "m_attackStaminaUseModifier", "m_blockStaminaUseModifier",
            "m_swimStaminaUseModifier", "m_jumpStaminaUseModifier", "m_sneakStaminaUseModifier",
            "m_staminaRegenMultiplier", "m_healthRegenMultiplier", "m_eitrRegenMultiplier",
            "m_fallDamageModifier", "m_stealthModifier", "m_noiseModifier", "m_staggerModifier",
            "m_raiseSkillModifier", "m_speedModifier", "m_damageModifier",
            "m_dodgeStaminaUseModifier", "m_swimSpeedModifier", "m_timedBlockBonus",
            AttackSpeed.Melee, AttackSpeed.Tools, AttackSpeed.Ranged,
            "*stamina:move", "*stamina:fight",
        };

        /// <summary>
        /// The specials: effects with no SE_Stats field behind them, handled by code here.
        /// Kept as a set so an unrecognised one is skipped with a warning rather than
        /// silently doing nothing, the same way an unknown field name is.
        /// </summary>
        internal static readonly HashSet<string> Specials = new HashSet<string>
        {
            "*inventoryrow", AttackSpeed.Melee, AttackSpeed.Tools, AttackSpeed.Ranged,
            "*stamina:move", "*stamina:fight",
        };
    }

    /// <summary>
    /// The catalogue, read once from cards.txt beside the DLL.
    /// </summary>
    internal static class Cards
    {
        private static readonly List<Card> _all = new List<Card>();
        private static readonly Dictionary<string, Card> _byId = new Dictionary<string, Card>();

        internal static IReadOnlyList<Card> All => _all;

        internal static Card Get(string id)
        {
            return id != null && _byId.TryGetValue(id, out var c) ? c : null;
        }

        internal static void Load()
        {
            _all.Clear();
            _byId.Clear();

            var dir = Path.GetDirectoryName(typeof(Cards).Assembly.Location);
            var path = Path.Combine(dir ?? ".", "cards.txt");

            if (!File.Exists(path))
            {
                RistPlugin.Log.LogError("cards.txt not found beside the DLL at " + path +
                                        " - no cards can be taken.");
                return;
            }

            // Declared to Core so the gate can compare it. Two ends running the same build
            // over different catalogues is a real and silent disagreement: this file names
            // what every rank is worth, effects are applied client-side from it, and the
            // server only ever checks the rank - so an edited line here is simply believed.
            //
            // Without Core there is nothing to declare it to, and that check is simply gone.
            // Not a fallback worth inventing: a hash Rist computes and compares against itself
            // proves nothing, since the disagreement being looked for is between two machines.
            if (RistPlugin.CorePresent) DeclareCatalogue(File.ReadAllText(path));

            var lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var parts = line.Split('|');
                if (parts.Length < 5)
                {
                    RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": expected 5 fields, got " +
                                              parts.Length + " - skipped.");
                    continue;
                }

                var card = new Card
                {
                    Id = parts[0].Trim(),
                    Name = parts[1].Trim(),
                    Flavour = parts[2].Trim(),
                    Effect = parts[3].Trim(),
                };

                if (!float.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out card.PerRank))
                {
                    RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": '" + parts[4].Trim() +
                                              "' is not a number - skipped.");
                    continue;
                }

                if (card.Id.Length == 0)
                {
                    RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": blank id - skipped.");
                    continue;
                }

                if (_byId.ContainsKey(card.Id))
                {
                    RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": duplicate id '" + card.Id +
                                              "' - skipped.");
                    continue;
                }

                if (!Resolve(card.Effect, lineNo, card.Id, out card.Field)) continue;

                // The capstone is optional, so a five-field line is still a valid card and
                // every card written before this existed keeps working untouched.
                if (parts.Length >= 8) card.Sigil = parts[7].Trim();

                if (parts.Length >= 7)
                {
                    card.BonusEffect = parts[5].Trim();
                    var bonusText = parts[6].Trim();

                    if (card.BonusEffect.Length > 0)
                    {
                        if (!float.TryParse(bonusText, NumberStyles.Float, CultureInfo.InvariantCulture,
                                            out card.BonusPerRank))
                        {
                            RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": bonus value '" +
                                                      bonusText + "' is not a number - bonus dropped.");
                            card.BonusEffect = "";
                        }
                        else if (!Resolve(card.BonusEffect, lineNo, card.Id, out card.BonusField))
                        {
                            // The card itself is fine; only its capstone is unusable. Losing
                            // the whole card over a typo in an optional field would be worse.
                            card.BonusEffect = "";
                        }
                    }
                }

                _all.Add(card);
                _byId[card.Id] = card;
            }

            RistPlugin.Log.LogInfo("Loaded " + _all.Count + " cards from cards.txt.");
        }

        /// <summary>
        /// Turn an effect name into the field it writes, or confirm it is a known special.
        ///
        /// An unknown name is logged and skipped rather than thrown, matching how the game
        /// treats a prefab name that does not resolve. A typo costs one card, not the whole
        /// catalogue.
        /// </summary>
        private static bool Resolve(string effect, int lineNo, string id, out FieldInfo field)
        {
            field = null;

            if (Card.IsSpecialEffect(effect))
            {
                if (Card.Specials.Contains(effect)) return true;

                RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": unknown special '" + effect +
                                          "' - card '" + id + "' skipped.");
                return false;
            }

            field = typeof(SE_Stats).GetField(effect, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(float)) return true;

            RistPlugin.Log.LogWarning("cards.txt line " + lineNo + ": SE_Stats has no public float field '" +
                                      effect + "' - card '" + id + "' skipped.");
            field = null;
            return false;
        }

        /// <summary>
        /// Never inlined, for the same reason as RistPlugin.RegisterWithCore: the JIT resolves
        /// a method's assemblies when it first compiles that method, so this call sitting
        /// inline in Load would drag Ezomic.Core in on a machine that has no Core - and the
        /// exception would land while the catalogue was being read, taking every card with it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeclareCatalogue(string contents)
        {
            Suite.Data(contents);
        }

    }
}
