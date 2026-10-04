using LCReplay.Core;

internal static class AlphaCoverageTests
{
    internal static void Run()
    {
        static float Legacy(byte[] pixels, int threshold, float target)
        {
            float bestScale = 1, bestError = float.MaxValue;
            for (var scale = 1f; scale <= 2.5f; scale += .125f)
            {
                var count = 0;
                foreach (var alpha in pixels) if (alpha * scale >= threshold) count++;
                var error = Math.Abs((float)count / pixels.Length - target);
                if (error < bestError) { bestError = error; bestScale = scale; }
            }
            return bestScale;
        }
        var histogram = new int[256];
        void Check(byte[] pixels, int threshold, float target)
        {
            Array.Clear(histogram);
            foreach (var alpha in pixels) histogram[alpha]++;
            var expected = Legacy(pixels, threshold, target);
            var actual = ReplayAlphaCoverage.SelectScale(histogram, pixels.Length, threshold, target);
            if (actual != expected) throw new Exception($"Coverage gain differs: threshold {threshold}, target {target}, expected {expected}, got {actual}");
        }
        // Exhaust every byte threshold and alpha value. This catches a changed
        // >= boundary or tie rule in the integer histogram conversion.
        for (var alpha = 0; alpha < 256; alpha++)
            for (var threshold = 0; threshold < 256; threshold++)
                Check(new[] { (byte)alpha }, threshold, 1f);
        var random = new Random(4816);
        foreach (var size in new[] { 1, 4, 16, 256, 4096, 65536 })
        {
            var pixels = new byte[size]; random.NextBytes(pixels);
            foreach (var threshold in new[] { 1, 31, 64, 127, 128, 192, 254, 255 })
                foreach (var target in new[] { 0f, .1f, .5f, .9f, 1f })
                    Check(pixels, threshold, target);
        }
    }
}
