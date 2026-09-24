using System;
using System.Collections.Generic;
using System.Globalization;
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
                "rist show | rist rank <card> <n> - this character's standing, and forcing a rank for a test",
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

            term.AddString("rist show            - level, xp, ranks and the armour the game is using");
            term.AddString("rist rank <card> <n> - force a card to exactly that rank");
            term.AddString("card ids are the first field of cards.txt: thickhide, steadyfoot, longstride...");
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

            return "running x" + running.ToString("0.00", CultureInfo.InvariantCulture)
                   + "  delay " + delayOff.ToString("0.00", CultureInfo.InvariantCulture)
                   + "s off (" + player.m_staminaRegenDelay.ToString("0.00", CultureInfo.InvariantCulture)
                   + "s of " + Sinews.VanillaDelay(player).ToString("0.00", CultureInfo.InvariantCulture)
                   + "s)  jump height x" + heightRatio.ToString("0.00", CultureInfo.InvariantCulture)
                   + " (push " + player.m_jumpForce.ToString("0.00", CultureInfo.InvariantCulture)
                   + " of " + jumpBase.ToString("0.00", CultureInfo.InvariantCulture) + ")";
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
