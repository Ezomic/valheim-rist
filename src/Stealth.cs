using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Low draw: drawing a bow from a crouch keeps you hidden.
    ///
    /// The game stands you up to draw. Player.UpdateCrouch clears the animator's crouching flag
    /// while InAttack or IsDrawingBow, even though the crouch toggle stays on, and
    /// Player.IsCrouching answers from the animation - so the moment the string comes back,
    /// UpdateStealth reads "not crouching" and aims the stealth factor at 1, fully seen. That is
    /// why a bow and sneaking never went together, and it is not a bug in the bow.
    ///
    /// Forcing the crouch animation through the draw was the other way to fix it, and it was
    /// turned down: whether the animator even has a crouched draw is asset data, and if it
    /// does not, the bow may never draw or fire. So the character still rises on screen, and
    /// the stealth system is told the truth about intent instead: while the toggle is on and a
    /// bow is being drawn, UpdateStealth sees a crouch. Only UpdateStealth - IsCrouching also
    /// decides movement speed, noise and jumping, and none of that should change. The stealth
    /// factor it produces is written to the ZDO, so every creature's owner reads the hidden
    /// value without any network code here.
    ///
    /// Ranks buy seconds of the draw; the capstone covers the whole draw. Either way the shot it
    /// ends in counts too: the release animation is an attack, the game clears the crouch for
    /// that as well, and a hidden draw that lit you up the instant the arrow left was hidden for
    /// nothing. "Hidden" means sneaking as usual, at your Sneak skill and in your light - not
    /// invisible.
    /// </summary>
    internal static class LowDraw
    {
        internal const string Seconds = "*lowdraw";
        internal const string Whole = "*lowdraw:whole";

        /// <summary>
        /// How long after the string leaves the bow a sneaking archer still counts as crouched:
        /// the release animation, then the blend back into the crouch. IsCrouching reads the
        /// animator's current state, which still reports the attack while it blends out.
        /// </summary>
        private const float ReleaseGrace = 1f;

        private static bool _inStealth;

        // What the last draw was worth, recorded while it happens rather than read afterwards:
        // loosing the arrow zeroes m_attackDrawTime, and the stealth update that has to judge the
        // shot comes after that, every half second.
        private static float _lastDraw;
        private static float _drawSeenAt = -99f;

        private static AccessTools.FieldRef<Player, bool> _crouchToggled;
        private static AccessTools.FieldRef<Humanoid, float> _drawTime;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _crouchToggled = AccessTools.FieldRefAccess<Player, bool>("m_crouchToggled");
                _drawTime = AccessTools.FieldRefAccess<Humanoid, float>("m_attackDrawTime");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Low draw could not reach the game's crouch or draw fields and is " +
                                        "off for this session: " + e.Message);
            }

            return _bound;
        }

        private static bool HoldingBow(Player player)
        {
            var weapon = player.GetCurrentWeapon();
            return weapon != null && weapon.m_shared != null && weapon.m_shared.m_skillType == Skills.SkillType.Bows;
        }

        /// <summary>The window in which a crouch is being asked for by the stealth update.</summary>
        internal static class Scope
        {
            [HarmonyPatch(typeof(Player), "UpdateStealth")]
            [HarmonyPrefix]
            private static void Enter() { _inStealth = true; }

            [HarmonyPatch(typeof(Player), "UpdateStealth")]
            [HarmonyFinalizer]
            private static void Leave() { _inStealth = false; }
        }

        /// <summary>Remembers each bow draw from a crouch as it happens.</summary>
        internal static class Draw
        {
            [HarmonyPatch(typeof(Player), "UpdateAttackBowDraw")]
            [HarmonyPrefix]
            private static void Drawing(Player __instance)
            {
                if (!ReferenceEquals(__instance, Player.m_localPlayer) || !Bind()) return;

                if (!_crouchToggled(__instance))
                {
                    // Standing up between shots gives the shot away, as it always did.
                    _drawSeenAt = -99f;
                    return;
                }

                var time = _drawTime(__instance);
                if (time <= 0f || !HoldingBow(__instance)) return;

                _lastDraw = time;
                _drawSeenAt = Time.time;
            }
        }

        internal static class Crouch
        {
            [HarmonyPatch(typeof(Player), nameof(Player.IsCrouching))]
            [HarmonyPostfix]
            private static void Crouching(Player __instance, ref bool __result)
            {
                // Cheapest test first: IsCrouching is asked every frame by movement.
                if (__result || !_inStealth) return;
                if (!RistConfig.Enabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (!Bind() || !_crouchToggled(__instance) || !HoldingBow(__instance)) return;

                var whole = Effects.TotalFor(Whole) > 0f;
                var grace = Effects.TotalFor(Seconds);

                if (__instance.IsDrawingBow())
                {
                    if (whole || (grace > 0f && _drawTime(__instance) <= grace)) __result = true;
                    return;
                }

                // The shot and the moment after it. The draw that led to it has to have
                // qualified, so a long draw past the grace does not become hidden by letting go.
                var justShot = __instance.InAttack() || Time.time - _drawSeenAt <= ReleaseGrace;
                if (justShot && _drawSeenAt > 0f && (whole || (grace > 0f && _lastDraw <= grace))) __result = true;
            }
        }
    }

    /// <summary>
    /// Unseen blow: a sneak attack lands harder, and at rank five it staggers.
    ///
    /// Both halves work on the attacker's side, in Character.Damage, before the hit is sent.
    /// That matters because the sneak attack itself is decided on the target's owner, in
    /// RPC_Damage - which may be another player's machine - out of fields the attacker cannot
    /// see. Raising HitData.m_backstabBonus before it leaves means the owner's own check applies
    /// the larger multiplier with no Rist code there at all. Only a bonus above 1 is raised, so
    /// a weapon the game gives no sneak attack does not gain one.
    ///
    /// The stagger cannot be decided by the owner without Rist there, so it is decided here,
    /// the same way the game would: the creature not alerted (its alert flag is synced), not a
    /// boss, not tamed, able to be staggered, and not ambushed by you in the last 300 seconds -
    /// the game's own sneak-attack cooldown, which lives on the owner, so it is mirrored per
    /// creature on this machine. It can disagree with the owner at the edges, such as a creature
    /// another player ambushed a minute ago; the cost of that is one early stagger.
    /// </summary>
    internal static class UnseenBlow
    {
        internal const string Bonus = "*sneakattack";
        internal const string Stagger = "*sneakattack:stagger";

        private const float AmbushCooldown = 300f;

        private static readonly Dictionary<ZDOID, float> _lastAmbush = new Dictionary<ZDOID, float>();

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        [HarmonyPrefix]
        private static void Damaging(Character __instance, HitData hit)
        {
            if (hit == null || hit.m_backstabBonus <= 1f || __instance == null) return;
            if (!RistConfig.Enabled.Value) return;

            var player = Player.m_localPlayer;
            if (player == null || !ReferenceEquals(hit.GetAttacker(), player) || __instance.IsPlayer()) return;

            // Every vanilla caller builds a fresh HitData for each Damage call, and the owner gets
            // its own deserialized copy, so there is no second pass to guard against here.
            var bonus = Effects.TotalFor(Bonus);
            var stagger = Effects.TotalFor(Stagger) > 0f;
            if (bonus <= 0f && !stagger) return;

            if (bonus > 0f) hit.m_backstabBonus *= 1f + bonus;

            if (stagger && WouldAmbush(__instance, player))
                hit.m_staggerMultiplier = Mathf.Max(hit.m_staggerMultiplier, 100f);
        }

        private static bool WouldAmbush(Character target, Player attacker)
        {
            var ai = target.GetBaseAI();
            if (ai == null || ai.IsAlerted()) return false;
            if (target.IsTamed() || Bosses.Is(target) || target.m_staggerDamageFactor <= 0f) return false;

            // The owner skips a sneak attack on passive mobs that can see you; so does this.
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.PassiveMobs) &&
                ai.CanSeeTarget(attacker))
                return false;

            var id = target.GetZDOID();
            if (_lastAmbush.TryGetValue(id, out var last) && Time.time - last <= AmbushCooldown) return false;

            _lastAmbush[id] = Time.time;
            return true;
        }
    }
}
