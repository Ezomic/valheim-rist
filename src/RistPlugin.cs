using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Ezomic.Core;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    // Soft, not hard. Rist installs and runs on its own; a hard dependency that is absent does
    // not degrade, the plugin simply never loads. Soft still buys the load-order guarantee when
    // Core is present, which is what registering needs. What standalone costs is set out in
    // TryRegisterWithCore, and it is more here than for most of the suite.
    [BepInDependency(CoreGuid, BepInDependency.DependencyFlags.SoftDependency)]
    // No BepInProcess. It used to say valheim.exe, which is a whitelist - and a dedicated
    // server runs valheim_server.exe, so the entire server half of this mod would never have
    // loaded there. The ledger, the gate and every authority decision live on the server; on a
    // dedicated host none of them would have existed and clients would have reported skill-ups
    // into nothing. Core and Utangard were both bitten by exactly this.
    public class RistPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ezomic.valheim.rist";
        public const string PluginName = "Rist";
        public const string PluginVersion = "1.2.2";
        public const string PluginAuthor = "Robbin Thijssen";

        /// <summary>Core's plugin GUID. Optional - see TryRegisterWithCore.</summary>
        internal const string CoreGuid = "ezomic.valheim.core";

        /// <summary>
        /// Whether Core answered at load. Read by Effects, which claims rows through Core when
        /// it is here and through Rist's own owner when it is not, and by Cards, which can only
        /// declare the catalogue to a gate that exists.
        /// </summary>
        internal static bool CorePresent;

        internal static ManualLogSource Log;

        private Harmony _harmony;

        private bool _saidHello;

        private void Awake()
        {
            Log = Logger;
            RistConfig.Bind(Config);
            TryRegisterWithCore();

            Cards.Load();

            _harmony = new Harmony(PluginGuid);

            // OwnInventoryRows goes first, ahead of every cosmetic and gameplay patch, and the
            // order is load-bearing. Its Player.Load prefix is the only thing standing between
            // a claimed row and the game deleting what is standing in it, and a PatchAll that
            // throws takes every patch after it with it. Patched last - as this was until
            // today - a failure anywhere above left the rows still being granted from Update
            // with nothing protecting them, which is the bug in its purest form: the feature
            // still works, and what is standing in the extra rows is silently at risk. Found
            // by the pre-1.0 audit, in this mod and in Core's InventoryRows together.
            //
            // Patched in only when Core is absent, so the two row owners can never both write
            // Inventory.m_height. Applying it unconditionally would mean two Player.Load
            // prefixes each widening the grid and each capturing the other's widened value as
            // the vanilla baseline, which is precisely the compounding both guard against.
            if (!CorePresent)
            {
                Patch(typeof(OwnInventoryRows));

                // Asked of Harmony rather than assumed from PatchAll returning: the tick will
                // not grant a single row until this says the guard is really attached.
                OwnInventoryRows.ConfirmLoadGuard(PluginGuid);
            }

            Patch(typeof(SkillWatch));
            Patch(typeof(DeathPenalty));
            Patch(typeof(UiInput));
            Patch(typeof(AttackSpeed));

            // Not "ready" when the catalogue never loaded. Rist with no cards is not a quieter
            // Rist - picks cannot be spent, and on a server the reconcile pass would read the
            // empty catalogue as "every card was deleted". The line a person greps for has to
            // say so rather than reporting a clean start.
            if (Cards.Unavailable)
                Log.LogError(PluginName + " " + PluginVersion + " by " + PluginAuthor
                    + " - NOT READY: the card catalogue is empty or unreadable. No card can be "
                    + "taken, and no card history will be reconciled until cards.txt is fixed "
                    + "and the game restarted.");
            else
                Log.LogInfo(PluginName + " " + PluginVersion + " by " + PluginAuthor + " - ready.");
        }

        /// <summary>
        /// One class's patches, applied so that a failure costs that class and nothing else.
        ///
        /// PatchAll throws a HarmonyException when a target method has moved, gained an
        /// overload or changed signature - which is exactly what a game update does, and
        /// Valheim 1.0 landed today. Uncaught, the first one aborts Awake: every class after
        /// it goes unpatched, and Rist is left half-applied with no line saying which half.
        /// Naming the class here means the log answers "what stopped working" directly.
        /// </summary>
        private void Patch(System.Type type)
        {
            try
            {
                _harmony.PatchAll(type);
            }
            catch (System.Exception e)
            {
                Log.LogError("Rist could not patch " + type.Name + " - that part of the mod is "
                    + "off for this session, the rest continues. " + e);
            }
        }

        /// <summary>
        /// Joins Core's version gate when Core is installed, and does without it when not.
        ///
        /// Rist gives up more than the rest of the suite does standing alone, so it is worth
        /// being exact about what.
        ///
        /// **The catalogue is no longer checked.** cards.txt names what every rank is worth,
        /// effects are applied client-side from it, and the server only ever checks the rank -
        /// so an edited line is simply believed. Suite.Data hands Core a hash of the file so
        /// the gate can report two ends running the same build over different catalogues.
        /// Without Core there is nothing to compare it against, and a client with an edited
        /// catalogue gets whatever it wrote. **Singleplayer is unaffected; on a server this is
        /// the difference between a curve the host decides and one a client can claim.**
        ///
        /// **The host's curve is no longer forced.** Suite.Sync is what stops a client with a
        /// different LevelBaseXp reading a different level out of the same xp. Standalone, the
        /// cfg files have to be matched by hand.
        ///
        /// **Extra inventory rows are claimed without an arbiter.** See OwnInventoryRows: it
        /// does the whole job correctly for Rist alone, and cannot know about a second mod
        /// wanting rows from the same private int.
        ///
        /// None of that is a reason to refuse to run - a solo player needs none of it, and
        /// someone who wants only Rist should be able to have only Rist. It is a reason to say
        /// so plainly, here and in the README.
        /// </summary>
        private void TryRegisterWithCore()
        {
            CorePresent = Chainloader.PluginInfos.ContainsKey(CoreGuid);

            if (!CorePresent)
            {
                Log.LogWarning("Core not installed - running standalone. The version gate, the "
                    + "cards.txt check and the host-authoritative curve are all unavailable, and "
                    + "extra inventory rows are claimed without an arbiter. Fine solo; on a "
                    + "server, install Core.");
                return;
            }

            RegisterWithCore();
        }

        /// <summary>
        /// Kept separate and never inlined on purpose. The JIT resolves the assemblies a method
        /// needs when it first compiles that method, so a Suite call sitting directly in Awake
        /// would drag Ezomic.Core in before the check above could prevent it - and the
        /// missing-assembly exception would land during plugin load, which is the failure this
        /// whole arrangement exists to avoid.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RegisterWithCore()
        {
            // Everyone, not HostOnly. Both ends have to agree about this mod, and the
            // disagreement is silent when they do not: a client that cannot resolve a prefab
            // hash discards the ZDO rather than erroring - destroying what is already standing
            // in the world - and item data that differs desyncs inventories.
            Suite.Register(PluginGuid, PluginName, PluginVersion, Config);

            // The host's curve is the one that counts. Without this a client with a different
            // LevelBaseXp reads a different level out of the same xp, and every number on its
            // screen disagrees with the server that decides them - which is how a cheapened
            // test curve on one machine made a dedicated server refuse every pick.
            Suite.Sync(RistConfig.XpPerSkillLevel, RistConfig.LevelBaseXp, RistConfig.LevelExponent,
                       RistConfig.MaxRank, RistConfig.BonusEvery,
                       RistConfig.SkillWeights, RistConfig.DefaultSkillWeight);

            // WeightGeneration is deliberately not in that list. It is not a shared rule about
            // what things are worth, it is a server-side instruction to re-price the ledger
            // once - and the ledger only exists on the server. Syncing it would push a stamp
            // to clients that have nothing to stamp.
        }

        private void OnDestroy()
        {
            Ledger.Flush();
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        private void Update()
        {
            // ZRoutedRpc does not exist at load and comes and goes with each session, so
            // registration is retried from here and is cheap once it has taken.
            Net.EnsureRegistered();

            // Core drives its own from CorePlugin.Update, so this is only ever the standalone
            // path. Both ticks are early-outs once settled, and the backdrop has to be driven
            // separately because it follows the GUI's lifetime rather than the player's.
            if (!CorePresent)
            {
                OwnInventoryRows.Tick();
                OwnInventoryRows.Backdrop.Tick();
            }

            if (!RistConfig.Enabled.Value) return;

            if (Net.IsServer) Ledger.Tick(Time.time);

            var player = Player.m_localPlayer;
            if (player == null)
            {
                // Left the world; the next one starts the introductions again.
                _saidHello = false;
                return;
            }

            SayHello();

            if (ClientState.Known) Effects.Apply(player, ClientState.Ranks);

            // The cloned bar lives in the HUD canvas rather than in OnGUI, so it is driven
            // from here. It rebuilds itself whenever the Hud is, which is once per world.
            HudBar.Update();

            // The compendium bar needs its fifth tab put back whenever the inventory window
            // is rebuilt. The extra rows and the backdrop behind them are Core's, because two
            // mods claiming rows must not each write the same private int.
            InfoTab.Update();
        }

        private void OnGUI()
        {
            XpBar.Draw();
            RistPanel.Draw();
        }

        /// <summary>
        /// Name the character being played, once per session.
        ///
        /// This is what the server keys the ledger on, together with the platform identity it
        /// reads off the socket itself. Sending it as a routed RPC rather than special-casing
        /// the host means singleplayer takes the identical path: a message addressed to
        /// yourself is handled locally, so the host introduces itself to itself.
        /// </summary>
        private void SayHello()
        {
            if (_saidHello || ZRoutedRpc.instance == null || Game.instance == null) return;

            var profile = Game.instance.GetPlayerProfile();
            if (profile == null) return;

            var id = PlayerIdOf(profile);
            if (id == 0L) return;

            _saidHello = true;
            Net.SayHello(id);
        }

        /// <summary>
        /// The character's unique id, which is half of the ledger key.
        ///
        /// Kept here rather than shared, now that the character-protection code this used to
        /// sit beside has moved to Dyrr. It is four lines of reflection; a dependency
        /// between two unrelated mods would cost more than the duplication does.
        ///
        /// m_playerID rather than the profile's filename: the filename is the character's
        /// name, which is reused the moment a character is deleted and another made with the
        /// same name, and keying on it once gave a brand new character an older one's record.
        /// </summary>
        private static long PlayerIdOf(PlayerProfile profile)
        {
            if (profile == null) return 0L;

            if (_playerId == null) _playerId = AccessTools.Field(typeof(PlayerProfile), "m_playerID");
            if (_playerId == null)
            {
                Log.LogError("PlayerProfile.m_playerID not found - Rist cannot identify characters.");
                return 0L;
            }

            return _playerId.GetValue(profile) is long id ? id : 0L;
        }

        private static System.Reflection.FieldInfo _playerId;

    }
}
