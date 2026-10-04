using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The second XP source: how many of each KIND of creature a character has killed (LHM-40).
    ///
    /// The count is the game's own, PlayerProfile's per-name kill counter, which is
    /// m_playerStats[0].m_enemyStats[0] and is keyed by Character.m_name ("$enemy_boar"). It is
    /// the lifetime set, so a character that earned its kills before Rist was installed is paid
    /// for them on its first login, the way CreditExistingSkills pays for existing skills.
    ///
    /// Nothing here counts anything. The client reads the dictionary the game keeps and reports
    /// the entries that changed; the server turns each count into milestones reached, prices them
    /// from the creature's own prefab, and stores the highest count it has seen per kind.
    /// Everything it pays is derived from that stored table, so it is idempotent: reporting the
    /// same count twice, or a lower one, pays nothing.
    ///
    /// **A kill is a claim**, like a skill-up, and is bounded the same way: the server ignores
    /// any name it cannot resolve to a creature prefab, and the most one kind can ever pay is
    /// (milestones) x KillXp x its weight, so a forged report is worth at most the whole
    /// bestiary once. There is no rate to limit because there is no rate to exploit.
    /// </summary>
    internal static class Kills
    {
        // ---- the milestones --------------------------------------------------------------

        private static int[] _marks = new int[0];
        private static string _marksFrom;

        /// <summary>The configured milestone counts, ascending and de-duplicated.</summary>
        internal static int[] Marks()
        {
            var raw = RistConfig.KillMilestones.Value ?? "";
            if (raw == _marksFrom) return _marks;

            _marksFrom = raw;
            var list = new List<int>();
            foreach (var part in raw.Split(','))
            {
                int n;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0 && !list.Contains(n))
                    list.Add(n);
            }

            list.Sort();
            _marks = list.ToArray();
            return _marks;
        }

        /// <summary>How many milestones a kill count has reached.</summary>
        internal static int Reached(float count)
        {
            var n = 0;
            foreach (var m in Marks())
            {
                if (count >= m) n++;
                else break;
            }

            return n;
        }

        /// <summary>The next milestone above a count, or 0 when every one has been reached.</summary>
        internal static int Next(float count)
        {
            foreach (var m in Marks())
                if (count < m) return m;

            return 0;
        }

        /// <summary>The panel heading's share from kills, or nothing before any has paid.</summary>
        internal static string Note()
        {
            return ClientState.KillXp < 0.5f ? "" : " (" + Mathf.RoundToInt(ClientState.KillXp).ToString("N0", CultureInfo.InvariantCulture) + " from kills)";
        }

        // ---- the price of a kind ---------------------------------------------------------

        private static readonly Dictionary<string, float> _health = new Dictionary<string, float>();
        private static ZNetScene _builtFor;

        /// <summary>
        /// Whether creature health can be read at all right now. False before a world's
        /// ZNetScene exists, and while false nothing may be priced: an unresolved name pays
        /// nothing, so pricing against an empty map would read as "this character has no kill
        /// XP" and a re-price would take it away.
        /// </summary>
        internal static bool Ready
        {
            get { Build(); return _health.Count > 0; }
        }

        private static void Build()
        {
            var scene = ZNetScene.instance;
            if (scene == null) { _builtFor = null; _health.Clear(); return; }
            if (ReferenceEquals(scene, _builtFor)) return;

            _builtFor = scene;
            _health.Clear();

            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;

                Character c;
                if (!prefab.TryGetComponent(out c)) continue;
                if (c is Player || string.IsNullOrEmpty(c.m_name)) continue;

                // Several prefabs share a name (a creature and its variants). The toughest
                // stands for the kind, because the counter cannot say which one died.
                float seen;
                if (!_health.TryGetValue(c.m_name, out seen) || c.m_health > seen)
                    _health[c.m_name] = c.m_health;
            }
        }

        /// <summary>The weight of a kind, or 0 when no creature of that name exists.</summary>
        internal static float WeightOf(string name)
        {
            Build();

            float health;
            if (name == null || !_health.TryGetValue(name, out health)) return 0f;

            var unit = Mathf.Max(1f, RistConfig.KillHealthUnit.Value);
            var lo = Mathf.Max(0f, RistConfig.KillWeightMin.Value);
            var hi = Mathf.Max(lo, RistConfig.KillWeightMax.Value);
            return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, health) / unit), lo, hi);
        }

        /// <summary>What one kind is worth at a kill count.</summary>
        internal static float WorthOf(string name, float count)
        {
            var xp = RistConfig.KillXp.Value;
            if (xp <= 0f) return 0f;

            return Reached(count) * xp * WeightOf(name);
        }

        /// <summary>What a whole table of counts is worth.</summary>
        internal static float WorthOf(Dictionary<string, int> counts)
        {
            if (counts == null) return 0f;

            var total = 0f;
            foreach (var kv in counts) total += WorthOf(kv.Key, kv.Value);
            return total;
        }

        // ---- client: report what changed ---------------------------------------------------

        private const float ReportEvery = 5f;
        private const int MaxKinds = 300;

        private static readonly Dictionary<string, int> _sent = new Dictionary<string, int>();
        private static float _next;

        /// <summary>A new connection starts again: the server has not heard any of it.</summary>
        internal static void Forget()
        {
            _sent.Clear();
            _next = 0f;
        }

        /// <summary>The character's own per-kind counter, or null before there is one.</summary>
        internal static Dictionary<string, float> Profile()
        {
            var game = Game.instance;
            var profile = game == null ? null : game.GetPlayerProfile();
            if (profile == null || profile.m_playerStats == null || profile.m_playerStats.Length == 0) return null;

            var stats = profile.m_playerStats[0];
            if (stats == null || stats.m_enemyStats == null || stats.m_enemyStats.Length == 0) return null;

            return stats.m_enemyStats[0];
        }

        /// <summary>
        /// Send the kinds whose count moved since the last send. Only after the server has
        /// answered the hello, because until then it cannot attribute a report to a character
        /// and would drop it, and a dropped diff is never sent again.
        /// </summary>
        internal static void Tick(float time)
        {
            if (time < _next || !ClientState.Known) return;
            _next = time + ReportEvery;

            Report(false);
        }

        /// <summary>Report now. <paramref name="all"/> sends every kind, for the console command.</summary>
        internal static bool Report(bool all)
        {
            var counts = Profile();
            if (counts == null || ZRoutedRpc.instance == null) return false;

            var sb = new StringBuilder();
            var kinds = 0;
            foreach (var kv in counts)
            {
                if (kinds >= MaxKinds) break;
                if (string.IsNullOrEmpty(kv.Key) || kv.Value < 1f) continue;
                if (kv.Key.IndexOfAny(new[] { ',', ':', '|' }) >= 0) continue;

                var n = (int)Mathf.Min(kv.Value, 1000000f);
                int before;
                if (!all && _sent.TryGetValue(kv.Key, out before) && before == n) continue;

                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key).Append(':').Append(n.ToString(CultureInfo.InvariantCulture));
                _sent[kv.Key] = n;
                kinds++;
            }

            if (sb.Length == 0) return false;

            Net.ReportKills(sb.ToString());

            if (RistConfig.Verbose.Value)
                RistPlugin.Log.LogInfo("Reported kills of " + kinds + " kind(s).");

            return true;
        }

        // ---- server: take a report ---------------------------------------------------------

        private const int MaxWire = 16 * 1024;

        /// <summary>
        /// Fold a report into the record. Returns true when it paid, so the caller can push the
        /// new standing. Stored counts only ever rise, and the pay is re-derived from the whole
        /// stored table rather than added up per report, which is what keeps it from paying a
        /// repeat twice.
        /// </summary>
        internal static bool Apply(string owner, RistRecord rec, string wire)
        {
            if (rec == null || string.IsNullOrEmpty(wire) || wire.Length > MaxWire) return false;
            if (RistConfig.KillXp.Value <= 0f || !Ready) return false;

            var touched = false;
            foreach (var pair in wire.Split(','))
            {
                var at = pair.LastIndexOf(':');
                if (at <= 0) continue;

                var name = pair.Substring(0, at);
                int count;
                if (!int.TryParse(pair.Substring(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1)
                    continue;

                if (WeightOf(name) <= 0f)
                {
                    if (RistConfig.Verbose.Value)
                        RistPlugin.Log.LogInfo("Kill report from " + owner + " names '" + name + "', which is no creature here; ignored.");
                    continue;
                }

                int seen;
                if (rec.KillSeen.TryGetValue(name, out seen) && count <= seen) continue;

                rec.KillSeen[name] = count;
                touched = true;
            }

            if (!touched) return false;
            Ledger.Touch();

            var worth = WorthOf(rec.KillSeen);
            var delta = worth - rec.KillXp;
            if (delta <= 0.001f) return false;

            var before = rec.Level;
            rec.KillXp = worth;
            rec.Xp += delta;

            RistPlugin.Log.LogInfo(owner + " +" + delta.ToString("0.#") + " xp from kills (" + rec.KillSeen.Count
                                   + " kinds, " + worth.ToString("0.#") + " in all) -> " + rec.Xp.ToString("0.#"));

            if (rec.Level > before)
                RistPlugin.Log.LogInfo(owner + " reached Rist level " + rec.Level + ".");

            return true;
        }

        /// <summary>
        /// Re-derive a record's kill XP under the config standing now, for the re-price. Only
        /// when creatures can be priced, so a world still loading cannot zero it.
        /// </summary>
        internal static void Reprice(RistRecord rec)
        {
            if (rec != null && Ready) rec.KillXp = WorthOf(rec.KillSeen);
        }
    }
}
