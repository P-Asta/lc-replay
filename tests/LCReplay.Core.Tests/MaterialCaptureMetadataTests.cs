using LCReplay.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class MaterialCaptureMetadataTests
{
    internal static void Run()
    {
        var legacy = JsonConvert.DeserializeObject<MaterialSnapshot>("{\"Id\":\"legacy\",\"Name\":\"old surface\"}")!;
        if (legacy.KeywordsComplete.HasValue || JObject.FromObject(legacy)["KeywordsComplete"] != null)
            throw new Exception("Legacy materials must not claim a complete empty keyword capture.");
        var complete = new MaterialSnapshot { Id = "complete", KeywordsComplete = true };
        var truncated = new MaterialSnapshot
        {
            Id = "truncated", KeywordsComplete = false,
            Properties = new List<MaterialPropertySnapshot>
            {
                new() { Name = "_Smoothness", Kind = "float", Values = new[] { 0f } },
                new() { Name = "_NormalMap", Kind = "texture", Values = new[] { 0f } }
            }
        };
        if ((bool?)JObject.FromObject(complete)["KeywordsComplete"] != true ||
            (bool?)JObject.FromObject(truncated)["KeywordsComplete"] != false)
            throw new Exception("Complete and truncated keyword states were conflated.");
        var world = new WorldSnapshot
        {
            Materials = new List<MaterialSnapshot> { legacy, complete, truncated },
            Geometry = new List<GeometrySnapshot>
            {
                new() { Id = "layered", Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f },
                    Triangles = new[] { 0, 1, 2 }, MaterialIds = new List<string> { legacy.Id, complete.Id, truncated.Id } }
            }
        };
        var path = Path.Combine(Path.GetTempPath(), "lcreplay-material38-" + Guid.NewGuid().ToString("N") + ".lcr");
        try
        {
            using (var writer = new ReplayWriter(path, new ReplayHeader()))
                if (!writer.TryWrite(new ReplayRecord { Kind = "world", World = world }))
                    throw new Exception("Material metadata fixture was not accepted.");
            var recording = ReplayReader.Read(path);
            var restored = recording.Worlds.Single().World!;
            if (!recording.IsComplete || !restored.Materials.Select(value => value.KeywordsComplete).SequenceEqual(new bool?[] { null, true, false }))
                throw new Exception("Keyword completeness changed through bounded file IO.");
            if (restored.Geometry[0].MaterialIds.Count != 3 || restored.Materials[2].Properties[0].Values.Single() != 0 ||
                restored.Materials[2].Properties[1].TextureId != "" || restored.Materials[2].Properties[1].Values.Single() != 0)
                throw new Exception("Layered material slots or explicit zero/null overrides changed through file IO.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
