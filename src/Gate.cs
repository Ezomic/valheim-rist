using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rist
{
    /// <summary>
    /// What this server has watched each of a character's skills reach.
    ///
    /// This used to be a judgement and is now only a measurement, and the difference is the
    /// whole history of the file. It began by refusing the connection outright, which is the
    /// wrong power for a levelling mod to hold: a bug in an XP system then locks people out of
    /// a server, and it did - the player got Valheim's generic kick screen with the reason only
    /// in the server's log. That check moved to Dyrr, where turning people away is the
    /// entire job and is done openly with its own message.
    ///
    /// What replaced it here was softer but not actually better: a character whose skills sat
    /// above the baseline was marked untrusted and paid no XP "until they line up again". They
    /// never could. The withholding returned before the snapshot was updated, and the login
    /// comparison only adopted a new baseline when it had found nothing wrong - so the baseline
    /// froze at the moment of judgement while the player's real skills only ever climbed. The
    /// gap widened forever, on that character, on that server, while the message on screen
    /// promised a recovery that no amount of play could reach.
    ///
    /// So the judgement is gone and the measurement stays. An imported character is simply
    /// **not paid for what it did elsewhere** - which is the property this was always after,
    /// and which Rist already had and already documents: nothing is ever backfilled, so a
    /// character arriving at skill 50 starts at Rist level 0 regardless. Preventing a character
    /// from being used across worlds at all is a door policy, and the door is Dyrr's.
    ///
    /// The baseline itself is still worth keeping, for a reason that has nothing to do with
    /// trust: <see cref="Throttle.Step"/> needs it to tell a plausible next level from a forged
    /// one, and an absent entry has to be distinguishable from a skill never used.
    /// </summary>
    internal static class Gate
    {
        /// <summary>
        /// Owners this server holds a complete skill list for, learned from the login exchange.
        ///
        /// Kept because an absent snapshot entry is ambiguous on its own: it means either "this
        /// skill has never been used" or "we have never looked". Anything comparing a report
        /// against the baseline has to know which, and only the login exchange can say - it is
        /// the one moment every skill is reported at once.
        /// </summary>
        private static readonly HashSet<string> Baselined = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether this server has seen this owner's whole skill list this session.</summary>
        internal static bool HasBaseline(string owner)
        {
            return owner != null && Baselined.Contains(owner);
        }

        /// <summary>
        /// Drop what was learned, for a new connection. Per-session by design: it describes a
        /// login rather than a character.
        /// </summary>
        internal static void Forget()
        {
            Baselined.Clear();
        }

        /// <summary>
        /// Every skill and the level it currently sits at, as "type:level" pairs.
        /// </summary>
        internal static string LocalSkills()
        {
            var player = Player.m_localPlayer;
            if (player == null) return "";

            var skills = player.GetSkills();
            if (skills == null) return "";

            var sb = new StringBuilder();
            foreach (var skill in skills.GetSkillList())
            {
                if (skill == null || skill.m_info == null) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append((int)skill.m_info.m_skill).Append(':')
                  .Append(skill.m_level.ToString("R", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        // ---- server side ---------------------------------------------------------------

        /// <summary>
        /// Take a joining character's skill list as the baseline, whatever it says.
        ///
        /// No comparison and no verdict. A character that turns up higher than this server last
        /// saw it gained that somewhere else and is paid nothing for it - not because it is
        /// refused, but because XP only ever comes from a level-up watched from here. Adopting
        /// the higher number is what stops the next honest level-up looking like a forgery to
        /// <see cref="Throttle.Step"/>.
        ///
        /// The list is self-reported, and always was. It is not load-bearing on its own: what
        /// bounds a forged one is Step's single-level ceiling and the earning cap over connected
        /// time, neither of which asks the client anything.
        /// </summary>
        internal static void Judge(long sender, string owner, string facts, string skills)
        {
            if (!RistConfig.CheckSkillBaseline.Value) return;
            if (string.IsNullOrEmpty(owner)) return;

            var reported = ParseSkills(skills);
            if (reported == null) return;

            var rec = Ledger.For(owner);
            if (rec == null) return;

            var first = !rec.HasSnapshot;
            Baselined.Add(owner);

            var raised = 0;
            foreach (var kv in reported)
            {
                if (rec.Snapshot.TryGetValue(kv.Key, out var seen) && kv.Value <= seen) continue;
                rec.Snapshot[kv.Key] = kv.Value;
                raised++;
            }

            if (raised > 0) Ledger.Touch();

            if (first)
                RistPlugin.Log.LogInfo("Baseline adopted for " + owner + " (" + reported.Count + " skills).");
            else if (raised > 0 && RistConfig.Verbose.Value)
                RistPlugin.Log.LogInfo("Baseline for " + owner + " raised on " + raised + " skill(s).");

            // After the baseline has been raised, so a character whose first ever login this
            // is gets recomputed against a full skill list rather than an empty one.
            var changed = Recompute(owner, rec);

            if (Credit(sender, owner, rec, reported) || changed) Net.PushState(sender, rec);
        }

        /// <summary>
        /// Re-price a character's whole history under the weights standing now.
        ///
        /// The one operation allowed to move xp downward, and the reason it is stamped rather
        /// than run every login. Routine crediting is deliberately one-way - it must never
        /// take levels away, or a skill lost to a death penalty would cost a card - so
        /// re-pricing cannot be folded into it and has to announce itself as a separate,
        /// once-per-generation act.
        ///
        /// Computed from <see cref="RistRecord.Snapshot"/> rather than from the character's
        /// current skills, for two reasons. The snapshot is this server's own high-water mark,
        /// so it cannot be talked down by a client reporting itself lower; and it is monotonic,
        /// so a death penalty between the old pricing and the new one does not read as levels
        /// that were never earned.
        ///
        /// Cards already taken are untouched. DraftsTaken outliving the level is a state Owed
        /// already handles - it floors at zero - so a character re-priced from 12 to 6 keeps
        /// its twelve cards and earns no new pick until it passes twelve again.
        /// </summary>
        internal static bool Recompute(string owner, RistRecord rec)
        {
            if (rec == null) return false;

            // The stamp is written into a pipe-separated ledger line, so a pipe in it would
            // split the record into a field that no longer parses back as the same text - and
            // a stamp that never matches means this logs a re-price on every single login.
            var generation = (RistConfig.WeightGeneration.Value ?? "").Replace("|", "/").Trim();
            if ((rec.WeightGen ?? "") == generation) return false;

            // Nothing to price from yet. Deliberately not stamped either: stamping here would
            // spend the generation on a record that was never recomputed, and the login that
            // finally learns its skills would find the job already marked done.
            if (!rec.HasSnapshot) return false;

            var before = rec.Xp;
            var beforeLevel = rec.Level;

            // Kills are re-priced in the same breath, so a re-price under new weights cannot
            // leave the kill share at what the old config paid.
            Kills.Reprice(rec);
            rec.Xp = Weights.WorthOf(rec.Snapshot) + rec.KillXp;
            rec.WeightGen = generation;
            Ledger.Touch();

            RistPlugin.Log.LogInfo("Re-priced " + owner + " at weight generation '" + generation +
                                   "': " + before.ToString("0") + " -> " + rec.Xp.ToString("0") +
                                   " xp, Rist level " + beforeLevel + " -> " + rec.Level +
                                   " (" + rec.DraftsTaken + " cards kept, " + rec.Owed + " to spend).");

            return true;
        }

        /// <summary>
        /// Pay a joining character for the skills it already has.
        ///
        /// The level is meant to sit beside the skills, so a character that turns up with
        /// skills worth twenty levels should have twenty levels - whether it earned them here,
        /// on another world, or before this mod was installed. Anything else makes the number
        /// a record of which server you were standing on rather than of the character.
        ///
        /// The arithmetic is not an estimate. XP is granted per skill level-up weighted by the
        /// level reached and by the skill, so a skill sitting at N has already produced
        /// weight * (1 + 2 + ... + N), which is weight * N(N+1)/2. Summing that over every
        /// skill gives exactly the XP the character would hold if every one of those level-ups
        /// had been watched from here under the weights standing now.
        ///
        /// That exactness is also what makes it safe to run on every login. A character that
        /// earned everything here computes the total it already has, so the credit is zero and
        /// nothing is double-paid; and it only ever raises, so a skill lost to a death penalty
        /// cannot take levels away.
        /// </summary>
        private static bool Credit(long sender, string owner, RistRecord rec, Dictionary<int, float> reported)
        {
            if (!RistConfig.CreditExistingSkills.Value) return false;

            // The same arithmetic, now weighted per skill - see Weights.WorthOf, which is
            // shared with the re-pricing above so the two can never drift apart. They must
            // agree exactly: if crediting priced a character higher than re-pricing does, the
            // next login would undo every re-price, and the mod would look like it forgot.
            var worth = Weights.WorthOf(reported) + rec.KillXp;

            // Never downward. Only the shortfall is paid, so this is idempotent.
            if (worth <= rec.Xp + 0.001f) return false;

            var before = rec.Level;
            rec.Xp = worth;
            Ledger.Touch();

            RistPlugin.Log.LogInfo("Credited " + owner + " for skills already held: " +
                                   worth.ToString("0") + " xp, Rist level " + before + " -> " + rec.Level + ".");

            return true;
        }

        private static Dictionary<int, float> ParseSkills(string wire)
        {
            if (wire == null) return null;

            var map = new Dictionary<int, float>();
            foreach (var pair in wire.Split(','))
            {
                if (pair.Length == 0) continue;
                var bits = pair.Split(':');
                if (bits.Length != 2) continue;
                if (!int.TryParse(bits[0], out var type)) continue;
                if (!float.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var level)) continue;
                map[type] = level;
            }

            return map;
        }
    }
}
