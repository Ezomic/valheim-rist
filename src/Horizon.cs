using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Two cards that widen what the world gives back rather than what you do to it.
    ///
    /// Both ride numbers the game already recomputes, so neither needs a restore path and
    /// neither can leak: the explore radius is passed in fresh on every explore tick, and the
    /// sail force is rebuilt every physics step from the ship's heading.
    ///
    /// They are here together because they answer the same design problem. Rist's move-speed
    /// card had to be capped at 10% - a permanent speed buff is always on, never noticed, and
    /// a player told us it had made his unmodded playthroughs feel broken. These two cannot do
    /// that. A bigger map radius is felt only while exploring ground you have not seen, and a
    /// narrower dead cone is felt only at sea with a sail up. Neither follows you home.
    /// </summary>
    internal static class Horizon
    {
        internal const string ExploreRadius = "*exploreradius";
        internal const string WindCone = "*windcone";
        internal const string RowSpeed = "*rowspeed";

        /// <summary>Fraction above vanilla. 0.25 means a quarter further. Set by Effects.</summary>
        internal static float ExtraExplore;

        /// <summary>How far the dead-upwind cone is narrowed, 0 to 1. Set by Effects.</summary>
        internal static float ConeNarrowing;

        /// <summary>Extra rowing force, as a fraction. Set by Effects.</summary>
        internal static float RowBonus;

        /// <summary>
        /// The map's explore radius, scaled where the game passes it in.
        ///
        /// Minimap.Explore(Vector3, float) has exactly one caller, UpdateExplore, which hands it
        /// the player's position and m_exploreRadius. So a prefix on the radius argument widens
        /// the player's own reveal and nothing else - the other three Explore calls are the
        /// (int, int) pixel overload used for loading and sharing the map.
        ///
        /// It used to overwrite m_exploreRadius from UpdateExplore, caching vanilla's value in a
        /// static the first time it saw a Minimap. That worked, and it was the weaker hook: a field
        /// written back every tick, a cached number that could go stale, and a result that fought
        /// any other mod doing the same. Scaling the argument mutates nothing and composes - it is
        /// how blaxxun-boop's Exploration and Sailing both do it, so a player running either
        /// beside Rist gets the two multiplied rather than one overwriting the other.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) })]
        internal static class Radius
        {
            [HarmonyPrefix]
            private static void Widen(ref float radius)
            {
                if (ExtraExplore > 0f) radius *= 1f + ExtraExplore;
            }
        }

        /// <summary>
        /// True when the local player is the one steering this ship.
        ///
        /// Every ship bonus is gated on it. The bonuses are the local player's own runestone
        /// values, so they only mean anything applied to a ship that player is sailing. Keying
        /// on physics ownership instead, as this did at first, is usually the same thing - taking
        /// the helm claims ownership - but not always, and when it is not, a passenger who happened
        /// to own the ship would sail it on their own ranks. Reading the helm directly is what
        /// blaxxun-boop's Sailing does.
        ///
        /// A remote helmsman's bonus is applied on their own machine, which is the one running
        /// that ship's physics, so nothing needs to cross the wire for it.
        /// </summary>
        private static bool LocalIsHelmsman(Ship ship)
        {
            var player = Player.m_localPlayer;
            if (ship == null || player == null || ship.m_shipControlls == null) return false;
            return ship.m_shipControlls.GetUser() == player.GetPlayerID();
        }

        /// <summary>
        /// Sailing closer to the wind.
        ///
        /// Ship.GetWindAngleFactor is the whole sailing model:
        ///
        ///     float num  = Dot(windDir, -transform.forward);
        ///     float num2 = Lerp(0.7f, 1f, 1f - Abs(num));      // across the wind is best
        ///     float num3 = 1f - LerpStep(0.75f, 0.8f, num);    // dead upwind is nothing
        ///     return num2 * num3;
        ///
        /// num3 is the dead zone and the only reason tacking exists. As angles off dead upwind it
        /// is fully dead inside acos(0.8), about 36.9 degrees, and fully live outside acos(0.75),
        /// about 41.4, fading between.
        ///
        /// The card narrows that zone: it scales both edges toward dead upwind, so each rank lets
        /// you point closer to the wind. num2 is left alone, because raising it would be a flat
        /// speed bonus wearing a nautical hat - the mistake the move-speed card already made.
        ///
        /// It used to put a floor under num3 instead, and that was the wrong mechanic twice over.
        /// The zone stayed exactly the same size, just no longer fully dead, so at rank 5 you could
        /// sail dead straight into the wind at 35% - which is the card removing tacking, not
        /// narrowing it, and the opposite of "she points nearer the wind". And it could never be
        /// drawn honestly: the wind ring's dead-zone arc stayed the same size while a player who
        /// had taken the card reasonably expected it to shrink, and concluded it did nothing.
        ///
        /// Narrowing the edges keeps dead upwind fully dead at every rank - an edge at a smaller
        /// angle is still an edge - so tacking survives, with a shallower zig-zag.
        /// </summary>
        [HarmonyPatch(typeof(Ship), nameof(Ship.GetWindAngleFactor))]
        internal static class WindAngle
        {
            [HarmonyPostfix]
            private static void Narrow(Ship __instance, ref float __result)
            {
                if (__instance == null || EnvMan.instance == null) return;
                if (ConeNarrowing <= 0f || !LocalIsHelmsman(__instance)) return;

                var into = Vector3.Dot(EnvMan.instance.GetWindDir(), -__instance.transform.forward);

                // Nothing downwind or across. The dead zone only exists into the wind.
                if (into <= 0f) return;

                float lo, hi;
                Edges(out lo, out hi);

                var across = Mathf.Lerp(0.7f, 1f, 1f - Mathf.Abs(into));
                var cone = 1f - Mathf.Clamp01((into - lo) / (hi - lo));

                // Narrowing only ever turns dead heading into live, so this is never below
                // vanilla's answer; the clamp is a guard, not a correction.
                __result = Mathf.Clamp(across * cone, __result, across);
            }
        }

        /// <summary>Vanilla's live edge, as an angle off dead upwind: acos(0.75).</summary>
        private static readonly float VanillaLiveAngle = Mathf.Acos(0.75f) * Mathf.Rad2Deg;

        /// <summary>Vanilla's dead edge, as an angle off dead upwind: acos(0.8).</summary>
        private static readonly float VanillaDeadAngle = Mathf.Acos(0.8f) * Mathf.Rad2Deg;

        /// <summary>
        /// The most the zone may be narrowed, as a fraction. At the default catalogue a fully
        /// carved card is 0.5, so this never binds there. It exists for a custom catalogue: at 1
        /// both edges collapse onto dead upwind and the dead zone vanishes, which is sailing
        /// straight into the wind for free.
        /// </summary>
        private const float MaxNarrowing = 0.8f;

        /// <summary>
        /// The dead zone's two edges under the current narrowing, as half-angles off dead upwind
        /// in degrees. Shared by the sailing patch and the wind ring, so the ring draws the zone
        /// the boat actually has.
        /// </summary>
        internal static void EdgeAngles(out float liveAngle, out float deadAngle)
        {
            var scale = 1f - Mathf.Clamp(ConeNarrowing, 0f, MaxNarrowing);
            liveAngle = VanillaLiveAngle * scale;
            deadAngle = VanillaDeadAngle * scale;
        }

        /// <summary>The same two edges as the dot products GetWindAngleFactor compares against.</summary>
        private static void Edges(out float lo, out float hi)
        {
            float live, dead;
            EdgeAngles(out live, out dead);
            lo = Mathf.Cos(live * Mathf.Deg2Rad);   // the sail starts to fade here
            hi = Mathf.Cos(dead * Mathf.Deg2Rad);   // and is fully dead here
        }

        /// <summary>Vanilla's live edge as the dot product GetWindAngleFactor tests: 0.75.</summary>
        private const float VanillaLiveDot = 0.75f;

        /// <summary>
        /// Points the sail's push forward inside the arc the card unlocks.
        ///
        /// Narrowing the dead zone on its own did nothing a player could feel, and not because
        /// the numbers were small. GetSailForce aims the force along
        /// Normalize(windDir + transform.forward), and pointing into the wind windDir is nearly
        /// -forward, so that sum collapses and the push swings sideways, where the hull's sideways
        /// damping eats it. Measured against the model: a fully carved card unlocked headings 20
        /// to 40 degrees off the wind, but the forward share of the force there was so small that
        /// every one of them made slower progress to windward than vanilla's own best heading at
        /// 52 degrees. So a player who took the card sailed exactly as before, and correctly.
        ///
        /// This bends that direction toward the bow by the same fraction the zone is narrowed,
        /// and only inside the arc the card opens - between vanilla's live edge and the narrowed
        /// dead edge. Everywhere else the game's own force stands untouched, including the part
        /// of the zone still dead, so sailing straight into the wind stays impossible.
        ///
        /// Tying the bend to the narrowing is what gives every rank a real step. A fixed bend was
        /// modelled and rejected: it made rank 1 do nearly all the work and ranks 2 to 5 almost
        /// nothing. At 0.06 a rank the best windward heading moves from 52 degrees to about 36 by
        /// rank 5, roughly 71% faster to windward.
        ///
        /// A replacement rather than a postfix, because the game SmoothDamps m_sailForce toward
        /// the target before returning it, and correcting the result afterwards would leave the
        /// stored force out of step with what was returned. The same approach as uwu's
        /// SailingGrace, which reached the same conclusion about the direction.
        /// </summary>
        [HarmonyPatch(typeof(Ship), "GetSailForce")]
        internal static class Sail
        {
            private static AccessTools.FieldRef<Ship, Vector3> _force;
            private static AccessTools.FieldRef<Ship, Vector3> _velocity;
            private static bool _bound;
            private static bool _broken;

            /// <summary>
            /// Bound on first use inside a try, never in a static initializer: a field ref that
            /// throws at type init poisons every patch on the class, and a game update renaming
            /// m_sailForce should cost the bend, not the sail.
            /// </summary>
            private static bool Bound()
            {
                if (_bound) return !_broken;
                _bound = true;

                try
                {
                    _force = AccessTools.FieldRefAccess<Ship, Vector3>("m_sailForce");
                    _velocity = AccessTools.FieldRefAccess<Ship, Vector3>("m_windChangeVelocity");
                }
                catch (System.Exception e)
                {
                    _broken = true;
                    RistPlugin.Log.LogWarning("Weatherly cannot bend the sail, the ship's sail fields "
                        + "have moved. The dead zone still narrows. " + e.Message);
                }

                return !_broken;
            }

            [HarmonyPrefix]
            private static bool Bend(Ship __instance, float sailSize, ref Vector3 __result)
            {
                if (__instance == null || EnvMan.instance == null) return true;
                if (ConeNarrowing <= 0f || !LocalIsHelmsman(__instance)) return true;

                var windDir = EnvMan.instance.GetWindDir();
                var forward = __instance.transform.forward;
                var into = Vector3.Dot(windDir, -forward);

                float lo, hi;
                Edges(out lo, out hi);

                // Outside the arc the card opens, the game's force is already right.
                if (into <= VanillaLiveDot || into >= hi) return true;
                if (!Bound()) return true;

                // Everything vanilla does, in order, with one change to the direction.
                var intensity = Mathf.Lerp(0.25f, 1f, EnvMan.instance.GetWindIntensity());
                var factor = __instance.GetWindAngleFactor() * intensity;

                var along = windDir + forward;
                var vanillaDir = along.sqrMagnitude < 1e-6f ? forward : along.normalized;
                var dir = Vector3.Lerp(vanillaDir, forward, Mathf.Clamp01(ConeNarrowing)).normalized;

                var target = dir * (factor * __instance.m_sailForceFactor * sailSize);

                ref var force = ref _force(__instance);
                ref var velocity = ref _velocity(__instance);
                force = Vector3.SmoothDamp(force, target, ref velocity, 1f, 99f);

                __result = force;
                return false;
            }
        }

        /// <summary>
        /// Rowing faster, the capstone on the wind card.
        ///
        /// Rowing and sailing are two separate forces in Ship.CustomFixedUpdate. The sail only
        /// pushes at Half and Full, through GetSailForce. Rowing is Slow and Back, and is the
        /// whole of this:
        ///
        ///     case Speed.Slow: zero += forward * (m_backwardForce * (1f - |m_rudderValue|));
        ///     case Speed.Back: zero += -forward * (m_backwardForce * (1f - |m_rudderValue|));
        ///
        /// So the capstone is a multiplier on m_backwardForce. It used to be a tacking speed
        /// bonus on the sail, and that was the wrong half of the ship: the wind card already
        /// makes sailing upwind better, so a sail bonus was more of the same. Rowing is what you
        /// are left doing when you still cannot point high enough, which makes the two answer the
        /// same annoyance from opposite ends.
        ///
        /// Scaled for the length of vanilla's own call and put back afterwards, rather than
        /// adding a force of our own in a postfix. Vanilla only rows when four things hold - it
        /// owns the ship, somebody is aboard and steering, the hull is in the water, and speed is
        /// Slow or Back - and a postfix runs whether or not the original returned early, so it
        /// would have to repeat all four. The water check alone means calling Floating with the
        /// ship's private buoyancy state, and getting it wrong pushes a beached ship across the
        /// sand. Borrowing vanilla's own call inherits every one of those rules and repeats none.
        ///
        /// m_backwardForce is read in exactly two places in the game, both the lines above, so
        /// nothing else changes while it is scaled. It is restored in a finalizer rather than a
        /// postfix because a postfix does not run when the original throws, and a force left
        /// scaled would compound on the next physics step.
        ///
        /// In multiplayer the bonus is the helmsman's, read from the helm rather than assumed from
        /// ownership, so a passenger carrying this capstone cannot stack theirs on the driver's.
        /// </summary>
        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        internal static class Row
        {
            [HarmonyPrefix]
            private static void Scale(Ship __instance, out float __state)
            {
                // Always capture, even when there is nothing to scale, so the finalizer's restore
                // is correct in every case and never has to know what the prefix decided.
                __state = __instance != null ? __instance.m_backwardForce : 0f;

                if (__instance == null || RowBonus <= 0f) return;
                if (!__instance.IsOwner() || !LocalIsHelmsman(__instance)) return;

                // Squared, so the number on the card is the speed the boat reaches rather than the
                // force pushing it. Forward drag in CustomFixedUpdate is quadratic -
                // `v * v * m_dampingForward`, and nothing else in Ship damps forward motion - so top
                // speed settles where force equals drag and goes with the square root of the force.
                // A plain 1.2 on the force would have printed "+20% rowing speed" and delivered
                // about 9.5%. 1.2 squared is 1.44, which is the force a real 20% needs.
                var mult = 1f + RowBonus;
                __instance.m_backwardForce = __state * mult * mult;
            }

            [HarmonyFinalizer]
            private static System.Exception Restore(Ship __instance, float __state, System.Exception __exception)
            {
                if (__instance != null) __instance.m_backwardForce = __state;
                return __exception;
            }
        }
    }
}
