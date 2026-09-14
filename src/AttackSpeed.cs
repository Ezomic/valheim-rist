using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The four attack-speed cards, split by what is in your hands.
    ///
    /// Two things live here that are not animator speed: Quick draw's crossbow reload, which is a
    /// minor action timed by GetWeaponLoadingTime, and Quick chant's capstone, a stagger guard.
    /// Both are halves of cards whose other half is the speed below.
    ///
    /// This is the first thing in Rist that patches a gameplay path rather than riding a stat
    /// the game already sums, and it is here because there is no other way: SE_Stats has no
    /// attack-speed field and SEMan has no ModifyAttackSpeed among its twenty-two hooks. Swing
    /// speed is the animator's speed, full stop.
    ///
    /// What makes it cheap anyway is that vanilla already owns that number and already puts it
    /// back. CharacterAnimEvent.CustomFixedUpdate runs
    ///
    ///     if (!InAttack() &amp;&amp; !InMinorAction() &amp;&amp; !InEmote() &amp;&amp; CanMove())
    ///         m_animator.speed = 1f;
    ///
    /// every fixed update, so a raised speed is reset the moment the swing ends. There is no
    /// restore to write and no timer to leak. Two more things fall out of the same design:
    /// ZSyncAnimation writes m_animator.speed into the ZDO on the owner and reads it on
    /// remotes, so other players see the faster swing without any network code of ours; and
    /// FreezeFrame captures the current speed into m_pauseSpeed before the hit-pause and
    /// restores it after, so the hit-stop still works.
    ///
    /// The speed is set through CharacterAnimEvent.Speed, which is public and does exactly
    /// this - no reflection, and the one seam the game itself offers.
    /// </summary>
    internal static class AttackSpeed
    {
        internal const string Melee = "*attackspeed:melee";
        internal const string Tools = "*attackspeed:tools";
        internal const string Ranged = "*attackspeed:ranged";
        internal const string Magic = "*attackspeed:magic";

        /// <summary>Quick chant's capstone: a flag, not an amount. See Unbroken.</summary>
        internal const string UnbrokenCast = "*unbroken";

        /// <summary>
        /// Which card, if any, covers the thing being swung.
        ///
        /// Read off m_skillType rather than the item type, because that is what the game
        /// itself dispatches on and it separates a pickaxe from a sword without a name list.
        ///
        /// The axe sits with the tools, by choice. It is SkillType.Axes whether it is meeting a
        /// tree or a greydwarf - one item, one animation, no way to tell during the swing - so
        /// it has to be one card or the other, and a card called tool speed that did not cover
        /// the thing most people chop with was the wrong half to keep. The cost is that axe
        /// *combat* is sped by the tool card rather than the melee one.
        ///
        /// A staff is ElementalMagic or BloodMagic, and Quick chant covers both. It was the one
        /// weapon no card touched for as long as there were three of these.
        /// </summary>
        private static string CategoryOf(ItemDrop.ItemData weapon)
        {
            if (weapon == null || weapon.m_shared == null) return null;

            switch (weapon.m_shared.m_skillType)
            {
                case Skills.SkillType.Bows:
                case Skills.SkillType.Crossbows:
                    return Ranged;

                case Skills.SkillType.ElementalMagic:
                case Skills.SkillType.BloodMagic:
                    return Magic;

                case Skills.SkillType.Pickaxes:
                case Skills.SkillType.WoodCutting:
                case Skills.SkillType.Axes:
                    return Tools;

                case Skills.SkillType.Swords:
                case Skills.SkillType.Knives:
                case Skills.SkillType.Clubs:
                case Skills.SkillType.Polearms:
                case Skills.SkillType.Spears:
                case Skills.SkillType.Unarmed:
                    return Melee;
            }

            // The hammer, hoe and cultivator carry no skill at all, so they fall through to
            // the item type. They are still Attacks and still animate.
            return weapon.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool ? Tools : null;
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        [HarmonyPostfix]
        private static void Started(Attack __instance, bool __result, Humanoid character, CharacterAnimEvent animEvent,
                                    ItemDrop.ItemData weapon)
        {
            if (!__result || !RistConfig.Enabled.Value) return;
            if (character == null || animEvent == null) return;

            // Local player only. The cards live in ClientState, which is this client's own
            // standing - another player's swing is driven by their game and arrives here
            // through the ZDO already at the right speed.
            if (!ReferenceEquals(character, Player.m_localPlayer)) return;

            var category = CategoryOf(weapon);
            if (category == null) return;

            var bonus = Effects.TotalFor(category);
            if (bonus <= 0f) return;

            // Clamped, because this multiplies an animation rather than a number in a table.
            // A mis-typed catalogue line could otherwise run the whole character at twenty
            // times speed, and animation events are what land the hit.
            bonus = Mathf.Min(bonus, Mathf.Max(0f, RistConfig.AttackSpeedMax.Value));

            if (category == Magic)
            {
                // A looping cast that pays its eitr once, when the loop starts, would fire more
                // for the same price at a faster loop. That is free damage rather than a faster
                // cast, so those staffs stay at vanilla speed. Only a looping projectile staff
                // that pays per burst costs the same per projectile at any speed, and is sped.
                // The test is the game's own: Attack.Update charges up front unless the attack
                // is a Projectile with m_perBurstResourceUsage, and ignores that flag on every
                // other attack type - so reading the flag alone would speed a looping area
                // attack that still pays once.
                if (__instance.m_loopingAttack &&
                    !(__instance.m_attackType == Attack.AttackType.Projectile && __instance.m_perBurstResourceUsage))
                    return;

                // A staff that fires several projectiles per cast times them on its own clock,
                // m_burstInterval, inside Attack.Update - and Update stops the moment the
                // animation ends. Speed the animation alone and the cast finishes before its
                // last bursts are due, so they are never fired: a faster cast that does less.
                // Scaled on __instance, which Humanoid.StartAttack cloned for this one cast, so
                // the weapon's own attack is never touched.
                if (__instance.m_projectileBursts > 1) __instance.m_burstInterval /= 1f + bonus;
            }

            animEvent.Speed(1f + bonus);
        }

        /// <summary>
        /// Quick draw on a crossbow. The crossbow's wait is its reload, a minor action whose
        /// length is GetWeaponLoadingTime, and neither the animator patch nor the bow draw below
        /// touches it - so without this the card sped up the shot and left the part of the cycle
        /// a crossbow actually spends its time in.
        ///
        /// One card for both because a bow's draw and a crossbow's reload are the same thing to
        /// the person holding the weapon: the wait before the shot. Splitting them into two
        /// cards made each one half a card.
        ///
        /// GetWeaponLoadingTime reads Player.m_localPlayer's skill itself and its only caller is
        /// the local player's reload, so the local-player test is the game's rather than ours.
        /// Its own class so a change to ItemData costs the reload and nothing beside it.
        /// </summary>
        internal static class Reload
        {
            [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetWeaponLoadingTime))]
            [HarmonyPostfix]
            private static void Loading(ItemDrop.ItemData __instance, ref float __result)
            {
                if (!RistConfig.Enabled.Value || __result <= 0f) return;
                if (__instance == null || __instance.m_shared == null) return;
                if (!__instance.m_shared.m_attack.m_requiresReload) return;
                if (__instance.m_shared.m_skillType != Skills.SkillType.Crossbows) return;

                var bonus = Mathf.Min(Effects.TotalFor(Ranged), Mathf.Max(0f, RistConfig.AttackSpeedMax.Value));
                if (bonus <= 0f) return;

                __result /= 1f + bonus;
            }
        }

        /// <summary>
        /// Quick chant's capstone: a hit cannot stagger you out of a cast until the spell has
        /// left the staff. After that, staggers land as they always did.
        ///
        /// "Before it has left the staff" needs its own record, because the game keeps none.
        /// Attack.OnAttackTrigger is where a cast fires, so it is marked there. The method's own
        /// early return - no ammo, or the caster already staggering - cannot happen during a
        /// guarded cast: StartAttack refuses to begin while staggering, and the guard below
        /// keeps a new stagger out until the mark is set.
        ///
        /// A staff that fires several projectiles per cast is not done at the trigger. The
        /// trigger only starts the volley; the projectiles follow on the burst clock, and a
        /// stagger in between aborts the attack and loses the rest of a volley already paid
        /// for. So such a cast counts as fired once every burst is out. A looping staff keeps
        /// the first trigger as the mark, because its burst counter never resets between loops
        /// and would otherwise read as unfired for as long as the button is held.
        ///
        /// The guard is on RPC_Stagger rather than Stagger, because the owner's Stagger calls it
        /// directly and a remote attacker's arrives through it. The stagger bar still fills and
        /// flashes on a blocked hit, as it does for any hit; only the stagger itself is refused,
        /// so the first hit after the spell leaves can land it.
        ///
        /// An eitr refund on an aborted cast was the other idea, and Abort cannot carry it: it is
        /// also how every looping attack ends when the button is released, so a refund there
        /// would pay back casts that finished.
        /// </summary>
        internal static class Unbroken
        {
            private static Attack _fired;

            private static AccessTools.FieldRef<Attack, Humanoid> _attackCharacter;
            private static AccessTools.FieldRef<Attack, ItemDrop.ItemData> _attackWeapon;
            private static AccessTools.FieldRef<Humanoid, Attack> _currentAttack;
            private static AccessTools.FieldRef<Attack, int> _burstsFired;
            private static bool _bound, _bindFailed;

            /// <summary>
            /// Bound on first use rather than in a static initialiser. A field renamed by a game
            /// update would otherwise throw at type-init and poison every patch in the class -
            /// the trap that once broke equipping tools in another mod. Here it costs the
            /// capstone and says so once.
            /// </summary>
            private static bool Bind()
            {
                if (_bound) return true;
                if (_bindFailed) return false;

                try
                {
                    _attackCharacter = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
                    _attackWeapon = AccessTools.FieldRefAccess<Attack, ItemDrop.ItemData>("m_weapon");
                    _currentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
                    _burstsFired = AccessTools.FieldRefAccess<Attack, int>("m_projectileBurstsFired");
                    _bound = true;
                }
                catch (System.Exception e)
                {
                    _bindFailed = true;
                    RistPlugin.Log.LogError("Quick chant's capstone could not reach the game's attack " +
                                            "fields and is off for this session: " + e.Message);
                }

                return _bound;
            }

            [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
            [HarmonyPrefix]
            private static void Fired(Attack __instance)
            {
                if (!Bind()) return;
                if (ReferenceEquals(_attackCharacter(__instance), Player.m_localPlayer)) _fired = __instance;
            }

            [HarmonyPatch(typeof(Character), "RPC_Stagger")]
            [HarmonyPrefix]
            private static bool Staggering(Character __instance)
            {
                if (!RistConfig.Enabled.Value) return true;

                var player = Player.m_localPlayer;
                if (player == null || !ReferenceEquals(__instance, player)) return true;
                if (!player.InAttack() || !Bind()) return true;

                var attack = _currentAttack(player);
                if (attack == null) return true;

                var weapon = _attackWeapon(attack);
                if (weapon == null || weapon.m_shared == null || CategoryOf(weapon) != Magic) return true;

                // Fired means triggered - and, for a volley, every burst out. See the summary.
                var fired = ReferenceEquals(attack, _fired) &&
                            (attack.m_loopingAttack || attack.m_projectileBursts <= 1 ||
                             _burstsFired(attack) >= attack.m_projectileBursts);
                if (fired) return true;

                // Checked last: TotalFor builds the hand's totals, and a stagger outside a cast
                // should not pay for that.
                return Effects.TotalFor(UnbrokenCast) <= 0f;
            }
        }

        /// <summary>
        /// Quick draw on a bow, which the animator patch above never reached.
        ///
        /// Attack.Start runs when the arrow is released, not when the string is pulled, so on a
        /// bow the card sped up the release and nothing else. The draw itself is
        /// Player.UpdateAttackBowDraw adding Time.fixedDeltaTime to m_attackDrawTime, and
        /// Humanoid.GetAttackDrawPercentage dividing that by the bow's draw duration. No
        /// animation speed touches either. So the tile said "draw speed" and a fully carved card
        /// drew exactly as slowly as none - the same way Far sight and Weatherly failed, only
        /// quieter, because the release did get faster and looked like something.
        ///
        /// Scaling the percentage where it is read rather than the timer where it is counted is
        /// the choice that matters. Every consumer goes through this one method - the power
        /// Attack.Start is handed (damage, spread, arrow speed), the reticle in Hud, the bow's
        /// bend through the "drawpercent" animator float, and the halved stamina drain once the
        /// draw is full - so all of them move together and none can disagree about how drawn
        /// the bow is. Clamp01(p * (1 + bonus)) is exactly a draw duration divided by (1 +
        /// bonus), including once the vanilla clamp has already reached 1.
        ///
        /// Its own class so a game update that moves this method costs the bow draw and not
        /// every swing-speed card beside it.
        /// </summary>
        internal static class BowDraw
        {
            private static int _frame = -1;
            private static float _bonus;

            [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GetAttackDrawPercentage))]
            [HarmonyPostfix]
            private static void Drawn(Humanoid __instance, ref float __result)
            {
                // Before anything else: Hud asks this every frame whether or not a bow is up,
                // and nearly every one of those answers is 0.
                if (__result <= 0f || __result >= 1f) return;
                if (!RistConfig.Enabled.Value) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                // Bows by skill, not by m_bowDraw. A staff can carry the draw flag too, and a
                // crossbow's wait is its reload, which is a different number on a different path.
                var weapon = __instance.GetCurrentWeapon();
                if (weapon == null || weapon.m_shared == null) return;
                if (weapon.m_shared.m_skillType != Skills.SkillType.Bows) return;

                // Once per rendered frame. This is read every fixed update and every frame for
                // as long as the string is held, and TotalFor builds its totals from scratch.
                if (_frame != Time.frameCount)
                {
                    _frame = Time.frameCount;
                    _bonus = Mathf.Min(Effects.TotalFor(Ranged), Mathf.Max(0f, RistConfig.AttackSpeedMax.Value));
                }

                if (_bonus <= 0f) return;

                __result = Mathf.Clamp01(__result * (1f + _bonus));
            }
        }
    }
}
