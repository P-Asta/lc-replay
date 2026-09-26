using System.Globalization;
using System.Text;
using LCReplay.Core;
using Newtonsoft.Json;

return MainCommand(args);

static int MainCommand(string[] arguments)
{
    if (arguments.Length == 1 && (arguments[0] == "--help" || arguments[0] == "-h"))
    {
        Help(); return 0;
    }
    try
    {
        switch (arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "")
        {
            case "inspect" when arguments.Length == 2:
                Inspect(arguments[1]); return 0;
            case "export" when arguments.Length == 3:
                Export(arguments[1], arguments[2]); return 0;
            case "demo" when arguments.Length == 2:
                Demo(arguments[1]); return 0;
            default:
                Help(); return 2;
        }
    }
    catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                                  error is NotSupportedException || error is JsonException || error is InvalidOperationException)
    {
        Console.Error.WriteLine("Error: " + error.Message);
        return 1;
    }
}

static void Help()
{
    Console.WriteLine("LCReplay file tools\n" +
        "  inspect <recording.lcr>                  Show metadata and counts.\n" +
        "  export <recording.lcr> <output.jsonl>     Export validated records as UTF-8 JSON lines.\n" +
        "  demo <output.lcr>                       Create a synthetic 12-second recording.\n" +
        "Output files must not already exist. Exit codes: 0 success, 1 failure, 2 usage error.");
}

static void Inspect(string path)
{
    var header = ReplayReader.ReadHeader(path);
    var singleFile = header.Metadata.TryGetValue("singleFile", out var marker) && marker == "true";
    var index = singleFile ? ReplayReader.IndexSingleFile(path) : null;
    var session = singleFile ? null : ReplayReader.Read(path);
    Console.WriteLine("File: " + Path.GetFullPath(path));
    Console.WriteLine("Session: " + header.SessionId);
    Console.WriteLine("Schema: " + header.SchemaVersion);
    Console.WriteLine("Game / Unity / Recorder: " + header.GameVersion + " / " + header.UnityVersion + " / " + header.RecorderVersion);
    Console.WriteLine("Started UTC: " + header.StartedUtc);
    Console.WriteLine("Perspective: " + header.Perspective);
    Console.WriteLine("Duration: " + (index?.Duration ?? session!.Duration).ToString("0.###", CultureInfo.InvariantCulture) + " seconds");
    Console.WriteLine("Sample rate: " + header.SampleRate + " Hz");
    Console.WriteLine($"Frames: {index?.FrameCount ?? session!.Frames.Count}; events: {index?.EventCount ?? session!.Events.Count}; worlds: {index?.WorldCount ?? session!.Worlds.Count}");
    Console.WriteLine("Complete: " + (index?.IsComplete ?? session!.IsComplete).ToString().ToLowerInvariant());
    if (index != null) Console.WriteLine("Playback windows: " + index.Windows.Count);
    if (header.Capabilities.Count > 0) Console.WriteLine("Capabilities: " + string.Join(", ", header.Capabilities));
    foreach (var pair in header.Metadata) Console.WriteLine("Metadata " + pair.Key + ": " + pair.Value);
    foreach (var warning in session?.Warnings ?? header.Warnings) Console.WriteLine("Warning: " + warning);
}

static void Export(string inputPath, string outputPath)
{
    var session = ReplayReader.Read(inputPath);
    var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, TypeNameHandling = TypeNameHandling.None };
    using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    using var output = new StreamWriter(file, new UTF8Encoding(false));
    long count = 0;
    void Write(ReplayRecord record) { output.WriteLine(JsonConvert.SerializeObject(record, settings)); count++; }
    Write(new ReplayRecord { Kind = "header", Header = session.Header });
    // OrderBy is stable: worlds precede frames and then events at equal timestamps.
    // Record arrays remain in their original per-kind order, including duplicate frame times.
    IEnumerable<ReplayRecord> records = session.Worlds
        .Concat(session.Frames.Select(frame => new ReplayRecord { Kind = "frame", Time = frame.Time, Frame = frame }))
        .Concat(session.Events.Select(item => new ReplayRecord { Kind = "event", Time = item.Time, Event = item }));
    foreach (var record in records.OrderBy(record => record.Time)) Write(record);
    if (session.IsComplete) Write(new ReplayRecord { Kind = "end", Time = session.Duration });
    output.Flush();
    Console.WriteLine($"Exported {count} records to {Path.GetFullPath(outputPath)}");
    foreach (var warning in session.Warnings) Console.Error.WriteLine("Warning: " + warning);
    if (!session.IsComplete) Console.Error.WriteLine("Warning: The recovered export has no end marker because the source recording is incomplete.");
}

static void Demo(string outputPath)
{
    var header = new ReplayHeader
    {
        SessionId = "synthetic-demo-" + Guid.NewGuid().ToString("N"),
        GameVersion = "SYNTHETIC - no game capture",
        UnityVersion = "not applicable",
        RecorderVersion = "LCReplay.Cli 0.1.0",
        StartedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        Perspective = "synthetic demonstration",
        SampleRate = 10,
        Metadata = new() { ["synthetic"] = "true", ["description"] = "Generated demonstration, not recorded Lethal Company gameplay." },
        Capabilities = new() { "synthetic-transforms", "world-mesh", "entity-state", "events" },
        Warnings = new() { "SYNTHETIC DEMO: This file contains generated motion and fabricated events, not gameplay." }
    };
    using var writer = new ReplayWriter(outputPath, header, 256);
    void Write(ReplayRecord record)
    {
        if (!writer.TryWrite(record)) throw new IOException("Demo writer refused a record: " + (writer.Error?.Message ?? "queue full"));
    }
    var ground = new GeometrySnapshot
    {
        Id = "demo-ground", Name = "Synthetic demonstration floor", Color = new[] { 0.25f, 0.29f, 0.3f, 1f },
        BoundsCenter = new Vec3(0, -0.05f, 0), BoundsSize = new Vec3(24, 0.1f, 24),
        Vertices = new float[] { -12, 0, -12, -12, 0, 12, 12, 0, 12, 12, 0, -12 },
        Triangles = new[] { 0, 1, 2, 0, 2, 3 }
    };
    Write(new ReplayRecord { Kind = "world", World = new WorldSnapshot { Scene = "SyntheticDemo", Geometry = new() { ground } } });
    for (int tick = 0; tick <= 120; tick++)
    {
        double time = tick / 10.0;
        var playerOne = SyntheticEntity("demo-player-1", "player", "Demo player 1", new Vec3((float)(-8 + 1.25 * time), 1, -2));
        playerOne.State["health"] = time < 6 ? "100" : time < 8 ? "35" : "0";
        playerOne.State["isPlayerDead"] = time >= 8 ? "true" : "false";
        if (time >= 8) playerOne.Position = new Vec3(2, 0.3f, -2);
        var playerTwo = SyntheticEntity("demo-player-2", "player", "Demo player 2", new Vec3((float)(4 * Math.Cos(time * 0.5)), 1, (float)(4 * Math.Sin(time * 0.5))));
        playerTwo.State["health"] = "100";
        playerTwo.State["isPlayerDead"] = "false";
        var enemy = SyntheticEntity("demo-enemy", "enemy", "Demo enemy", new Vec3((float)(7 - time * 0.5), 1.3f, (float)(-2 + Math.Sin(time))));
        enemy.State["enemyHP"] = "3";
        var item = SyntheticEntity("demo-item", "item", "Demo scrap", time < 4 ? new Vec3(-2, 0.4f, 1) : new Vec3(playerTwo.Position.X, 1.3f, playerTwo.Position.Z));
        item.Scale = new Vec3(0.35f, 0.35f, 0.35f);
        item.State["scrapValue"] = "42";
        item.State["isHeld"] = time >= 4 ? "true" : "false";
        item.State["holder"] = time >= 4 ? playerTwo.Id : "";
        var frame = new ReplayFrame
        {
            Time = time, Entities = new() { playerOne, playerTwo, enemy, item },
            State = new() { ["synthetic"] = "true", ["scene"] = "SyntheticDemo" }
        };
        Write(new ReplayRecord { Kind = "frame", Time = time, Frame = frame });
        if (tick == 40) DemoEvent(Write, time, "item", "synthetic_pickup", playerTwo.Id, new() { ["item"] = item.Id });
        if (tick == 60) DemoEvent(Write, time, "player", "synthetic_damage", playerOne.Id, new() { ["health"] = "35" });
        if (tick == 80) DemoEvent(Write, time, "player", "synthetic_death", playerOne.Id, new() { ["health"] = "0" });
    }
    writer.Dispose();
    if (writer.Error != null) throw new IOException("Could not complete demo replay.", writer.Error);
    Console.WriteLine("Created SYNTHETIC demonstration: " + Path.GetFullPath(outputPath));
    Console.WriteLine("12 seconds; 121 frames; 2 players, 1 enemy, 1 item; 3 fabricated events; 1 floor mesh.");
}

static EntitySnapshot SyntheticEntity(string id, string kind, string name, Vec3 position) => new()
{ Id = id, Kind = kind, Name = name, Position = position, State = new() { ["synthetic"] = "true" } };

static void DemoEvent(Action<ReplayRecord> write, double time, string category, string name, string id, Dictionary<string, string> data) =>
    write(new ReplayRecord { Kind = "event", Time = time, Event = new ReplayEvent
    { Time = time, Category = category, Name = name, EntityId = id, Data = data } });
