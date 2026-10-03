using System;

namespace LCReplay.Core
{
    // RGBE keeps dim HDR sky pixels that an ordinary 8-bit color PNG rounds to black.
    public static class ReplaySkyRgbe
    {
        public static void Encode(float red, float green, float blue,
            out byte r, out byte g, out byte b, out byte exponent)
        {
            red = Clamp(red); green = Clamp(green); blue = Clamp(blue);
            var peak = Math.Max(red, Math.Max(green, blue));
            if (peak < 1e-32f)
            { r = g = b = exponent = 0; return; }
            var power = (int)Math.Floor(Math.Log(peak, 2)) + 1;
            var scale = 256.0 / Math.Pow(2, power);
            r = (byte)Math.Min(255, (int)(red * scale));
            g = (byte)Math.Min(255, (int)(green * scale));
            b = (byte)Math.Min(255, (int)(blue * scale));
            exponent = (byte)(power + 128);
        }

        public static void Decode(byte r, byte g, byte b, byte exponent,
            out float red, out float green, out float blue)
        {
            if (exponent == 0) { red = green = blue = 0; return; }
            var scale = (float)(Math.Pow(2, exponent - 128) / 256.0);
            red = r * scale; green = g * scale; blue = b * scale;
        }

        private static float Clamp(float value) => float.IsNaN(value) || value <= 0 ? 0 :
            float.IsPositiveInfinity(value) ? 65504f : Math.Min(value, 65504f);
    }
}
