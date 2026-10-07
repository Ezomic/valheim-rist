namespace Rist
{
    /// <summary>
    /// What a creature's or a ship's owner needs to know about the player it is dealing with.
    ///
    /// Two capstones change what the world does about you, and the world's logic runs on whichever client
    /// owns the thing: a creature's AI on the machine that owns the creature, a ship's physics on the
    /// machine that owns the ship. That machine holds its own player's runestones and nobody else's, so
    /// the carver publishes one flag word on their own player's ZDO, which every client can read, and
    /// the owner's patch reads it. Written once for both, the way Blood-sworn's extra summon is
    /// published, since the shape is the same. Every client runs Rist behind Core's gate, so the patch
    /// that reads the word is present on every machine that could own the creature.
    ///
    /// A bit set means the capstone is carved, not that it is doing anything: each reader still applies its
    /// own conditions. Written only when the word changes, and only by the owner of the player.
    /// </summary>
    internal static class Carried
    {
        internal const string ZdoKey = "rist_carried";

        internal const int LoseThem = 1;
        // 2 was Sleepers sleep on, whose capstone is gone. The bit values are kept so a mixed pair of builds still reads the others right.
        internal const int RidesTheWaves = 4;

        internal const string LoseThemKey = "*stealth:losethem";
        internal const string WavesKey = "*sail:waves";

        private static Player _publishedTo;
        private static int _published = -1;

        private static int Want()
        {
            var word = 0;
            if (Effects.Cached(LoseThemKey) > 0f) word |= LoseThem;
            if (Effects.Cached(WavesKey) > 0f) word |= RidesTheWaves;
            return word;
        }

        /// <summary>From the plugin's Update. Not behind the plate setting: this decides what creatures do.</summary>
        internal static void Publish(Player player)
        {
            if (player == null) return;

            if (!ReferenceEquals(player, _publishedTo))
            {
                _publishedTo = player;
                _published = -1;
            }

            var want = Want();
            if (want == _published) return;

            if (!player.TryGetComponent<ZNetView>(out var nview) || !nview.IsValid() || !nview.IsOwner()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            zdo.Set(ZdoKey, want);
            _published = want;
        }

        internal static void Forget()
        {
            _publishedTo = null;
            _published = -1;
        }

        /// <summary>What a player has published, read off their ZDO: the word any client would read.</summary>
        internal static bool Has(Player player, int bit)
        {
            if (player == null || !player.TryGetComponent<ZNetView>(out var nview) || !nview.IsValid()) return false;

            var zdo = nview.GetZDO();
            return zdo != null && (zdo.GetInt(ZdoKey, 0) & bit) != 0;
        }

        internal static string Probe(Player player)
        {
            return "carried: lose them " + (Has(player, LoseThem) ? "yes" : "no")
                   + ", rides the waves " + (Has(player, RidesTheWaves) ? "yes" : "no");
        }
    }
}
