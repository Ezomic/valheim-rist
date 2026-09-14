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

        /// <summary>
        /// The ætt this rist stands in on the panel: the name of the nearest "== Name" line
        /// above it in cards.txt, or empty when there is none. Layout only - nothing that
        /// grants, applies or stores a rank reads it.
        /// </summary>
        internal string Aett = "";

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

        /// <summary>
        /// Effects stored as a positive amount of benefit but read by a player as something
        /// shrinking. Weatherly's value is how much of the dead zone is taken away, and
        /// "+6% dead zone" would say the zone grows. Shown with the sign turned, so the tile
        /// reads "-6% dead zone" the same way the stamina cards read "-5% move stamina".
        ///
        /// Display only. The catalogue and every consumer keep the positive fraction, so
        /// LowerIsBetter and the ConeNarrowing clamp are untouched.
        /// </summary>
        private static readonly HashSet<string> ShownAsReduction = new HashSet<string>
        {
            Horizon.WindCone,
        };

        private static string Format(string effect, float total)
        {
            if (!Labels.TryGetValue(effect, out var label)) label = effect;

            // An unlock has no amount worth printing: "+1 a hit cannot break a cast" says
            // nothing the label does not. The value is only a flag that it is carved.
            if (Unlocks.Contains(effect)) return label;

            if (ShownAsReduction.Contains(effect)) total = -total;

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
        /// <summary>
        /// Whether an effect has a readable name, so the catalogue can be checked at load.
        ///
        /// A method rather than exposing Labels: the table stays private and the question the
        /// caller actually has is answered directly.
        /// </summary>
        internal static bool HasLabel(string effect)
        {
            return !string.IsNullOrEmpty(effect) && Labels.ContainsKey(effect);
        }

        /// <summary>
        /// True when a value is plainly a fraction but its effect is not in Percent, so the
        /// panel would print it as a raw decimal.
        ///
        /// "Plainly a fraction" is a non-whole number below 1. Every additive card in the
        /// catalogue writes a whole number - 30 carry weight, 2 armour, 2 skill levels - and
        /// every fractional one is meant as a percentage, so the line between them is clean
        /// today. A card that genuinely wants "+0.5 of something" would be the first to trip it,
        /// and a warning in the log is the right cost for that.
        /// </summary>
        internal static bool ReadsAsRawFraction(string effect, float value)
        {
            if (string.IsNullOrEmpty(effect) || Percent.Contains(effect) || Seconds.Contains(effect)) return false;
            if (Mathf.Abs(value) >= 1f) return false;
            return !Mathf.Approximately(value, Mathf.Round(value));
        }

        /// <summary>
        /// Fields SE_Stats declares that the game never reads. A card naming one loads, shows a
        /// number on its tile and changes nothing, which is the worst way for a card to fail.
        ///
        /// m_runStaminaUseModifier is the one found so far: declared, printed in SE_Stats'
        /// tooltip, and read nowhere else. Sprinting is charged through
        /// SEMan.ModifyRunStaminaDrain, which reads m_runStaminaDrainModifier. Tireless's running
        /// share and the capstones of Long wind and Long stride all pointed at it for every
        /// release up to 1.3.1.
        /// </summary>
        private static readonly HashSet<string> DeadFields = new HashSet<string>
        {
            "m_runStaminaUseModifier",
        };

        internal static bool IsDeadField(string effect)
        {
            return !string.IsNullOrEmpty(effect) && DeadFields.Contains(effect);
        }

        /// <summary>
        /// Fields where the helpful direction is negative, so a positive value is a drawback -
        /// and the catalogue carries none, on purpose.
        ///
        /// The stamina costs and fall damage are the obvious ones. Stealth is the one that was
        /// got wrong: ModifyStealth scales the factor BaseAI multiplies a creature's view range
        /// by, and MonsterAI its alert range, so a positive m_stealthModifier has you seen from
        /// further away. Soft step shipped at +0.08 a rank and Quiet wake's capstone at +0.10,
        /// both making the player easier to find while the tile promised the reverse.
        /// </summary>
        private static readonly HashSet<string> LowerIsBetter = new HashSet<string>
        {
            "m_runStaminaDrainModifier", "m_attackStaminaUseModifier", "m_blockStaminaUseModifier",
            "m_swimStaminaUseModifier", "m_jumpStaminaUseModifier", "m_sneakStaminaUseModifier",
            "m_dodgeStaminaUseModifier", "*stamina:move", "*stamina:fight",
            "m_fallDamageModifier", "m_stealthModifier", "m_noiseModifier", "m_staggerModifier",
            Oathbound.Cooldown,
        };

        internal static bool PointsAtDrawback(string effect, float value)
        {
            return value > 0f && !string.IsNullOrEmpty(effect) && LowerIsBetter.Contains(effect);
        }

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "m_addMaxCarryWeight", "carry weight" },
            { "m_addArmor", "armour" },
            { "*inventoryrow", "inventory row" },
            { "*exploreradius", "map sight" },
            // What a sailor sees shrink, not what the card is for. "+6% sailing into the wind"
            // was a number with no unit anyone could picture; the wind ring's black arc is on
            // screen the whole time the sail is up, and it narrows by exactly this much.
            { "*windcone", "dead zone" },
            { "*rowspeed", "rowing speed" },
            { "m_runStaminaUseModifier", "run stamina" },
            { "m_runStaminaDrainModifier", "run stamina drain" },
            { "m_attackStaminaUseModifier", "attack stamina" },
            { "m_blockStaminaUseModifier", "block stamina" },
            { "m_swimStaminaUseModifier", "swim stamina" },
            { "m_jumpStaminaUseModifier", "jump stamina" },
            { "m_sneakStaminaUseModifier", "sneak stamina" },
            { "m_staminaRegenMultiplier", "stamina regen" },
            { "m_healthRegenMultiplier", "health regen" },
            { "m_eitrRegenMultiplier", "eitr regen" },
            { "m_fallDamageModifier", "fall damage" },
            // Not "stealth". The field scales how far away a creature sees you while you crouch,
            // so the card that helps carries a negative number, and "-40% stealth" reads as the
            // opposite of what it does.
            { "m_stealthModifier", "detection" },
            { "m_noiseModifier", "noise" },
            { "m_staggerModifier", "stagger taken" },
            { "m_raiseSkillModifier", "skill gain" },
            { "m_skillLevelModifier", "skill levels" },
            { "m_speedModifier", "movement speed" },
            { "m_damageModifier", "damage" },
            { "m_dodgeStaminaUseModifier", "dodge stamina" },
            { "m_swimSpeedModifier", "swim speed" },
            { "m_timedBlockBonus", "parry bonus" },
            { "*stamina:move", "move stamina" },
            { "*stamina:fight", "combat stamina" },
            { AttackSpeed.Melee, "melee speed" },
            { AttackSpeed.Tools, "tool speed" },
            { AttackSpeed.Ranged, "draw and reload" },
            { AttackSpeed.Magic, "cast speed" },
            { AttackSpeed.UnbrokenCast, "a hit cannot break a cast" },
            { RangedDamage.Key, "bow and crossbow damage" },
            { AnsweringBlow.Bonus, "answering blow" },
            { AnsweringBlow.Stagger, "chance the answering blow staggers" },
            { LowDraw.Seconds, "s unseen draw" },
            { LowDraw.Silent, "arrows from a crouch land silent" },
            { UnseenBlow.Bonus, "sneak attack" },
            { UnseenBlow.Stagger, "an ambush staggers" },
            { DeepDraught.Duration, "mead duration" },
            { DeepDraught.FullCask, "chance a mead is not used up" },
            { Oathbound.Cooldown, "power cooldown" },
            { Oathbound.Duration, "s of forsaken power" },
        };

        private static readonly HashSet<string> Percent = new HashSet<string>
        {
            "m_runStaminaUseModifier", "m_attackStaminaUseModifier", "m_blockStaminaUseModifier",
            "m_swimStaminaUseModifier", "m_jumpStaminaUseModifier", "m_sneakStaminaUseModifier",
            "m_staminaRegenMultiplier", "m_healthRegenMultiplier", "m_eitrRegenMultiplier",
            "m_fallDamageModifier", "m_stealthModifier", "m_noiseModifier", "m_staggerModifier",
            "m_raiseSkillModifier", "m_speedModifier", "m_damageModifier",
            "m_dodgeStaminaUseModifier", "m_swimSpeedModifier", "m_timedBlockBonus",
            AttackSpeed.Melee, AttackSpeed.Tools, AttackSpeed.Ranged, AttackSpeed.Magic,
            RangedDamage.Key, AnsweringBlow.Bonus, AnsweringBlow.Stagger, UnseenBlow.Bonus, DeepDraught.Duration,
            DeepDraught.FullCask,
            Oathbound.Cooldown,
            "*stamina:move", "*stamina:fight",
            // Every one of these is a fraction the card means as a percentage, and leaving one
            // out does not fail - it prints the raw number instead. 1.3.0 shipped four that way:
            // Tireless's capstone read "-0.2 m_runStaminaDrainModifier" (the field had no label
            // either, and -0.15 rounded to one decimal), and Far sight and Weatherly read "+0.1
            // map sight" where they meant +5%. WarnAboutMissingLabels checks for both at load.
            "m_runStaminaDrainModifier",
            Horizon.ExploreRadius, Horizon.WindCone, Horizon.RowSpeed,
        };

        /// <summary>
        /// The specials: effects with no SE_Stats field behind them, handled by code here.
        /// Kept as a set so an unrecognised one is skipped with a warning rather than
        /// silently doing nothing, the same way an unknown field name is.
        /// </summary>
        internal static readonly HashSet<string> Specials = new HashSet<string>
        {
            "*inventoryrow", AttackSpeed.Melee, AttackSpeed.Tools, AttackSpeed.Ranged,
            AttackSpeed.Magic, AttackSpeed.UnbrokenCast, RangedDamage.Key,
            AnsweringBlow.Bonus, AnsweringBlow.Stagger, LowDraw.Seconds, LowDraw.Silent,
            UnseenBlow.Bonus, UnseenBlow.Stagger, DeepDraught.Duration, DeepDraught.FullCask,
            Oathbound.Cooldown, Oathbound.Duration,
            Horizon.ExploreRadius, Horizon.WindCone, Horizon.RowSpeed,
            "*stamina:move", "*stamina:fight",
        };

        /// <summary>
        /// Effects that are a thing you can now do rather than an amount - shown as their label
        /// alone. Written in the catalogue with a value of 1.
        /// </summary>
        private static readonly HashSet<string> Unlocks = new HashSet<string>
        {
            AttackSpeed.UnbrokenCast, LowDraw.Silent, UnseenBlow.Stagger,
        };

        /// <summary>
        /// Effects counted in seconds. Printed as a plain number beside their "s" label, and
        /// exempt from the raw-fraction warning, since half a second is meant as half a second.
        /// </summary>
        private static readonly HashSet<string> Seconds = new HashSet<string>
        {
            LowDraw.Seconds, Oathbound.Duration,
        };
    }

    /// <summary>
    /// One group of rists as the panel stands them: a heading and at most eight stones.
    ///
    /// Eight because the futhark itself is cut into three ættir of eight, and because eight
    /// is two stones wide by four tall - the tower the panel draws. Indices rather than cards,
    /// so the panel's selection stays an index into Cards.All and every path that already
    /// works in catalogue order keeps working.
    /// </summary>
    internal sealed class Aett
    {
        internal const int Capacity = 8;

        internal string Name;
        internal readonly List<int> Indices = new List<int>();
    }

    /// <summary>
    /// The catalogue, read once from cards.txt beside the DLL.
    /// </summary>
    internal static class Cards
    {
        private static readonly List<Card> _all = new List<Card>();
        private static readonly Dictionary<string, Card> _byId = new Dictionary<string, Card>();
        private static readonly List<Aett> _aetts = new List<Aett>();

        internal static IReadOnlyList<Card> All => _all;

        /// <summary>The ættir in catalogue order, each holding at most Aett.Capacity cards.</summary>
        internal static IReadOnlyList<Aett> Aetts => _aetts;

        /// <summary>
        /// The hard flag: the catalogue is not usable at all. cards.txt is missing, could not
        /// be read, or produced no cards whatsoever.
        ///
        /// It exists because an empty catalogue and a deliberately emptied one are the same
        /// data structure, and one of the two is a server-side delete of every player's card
        /// history: Reconcile hands back the picks spent on cards that no longer exist, and
        /// with nothing in the catalogue that is every card everyone holds, flushed to disk
        /// about ten seconds later. So the failure is recorded as a fact rather than inferred
        /// from Count, and both Ledger's reconcile gate and the plugin's ready line read it.
        /// </summary>
        internal static bool Unavailable { get; private set; }

        /// <summary>How many cards actually parsed. Read by the reconcile gate.</summary>
        internal static int Count => _all.Count;

        internal static Card Get(string id)
        {
            return id != null && _byId.TryGetValue(id, out var c) ? c : null;
        }

        internal static void Load()
        {
            _all.Clear();
            _byId.Clear();
            _aetts.Clear();
            Unavailable = false;

            var dir = Path.GetDirectoryName(typeof(Cards).Assembly.Location);
            var path = Path.Combine(dir ?? ".", "cards.txt");

            if (!File.Exists(path))
            {
                Unavailable = true;
                RistPlugin.Log.LogError("cards.txt not found beside the DLL at " + path +
                                        " - no cards can be taken, and no card history will be " +
                                        "reconciled. Reinstall the mod: the catalogue ships with " +
                                        "it and is not optional.");
                return;
            }

            // Read once, up front and inside a try, rather than twice inline further down. An
            // IO exception here used to escape Load and abort the whole of Awake - which meant
            // a locked or half-written cards.txt cost every patch in the mod, silently, with
            // the reason landing somewhere nobody reads. A file that cannot be read is the
            // same fact as a file that is not there, and is recorded as such.
            string contents;
            string[] rawLines;
            try
            {
                contents = File.ReadAllText(path);
                rawLines = File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                Unavailable = true;
                RistPlugin.Log.LogError("cards.txt at " + path + " could not be read (" + e.Message +
                                        ") - no cards can be taken, and no card history will be " +
                                        "reconciled.");
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
            if (RistPlugin.CorePresent) DeclareCatalogue(contents);

            var lineNo = 0;
            var aett = "";
            foreach (var raw in rawLines)
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                // "== Combat" starts an ætt. Checked before the field split, since a header has
                // no pipes and would otherwise be reported as a card with too few fields.
                if (line.StartsWith("==", StringComparison.Ordinal))
                {
                    aett = line.Substring(2).Trim();
                    continue;
                }

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
                    Aett = aett,
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

            // A file that is present and yields nothing is the same disaster as one that is
            // absent, and it is the likelier of the two after a game update: every effect name
            // is a field on the game's own SE_Stats, so a rename in Valheim leaves cards.txt
            // untouched and drops every line that referenced it. Recorded as unavailable so
            // the reconcile gate refuses and the ready line says so.
            if (_all.Count == 0)
            {
                Unavailable = true;
                RistPlugin.Log.LogError("cards.txt parsed to zero usable cards. Every line was " +
                                        "blank, commented out or rejected - see the warnings " +
                                        "above. No card can be taken, and no card history will " +
                                        "be reconciled.");
                return;
            }

            RistPlugin.Log.LogInfo("Loaded " + _all.Count + " cards from cards.txt.");

            BuildAetts();

            WarnAboutMissingLabels();
        }

        /// <summary>
        /// Group the loaded cards into ættir for the panel, in catalogue order.
        ///
        /// A ninth card under one heading starts a second ætt of the same name rather than
        /// being dropped or squeezed in: a tower is two by four and cannot hold it, and losing a
        /// rist from the panel over a layout rule would be far worse than an extra tower. It is
        /// warned about, because the catalogue is meant to be written to fill ættir.
        ///
        /// Cards above the first heading form an ætt with no name, so a catalogue written
        /// before headings existed still shows every stone.
        /// </summary>
        private static void BuildAetts()
        {
            Aett current = null;
            var spilled = new List<string>();
            var repeated = new List<string>();
            var closed = new HashSet<string>();
            var unnamed = 0;

            for (var i = 0; i < _all.Count; i++)
            {
                var name = _all[i].Aett ?? "";
                if (name.Length == 0) unnamed++;

                if (current == null || current.Name != name || current.Indices.Count >= Aett.Capacity)
                {
                    var overflow = current != null && current.Name == name;

                    if (overflow && name.Length > 0 && !spilled.Contains(name)) spilled.Add(name);

                    // The same heading written twice, further apart than one overflow, makes a
                    // second tower of that name just as quietly - the likely way to get here is
                    // appending a new card under its theme at the bottom of the file.
                    if (!overflow && name.Length > 0 && closed.Contains(name) && !repeated.Contains(name))
                        repeated.Add(name);

                    if (current != null) closed.Add(current.Name);

                    current = new Aett { Name = name };
                    _aetts.Add(current);
                }

                current.Indices.Add(i);
            }

            if (spilled.Count > 0)
                RistPlugin.Log.LogWarning("These ættir hold more than " + Aett.Capacity + " rists, so the panel "
                                          + "stands each overflow as a second tower of the same name: "
                                          + string.Join(", ", spilled.ToArray())
                                          + ". Split the heading in cards.txt.");

            if (repeated.Count > 0)
                RistPlugin.Log.LogWarning("These ætt headings appear more than once in cards.txt, so each "
                                          + "stands as more than one tower: "
                                          + string.Join(", ", repeated.ToArray())
                                          + ". Move the cards under a single heading.");

            // A tower with a count and no name is what a catalogue written before headings, or a
            // card above the first one, looks like on the panel. Every card still shows; it is
            // simply nowhere a player would think to look for it.
            if (unnamed > 0)
                RistPlugin.Log.LogWarning(unnamed + " rists in cards.txt stand under no \"== Name\" heading, "
                                          + "so the panel shows them in towers with no name. Add a heading "
                                          + "above them.");
        }

        /// <summary>
        /// Say so when a card's line is wrong in a way that still loads.
        ///
        /// Four checks, all of them mistakes that shipped: an effect with no readable name, a
        /// fraction missing from Percent and printed raw, a field the game never reads
        /// (Card.DeadFields), and a positive value on a field where lower is better
        /// (Card.LowerIsBetter). The name is older than the last two.
        ///
        /// Format falls back to the raw field when Labels has no entry, which is the right
        /// behaviour - a card with an unnamed effect still works and still says how much it
        /// gives. What was wrong is that the fallback was silent, so Quick study's capstone
        /// read "+2 m_skillLevelModifier at rank 5" on a player's screen for a whole release
        /// and the only way to find out was for somebody to send a screenshot.
        ///
        /// Checked here rather than in Format because Format runs per frame per card while the
        /// panel is open, and because the useful moment to hear about it is once, at load,
        /// naming the card - which is what makes it actionable rather than decorative.
        /// </summary>
        private static void WarnAboutMissingLabels()
        {
            var missing = new List<string>();

            var raw = new List<string>();

            var dead = new List<string>();

            var backwards = new List<string>();

            foreach (var card in _all)
            {
                if (Card.IsDeadField(card.Effect)) dead.Add(card.Id + " (" + card.Effect + ")");
                if (Card.IsDeadField(card.BonusEffect)) dead.Add(card.Id + " capstone (" + card.BonusEffect + ")");

                if (Card.PointsAtDrawback(card.Effect, card.PerRank))
                    backwards.Add(card.Id + " (" + card.Effect + " " + card.PerRank + ")");

                if (Card.PointsAtDrawback(card.BonusEffect, card.BonusPerRank))
                    backwards.Add(card.Id + " capstone (" + card.BonusEffect + " " + card.BonusPerRank + ")");

                if (!string.IsNullOrEmpty(card.Effect) && !Card.HasLabel(card.Effect))
                    missing.Add(card.Id + " (" + card.Effect + ")");

                if (!string.IsNullOrEmpty(card.BonusEffect) && !Card.HasLabel(card.BonusEffect))
                    missing.Add(card.Id + " capstone (" + card.BonusEffect + ")");

                if (Card.ReadsAsRawFraction(card.Effect, card.PerRank))
                    raw.Add(card.Id + " (" + card.Effect + " " + card.PerRank + ")");

                if (Card.ReadsAsRawFraction(card.BonusEffect, card.BonusPerRank))
                    raw.Add(card.Id + " capstone (" + card.BonusEffect + " " + card.BonusPerRank + ")");
            }

            if (missing.Count > 0)
                RistPlugin.Log.LogWarning("These card effects have no readable name, so the panel "
                                          + "shows the game's own field name to the player: "
                                          + string.Join(", ", missing.ToArray())
                                          + ". Add them to Cards.Labels.");

            // The label check alone let 1.3.0 ship Far sight and Weatherly reading "+0.1 map
            // sight", because both had a label and were only missing from Percent - which does
            // not fail either, it just prints the fraction. Nothing else would have noticed.
            if (raw.Count > 0)
                RistPlugin.Log.LogWarning("These card values are fractions but will be shown as raw "
                                          + "numbers rather than percentages, so 0.05 reads as "
                                          + "\"+0.1\": " + string.Join(", ", raw.ToArray())
                                          + ". Add them to Cards.Percent.");

            // Both of these load cleanly and put a believable number on the tile, which is why
            // Long wind, Long stride, Soft step and Quiet wake shipped this way, and it took reading
            // the game's code to find them. Tireless's share was the same bug in Effects.Spread,
            // where this check cannot see it.
            if (dead.Count > 0)
                RistPlugin.Log.LogWarning("These card effects name a field the game never reads, so "
                                          + "they do nothing whatever the tile says: "
                                          + string.Join(", ", dead.ToArray())
                                          + ". See Card.DeadFields for what to use instead.");

            if (backwards.Count > 0)
                RistPlugin.Log.LogWarning("These card values point the wrong way - on these fields "
                                          + "negative is the benefit, so a positive value is a "
                                          + "drawback: " + string.Join(", ", backwards.ToArray())
                                          + ". Flip the sign.");
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
