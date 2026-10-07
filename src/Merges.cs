using System.Collections.Generic;
using System.Globalization;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Three stones that took in the idea next to them (LHM-53): Turned blade learned to parry what
    /// comes from over your shoulder, Brimming learned to spend less and to lend one more cast, and
    /// Answering blow learned to drive its victims back and to stand through its own secondary attack.
    ///
    /// Nothing a carver already had is taken away. Every rank's worth of the old effect is still
    /// there, and the new behaviour is added beside it: through Card.Companions where one catalogue
    /// line has to give two things, which is how Turned blade keeps its block stamina and Brimming
    /// keeps its extra regen on the capstone that now also does something new.
    /// </summary>
    internal static class Merges
    {
        internal const string ParryRear = "*parry:rear";
        internal const string EitrRegen = "*eitr:regen";
        internal const string EitrThrift = "*eitr:thrift";
        internal const string LastCast = "*eitr:lastcast";
        internal const string StaggerDealt = "*stagger:dealt";
        internal const string StaggerSecondary = "*stagger:secondary";

        private static bool Mine(Character character)
        {
            return character != null && RistConfig.Enabled.Value && ReferenceEquals(character, Player.m_localPlayer);
        }

        // ------------------------------------------------------------ Turned blade

        /// <summary>
        /// Turned blade's capstone: a parry covers a wider arc, reaching round behind you.
        ///
        /// Humanoid.BlockAttack opens with one test, and it is the whole of the arc: a hit whose
        /// direction of travel has a positive dot with your forward vector comes from the back half,
        /// and the block is refused. The 30 degrees is read as past the line of your shoulders, so the
        /// front 180 becomes 240: a hit is refused only when the dot with forward is above sin(30), which
        /// is 0.5. Which of the two readings of "30 degrees behind you" the ticket meant was not
        /// said, and a cone of 30 degrees straight behind would leave the shoulders open while
        /// covering the one place nobody is hit from.
        ///
        /// The 0 that test compares against is the one constant in it, so a transpiler swaps it for a
        /// call that answers per character: zero for everyone, and for the local player carrying the
        /// stone the sine of the arc. A prefix could not do this. Moving the hit's direction to
        /// satisfy the test would also move the stagger and the push that follow it. It runs on the
        /// owner's machine like the rest of BlockAttack, so it needs only the local hand.
        /// If the game changes that line, the transpiler finds nothing, says so, and the arc stays as
        /// vanilla rather than breaking the block.
        /// </summary>
        internal static class Parry
        {
            [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Arc(IEnumerable<CodeInstruction> code)
            {
                var dot = AccessTools.Method(typeof(Vector3), nameof(Vector3.Dot));
                var threshold = AccessTools.Method(typeof(Parry), nameof(Threshold));
                var list = new List<CodeInstruction>(code);

                for (var i = 0; i + 1 < list.Count; i++)
                {
                    if (!list[i].Calls(dot)) continue;

                    var next = list[i + 1];
                    if (next.opcode != OpCodes.Ldc_R4 || !(next.operand is float f) || f != 0f) continue;

                    var replaced = new CodeInstruction(OpCodes.Ldarg_0) { labels = next.labels };
                    list[i + 1] = replaced;
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Call, threshold));
                    return list;
                }

                RistPlugin.Log.LogError("Turned blade could not find the arc test in Humanoid.BlockAttack and is " +
                                        "off for this session: the parry arc stays as the game has it.");
                return list;
            }

            /// <summary>What the arc test compares against. Zero is vanilla, 0.5 is thirty degrees.</summary>
            internal static float Threshold(Humanoid human)
            {
                if (!Mine(human)) return 0f;

                var degrees = Effects.Cached(ParryRear);
                return degrees > 0f ? Mathf.Sin(Mathf.Min(degrees, 90f) * Mathf.Deg2Rad) : 0f;
            }
        }

        // ------------------------------------------------------------ Brimming

        /// <summary>
        /// Brimming's thrift: staff casts cost less eitr.
        ///
        /// Attack.GetAttackEitr(character, weapon) is the one place an attack's eitr price is
        /// worked out. The cast's start check, the per-burst check of a looping staff and the
        /// payment all go through it, so one postfix moves all of them together. A staff's drain per
        /// second while a hold is kept up is a different field and is not changed, which the README says.
        /// A cap of one half keeps a typo from making a cast free.
        /// The argument types on the patch are not optional: 1.0.17 has a private no-argument overload
        /// beside this one, and a patch naming only the method throws AmbiguousMatchException and takes
        /// the class with it. That overload just calls this one, so nothing is applied twice.
        /// </summary>
        internal static class Thrift
        {
            private const float Cap = 0.5f;

            [HarmonyPatch(typeof(Attack), nameof(Attack.GetAttackEitr), typeof(Character), typeof(ItemDrop.ItemData))]
            [HarmonyPostfix]
            private static void Cheaper(Character character, ref float __result)
            {
                if (__result <= 0f || !Mine(character)) return;

                var cut = Mathf.Clamp(Effects.Cached(EitrThrift), 0f, Cap);
                if (cut > 0f) __result *= 1f - cut;
            }
        }

        /// <summary>
        /// Brimming's capstone: one cast on an empty bar, once per refill.
        ///
        /// Character.TryUseEitr is the gate Attack.Start asks before a cast may begin, and it says no
        /// when the bar cannot cover the price. This turns one such no into a yes and records that it
        /// did; the cast then pays what is left and the bar floors at zero, as every overdrawn payment
        /// does. It is ready again when the bar is back to full, which is what "per refill" means, not
        /// when it has merely regained enough for the next cast.
        ///
        /// A staff that charges per burst (m_perBurstResourceUsage) is not lent a cast: the game
        /// re-checks the bar for every burst and would stop the attack on an empty one. The lend is
        /// recorded only when Attack.Start succeeds, since HaveAmmo and others run after the gate.
        ///
        /// "Empty" is read as "cannot afford it", not as exactly zero: a bar with a few points in it
        /// that cannot pay for a cast is empty for the purposes of the cast. Only magic weapons use
        /// it, because TryUseEitr is also called for a reload drain, and a bow's reload must not spend
        /// it. A character with no eitr pool at all is left to the game's own message.
        /// </summary>
        internal static class Last
        {
            private static bool _ready = true;
            private static Player _for;
            private static Attack _starting;
            private static bool _lent;

            [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
            [HarmonyPrefix]
            private static void Begin(Attack __instance)
            {
                _starting = __instance;
                _lent = false;
            }

            // Attack.Start asks TryUseEitr before HaveAmmo and the other refusals, so the lend is
            // only recorded here, once the attack really began. A cast that is lent and then
            // refused later costs nothing and is lent again.
            [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
            [HarmonyPostfix]
            private static void End(bool __result)
            {
                if (_lent && __result)
                {
                    _ready = false;
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Brimming lends one more cast");
                }
            }

            [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
            [HarmonyFinalizer]
            private static void Done()
            {
                _starting = null;
                _lent = false;
            }

            [HarmonyPatch(typeof(Character), nameof(Character.TryUseEitr))]
            [HarmonyPostfix]
            private static void Lend(Character __instance, float eitrUse, ref bool __result)
            {
                if (__result || eitrUse <= 0f || _starting == null || !Mine(__instance)) return;
                if (Effects.Cached(LastCast) <= 0f) return;

                // A staff that pays per burst skips the payment in Attack.Update and asks HaveEitr
                // again for every burst, which answers no on an empty bar and stops the attack. A
                // lent cast there would be spent for nothing, so it is not lent.
                if (_starting.m_attackType == Attack.AttackType.Projectile && _starting.m_perBurstResourceUsage) return;

                var player = __instance as Player;
                if (player == null || player.GetMaxEitr() <= 0f) return;

                Sync(player);
                if (!_ready || !IsMagic(player)) return;

                _lent = true;
                __result = true;
            }

            private static bool IsMagic(Player player)
            {
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || weapon.m_shared == null) return false;

                var skill = weapon.m_shared.m_skillType;
                return skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic;
            }

            private static void Sync(Player player)
            {
                if (ReferenceEquals(player, _for)) return;

                _for = player;
                _ready = true;
            }

            /// <summary>Ready again once the bar is full. From the plugin's Update.</summary>
            internal static void Tick(Player player)
            {
                if (player == null) return;

                Sync(player);
                if (!_ready && player.GetEitr() >= player.GetMaxEitr() - 0.05f) _ready = true;
            }

            internal static bool Ready => _ready;
        }

        // ------------------------------------------------------------ Answering blow

        /// <summary>
        /// Answering blow's drive: a melee hit of yours staggers harder.
        ///
        /// HitData carries m_staggerMultiplier to the victim, and Character.RPC_Damage turns it
        /// into stagger by multiplying the hit's own stagger damage. It is scaled on the
        /// attacker's side in a prefix on Character.Damage, which is where Answering blow itself
        /// edits a hit, so a hit that the answering blow sends with a certain stagger (100) is not
        /// reduced by this and a hit that is not answered is merely heavier. Melee is a hit that is
        /// not ranged: a staff's area and an arrow both set m_ranged.
        /// </summary>
        internal static class Reeling
        {
            [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
            [HarmonyPrefix]
            private static void Heavier(Character __instance, HitData hit)
            {
                if (hit == null || __instance == null || hit.m_ranged) return;
                if (hit.m_hitType != HitData.HitType.PlayerHit) return;

                var player = Player.m_localPlayer;
                if (player == null || !ReferenceEquals(hit.GetAttacker(), player)) return;
                if (!BaseAI.IsEnemy(player, __instance)) return;

                var factor = Factor();
                if (factor > 1f) hit.m_staggerMultiplier *= factor;
            }

            internal static float Factor()
            {
                if (!RistConfig.Enabled.Value) return 1f;

                var bonus = Effects.Cached(StaggerDealt);
                return bonus > 0f ? 1f + bonus : 1f;
            }
        }

        /// <summary>
        /// Answering blow's capstone guard: nothing staggers you during your own secondary attack.
        ///
        /// Refused in RPC_Stagger, the same place Quick chant's guard is, because the owner's own
        /// Stagger calls it directly and a remote attacker's arrives through it. The stagger bar still
        /// fills and flashes on a hit, as it does for every hit, and a stagger that lands after the
        /// attack is over is not refused. Humanoid keeps whether the attack in hand is the secondary
        /// one in a protected field, read once per stagger and never written.
        /// </summary>
        internal static class Secondary
        {
            private static AccessTools.FieldRef<Humanoid, bool> _secondary;
            private static bool _bound, _bindFailed;

            private static bool Bind()
            {
                if (_bound) return true;
                if (_bindFailed) return false;

                try
                {
                    _secondary = AccessTools.FieldRefAccess<Humanoid, bool>("m_currentAttackIsSecondary");
                    _bound = true;
                }
                catch (System.Exception e)
                {
                    _bindFailed = true;
                    RistPlugin.Log.LogError("Answering blow's secondary-attack guard could not reach the game's " +
                                            "attack field and is off for this session: " + e.Message);
                }

                return _bound;
            }

            [HarmonyPatch(typeof(Character), "RPC_Stagger")]
            [HarmonyPrefix]
            private static bool Staggering(Character __instance)
            {
                var player = __instance as Player;
                if (player == null || !Mine(player) || !player.InAttack() || !Bind()) return true;
                if (!_secondary(player)) return true;

                return !(Effects.Cached(StaggerSecondary) > 0f);
            }

            internal static bool Active => Effects.Cached(StaggerSecondary) > 0f;
        }

        // ------------------------------------------------------------ probe

        /// <summary>
        /// What `rist show` prints for the three stones. The eitr line works the game's own price
        /// for the first staff in ObjectDB twice, once as it stands and once with a stand-in for
        /// nobody, so the ratio is the thrift and nothing else. The stagger line runs a made-up melee
        /// hit through the same method the patch calls.
        /// </summary>
        internal static string Probe(Player player)
        {
            var arc = Effects.Cached(ParryRear);
            var line = "parry reaches " + arc.ToString("0", CultureInfo.InvariantCulture) + " degrees behind you (threshold "
                       + Parry.Threshold(player).ToString("0.00", CultureInfo.InvariantCulture) + ")";

            line += "  melee stagger x" + Reeling.Factor().ToString("0.00", CultureInfo.InvariantCulture)
                    + "  secondary attack guard " + (Secondary.Active ? "on" : "off");

            line += "  thrift=" + (RistPlugin.Applied.TryGetValue("Merges.Thrift", out var thrift) && thrift ? "applied" : "missing");
            line += "  last cast " + (Effects.Cached(LastCast) > 0f ? (Last.Ready ? "ready" : "spent") : "not carved");

            ItemDrop.ItemData staff = null;
            if (ObjectDB.instance != null)
                foreach (var prefab in ObjectDB.instance.m_items)
                {
                    if (prefab == null || !prefab.TryGetComponent<ItemDrop>(out var drop)) continue;

                    var shared = drop.m_itemData.m_shared;
                    if (shared == null || shared.m_attack == null || shared.m_attack.m_attackEitr <= 0f) continue;
                    if (shared.m_skillType != Skills.SkillType.ElementalMagic) continue;

                    staff = drop.m_itemData;
                    break;
                }

            if (staff == null) return line + "  eitr cost: no staff in ObjectDB";

            var attack = new Attack();
            var now = attack.GetAttackEitr(player, staff);
            var bare = staff.m_shared.m_attack.m_attackEitr
                       - staff.m_shared.m_attack.m_attackEitr * 0.33f * player.GetSkillFactor(staff.m_shared.m_skillType);

            return line + "  eitr cost x" + (bare > 0f ? now / bare : 1f).ToString("0.00", CultureInfo.InvariantCulture)
                   + " (" + staff.m_shared.m_name.TrimStart('$') + ")";
        }
    }
}
