using System;
using System.Globalization;

namespace LCReplay.Core
{
    public static class ReplayAnimationClock
    {
        // AnimatorStateInfo.length already includes the state's speed and blend
        // tree multiplier. Multiplying by that speed again makes walking clips
        // race ahead and jump back whenever another state observation arrives.
        public static bool TrySample(ReplayEvent state, double time, out float normalized,
            ReplayEvent? next = null)
        {
            normalized = 0;
            if (!Number(state, "normalizedTime", out var start) || !Finite(time)) return false;
            var elapsed = Math.Max(0, time - state.Time);
            double result;
            if (Number(state, "normalizedRate", out var rate)) result = start + elapsed * rate;
            else
            {
                var speed = Number(state, "speed", out var recordedSpeed) ? recordedSpeed : 1;
                if (next != null && next.Time > state.Time && time <= next.Time &&
                    Same(state, next, "hash") && Same(state, next, "clip") && Same(state, next, "controller") &&
                    Number(next, "normalizedTime", out var end) &&
                    (speed > 0 && end >= start || speed < 0 && end <= start || speed == 0 && end == start))
                {
                    // Legacy files have no separate Animator.speed. Observed
                    // endpoints recover its effect, without crossing a restart
                    // or blending a different animation into the current state.
                    result = start + (end - start) * elapsed / (next.Time - state.Time);
                }
                else result = start + (Number(state, "duration", out var duration) && duration > .001
                    ? elapsed * Math.Sign(speed) / duration : 0);
            }
            if (!Finite(result) || result < -float.MaxValue || result > float.MaxValue) return false;
            normalized = (float)result;
            return true;
        }

        private static bool Same(ReplayEvent a, ReplayEvent b, string key) =>
            a.Data.TryGetValue(key, out var left) == b.Data.TryGetValue(key, out var right) && left == right;

        private static bool Number(ReplayEvent evt, string key, out double value)
        {
            value = 0;
            return evt.Data.TryGetValue(key, out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Finite(value);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
