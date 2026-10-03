using System;
using System.Collections.Generic;
using System.Globalization;

namespace LCReplay.Core
{
    public static class ReplayGameClock
    {
        public static double? Normalized(IReadOnlyDictionary<string, string> state)
        {
            if (state.TryGetValue("TimeOfDay.normalizedTimeOfDay", out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                !double.IsNaN(value) && !double.IsInfinity(value)) return Math.Max(0, Math.Min(1, value));
            return null;
        }
        public static string Format(double normalized, double numberOfHours = 16)
        {
            if (double.IsNaN(normalized) || double.IsInfinity(normalized) || !double.IsFinite(numberOfHours) || numberOfHours <= 0 || numberOfHours > 24) return "--:--";
            var minutes = (int)(Math.Max(0, Math.Min(1, normalized)) * 60 * numberOfHours) + 360;
            var hour = minutes / 60;
            if (hour >= 24) return "12:00 AM";
            return ((hour % 12 == 0 ? 12 : hour % 12).ToString("00", CultureInfo.InvariantCulture)) + ":" +
                (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + (hour < 12 ? " AM" : " PM");
        }
        public static double Hours(IReadOnlyDictionary<string, string> state, IReadOnlyDictionary<string, string> metadata)
        {
            var text = state.TryGetValue("TimeOfDay.numberOfHours", out var current) ? current :
                metadata.TryGetValue("gameNumberOfHours", out var initial) ? initial : "16";
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours > 0 && hours <= 24 ? hours : 16;
        }
    }
}
