using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// What this client has been told about the other characters on the server, for the page
    /// that shows their stones. Display only, and read only: nothing here is ever sent back,
    /// and the server never reads any of it, so a client has no way to change another
    /// character's ledger by way of this page.
    ///
    /// It is asked for when the page opens and answered once. Nothing is broadcast and nothing
    /// is stored for it: each answer line is the ledger's own text for one character (see
    /// RistRecord.TakenWire), so the page is a view of the file the server already keeps.
    /// </summary>
    internal static class Others
    {
        internal sealed class Peer
        {
            internal long CharacterId;
            internal bool Online;

            /// <summary>
            /// Empty when the server does not know one. The ledger holds no names and an offline
            /// character is not in the player list, so only a character that is connected has a
            /// name; the rest are shown by the last digits of their character id.
            /// </summary>
            internal string Name = "";

            internal int Level;
            internal readonly Dictionary<string, List<int>> Taken = new Dictionary<string, List<int>>();
            internal readonly Dictionary<string, int> Ranks = new Dictionary<string, int>();

            internal bool Named => Name.Length > 0;

            /// <summary>
            /// The tail of the character id an unnamed character is shown by. Four digits unless
            /// another unnamed one on the same list ends the same, then as many as it takes.
            /// </summary>
            internal string Suffix = "";

            internal string Label => Named ? Name : "Char " + Suffix;

            internal int RankOf(string id)
            {
                return id != null && Ranks.TryGetValue(id, out var r) ? r : 0;
            }

            internal List<int> LevelsOf(string id)
            {
                return id != null && Taken.TryGetValue(id, out var levels) ? levels : null;
            }
        }

        internal static readonly List<Peer> List = new List<Peer>();

        /// <summary>
        /// Bumped whenever the list is replaced, so the panel knows its tab strip has to be
        /// measured again.
        /// </summary>
        internal static int Version;

        /// <summary>The character being looked at; 0 is the viewer's own page.</summary>
        internal static long Selected;

        /// <summary>An answer has arrived since the world was entered.</summary>
        internal static bool Answered;

        internal static Peer Current
        {
            get
            {
                if (Selected == 0L) return null;

                foreach (var peer in List)
                    if (peer.CharacterId == Selected) return peer;

                return null;
            }
        }

        internal static bool AnyUnnamed
        {
            get
            {
                foreach (var peer in List)
                    if (!peer.Named) return true;

                return false;
            }
        }

        /// <summary>
        /// The last four digits of the id, sign dropped. Short enough for a tab, and enough to
        /// tell two characters apart on any server this will run on; the full id is in the log.
        /// </summary>
        internal static string Digits(long id, int length = 4)
        {
            var text = System.Math.Abs(id).ToString(CultureInfo.InvariantCulture);
            return text.Length <= length ? text : text.Substring(text.Length - length);
        }

        private static void AssignSuffixes()
        {
            for (var length = 4; length <= 20; length++)
            {
                var seen = new HashSet<string>();
                var clash = false;

                foreach (var peer in List)
                {
                    if (peer.Named) continue;

                    peer.Suffix = Digits(peer.CharacterId, length);
                    if (!seen.Add(peer.Suffix)) clash = true;
                }

                if (!clash) return;
            }
        }

        internal static void Clear()
        {
            List.Clear();
            Selected = 0L;
            Answered = false;
            _askedAt = -1f;
            Version++;
        }

        /// <summary>One line per character: id|online|name|level|cards, lines joined with a newline.</summary>
        internal static string ToLine(long characterId, bool online, string name, RistRecord rec)
        {
            return characterId.ToString(CultureInfo.InvariantCulture) + "|" + (online ? "1" : "0") + "|" +
                   Clean(name) + "|" + rec.Level.ToString(CultureInfo.InvariantCulture) + "|" + rec.TakenWire();
        }

        /// <summary>
        /// A name can hold anything a player typed, and both separators are used by the line.
        /// </summary>
        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("|", "_").Replace("\n", " ").Replace("\r", " ").Trim();
        }

        internal static void FromWire(string wire)
        {
            List.Clear();
            Answered = true;
            Version++;

            if (!string.IsNullOrEmpty(wire))
            {
                foreach (var line in wire.Split('\n'))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 5) continue;
                    if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id == 0L) continue;

                    var peer = new Peer { CharacterId = id, Online = parts[1] == "1", Name = parts[2] };
                    int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out peer.Level);

                    RistRecord.ParseTaken(parts[4], peer.Taken);
                    foreach (var kv in peer.Taken) peer.Ranks[kv.Key] = kv.Value.Count;

                    List.Add(peer);
                }
            }

            AssignSuffixes();

            // A page left on a character who is no longer listed goes back to the viewer's own.
            if (Selected != 0L && Current == null) Selected = 0L;
        }

        private const float PendingSeconds = 3f;
        private static float _askedAt = -1f;

        /// <summary>
        /// The first request of this world has gone out and nothing has come back yet. The panel
        /// reserves the room for the note while this is true, because whether anyone is listed
        /// is unknown until the answer, and a board that drops a rung when it arrives is a
        /// board that jumps in front of the player. Bounded, so a server that predates the
        /// request and never answers does not leave the board a rung smaller for good.
        /// </summary>
        internal static bool Pending
        {
            get { return !Answered && _askedAt >= 0f && Time.realtimeSinceStartup - _askedAt < PendingSeconds; }
        }

        internal static void Ask()
        {
            if (!Answered && !Pending) _askedAt = Time.realtimeSinceStartup;
            Net.AskOthers();
        }

        /// <summary>The counts the page and the console command both state.</summary>
        internal static void Counts(Peer peer, out int carved, out int marks)
        {
            carved = 0;
            marks = 0;

            foreach (var kv in peer.Ranks)
            {
                if (kv.Value <= 0 || Cards.Get(kv.Key) == null) continue;
                carved++;
                marks += Mathf.Min(kv.Value, Mathf.Max(1, RistConfig.MaxRank.Value));
            }
        }
    }
}
