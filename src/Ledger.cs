using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace Rist
{
    /// <summary>
    /// The server's record of every player, on the server's own disk.
    ///
    /// This is the point of the whole design. Valheim keeps characters client-side - even
    /// ZNet.SaveOtherPlayerProfiles only sends each client an RPC telling it to save its own
    /// profile locally - so the server never sees a character file and anything stored there
    /// is forgeable. Levels and cards therefore live here, keyed by the platform identity of
    /// the connection, where a client cannot reach them.
    /// </summary>
    internal static class Ledger
    {
        private static readonly Dictionary<string, RistRecord> _records =
            new Dictionary<string, RistRecord>();

        private static bool _loaded;
        private static bool _dirty;
        private static float _nextFlush;

        /// <summary>
        /// The ledger file exists and could not be read. Nothing is written while this stands:
        /// an empty in-memory ledger written over a real file is every player deleted, and the
        /// records were never loaded to write back.
        /// </summary>
        private static bool _readFailed;

        /// <summary>Said once, because Flush is attempted every ten seconds.</summary>
        private static bool _saidRefusingToWrite;

        private static string LedgerPath => Path.Combine(Paths.ConfigPath, "rist-ledger.txt");

        internal static RistRecord For(string owner)
        {
            Load();

            if (string.IsNullOrEmpty(owner)) return null;

            if (!_records.TryGetValue(owner, out var rec))
            {
                rec = new RistRecord { Owner = owner };
                _records[owner] = rec;
                _dirty = true;
            }

            return rec;
        }

        internal static void Touch()
        {
            _dirty = true;
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            if (File.Exists(LedgerPath))
            {
                // Inside a try, because the alternative is worse than it looks. The read used
                // to be bare: an IO exception escaped through Ledger.For into the RPC handler
                // that called it, _loaded was already true, and the next call carried on
                // against an empty dictionary - which Flush then wrote over the top of the
                // real file. A locked or half-copied ledger deleted every player on the server
                // ten seconds later. The file exists and could not be read is a refusal to
                // write, never a fresh start.
                try
                {
                    var bad = 0;
                    foreach (var line in File.ReadAllLines(LedgerPath))
                    {
                        if (line.Trim().Length == 0) continue;

                        var rec = RistRecord.Parse(line);
                        if (rec == null) { bad++; continue; }
                        _records[rec.Owner] = rec;
                    }

                    RistPlugin.Log.LogInfo("Ledger loaded: " + _records.Count + " players" +
                                           (bad > 0 ? ", " + bad + " unreadable lines skipped" : "") + ".");
                }
                catch (Exception e)
                {
                    _readFailed = true;
                    _records.Clear();

                    RistPlugin.Log.LogError("The ledger at " + LedgerPath + " could not be read ("
                        + e.Message + "). Rist will not write to it for the rest of this session, "
                        + "so the file on disk is left exactly as it stands and nobody's progress "
                        + "is lost. Nothing earned until the server is restarted will be kept.");
                }
            }
            else
            {
                RistPlugin.Log.LogInfo("No ledger yet; starting a fresh one at " + LedgerPath);
            }

            // Judged once, against the ledger exactly as it came off disk. That timing is the
            // whole point - see JudgeReconcile.
            JudgeReconcile();
        }

        // ---- the reconcile gate ------------------------------------------------------------

        private static bool _reconcileBlocked;
        private static string _reconcileWhy = "";

        /// <summary>
        /// Whether handing back the picks spent on vanished cards is safe right now.
        ///
        /// RistRecord.Reconcile deletes a player's history for every card id the catalogue
        /// cannot resolve. That is correct when somebody deleted a card on purpose, and it is
        /// a permanent server-side wipe of everyone's progress when the catalogue merely
        /// failed to load - and the two are indistinguishable from inside Reconcile, because
        /// both are "Cards.Get returned null". Ten seconds later Ledger.Tick writes it out and
        /// there is nothing to recover from. So the question is asked once, here, where the
        /// whole ledger and the whole catalogue can be seen at the same time.
        /// </summary>
        internal static bool ReconcileAllowed(out string why)
        {
            Load();

            why = _reconcileWhy;
            return !_reconcileBlocked;
        }

        /// <summary>
        /// Decide whether reconciling is allowed, from the ledger as loaded and the catalogue
        /// as parsed.
        ///
        /// Four refusals, tightening.
        ///
        /// The first three are absolute and no threshold softens them: a ledger that could not
        /// be read is not the ledger, and a catalogue that failed to load (Cards.Unavailable)
        /// or that holds nothing may never be reconciled against. There is no edit to
        /// cards.txt that legitimately empties it - that is a deleted file, a locked file, or
        /// a game update that renamed the SE_Stats fields the lines name.
        ///
        /// The fourth is the interesting one: how far the catalogue may have shrunk. The
        /// baseline is the ledger's own set of card ids rather than a remembered count, and
        /// that is a deliberate choice over writing the previous size to disk beside the
        /// ledger. The set of ids standing in the ledger *is* the catalogue as the ledger last
        /// reconciled against it, minus the cards nobody ever bought - it needs no new file to
        /// go stale, no format bump, and it measures the thing that actually gets destroyed
        /// rather than a proxy for it. A cards.txt that lost ten cards nobody had taken costs
        /// nobody anything and is waved through; one that lost the four everybody is holding
        /// is stopped.
        ///
        /// Refusing is not free - it strands picks in cards that really are gone, which is the
        /// complaint that made Reconcile exist. That is why it is loud and why the threshold
        /// is config: the admin who meant it raises ReconcileMaxLoss, restarts, and the pass
        /// runs. An admin who did not mean it keeps everyone's cards.
        /// </summary>
        private static void JudgeReconcile()
        {
            _reconcileBlocked = false;
            _reconcileWhy = "";

            if (_readFailed)
            {
                Block("the ledger on disk could not be read, so what is in memory is not what "
                      + "anybody actually holds");
                return;
            }

            if (Cards.Unavailable)
            {
                Block("the card catalogue did not load, so every card in the ledger looks deleted");
                return;
            }

            if (Cards.Count == 0)
            {
                // Cards.Load already flags that case, so reaching here means something else
                // emptied the catalogue afterwards. Checked anyway: this gate is the last
                // thing between a bad catalogue and everyone's history.
                Block("the card catalogue is empty");
                return;
            }

            var held = 0;
            var lost = 0;
            var seen = new HashSet<string>();

            foreach (var rec in _records.Values)
            {
                foreach (var kv in rec.Taken)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;
                    if (!seen.Add(kv.Key)) continue;

                    held++;
                    if (Cards.Get(kv.Key) == null) lost++;
                }
            }

            // Nothing is held, so there is nothing to lose and no judgement to make. A fresh
            // server lands here.
            if (held == 0 || lost == 0) return;

            var limit = RistConfig.ReconcileMaxLoss == null ? 0.34f : RistConfig.ReconcileMaxLoss.Value;
            if (limit < 0f) limit = 0f;
            if (limit > 1f) limit = 1f;

            var loss = (float)lost / held;
            if (loss <= limit)
            {
                RistPlugin.Log.LogInfo("Reconcile: " + lost + " of " + held + " card ids in the "
                                       + "ledger are no longer in the catalogue; the picks spent "
                                       + "on them will be returned as players log in.");
                return;
            }

            Block(lost + " of the " + held + " card ids in the ledger are missing from the "
                  + "catalogue (" + (loss * 100f).ToString("0") + "%, over the "
                  + (limit * 100f).ToString("0") + "% allowed by ReconcileMaxLoss), which looks "
                  + "far more like a broken cards.txt than a deliberate edit");
        }

        private static void Block(string why)
        {
            _reconcileBlocked = true;
            _reconcileWhy = why;

            // LogError, and worded to be greppable, because the alternative to this noise is
            // a silent permanent delete of every player's cards.
            RistPlugin.Log.LogError("Rist is NOT reconciling the ledger: " + why + ". No picks "
                + "will be returned and no card history will be removed while this stands. Fix "
                + "cards.txt and restart, or raise ReconcileMaxLoss if the catalogue really did "
                + "shrink that much on purpose.");
        }

        /// <summary>
        /// Write if anything changed. On a timer rather than on every grant, because skill-ups
        /// can arrive several times a second and the file would be rewritten each time.
        /// </summary>
        internal static void Tick(float time)
        {
            if (!_dirty || time < _nextFlush) return;
            _nextFlush = time + 10f;
            Flush();
        }

        internal static void Flush()
        {
            if (!_dirty || !_loaded) return;

            if (_readFailed)
            {
                if (!_saidRefusingToWrite)
                {
                    _saidRefusingToWrite = true;
                    RistPlugin.Log.LogError("Refusing to write the ledger: it could not be read at "
                        + "startup, so what is in memory is not the whole file and saving it would "
                        + "delete every player who is not currently connected. Restart the server "
                        + "once " + LedgerPath + " is readable.");
                }

                return;
            }

            try
            {
                var lines = new List<string>(_records.Count);
                foreach (var rec in _records.Values) lines.Add(rec.Serialise());

                // Write beside and move into place, so a crash mid-write cannot leave every
                // player's progress truncated.
                var tmp = LedgerPath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(LedgerPath)) File.Delete(LedgerPath);
                File.Move(tmp, LedgerPath);

                _dirty = false;
            }
            catch (Exception e)
            {
                RistPlugin.Log.LogError("Could not write the ledger: " + e.Message);
            }
        }
    }
}
