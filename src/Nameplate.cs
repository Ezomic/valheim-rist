using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// A Rist line above other players' heads: level, total XP, and days since they last died.
    ///
    /// The HUD half of this is nearly free. EnemyHud rebuilds every plate's caption from
    /// Character.GetHoverName() on every frame of UpdateHuds, the caption is a TextMeshPro
    /// label, and TMP reads rich text - so one postfix on Player.GetHoverName buys the whole
    /// line, with per-field colour and a second line, without cloning a prefab or touching
    /// the plate's layout.
    ///
    /// The data half is the actual work, and it is why this file exists rather than being
    /// three lines in HudBar. Nothing about another player's Rist state is on the wire: Net
    /// pushes a record to the peer it belongs to and to nobody else, and the ledger the
    /// server keeps is never broadcast. So each client publishes its own three numbers onto
    /// its own character ZDO, which every client in range already replicates for free.
    ///
    /// The server cannot write them instead, however much it would prefer to own the numbers
    /// it is the authority on: a write to a ZDO you do not own is discarded in silence, and
    /// the character ZDO belongs to the client playing it. That means a modded client can lie
    /// about its own plate. It is worth saying out loud and it is worth accepting - the plate
    /// is decoration, the ledger is still the only thing that grants a pick, and a client that
    /// wanted to cheat would cheat at something that mattered.
    /// </summary>
    internal static class Nameplate
    {
        /// <summary>
        /// ZDO keys. These are hashed with GetStableHashCode and share a namespace with every
        /// vanilla key, so they carry the mod's name rather than something like "level".
        /// </summary>
        private const string KeyLevel = "rist_level";
        private const string KeyXp = "rist_xp";
        private const string KeyAliveSince = "rist_alive_since";

        /// <summary>
        /// The character-file key holding the world day of the last death, per world.
        ///
        /// Per world because the day counter is a property of the world, not of the character:
        /// one save at day 300 and a fresh one at day 4 would otherwise trade numbers and read
        /// as a character who has been alive for minus 296 days. m_customData is saved with
        /// the character rather than the world, so the key has to carry the world itself.
        /// </summary>
        private const string DataAliveSince = "rist.aliveSince.";

        /// <summary>
        /// True only while EnemyHud is composing a plate.
        ///
        /// GetHoverName is not the nameplate's method, it is the character's - the same string
        /// answers the crosshair, and anything else that ever asks a player its name. Widening
        /// all of that to a decorated three-field line is a change nobody asked for, so the
        /// postfix is gated on the one caller this feature is about. The alternative was
        /// patching UpdateHuds itself and writing the label directly, which means reaching
        /// into EnemyHud's private nested HudData by reflection every frame, for every player
        /// on screen, to arrive at the same string.
        /// </summary>
        internal static bool Composing;

        private static float _nextPublish;
        private static int _lastLevel = int.MinValue;
        private static int _lastXp = int.MinValue;
        private static int _lastSince = int.MinValue;

        /// <summary>
        /// Put the local character's three numbers on its own ZDO, at most once a second and
        /// only when one of them has moved.
        ///
        /// Driven from the plugin's Update rather than hooked to anything: there is no single
        /// moment when the player, the ZDO and EnvMan all exist, and every one of them is gone
        /// again between worlds. Cheap enough to ask every frame, and it early-outs on the
        /// clock before it touches the scene.
        /// </summary>
        internal static void Publish(Player player)
        {
            if (!RistConfig.ShowPlate.Value || player == null) return;
            if (Time.time < _nextPublish) return;
            _nextPublish = Time.time + 1f;

            if (!player.TryGetComponent<ZNetView>(out var nview)) return;
            if (!nview.IsValid() || !nview.IsOwner()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            var level = ClientState.Known ? ClientState.Level : 0;
            var xp = ClientState.Known ? Mathf.RoundToInt(ClientState.Xp) : 0;
            var since = AliveSince(player);

            // Level 0 is "the server has not told this client anything yet", and publishing it
            // would put (LVL 0) over a character's head for the first seconds of every login.
            if (level <= 0) return;

            if (level == _lastLevel && xp == _lastXp && since == _lastSince) return;

            zdo.Set(KeyLevel, level);
            zdo.Set(KeyXp, xp);
            zdo.Set(KeyAliveSince, since);

            _lastLevel = level;
            _lastXp = xp;
            _lastSince = since;
        }

        /// <summary>Forget what was published, so the next world republishes from scratch.</summary>
        internal static void Forget()
        {
            _lastLevel = int.MinValue;
            _lastXp = int.MinValue;
            _lastSince = int.MinValue;
        }

        /// <summary>
        /// The world day this character was last alive from, stamped into the character file.
        ///
        /// A character that has never carried the stamp gets today, not day zero. The game
        /// keeps no record of when a character was born - m_timeSinceDeath is real seconds of
        /// play, private, and never leaves the machine it is on - so the honest answer for an
        /// existing character is "counting from now", and every character in the world reads 0
        /// on the day this ships. Inventing a birthday from a save file's timestamp would put
        /// a number on the plate that means nothing.
        /// </summary>
        private static int AliveSince(Player player)
        {
            var today = Today();
            if (today < 0) return 0;

            var key = DataKey();
            if (key == null) return today;

            if (player.m_customData.TryGetValue(key, out var stored)
                && int.TryParse(stored, out var day))
                return Mathf.Clamp(day, 0, today);

            player.m_customData[key] = today.ToString();
            return today;
        }

        /// <summary>Restart the count. Called from the death patch, on the dying player.</summary>
        internal static void Died(Player player)
        {
            if (player == null) return;

            var today = Today();
            var key = DataKey();
            if (today < 0 || key == null) return;

            player.m_customData[key] = today.ToString();

            // Straight onto the wire rather than waiting for the next tick: a plate that still
            // reads 36 days over a fresh corpse is the one moment this number is being watched.
            _nextPublish = 0f;
            _lastSince = int.MinValue;
        }

        private static int Today()
        {
            return EnvMan.instance == null || ZNet.instance == null ? -1 : EnvMan.instance.GetDay();
        }

        private static string DataKey()
        {
            return ZNet.instance == null ? null : DataAliveSince + ZNet.instance.GetWorldUID();
        }

        /// <summary>
        /// The finished caption for one player's plate, or the plain name when this player has
        /// published nothing - an older client, or one still in its first seconds in the world.
        /// </summary>
        internal static string Decorate(Player player, string name)
        {
            if (!Composing || !RistConfig.ShowPlate.Value || player == null) return name;
            if (!player.TryGetComponent<ZNetView>(out var nview) || !nview.IsValid()) return name;

            var zdo = nview.GetZDO();
            if (zdo == null) return name;

            var level = zdo.GetInt(KeyLevel, 0);
            if (level <= 0) return name;

            var today = Today();
            var since = zdo.GetInt(KeyAliveSince, today);
            var days = today < 0 ? 0 : Mathf.Max(0, today - since);

            return RistConfig.PlateFormat.Value
                .Replace("\\n", "\n")
                .Replace("{lvl}", level.ToString())
                .Replace("{xp}", zdo.GetInt(KeyXp, 0).ToString())
                .Replace("{days}", days.ToString())
                .Replace("{name}", name);
        }
    }

    /// <summary>
    /// The plate's caption. Player overrides GetHoverName, so this needs no test for what kind
    /// of character it is standing on.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetHoverName))]
    internal static class NameplateText
    {
        private static void Postfix(Player __instance, ref string __result)
        {
            __result = Nameplate.Decorate(__instance, __result);
        }
    }

    /// <summary>
    /// Marks the window in which a name is being asked for by a nameplate rather than by
    /// anything else. See Nameplate.Composing.
    ///
    /// A finalizer rather than a postfix so the flag comes down even if UpdateHuds throws:
    /// left standing, it would decorate every other caller of GetHoverName from then on, and
    /// the cause would be a stack trace in Player.log that has nothing to do with names.
    /// Returning nothing from a finalizer leaves the original exception alone.
    /// </summary>
    [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
    internal static class NameplateScope
    {
        private static void Prefix()
        {
            Nameplate.Composing = true;
        }

        private static void Finalizer()
        {
            Nameplate.Composing = false;
        }
    }

    /// <summary>
    /// Restarts the days-alive count.
    ///
    /// Player.OnDeath runs on the owner alone, which is exactly right here - the character
    /// that died is the one holding the character file this is stamped into, and it is the one
    /// that owns the ZDO the plate is read from.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class NameplateDeath
    {
        private static void Postfix(Player __instance)
        {
            if (RistConfig.ShowPlate.Value) Nameplate.Died(__instance);
        }
    }
}
