using System;

namespace StbcRevival.Science
{
    // What changed in one frame of using the science console (ConsoleUse.Step); the mod does the Unity side of each.
    [Flags]
    public enum UseChange
    {
        None = 0,
        Register = 1,       // the hands may use the screen's controls (VR, in reach)
        Unregister = 2,     // they may not any more
        Leave = 4,          // left reach or sat down: the scan's progress reported and the console freed
        Engage = 8,         // on a monitor: the view goes to the screen and the mouse is freed
        Disengage = 16,     // back to walking
        SwallowEscape = 32, // the Escape that left the console mustn't also sit you down
    }

    // One frame's input to ConsoleUse.Step.
    public struct UseFrame
    {
        public bool Standing;   // stood up with Bridge Walk
        public bool Vr;         // standing in VR
        public bool Active;     // the console is on and its screen made
        public float Distance;  // from the console's stand point along the floor, metres (infinity if none)
        public bool UseKey;     // [Science] UseKey went down this frame (not while typing)
        public bool Escape;     // Escape went down this frame (not while typing)
    }

    /// <summary>
    /// Using the science console standing up (ScienceUse in the mod), without Unity: who is in reach, the monitor's
    /// "engaged" state, and what each frame changes.
    ///   - In reach: standing, the console on, within ReachMetres of its stand point. Seated, never.
    ///   - VR: in reach, the hands may use the screen (Register; Unregister on leaving) and their index-pull and grab
    ///     get through Bridge Walk's input block (PassesHandInput).
    ///   - Monitor: in reach, the use key engages (the view goes to the screen, walking and mouse look stop, the mouse
    ///     is ours for the screen); the use key or Escape disengages (Escape swallowed, so it doesn't sit you down too),
    ///     as do leaving reach, sitting down or the console going off.
    ///   - Leaving reach (walking off, sitting down, the console going off) frees the console (Leave); the scan's
    ///     progress stays on the board.
    /// </summary>
    public sealed class ConsoleUse
    {
        public const float ReachMetres = 1.2f;
        public const float EaseSeconds = 0.3f;

        public bool InReach { get; private set; }
        public bool Engaged { get; private set; }
        public bool Registered { get; private set; }
        public bool Vr { get; private set; }

        // The hands' index-pull and grab (and the index touch) get through while standing.
        public bool PassesHandInput { get { return InReach && Vr; } }

        public static bool Within(float distance) { return distance >= 0f && distance <= ReachMetres; } // NaN: false

        public UseChange Step(UseFrame f)
        {
            UseChange c = UseChange.None;
            bool reach = f.Standing && f.Active && Within(f.Distance);
            bool wasReach = InReach;
            InReach = reach;
            Vr = f.Standing && f.Vr;

            if (Engaged)
            {
                if (!reach || Vr) { Engaged = false; c |= UseChange.Disengage; }
                else if (f.Escape) { Engaged = false; c |= UseChange.Disengage | UseChange.SwallowEscape; }
                else if (f.UseKey) { Engaged = false; c |= UseChange.Disengage; }
            }
            else if (reach && !Vr && f.UseKey) { Engaged = true; c |= UseChange.Engage; }

            bool want = reach && Vr;
            if (want != Registered)
            {
                Registered = want;
                c |= want ? UseChange.Register : UseChange.Unregister;
            }
            if (wasReach && !reach) c |= UseChange.Leave;
            return c;
        }

        /// <summary>The view's blend toward the screen (0 your eyes, 1 the screen), moved one frame toward where it's going: EaseSeconds from end to end.</summary>
        public static float Ease(float blend, bool engaged, float dt)
        {
            float step = dt > 0f ? dt / EaseSeconds : 0f;
            float to = engaged ? 1f : 0f;
            if (blend < to) return Math.Min(to, blend + step);
            if (blend > to) return Math.Max(to, blend - step);
            return blend;
        }

        /// <summary>Smoothstep: the blend eased in and out.</summary>
        public static float Smooth(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }
    }
}
