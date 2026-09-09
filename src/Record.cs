using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rist
{
    /// <summary>
    /// One player's standing with Rist: how much XP they have earned on this server, which
    /// cards they hold, and at which level each rank of each card was bought.
    ///
    /// Held by the server, never by the character. That is the whole anti-cheat premise - a
    /// character file sits on the player's own disk and can be edited, so nothing that
    /// decides rewards may live there.
    ///
    /// Keyed by <see cref="Owner"/>, the platform user id read from the peer's own socket
    /// server-side (ISocket.GetHostName). Deliberately not the character's Player.GetPlayerID:
    /// that arrives from the client and could name someone else's record. The socket identity
    /// is established by the platform before any of our code runs, so it cannot be claimed.
    /// </summary>
    internal sealed class RistRecord
    {
        internal string Owner = "";
        internal float Xp;

        /// <summary>Levels already spent on cards. Lags Level when picks are owed.</summary>
        internal int DraftsTaken;

        /// <summary>
        /// Card id to the levels that bought its ranks, in the order they were bought. The
        /// rank *is* the count, so the two can never disagree - an earlier design kept a
        /// separate rank number beside the history and would have needed them reconciled.
        ///
        /// A 0 means the rank is real but its level is unknown: ledger lines written before
        /// this existed recorded only a rank, and there is nothing on disk that says which
        /// level bought it. Those stay 0 forever rather than being guessed at.
        /// </summary>
        internal readonly Dictionary<string, List<int>> Taken = new Dictionary<string, List<int>>();

        /// <summary>
        /// The highest level this server has itself watched each skill reach, keyed by
        /// (int)Skills.SkillType.
        ///
        /// This is the answer to the one hole the travel check cannot close. A character file
        /// is client-side and can be hand-edited to strip its world history, but it cannot
        /// reach this - the baseline lives here. A character coming back with a skill above
        /// what this server saw it reach gained that level somewhere else, whatever its file
        /// claims about where it has been.
        /// </summary>
        internal readonly Dictionary<int, float> Snapshot = new Dictionary<int, float>();

        /// <summary>
        /// The RistConfig.WeightGeneration this record's xp was last recomputed at.
        ///
        /// Empty means never, which is every line written before skill weights existed. The
        /// stamp is what makes the recompute a one-shot: it is the only operation allowed to
        /// move xp downward, so it must be able to say it has already run rather than firing
        /// on every login and re-flooring a character each time.
        /// </summary>
        internal string WeightGen = "";

        internal bool HasSnapshot => Snapshot.Count > 0;

        internal int Level => Levels.LevelForXp(Xp);

        /// <summary>How many picks the player still has to spend.</summary>
        internal int Owed => Math.Max(0, Level - DraftsTaken);

        internal int RankOf(string id)
        {
            return id != null && Taken.TryGetValue(id, out var levels) ? levels.Count : 0;
        }

        /// <summary>
        /// Spend one pick on a card. The level recorded is the one that *granted* the pick,
        /// not the level standing when it was spent: someone who banks three picks and spends
        /// them all at level 12 earned them at 10, 11 and 12, and the panel should say so.
        /// </summary>
        internal void Take(string id)
        {
            if (!Taken.TryGetValue(id, out var levels))
            {
                levels = new List<int>();
                Taken[id] = levels;
            }

            levels.Add(DraftsTaken + 1);
            DraftsTaken++;
        }

        /// <summary>
        /// Hand back the picks spent on cards that no longer exist.
        ///
        /// Removing a card from cards.txt used to strand every rank already bought in it: the
        /// ranks stayed in the ledger, counted toward the totals in the header, and had no
        /// stone to appear on - so a player with twelve picks spent could only find seven of
        /// them. Deleting Deep pack did exactly that to five.
        ///
        /// Returning them is the only honest answer. The catalogue is a text file and is meant
        /// to be edited; an edit that quietly eats progress makes it something you have to be
        /// careful with instead.
        /// </summary>
        internal bool Reconcile()
        {
            // The floor, and it belongs before the loop rather than inside it. "Cards.Get
            // returned null" means the same thing for a card somebody deleted on purpose and
            // for a catalogue that never loaded, and in the second case this loop walks off
            // with every card every player holds - server-side, flushed to disk ten seconds
            // later by Ledger.Tick, with no copy anywhere. Ledger.ReconcileAllowed is where
            // the two are told apart, because it can see the whole ledger and the whole
            // catalogue at once and this method can see neither.
            if (!Ledger.ReconcileAllowed(out var why))
            {
                RistPlugin.Log.LogWarning("Kept " + Owner + "'s cards as they are: " + why + ".");
                return false;
            }

            List<string> gone = null;

            foreach (var kv in Taken)
            {
                if (Cards.Get(kv.Key) != null) continue;

                if (gone == null) gone = new List<string>();
                gone.Add(kv.Key);
            }

            if (gone == null) return false;

            foreach (var id in gone)
            {
                var returned = Taken[id].Count;
                Taken.Remove(id);

                // The picks go back rather than being written off, so they can be spent again.
                DraftsTaken = Math.Max(0, DraftsTaken - returned);

                RistPlugin.Log.LogInfo("Card '" + id + "' is no longer in the catalogue - " +
                                       returned + " pick(s) returned to " + Owner + ".");
            }

            return true;
        }

        // ---- serialisation -------------------------------------------------------------
        //
        // A flat line rather than JSON: the ledger is read and written by this mod alone, it
        // has to survive being opened in a text editor on a server, and a dependency for
        // four fields is not worth it.
        //
        //   v4|owner|xp|draftsTaken|id:level;level,id:level|skillType:level,...|weightGen
        //
        // v1 and v2 are still read. Both carried "id:rank" and a field of standing offers,
        // from when a level dealt three cards at random rather than letting you choose. Their
        // ranks are adopted with unknown levels and the offer field is dropped on the floor.
        //
        // v3 is v4 without the trailing weight generation, and reads back as one that has
        // never been recomputed - which is exactly what it is.

        internal string Serialise()
        {
            var sb = new StringBuilder();
            sb.Append("v4|").Append(Owner).Append('|')
              .Append(Xp.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(DraftsTaken).Append('|');

            AppendTaken(sb);
            sb.Append('|');

            var first = true;
            foreach (var kv in Snapshot)
            {
                if (!first) sb.Append(',');
                sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture));
                first = false;
            }

            sb.Append('|').Append(WeightGen ?? "");

            return sb.ToString();
        }

        internal static RistRecord Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;

            var parts = line.Split('|');
            var version = parts[0];
            if (version != "v1" && version != "v2" && version != "v3" && version != "v4") return null;

            // v3 dropped the offer field, so everything after the cards sits one place earlier.
            var legacy = version == "v1" || version == "v2";
            if (parts.Length < (legacy ? 6 : 5)) return null;

            if (parts[1].Length == 0) return null;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var xp)) return null;
            if (!int.TryParse(parts[3], out var drafts)) return null;

            var rec = new RistRecord { Owner = parts[1], Xp = xp, DraftsTaken = drafts };

            foreach (var entry in parts[4].Split(','))
            {
                if (entry.Length == 0) continue;

                var bits = entry.Split(':');
                if (bits.Length != 2) continue;

                var levels = new List<int>();

                if (legacy)
                {
                    // "id:rank" - the ranks are real, the levels behind them are not recorded
                    // anywhere and cannot be recovered, so they come back as unknown.
                    if (!int.TryParse(bits[1], out var rank)) continue;
                    for (var i = 0; i < rank; i++) levels.Add(0);
                }
                else
                {
                    foreach (var text in bits[1].Split(';'))
                    {
                        if (text.Length == 0) continue;
                        if (int.TryParse(text, out var level)) levels.Add(level);
                    }
                }

                if (levels.Count > 0) rec.Taken[bits[0]] = levels;
            }

            var snapshotAt = legacy ? 6 : 5;
            if (parts.Length > snapshotAt)
            {
                foreach (var pair in parts[snapshotAt].Split(','))
                {
                    if (pair.Length == 0) continue;
                    var bits = pair.Split(':');
                    if (bits.Length != 2) continue;
                    if (!int.TryParse(bits[0], out var type)) continue;
                    if (!float.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var level)) continue;
                    rec.Snapshot[type] = level;
                }
            }

            // Absent on v1..v3, and absent is correct for them: those lines predate weights
            // and have never been recomputed under any generation.
            var genAt = snapshotAt + 1;
            if (parts.Length > genAt) rec.WeightGen = parts[genAt];

            return rec;
        }

        /// <summary>
        /// The form sent to the client for display. Deliberately not the ledger line: the
        /// client never needs the owner id, and must never be handed anything it could send
        /// back as authority.
        ///
        ///   xp|draftsTaken|id:level;level,id:level|owed|level
        ///
        /// Owed and level are sent rather than left to the client to work out. It used to
        /// derive both from xp through the level curve, which is config - and a client whose
        /// curve differs from the host's computes a different level from the same xp. On a
        /// server with the shipping curve and a client still on a cheapened test one, the
        /// client believed it had picks and every one of them came back "nothing owed".
        /// </summary>
        internal string ToWire()
        {
            var sb = new StringBuilder();
            sb.Append(Xp.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(DraftsTaken).Append('|');
            AppendTaken(sb);
            sb.Append('|').Append(Owed).Append('|').Append(Level);
            return sb.ToString();
        }

        private void AppendTaken(StringBuilder sb)
        {
            var first = true;
            foreach (var kv in Taken)
            {
                if (kv.Value.Count == 0) continue;
                if (!first) sb.Append(',');

                sb.Append(kv.Key).Append(':');
                for (var i = 0; i < kv.Value.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(kv.Value[i]);
                }

                first = false;
            }
        }
    }
}
