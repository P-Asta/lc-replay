using System;
using System.IO;

namespace LCReplay.Core
{
    /// <summary>Independent mono IMA-ADPCM blocks for bounded replay audio events.</summary>
    public static class ReplayAudioCodec
    {
        public const int SampleRate = 22050;
        public const int MaxSamplesPerBlock = 8192;
        private static readonly int[] Steps =
        {
            7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,
            107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,
            724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,
            3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,
            16818,18500,20350,22385,24623,27086,29794,32767
        };
        private static readonly int[] IndexChange = { -1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8 };

        public static byte[] Encode(short[] samples, int count)
        {
            if (samples == null || count < 1 || count > samples.Length || count > MaxSamplesPerBlock)
                throw new ArgumentOutOfRangeException(nameof(count));
            var result = new byte[2 + count / 2];
            int predictor = samples[0], index = 0;
            result[0] = (byte)predictor; result[1] = (byte)(predictor >> 8);
            for (var i = 1; i < count; i++)
            {
                var step = Steps[index];
                var diff = (int)samples[i] - predictor;
                var code = diff < 0 ? 8 : 0;
                if (diff < 0) diff = -diff;
                var delta = step >> 3;
                if (diff >= step) { code |= 4; diff -= step; delta += step; }
                if (diff >= (step >> 1)) { code |= 2; diff -= step >> 1; delta += step >> 1; }
                if (diff >= (step >> 2)) { code |= 1; delta += step >> 2; }
                predictor = Math.Max(short.MinValue, Math.Min(short.MaxValue, predictor + ((code & 8) != 0 ? -delta : delta)));
                index = Math.Max(0, Math.Min(88, index + IndexChange[code]));
                var byteIndex = 2 + (i - 1) / 2;
                if ((i & 1) != 0) result[byteIndex] = (byte)code;
                else result[byteIndex] |= (byte)(code << 4);
            }
            return result;
        }

        public static float[] Decode(byte[] data, int count)
        {
            if (data == null || count < 1 || count > MaxSamplesPerBlock || data.Length != 2 + count / 2)
                throw new InvalidDataException("Invalid replay audio block size.");
            var result = new float[count];
            int predictor = (short)(data[0] | data[1] << 8), index = 0;
            result[0] = predictor / 32768f;
            for (var i = 1; i < count; i++)
            {
                var packed = data[2 + (i - 1) / 2];
                var code = (i & 1) != 0 ? packed & 15 : packed >> 4;
                var step = Steps[index];
                var delta = step >> 3;
                if ((code & 4) != 0) delta += step;
                if ((code & 2) != 0) delta += step >> 1;
                if ((code & 1) != 0) delta += step >> 2;
                predictor = Math.Max(short.MinValue, Math.Min(short.MaxValue, predictor + ((code & 8) != 0 ? -delta : delta)));
                index = Math.Max(0, Math.Min(88, index + IndexChange[code]));
                result[i] = predictor / 32768f;
            }
            return result;
        }
    }
}
