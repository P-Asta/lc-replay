using System.IO.Compression;
using System.Text;
using LCReplay.Core;
using Newtonsoft.Json;

internal static class LightPipelineTests
{
    internal static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "LCReplayLight-" + Guid.NewGuid().ToString("N") + ".lcr");
        LightSnapshot Light() => new()
        {
            Id = "l-authored", Type = "Rectangle", Intensity = 37, BakeType = "Realtime",
            LightPipelineParameters = new()
            {
                new() { Name = "shapeRadius", Kind = "float", Values = new[] { 2.2f } },
                new() { Name = "affectSpecular", Kind = "bool", Values = new[] { 0f } },
                new() { Name = "areaLightShape", Kind = "enum", Text = "Rectangle" }
            }
        };
        void Write(LightSnapshot light)
        {
            using var output = File.Create(path);
            output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            foreach (var record in new[]
            {
                new ReplayRecord { Kind = "header", Header = new ReplayHeader { SessionId = "light-test", GameVersion = "test",
                    StartedUtc = "2026-10-05T00:00:00Z" } },
                new ReplayRecord { Kind = "world", World = new WorldSnapshot { Lights = new() { light } } }
            })
            {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record));
                using var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(bytes);
                using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                writer.Write((int)compressed.Length); writer.Write(bytes.Length); writer.Write(compressed.ToArray());
            }
        }
        void Reject(Action<LightSnapshot> mutate)
        {
            var light = Light(); mutate(light); Write(light);
            try { ReplayReader.Read(path); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid light pipeline metadata was accepted.");
        }
        try
        {
            var light = Light(); Write(light);
            var restored = ReplayReader.Read(path).Worlds.Single().World!.Lights.Single();
            if (restored.Type != "Rectangle" || restored.LightPipelineParameters.Count != 3 ||
                restored.LightPipelineParameters[0].Values[0] != 2.2f || restored.LightPipelineParameters[2].Text != "Rectangle")
                throw new Exception("Light shapes and parameters did not round trip.");
            var record = new ReplayRecord { Kind = "world", World = new WorldSnapshot { Lights = new() { light } } };
            var detailed = ReplayRecordMemory.Estimate(record);
            light.LightPipelineParameters.Clear();
            if (detailed - ReplayRecordMemory.Estimate(record) < 3 * 512)
                throw new Exception("Light pipeline metadata bypasses queue memory accounting.");
            if (JsonConvert.SerializeObject(light).Contains("LightPipelineParameters"))
                throw new Exception("Legacy lights acquired unnecessary serialized metadata.");
            Write(light);
            if (ReplayReader.Read(path).Worlds.Single().World!.Lights.Single().LightPipelineParameters.Count != 0)
                throw new Exception("Legacy light defaults were not retained.");
            light.Type = "Disc"; Write(light); ReplayReader.Read(path);
            Reject(value => value.LightPipelineParameters.Add(value.LightPipelineParameters[0]));
            Reject(value => value.LightPipelineParameters.Add(null!));
            Reject(value => value.LightPipelineParameters[0].Values[0] = float.NaN);
            Reject(value => value.LightPipelineParameters[0].Values[0] = float.PositiveInfinity);
            Reject(value => value.LightPipelineParameters[0].Values[0] = 1000001f);
            Reject(value => value.LightPipelineParameters[0].Values = new[] { 1f, 2f });
            Reject(value => value.LightPipelineParameters[1].Values[0] = 2);
            Reject(value => value.LightPipelineParameters[2].Text = new string('x', 65));
            Reject(value => value.LightPipelineParameters[0].CurveKeys = new float[7]);
            Reject(value => value.LightPipelineParameters[0].Name = new string('x', 65));
            Reject(value => value.LightPipelineParameters = Enumerable.Range(0, 41)
                .Select(i => new EnvironmentParameterSnapshot { Name = "value" + i, Kind = "float", Values = new[] { 1f } }).ToList());
        }
        finally { File.Delete(path); }
    }
}
