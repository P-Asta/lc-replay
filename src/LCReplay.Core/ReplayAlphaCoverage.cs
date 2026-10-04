using System;

namespace LCReplay.Core
{
    /// <summary>Selects the same bounded alpha gain as a pixel-by-pixel coverage search.</summary>
    public static class ReplayAlphaCoverage
    {
        public static float SelectScale(ReadOnlySpan<int> histogram, int pixelCount, int threshold, float targetCoverage)
        {
            if (histogram.Length != 256) throw new ArgumentException("Expected one count per alpha byte.", nameof(histogram));
            if (pixelCount <= 0) throw new ArgumentOutOfRangeException(nameof(pixelCount));
            if (threshold < 0 || threshold > 255) throw new ArgumentOutOfRangeException(nameof(threshold));
            Span<int> visibleAt = stackalloc int[256];
            var visible = 0;
            for (var alpha = 255; alpha >= 0; alpha--)
            {
                visible += histogram[alpha];
                visibleAt[alpha] = visible;
            }
            float bestScale = 1f, bestError = float.MaxValue;
            for (var scale = 1f; scale <= 2.5f; scale += .125f)
            {
                var count = visibleAt[(int)Math.Ceiling(threshold / (double)scale)];
                var error = Math.Abs((float)count / pixelCount - targetCoverage);
                if (error < bestError) { bestError = error; bestScale = scale; }
            }
            return bestScale;
        }
    }
}
