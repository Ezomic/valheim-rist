using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// `rist`, the console command: read this character's standing, and force a rank.
    ///
    /// It exists because nothing else could. A rank is bought by clicking a stone in the
    /// panel, the panel is IMGUI, and a scenario can neither click it nor read it - so there
    /// was no way to put a character at rank 5 of one card and look at the result, and every
    /// card in the catalogue was therefore untestable. Setting a rank is also the thing most
    /// wanted while balancing a stone by hand: five ranks is otherwise nine skill levels away.
    ///
    /// Registered with isCheat, so it needs devcommands, and `Console.IsCheatsEnabled()` is
    /// `ZNet.IsServer()` - which means it works in singleplayer and when you host, and is dead
    /// on a dedicated server for every client connected to it. That is the same fence the
    /// record itself sits behind: ranks are held by the server, so on Longhouse this cannot
    /// reach them however hard a client tries. It refuses politely rather than silently when
    /// run on a client, because "nothing happened" is the worst answer a dev tool can give.
    ///
    /// The armour line prints the game's own number, not Rist's. GetBodyArmor is what
    /// Character.Damage passes to the armour curve, so a ratio computed from it proves the
    /// modifier reached the place damage is worked out. Rist's own total is printed beside it
    /// and labelled, for reading rather than for asserting: a test that checks a mod's opinion
    /// of itself passes while the mod does nothing.
    /// </summary>
    internal static class DevConsole
    {
        /// <summary>
        /// Process-wide, unlike the prefab registrations: Terminal's command dictionary is a
        /// private static that nothing ever clears, so a second registration of the same name
        /// is a duplicate that outlives the world rather than a world-local one to redo.
        /// </summary>
        private static bool _registered;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        internal static class Hook
        {
            private static void Postfix()
            {
                Register();
            }
        }

        private static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("rist",
                "rist show | rist rank <card> <n> | rist powers | rist others [ask] | rist kills | rist roll | rist perfect | rist fall <m> [roll] - this character's standing, and forcing a rank for a test",
                OnCommand, isCheat: true);

            RistPlugin.Log.LogInfo("Console command 'rist' registered (needs devcommands, host or singleplayer).");
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            var term = args.Context;
            if (term == null) return;

            var what = args.Length > 1 ? args[1].ToLowerInvariant() : "";

            if (what == "show") { Show(term); return; }
            if (what == "rank") { Rank(term, args); return; }
            if (what == "powers") { Powers(term); return; }
            if (what == "kills") { KillsCommand(term, args); return; }
            if (what == "others") { OthersList(term, args.Length > 2 && args[2].ToLowerInvariant() == "ask"); return; }
            if (what == "roll") { Say(term, Player.m_localPlayer == null ? "rist: no player." : EelSlick.Start(Player.m_localPlayer)); return; }
            if (what == "fall") { Say(term, Fall(args)); return; }
            if (what == "perfect") { Say(term, Player.m_localPlayer == null ? "rist: no player." : EelSlick.HitInWindow(Player.m_localPlayer)); return; }

            term.AddString("rist show            - level, xp, ranks and the armour the game is using");
            term.AddString("rist rank <card> <n> - force a card to exactly that rank");
            term.AddString("rist powers          - each forsaken power against every stone carved, and what reaches zero");
            term.AddString("rist others [ask]    - the other characters the page lists; ask sends the request first");
            term.AddString("rist kills [set <name> <n> | send | reset] - kill XP per kind of creature; set and reset are singleplayer or host only");
            term.AddString("rist roll            - start a dodge roll, as the key does");
            term.AddString("rist fall <m> [roll] - land from m metres up, with a dodge pressed as you land when roll is given");
            term.AddString("rist perfect         - send a hit into the roll in progress, which the game counts as a perfect roll");
            term.AddString("card ids are the first field of cards.txt: thickhide, steadyfoot, longstride...");
        }

        private static string Fall(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null) return "rist: no player.";

            float metres;
            if (args.Length < 3 || !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out metres) || metres <= 0f)
                return "rist: fall how many metres? rist fall 20 [roll]";

            return Sinews.Landing.Fall(player, metres, args.Length > 3 && args[3].ToLowerInvariant() == "roll");
        }

        // ---------------------------------------------------------------- powers

        /// <summary>
        /// The stats the game adds together and stops at zero, where a lower number helps. A sum
        /// at or past -1 on any of them is free, immune or unseen: stamina costs nothing, a fall
        /// does no damage, a creature cannot see or hear you, a hit cannot stagger you. Read off
        /// SE_Stats' Modify methods, every one of which is `x += base * modifier` - the same
        /// shape as run stamina, which is where Eikthyr and a hand of cards went through the
        /// floor.
        /// </summary>
        private static readonly string[][] AddsToZero =
        {
            new[] { "m_runStaminaDrainModifier", "run stamina" },
            new[] { "m_jumpStaminaUseModifier", "jump stamina" },
            new[] { "m_dodgeStaminaUseModifier", "dodge stamina" },
            new[] { "m_swimStaminaUseModifier", "swim stamina" },
            new[] { "m_sneakStaminaUseModifier", "sneak stamina" },
            new[] { "m_attackStaminaUseModifier", "attack stamina" },
            new[] { "m_blockStaminaUseModifier", "block stamina" },
            new[] { "m_homeItemStaminaUseModifier", "tool stamina" },
            new[] { "m_fallDamageModifier", "fall damage" },
            new[] { "m_stealthModifier", "detection" },
            new[] { "m_noiseModifier", "noise" },
            new[] { "m_staggerModifier", "stagger taken" },
        };

        /// <summary>
        /// Every forsaken power, read out of ObjectDB, beside the most Rist can add to the same
        /// stat - and a flag wherever the two together reach zero.
        ///
        /// Written after the run-stamina bug, to check the other powers for the same thing. The
        /// powers' numbers are asset data on StatusEffect objects, readable only in a running
        /// game; Eikthyr's -0.60 was assumed from the game's description until a scenario
        /// measured it, and this reads all of them the same way.
        ///
        /// The Rist side is every stone at MaxRank at once, which no character may ever hold.
        /// That is the right bound for a floor: if the most Rist can ever add plus a power stays
        /// above zero, no real hand can reach it either.
        ///
        /// Flagged only when Rist alone and the power alone both stay above -1 and together they
        /// do not. Sure-footed tops out at -0.50 on fall damage now that its capstone is a landing
        /// roll and not a second -50%, so fall damage is judged like every other stat: a power that
        /// carried -0.50 of its own would be the one to flag. Run stamina is reported and not
        /// flagged, because MinRunStaminaCost floors it.
        ///
        /// "Every power at once" is not a thought experiment. A power reaches every player in
        /// range when it is cast, so two friends each casting a different one put both on you.
        /// </summary>
        private static void Powers(Terminal term)
        {
            if (ObjectDB.instance == null || ObjectDB.instance.m_StatusEffects == null
                || ObjectDB.instance.m_StatusEffects.Count == 0)
            {
                Say(term, "rist powers: no ObjectDB yet - load a world first.");
                return;
            }

            var ranks = new Dictionary<string, int>();
            foreach (var card in Cards.All)
                if (card != null && !string.IsNullOrEmpty(card.Id)) ranks[card.Id] = RistConfig.MaxRank.Value;
            var rist = Effects.TotalsFor(ranks);

            var floor = RistConfig.MinRunStaminaCost.Value;

            var alone = new StringBuilder("rist powers: every stone at rank " + RistConfig.MaxRank.Value + " adds");
            foreach (var pair in AddsToZero)
            {
                rist.TryGetValue(pair[0], out var r);
                if (r != 0f) alone.Append("  " + pair[1] + " " + Signed(r));
            }
            Say(term, alone.ToString());

            var together = new Dictionary<string, float>();
            var found = 0;
            var zeroes = 0;

            foreach (var effect in ObjectDB.instance.m_StatusEffects)
            {
                if (effect == null || effect.name == null
                    || !effect.name.StartsWith("GP_", StringComparison.Ordinal)) continue;

                found++;

                if (!(effect is SE_Stats stats))
                {
                    Say(term, "  " + effect.name + " (" + effect.GetType().Name + "): no stat modifiers, nothing to add up");
                    continue;
                }

                Say(term, "  " + effect.name + ": " + Describe(stats));

                foreach (var pair in AddsToZero)
                {
                    var p = Read(stats, pair[0]);
                    if (p == 0f) continue;

                    together.TryGetValue(pair[0], out var sum);
                    together[pair[0]] = sum + p;

                    rist.TryGetValue(pair[0], out var r);
                    zeroes += Judge(term, "    ", pair, p, r, floor);
                }
            }

            if (found == 0)
            {
                Say(term, "rist powers: no GP_ status effects in ObjectDB - is this the stub ObjectDB?");
                return;
            }

            if (together.Count > 0)
            {
                Say(term, "  every power at once, as when several players cast on you:");
                foreach (var pair in AddsToZero)
                {
                    if (!together.TryGetValue(pair[0], out var p)) continue;
                    rist.TryGetValue(pair[0], out var r);
                    zeroes += Judge(term, "    ", pair, p, r, floor);
                }
            }

            Say(term, zeroes == 0
                ? "rist powers: " + found + " powers read, nothing reaches zero"
                : "rist powers: " + found + " powers read, " + zeroes + " REACH ZERO");
        }

        /// <summary>One stat, one power: prints the sum and returns 1 when it reaches zero.</summary>
        private static int Judge(Terminal term, string indent, string[] pair, float power, float rist, float floor)
        {
            var total = power + rist;
            var line = indent + pair[1] + ": power " + Signed(power) + ", rist " + Signed(rist) + ", together " + Signed(total);

            if (pair[0] == "m_runStaminaDrainModifier" && floor > 0f && total <= -1f + floor)
            {
                Say(term, line + " - floored at x" + floor.ToString("0.00", CultureInfo.InvariantCulture) + " by MinRunStaminaCost");
                return 0;
            }

            // The power alone already at zero is vanilla, not a mix: Bonemass makes blocking free
            // and the Queen makes sneaking free all by themselves, measured 2026-09-24, and no hand
            // of cards changes free. Reported so the readout is complete, never flagged.
            if (power <= -1f)
            {
                Say(term, line + " - the power alone is already zero, as in vanilla");
                return 0;
            }

            if (rist > -1f && total <= -1f)
            {
                Say(term, line + " - REACHES ZERO");
                return 1;
            }

            Say(term, line);
            return 0;
        }

        /// <summary>
        /// Everything a stat effect changes: each float field away from its neutral value, the
        /// jump vector, the attack skill a damage modifier is gated on, and the resistances.
        /// </summary>
        private static string Describe(SE_Stats stats)
        {
            var parts = new List<string>();

            foreach (var field in typeof(SE_Stats).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(float)) continue;

                var value = (float)field.GetValue(stats);
                var neutral = Card.IsOneBased(field.Name) ? 1f : 0f;
                if (Math.Abs(value - neutral) < 0.0001f) continue;

                var text = field.Name + " " + value.ToString("0.###", CultureInfo.InvariantCulture);
                if (field.Name == "m_damageModifier") text += " on " + stats.m_modifyAttackSkill;
                parts.Add(text);
            }

            if (stats.m_jumpModifier != Vector3.zero)
                parts.Add("m_jumpModifier " + stats.m_jumpModifier.ToString("0.###"));

            if (stats.m_mods != null)
                foreach (var mod in stats.m_mods)
                    parts.Add(mod.m_type + " " + mod.m_modifier);

            return parts.Count == 0 ? "(no stat changes)" : string.Join(", ", parts.ToArray());
        }

        private static float Read(SE_Stats stats, string name)
        {
            var field = typeof(SE_Stats).GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null && field.FieldType == typeof(float) ? (float)field.GetValue(stats) : 0f;
        }

        private static string Signed(float value)
        {
            return value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>To the console and to the log, so a scenario run leaves the numbers on disk.</summary>
        private static void Say(Terminal term, string line)
        {
            term.AddString(line);
            RistPlugin.Log.LogInfo(line);
        }

        // ---------------------------------------------------------------- show

        private static void Show(Terminal term)
        {
            var player = Player.m_localPlayer;
            if (player == null) { term.AddString("rist: no player."); return; }

            term.AddString("level " + ClientState.Level + ", xp " + ClientState.Xp.ToString("0.#", CultureInfo.InvariantCulture)
                           + ", owed " + ClientState.Owed + (Net.IsServer ? "" : " (client - the server holds the record)"));

            var any = false;
            foreach (var pair in ClientState.Ranks)
            {
                if (pair.Value <= 0) continue;
                any = true;
                var card = Cards.Get(pair.Key);
                term.AddString("  " + pair.Key + " rank " + pair.Value + (card == null ? " (no such card)" : " - " + card.Name));
            }
            if (!any) term.AddString("  no ranks");

            term.AddString(Armour(player));
            term.AddString(Moving(player));
            term.AddString(BloodSworn.Probe(player));
            term.AddString(EelSlick.Probe());
            term.AddString("falls: " + Sinews.Landing.Probe());
            term.AddString(HallRaiser.Probe(player));
            term.AddString(Engineer.Probe(player));
            term.AddString(Merges.Probe(player));
        }

        /// <summary>
        /// The three numbers the movement stones change, read back off the game.
        ///
        /// Same rule as Armour above: print what the game would use, not what Rist believes it
        /// wrote. The running figure is the game's own, taken through the public
        /// SEMan.ModifyRunStaminaDrain with a base of 1 - which is exactly the call
        /// Player.GetEquipmentModifierPlusSE(9) makes for the cost readout, so a multiplier here
        /// is the multiplier a sprint is charged. It is therefore the only place that can show
        /// MinRunStaminaCost doing its job: with the floor working this bottoms out at the
        /// configured fraction however much is stacked, and without it a deep enough stack reads
        /// x0.00 and running is free.
        ///
        /// minZero: false on purpose. The game's own clamp would turn a negative sum into zero
        /// and hide whether the floor or the clamp produced it, and a test that cannot tell
        /// those apart passes on the bug it was written for.
        /// </summary>
        private static string Moving(Player player)
        {
            var seman = player.GetSEMan();

            var running = 1f;
            if (seman != null) seman.ModifyRunStaminaDrain(1f, ref running, Vector3.zero, minZero: false);

            var jumpBase = Sinews.VanillaJump(player);
            var jumpRatio = jumpBase > 0f ? player.m_jumpForce / jumpBase : 1f;

            // Height, not push: the rise goes with the square of the push, and height is what the
            // card promises and what a player sees. Worked out from the game's own jump force
            // rather than read back off the hand, so a card that wrote the wrong push shows here.
            var heightRatio = jumpRatio * jumpRatio;

            // A ratio and a difference rather than the two raw numbers, because both baselines
            // are asset data on the Player prefab and neither is readable outside the running
            // game. Asserting "jump 12.0" in a scenario would be asserting a value nobody here
            // has measured; "jump height x1.15" is true whatever the prefab carries. The absolutes are
            // printed after them for reading, which is the same split as armour above.
            //
            // The delay figure is what was actually taken off, not what the cards asked for, so
            // a run into the 0.25s floor shows up as a smaller number instead of passing.
            var delayOff = Sinews.VanillaDelay(player) - player.m_staminaRegenDelay;

            // Stagger through the game's own sum, which is what a hit is scaled by. The overload
            // drain is a ratio for the same reason jump is: its baseline is prefab data.
            var stagger = 1f;
            if (seman != null) seman.ModifyStagger(1f, ref stagger);

            var overloadBase = Sinews.VanillaOverload(player);
            var overload = overloadBase > 0f ? player.m_encumberedStaminaDrain / overloadBase : 1f;

            return "running x" + running.ToString("0.00", CultureInfo.InvariantCulture)
                   + "  delay " + delayOff.ToString("0.00", CultureInfo.InvariantCulture)
                   + "s off (" + player.m_staminaRegenDelay.ToString("0.00", CultureInfo.InvariantCulture)
                   + "s of " + Sinews.VanillaDelay(player).ToString("0.00", CultureInfo.InvariantCulture)
                   + "s)  jump height x" + heightRatio.ToString("0.00", CultureInfo.InvariantCulture)
                   + " (push " + player.m_jumpForce.ToString("0.00", CultureInfo.InvariantCulture)
                   + " of " + jumpBase.ToString("0.00", CultureInfo.InvariantCulture) + ")"
                   + "  stagger x" + stagger.ToString("0.00", CultureInfo.InvariantCulture)
                   + "  overloaded x" + overload.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// "armour 24.0 (bare 20.0, x1.20)" - the line a scenario asserts on.
        ///
        /// bare is the four worn pieces added up, which is what GetBodyArmor starts from
        /// before it hands the total to the status effects; armour is what it returns. The
        /// ratio is therefore everything every mod on this machine did to armour, measured
        /// where the game reads it rather than where Rist writes it.
        /// </summary>
        private static string Armour(Player player)
        {
            // The worn four, reached through the inventory because Humanoid keeps the slots
            // protected. Everything else equipped carries m_armor 0, so the filter lands on
            // exactly what GetBodyArmor adds up: chest, legs, helmet and cape.
            var bare = 0f;
            var inventory = player.GetInventory();
            if (inventory != null)
            {
                foreach (var item in inventory.GetEquippedItems())
                {
                    if (item == null || item.m_shared == null || item.m_shared.m_armor <= 0f) continue;
                    bare += item.GetArmor();
                }
            }

            var armour = player.GetBodyArmor();
            var ratio = bare > 0f ? armour / bare : 1f;

            // "game" and "rist" are labelled apart because a scenario has to be able to name
            // which one it is asserting on. They were one number in the first draft and the
            // assertion would have matched either, which is a test that passes on this mod's
            // own opinion of itself while the game ignores it. The two disagreeing is a real
            // finding in its own right: it means Rist wrote a modifier the game never read.
            var mine = Effects.TotalFor("m_armorMultiplier");
            var flat = Effects.TotalFor("m_addArmor");

            var line = "armour " + armour.ToString("0.0", CultureInfo.InvariantCulture)
                       + "  bare " + bare.ToString("0.0", CultureInfo.InvariantCulture)
                       + "  game x" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                       + "  rist x" + (1f + mine).ToString("0.00", CultureInfo.InvariantCulture);

            if (Math.Abs(flat) > 0.0001f)
                line += " and +" + flat.ToString("0.#", CultureInfo.InvariantCulture) + " flat";

            return line;
        }

        // ---------------------------------------------------------------- others

        /// <summary>
        /// What the other-players page has been told, one line a character. The numbers are the
        /// ones the page draws from, so a scenario asserting on them is asserting on the page's
        /// own data and not on a second reading of the ledger.
        ///
        /// `ask` sends the request and says so without printing the list: the answer is a routed
        /// message and is not here yet, on anything but a host. A second call, after a wait,
        /// reads it. Needs no devcommands on a client, and changes nothing on any machine.
        /// </summary>
        private static void OthersList(Terminal term, bool ask)
        {
            if (ask)
            {
                Others.Ask();
                term.AddString("rist others: asked.");
                return;
            }

            var online = 0;
            foreach (var peer in Others.List)
                if (peer.Online) online++;

            term.AddString("others: " + Others.List.Count + " listed, " + online + " online, answered " +
                           (Others.Answered ? "yes" : "no"));

            foreach (var peer in Others.List)
            {
                Others.Counts(peer, out var carved, out var marks);

                term.AddString("  " + (peer.Online ? "online" : "offline") + " " + peer.Label +
                               (peer.Named ? "" : " (no name)") + " level " + peer.Level +
                               " carved " + carved + " marks " + marks);
            }
        }

        // ---------------------------------------------------------------- kills

        private static void KillsCommand(Terminal term, Terminal.ConsoleEventArgs args)
        {
            var sub = args.Length > 2 ? args[2].ToLowerInvariant() : "";

            if (sub == "send")
            {
                term.AddString(Kills.Report(true) ? "rist kills: sent every kind to the server." : "rist kills: nothing to send.");
                return;
            }

            if (sub == "set")
            {
                int n;
                if (args.Length < 5 || !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0)
                { term.AddString("rist kills set <name> <n>, e.g. rist kills set $enemy_boar 10"); return; }

                var counts = Kills.Profile();
                if (counts == null) { term.AddString("rist kills: no character profile yet."); return; }
                if (Kills.WeightOf(args[3]) <= 0f) { term.AddString("rist kills: no creature called '" + args[3] + "'."); return; }

                counts[args[3]] = n;
                term.AddString("rist kills: " + args[3] + " set to " + n + " in this character's file. Run rist kills send to report it.");
                return;
            }

            if (sub == "reset")
            {
                long peer;
                string owner;
                if (!Net.IsServer || !Net.LocalOwner(out peer, out owner))
                { term.AddString("rist kills: reset needs singleplayer or a host with a record."); return; }

                var rec = Ledger.For(owner);
                if (rec == null) { term.AddString("rist kills: no ledger record."); return; }

                var taken = rec.KillXp;
                rec.Xp = Mathf.Max(0f, rec.Xp - rec.KillXp);
                rec.KillXp = 0f;
                rec.KillSeen.Clear();
                Ledger.Touch();
                Net.PushState(peer, rec);
                term.AddString("rist kills: took back " + taken.ToString("0.#", CultureInfo.InvariantCulture) + " xp and forgot every reported count.");
                return;
            }

            var mine = Kills.Profile();
            if (mine == null) { term.AddString("rist kills: no character profile yet."); return; }

            var marks = Kills.Marks();
            var local = 0f;
            var kinds = 0;
            foreach (var kv in mine)
            {
                var weight = Kills.WeightOf(kv.Key);
                if (weight <= 0f || kv.Value < 1f) continue;

                kinds++;
                local += Kills.WorthOf(kv.Key, kv.Value);

                if (sub.Length > 0 && !kv.Key.ToLowerInvariant().Contains(sub)) continue;

                var next = Kills.Next(kv.Value);
                term.AddString("  " + kv.Key + ": " + kv.Value.ToString("0", CultureInfo.InvariantCulture) + " kills, "
                               + Kills.Reached(kv.Value) + " of " + marks.Length + " milestones, weight "
                               + weight.ToString("0.00", CultureInfo.InvariantCulture) + ", worth "
                               + Kills.WorthOf(kv.Key, kv.Value).ToString("0.#", CultureInfo.InvariantCulture) + " xp"
                               + (next > 0 ? ", next at " + next : ", all reached"));
            }

            term.AddString("kills: " + kinds + " kinds count, worth " + local.ToString("0.#", CultureInfo.InvariantCulture)
                           + " xp by this config, server says " + ClientState.KillXp.ToString("0.#", CultureInfo.InvariantCulture));

            // Equal to a tenth of an xp rather than exact: the server sums a table of whole
            // counts and this sums floats, and a scenario must not fail on rounding.
            term.AddString(Mathf.Abs(local - ClientState.KillXp) < 0.1f ? "kills agree" : "kills disagree");
        }

        // ---------------------------------------------------------------- rank

        private static void Rank(Terminal term, Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 4) { term.AddString("rist rank <card> <n>"); return; }

            if (!Net.IsServer)
            {
                term.AddString("rist: ranks live on the server, so this only works in singleplayer or when you host.");
                return;
            }

            var id = args[2];
            var card = Cards.Get(id);
            if (card == null) { term.AddString("rist: no card called '" + id + "'."); return; }

            int rank;
            if (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out rank) || rank < 0)
            { term.AddString("rist: '" + args[3] + "' is not a rank."); return; }

            var max = RistConfig.MaxRank.Value;
            if (rank > max) { term.AddString("rist: MaxRank is " + max + "."); return; }

            long peer;
            string owner;
            if (!Net.LocalOwner(out peer, out owner))
            { term.AddString("rist: this character has no record yet - wait a moment after spawning."); return; }

            var rec = Ledger.For(owner);
            if (rec == null) { term.AddString("rist: no ledger record for " + owner + "."); return; }

            var before = rec.RankOf(id);
            rec.SetRank(id, rank);
            Ledger.Touch();
            Net.PushState(peer, rec);

            term.AddString(card.Name + ": rank " + before + " -> " + rec.RankOf(id)
                           + ". " + rec.DraftsTaken + " picks spent, " + rec.Owed + " owed.");
        }
    }
}
