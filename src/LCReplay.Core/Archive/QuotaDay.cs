using System;
using System.Collections.Generic;
using System.Globalization;

namespace LCReplay.Core.Archive
{
    public static class QuotaDay
    {
        // Match TimeOfDay.UpdateProfitQuotaCurrentTime, including fractional
        // days after takeoff. Rounding up repeats Day 1 on the next expedition.
        public static int? Remaining(float seconds, float daySeconds, int? fallback)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0 ||
                float.IsNaN(daySeconds) || float.IsInfinity(daySeconds) || daySeconds <= 0) return fallback;
            var days = Math.Floor(seconds / daySeconds);
            return days >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)days);
        }

        // Vanilla quotas have three expedition days. Preparation and overdue
        // metadata must never produce a Day 0 or a fourth/fifth day in the UI.
        public static int Number(int? remaining, int fallback = 1) => remaining.HasValue
            ? Math.Max(1, Math.Min(3, 4 - Math.Max(0, remaining.Value))) : Math.Max(1, Math.Min(3, fallback));

        public static int Number(IReadOnlyDictionary<string, string> metadata)
        {
            if (metadata.TryGetValue("deadlineDaysRemaining", out var text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)) return Number(remaining);
            return metadata.TryGetValue("dayNumber", out text) && int.TryParse(text, out var day) ? Number(null, day) : 1;
        }
    }
}
