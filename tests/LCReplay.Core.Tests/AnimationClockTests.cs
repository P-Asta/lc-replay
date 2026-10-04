using System.Globalization;
using LCReplay.Core;

internal static class AnimationClockTests
{
    internal static void Run()
    {
        ReplayEvent State(double time, double phase, double duration, double speed) => new ReplayEvent
        {
            Time = time, Data = new Dictionary<string, string>
            {
                ["hash"] = "42", ["clip"] = "Walk", ["controller"] = "Enemy",
                ["normalizedTime"] = phase.ToString("R", CultureInfo.InvariantCulture),
                ["duration"] = duration.ToString("R", CultureInfo.InvariantCulture),
                ["speed"] = speed.ToString("R", CultureInfo.InvariantCulture)
            }
        };
        void Expect(ReplayEvent state, double time, double expected, ReplayEvent? next = null)
        {
            if (!ReplayAnimationClock.TrySample(state, time, out var actual, next) || Math.Abs(actual - expected) > .00001)
                throw new Exception($"Animation phase at {time}: expected {expected}, got {actual}");
        }

        // Jester WalkDocile has a two-second source clip. State speed 4
        // produces a .5-second duration; it must advance twice, not eight times.
        var fast = State(10, .25, .5, 4);
        Expect(fast, 10.25, .75);
        Expect(State(10, .25, .5, 0), 11, .25);
        Expect(State(10, .75, .5, -4), 10.25, .25);
        var next = State(10.5, 1.5, .5, 4);
        Expect(fast, 10.25, .875, next);
        Expect(fast, 10.499999, 1.4999975, next);
        next.Data["hash"] = "43";
        Expect(fast, 10.25, .75, next);
        next.Data["hash"] = "42";
        next.Data["normalizedTime"] = "0.1";
        Expect(fast, 10.25, .75, next); // Same-state restart is discrete.
        fast.Data["normalizedRate"] = "3";
        Expect(fast, 10.25, 1);
        fast.Data["normalizedRate"] = "0";
        Expect(fast, 10.25, .25);
        fast.Data["normalizedRate"] = "-2";
        Expect(fast, 10.25, -.25);
        fast.Data["normalizedRate"] = "NaN";
        Expect(fast, 10.25, .75);
        fast.Data["normalizedTime"] = "NaN";
        if (ReplayAnimationClock.TrySample(fast, 11, out _)) throw new Exception("Non-finite phase accepted");
    }
}
