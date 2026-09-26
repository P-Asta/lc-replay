using LCReplay.Core;
using LCReplay.Core.Archive;
using LCReplay.Plugin.Playback;

var suite = new (string Name, Action Run)[]
{
    ("One launch preserves two sessions and three day recordings", Hierarchy),
    ("Rotation continues only within the same day in numeric order", Rotation),
    ("Relaunch, reconnect and repeated day numbers never overwrite files", UniquePaths),
    ("Missing and torn manifests recover all files from bounded headers", Recovery),
    ("Legacy recordings remain visible without accidental continuation", Legacy),
    ("Legacy multipart replays group only by authoritative group and part headers", LegacyGroups),
    ("Unknown, incomplete and oversized headers remain visible", BrokenHeaders),
    ("Index never reads frame payloads", HeaderOnly),
    ("Archive scans obey entry, file, directory and recovery limits", Bounds),
    ("Detached scans can run concurrently with manifest updates", ConcurrentScan),
    ("Atomic manifest replacement survives a transient reader without delete sharing", TransientManifestLock),
    ("A persistent manifest lock preserves the complete previous index", PersistentManifestLock),
    ("Delete-sharing readers retain their old snapshot during atomic replacement", SharedManifestReader),
    ("Archive duration matches playback gaps and overlaps without joining separate clips", IndexedTimelineDuration),
    ("Past recording manifests are interrupted and paths cannot escape archive", Safety),
    ("Clean shutdown persists a complete run while crashes remain interrupted", RunCompletion),
    ("Long Gale profile roots save and replay complete compact archives below MAX_PATH", LongProfileRoot),
    ("Compact archives coexist with and recover the original full GUID folder layout", OriginalFolderLayout),
    ("Excessive configured roots fail clearly before creating an unusable day folder", ExcessiveRoot),
    ("Atomic saves fit the 259 character boundary and preserve pre-existing temporary files", ExactPathBoundary),
    ("New recordings use one quota/deadline .lcr per day with authoritative metadata", QuotaHierarchy),
    ("Quota groups reuse across reconnects while each deadline recording remains distinct", QuotaReconnects),
    ("Unavailable quota and deadline values remain unknown instead of invented zeroes", UnknownQuota),
    ("Quota hierarchy recovers numeric grouping and header metadata after manifest loss", QuotaRecovery),
    ("Quota, compact session and loose legacy archives coexist without rewriting old files", MixedQuotaArchive),
    ("Numeric quota folder collisions preserve unrelated files and long paths remain bounded", QuotaPathSafety),
    ("Full recording playback keeps the readable span around a corrupt sibling", LoadedRecordingCorruptSibling),
    ("Full recording playback never silently bridges missing part numbers", LoadedRecordingMissingPart),
    ("Truncated final segments recover while selected corruption and cancellation propagate", LoadedRecordingRecovery),
};
int failures = 0;
foreach (var test in suite)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"{suite.Length - failures}/{suite.Length} archive test groups passed.");
return failures == 0 ? 0 : 1;

static void Hierarchy() => WithTemp(root =>
{
    var archive = new ReplayArchive(root);
    var run = archive.BeginRun(Now());
    var first = archive.BeginSession(run, Now(), "Friends lobby", "host");
    var day1 = archive.BeginDay(first, 1, Now(), "Experimentation", 4);
    Write(archive, archive.AllocateSegment(day1, 1), 10);
    archive.EndDay(day1, "complete");
    var day2 = archive.BeginDay(first, 2, Now().AddMinutes(1), "Assurance", 5);
    Write(archive, archive.AllocateSegment(day2, 1), 20);
    archive.EndDay(day2, "complete"); archive.EndSession(first);
    var second = archive.BeginSession(run, Now().AddHours(1), "New lobby", "client");
    var day3 = archive.BeginDay(second, 1, Now().AddHours(1), "Titan");
    Write(archive, archive.AllocateSegment(day3, 1), 30);
    archive.EndDay(day3, "complete"); archive.EndSession(second);
    var index = archive.Scan();
    Check(index.Warnings.Count == 0 && index.Runs.Count == 1, "one run, no warnings");
    var found = index.Runs.Single();
    Check(found.Sessions.Count == 2 && found.Sessions.Sum(s => s.Days.Count) == 3, "session/day counts");
    Check(found.Sessions.All(s => s.Status == "complete"), "closed sessions");
    Check(found.DurationSeconds == 60 && found.Bytes > 0, "tree aggregate duration and size");
    var recorded = found.Sessions.Single(s => s.Id == first.Id).Days.Single(d => d.DayNumber == 1);
    Check(recorded.CampaignDay == 4 && recorded.Moon == "Experimentation", "day metadata");
    Check(AllSegments(index).All(s => Path.IsPathFullyQualified(s.FilePath) && File.Exists(s.FilePath)), "absolute existing paths");
    Check(Directory.GetFiles(root, "*.lcr", SearchOption.TopDirectoryOnly).Length == 0, "new files live in hierarchy");
});

static void Rotation() => WithTemp(root =>
{
    var archive = new ReplayArchive(root);
    var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "Lobby", "host");
    var day = archive.BeginDay(session, 1, Now(), "Titan");
    var ten = archive.AllocateSegment(day, 10); Write(archive, ten, 1);
    var two = archive.AllocateSegment(day, 2); Write(archive, two, 2);
    var one = archive.AllocateSegment(day, 1); Write(archive, one, 3);
    var other = archive.BeginDay(session, 2, Now(), "Titan"); Write(archive, archive.AllocateSegment(other, 1), 4);
    var index = archive.Scan();
    Check(index.FindNextSegment(one.FilePath)?.FilePath == two.FilePath, "part one -> two");
    Check(index.FindNextSegment(two.FilePath)?.FilePath == ten.FilePath, "numeric two -> ten");
    Check(index.FindNextSegment(ten.FilePath) == null, "never auto-advance to another day");
    var header = ReplayReader.ReadHeader(two.FilePath);
    Check(header.Metadata["dayId"] == day.Id && header.Metadata["part"] == "2", "rotation header identity");
    Check(header.Metadata["runId"] == run.Id && header.Metadata["sessionId"] == session.Id, "ancestry header identity");
});

static void UniquePaths() => WithTemp(root =>
{
    var archive = new ReplayArchive(root);
    var one = archive.BeginRun(Now()); var two = archive.BeginRun(Now());
    Check(one.DirectoryPath != two.DirectoryPath, "same-second launch uniqueness");
    var lobby = archive.BeginSession(one, Now(), "Lobby", "host");
    var reconnect = archive.BeginSession(one, Now(), "Lobby", "host");
    Check(lobby.DirectoryPath != reconnect.DirectoryPath && reconnect.SessionNumber == 2, "reconnection identity");
    var day = archive.BeginDay(lobby, 1, Now(), "Titan");
    var repeat = archive.BeginDay(lobby, 1, Now(), "Titan");
    Check(day.DirectoryPath != repeat.DirectoryPath, "same day reload identity");
    var file = archive.AllocateSegment(day, 1); Write(archive, file, 5);
    byte[] original = File.ReadAllBytes(file.FilePath);
    var second = archive.AllocateSegment(day, 1); Write(archive, second, 6);
    Check(second.Part == 2 && original.SequenceEqual(File.ReadAllBytes(file.FilePath)), "never overwrite allocated segments");
});

static void Recovery() => WithTemp(root =>
{
    var archive = new ReplayArchive(root);
    var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "Friends", "host");
    var day = archive.BeginDay(session, 3, Now(), "Dine", 12);
    var one = archive.AllocateSegment(day, 1); Write(archive, one, 5);
    var two = archive.AllocateSegment(day, 2); Write(archive, two, 5);
    File.WriteAllText(Path.Combine(run.DirectoryPath, "run.json"), "{broken");
    File.Delete(Path.Combine(session.DirectoryPath, "session.json"));
    File.WriteAllText(Path.Combine(day.DirectoryPath, "day.json"), "null");
    File.WriteAllText(Path.ChangeExtension(one.FilePath, ".json"), "{}");
    File.WriteAllText(Path.ChangeExtension(two.FilePath, ".json"), "{torn");
    File.WriteAllText(Path.ChangeExtension(two.FilePath, ".json") + ".tmp-abandoned", "{unfinished");
    var index = new ReplayArchive(root).Scan();
    Check(index.Warnings.Count >= 3, "malformed manifests surface warnings");
    Check(AllSegments(index).Count() == 2 && AllSegments(index).All(s => s.HeaderReadable), "all valid files survive index loss");
    var recovered = index.Runs.Single().Sessions.Single().Days.Single();
    Check(recovered.DayNumber == 3 && recovered.Moon == "Dine" && recovered.CampaignDay == 12, "header and folder metadata recovery");
    Check(index.FindNextSegment(one.FilePath)?.FilePath == two.FilePath, "recovered rotation keeps same folder day");
});

static void Legacy() => WithTemp(root =>
{
    string one = Path.Combine(root, "same-0001.lcr"), two = Path.Combine(root, "same-0002.lcr");
    RawReplay(one, new ReplayHeader { SessionId = "legacy", StartedUtc = Now().ToString("O") });
    RawReplay(two, new ReplayHeader { SessionId = "legacy", StartedUtc = Now().ToString("O") });
    var index = new ReplayArchive(root).Scan();
    Check(index.Runs.Count == 1 && index.Runs[0].Id == "legacy" && AllSegments(index).Count() == 2, "legacy files visible");
    Check(index.FindNextSegment(one) == null, "legacy filename suffix never guesses same day");
});

static void LegacyGroups() => WithTemp(root =>
{
    string one = Path.Combine(root, "arbitrary-name.lcr"), two = Path.Combine(root, "entirely-unrelated-name.lcr");
    string other = Path.Combine(root, "arbitrary-name-part0003.lcr"), unnumbered = Path.Combine(root, "without-explicit-part.lcr");
    string duplicate = Path.Combine(root, "duplicate-part.lcr");
    RawReplay(one, new ReplayHeader { SessionId = "old-file-one", Metadata = new() { ["recordingGroup"] = "group-A", ["part"] = "1" } });
    RawReplay(two, new ReplayHeader { SessionId = "old-file-two", Metadata = new() { ["recordingGroup"] = "group-A", ["part"] = "2" } });
    RawReplay(other, new ReplayHeader { SessionId = "old-other", Metadata = new() { ["recordingGroup"] = "group-B", ["part"] = "3" } });
    RawReplay(unnumbered, new ReplayHeader { SessionId = "old-unnumbered", Metadata = new() { ["recordingGroup"] = "group-A" } });
    RawReplay(duplicate, new ReplayHeader { SessionId = "old-duplicate", Metadata = new() { ["recordingGroup"] = "group-A", ["part"] = "1" } });
    var index = new ReplayArchive(root).Scan();
    Check(AllSegments(index).Count() == 5 && index.Runs.Single().Sessions.Single().Days.Count == 4, "only authoritative distinct parts grouped");
    Check(index.FindNextSegment(one)?.FilePath == two, "legacy multipart playback preserved independently of names");
    Check(index.FindNextSegment(two) == null && index.FindNextSegment(other) == null && index.FindNextSegment(unnumbered) == null, "no chaining across actual groups or missing part metadata");
    Check(index.FindNextSegment(duplicate) == null && index.Warnings.Count == 1, "duplicate parts remain separate and visible");
});

static void BrokenHeaders() => WithTemp(root =>
{
    File.WriteAllText(Path.Combine(root, "bad.lcr"), "not a replay");
    File.WriteAllBytes(Path.Combine(root, "truncated.lcr"), new byte[] { 1, 2 });
    var index = new ReplayArchive(root).Scan();
    Check(AllSegments(index).Count() == 2 && AllSegments(index).All(s => !s.HeaderReadable && s.Status == "unreadable"), "bad files shown with error");
    Check(index.Warnings.Count == 2, "errors reported");
});

static void HeaderOnly() => WithTemp(root =>
{
    string path = Path.Combine(root, "damaged-tail.lcr");
    RawReplay(path, new ReplayHeader { SessionId = "header-only" });
    using (var stream = new FileStream(path, FileMode.Append)) stream.Write(new byte[] { 255, 255, 255, 127, 255, 255, 255, 127 });
    Check(ReplayReader.ReadHeader(path).SessionId == "header-only", "header succeeds despite invalid tail");
    Expect<InvalidDataException>(() => ReplayReader.Read(path));
    var index = new ReplayArchive(root).Scan();
    Check(AllSegments(index).Single().HeaderReadable && index.Warnings.Count == 0, "scan does not inspect data tail");
    byte[] bytes = File.ReadAllBytes(path);
    BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 8);
    File.WriteAllBytes(path, bytes);
    Expect<InvalidDataException>(() => ReplayReader.ReadHeader(path));
});

static void Bounds() => WithTemp(root =>
{
    for (int i = 0; i < 8; i++) RawReplay(Path.Combine(root, $"{i}.lcr"), new ReplayHeader { SessionId = "bounded" });
    var files = new ReplayArchive(root).Scan(new ArchiveScanLimits { MaxReplayFiles = 3 });
    Check(files.IsTruncated && AllSegments(files).Count() == 3, "file limit");
    var headers = new ReplayArchive(root).Scan(new ArchiveScanLimits { MaxHeaderReads = 2 });
    Check(headers.IsTruncated && AllSegments(headers).Count() == 8 && AllSegments(headers).Count(s => s.HeaderReadable) == 2, "header recovery bound still lists files");
    var entries = new ReplayArchive(root).Scan(new ArchiveScanLimits { MaxEntries = 2 });
    Check(entries.IsTruncated && AllSegments(entries).Count() == 2, "entry budget");
    Directory.CreateDirectory(Path.Combine(root, "run"));
    var directories = new ReplayArchive(root).Scan(new ArchiveScanLimits { MaxDirectories = 1 });
    Check(directories.IsTruncated && directories.Runs.Count == 1 && directories.Runs[0].Id == "legacy", "directory budget");
});

static void ConcurrentScan() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "Concurrent", "host");
    var day = archive.BeginDay(session, 1, Now(), "Titan"); var segment = archive.AllocateSegment(day, 1); Write(archive, segment, 1);
    var snapshot = archive.Scan();
    var writer = Task.Run(() => { for (int i = 0; i < 30; i++) archive.UpdateDay(day, "Titan " + i, "recording"); });
    var reader = Task.Run(() => { for (int i = 0; i < 30; i++) Check(archive.Scan().Warnings.Count == 0, "atomic manifests remain readable"); });
    Task.WaitAll(writer, reader);
    Check(snapshot.Runs[0].Sessions[0].Days[0].Moon == "Titan", "scan returns detached snapshot");
    Check(archive.Scan().Runs[0].Sessions[0].Days[0].Moon == "Titan 29", "latest manifest saved");
});

static void TransientManifestLock() => WithTemp(root =>
{
    if (!OperatingSystem.IsWindows()) return; // Windows rejects replacement without FILE_SHARE_DELETE.
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Concurrent", "host"); var day = archive.BeginDay(group, 1, Now(), "Titan");
    var manifest = Path.Combine(day.DirectoryPath, "day.json"); var before = File.ReadAllText(manifest);
    Task writer;
    using (var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        writer = Task.Run(() => archive.UpdateDay(day, "Assurance", "exploring"));
        Check(SpinWait.SpinUntil(() => Directory.EnumerateFiles(day.DirectoryPath, "*.tmp").Any() || writer.IsCompleted, 2000), "writer prepared its durable sibling");
        Thread.Sleep(100);
        Check(!writer.IsCompleted, "writer retries the temporary replacement conflict instead of stopping recording");
        using var reader = new StreamReader(held, leaveOpen: true);
        Check(reader.ReadToEnd() == before, "the old complete manifest stays readable throughout the conflict");
    }
    Check(writer.Wait(3000), "replacement succeeds after the conflicting reader closes");
    writer.GetAwaiter().GetResult();
    Check(archive.Scan().Runs.Single().Sessions.Single().Days.Single().Moon == "Assurance", "new index published after retry");
    Check(!Directory.EnumerateFiles(day.DirectoryPath, "*.tmp").Any(), "successful retry leaves no temporary file");
});

static void PersistentManifestLock() => WithTemp(root =>
{
    if (!OperatingSystem.IsWindows()) return;
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Locked", "host"); var day = archive.BeginDay(group, 1, Now(), "Titan");
    var manifest = Path.Combine(day.DirectoryPath, "day.json"); var before = File.ReadAllBytes(manifest);
    using (var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Expect<IOException>(() => archive.UpdateDay(day, "Assurance", "exploring"));
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(4), "persistent contention fails in a bounded time");
        Check(File.ReadAllBytes(manifest).SequenceEqual(before), "failed publish never removes or truncates the previous manifest");
    }
    Check(!Directory.EnumerateFiles(day.DirectoryPath, "*.tmp").Any(), "failed retry cleans up only its own temporary file");
    Check(new ReplayArchive(root).Scan().Runs.Single().Sessions.Single().Days.Single().Moon == "Titan", "prior index still recovers after failure");
    archive.UpdateDay(day, "Assurance", "exploring");
    Check(archive.Scan().Runs.Single().Sessions.Single().Days.Single().Moon == "Assurance", "a later retry succeeds once the lock is gone");
});

static void SharedManifestReader() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Shared", "host"); var day = archive.BeginDay(group, 1, Now(), "Titan");
    var manifest = Path.Combine(day.DirectoryPath, "day.json"); var before = File.ReadAllText(manifest);
    using var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    archive.UpdateDay(day, "Assurance", "exploring");
    using var reader = new StreamReader(held);
    Check(reader.ReadToEnd() == before, "delete-sharing reader keeps the complete previous snapshot");
    Check(archive.Scan().Runs.Single().Sessions.Single().Days.Single().Moon == "Assurance", "new readers see the complete replacement");
});

static void IndexedTimelineDuration() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Clock", "host");
    var first = archive.BeginDay(group, 1, Now(), "Titan");
    var second = archive.BeginDay(group, 2, Now().AddHours(1), "Assurance");
    var fixtures = new[]
    {
        (Day: first, Offsets: new[] { 0d, 12d, 18d }, Durations: new[] { 10d, 8d, 6d }, Expected: 26d),
        (Day: second, Offsets: new[] { 3600d, 3615d }, Durations: new[] { 5d, 3d }, Expected: 18d)
    };
    foreach (var fixture in fixtures)
        for (int i = 0; i < fixture.Offsets.Length; i++)
        {
            var segment = archive.AllocateSegment(fixture.Day, i + 1);
            segment.StartedUtc = Now().AddSeconds(fixture.Offsets[i]);
            Write(archive, segment, fixture.Durations[i]);
        }
    var index = archive.Scan(); var indexedGroup = index.Runs.Single().Sessions.Single();
    foreach (var fixture in fixtures)
    {
        var indexed = indexedGroup.Days.Single(day => day.Id == fixture.Day.Id);
        var playback = LoadedRecording.Read(index, fixture.Day.Segments[0].FilePath, CancellationToken.None);
        Check(indexed.DurationSeconds == fixture.Expected && indexed.DurationSeconds == playback.Timeline.Duration,
            "index and full playback use identical gap/overlap clock rules");
    }
    Check(indexedGroup.DurationSeconds == 44 && index.Runs.Single().DurationSeconds == 44,
        "group/run sum clip durations without adding the hour between independent recordings");
    Check(index.Warnings.Count == 0, "valid gaps and overlapping wall timestamps need no warning");
});

static void Safety() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "Open", "host");
    var day = archive.BeginDay(session, 1, Now(), "Titan"); var segment = archive.AllocateSegment(day, 1);
    RawReplay(segment.FilePath, new ReplayHeader { SessionId = "active", Metadata = segment.Metadata });
    Check(AllSegments(archive.Scan()).Single().Status == "recording", "current instance recognizes active writer");
    Check(AllSegments(new ReplayArchive(root).Scan()).Single().Status == "interrupted", "past active marker never remains recording");
    Expect<ArgumentException>(() => archive.EndDay(new ArchiveDay { DirectoryPath = root }, "complete"));
    segment.FilePath = Path.Combine(Path.GetDirectoryName(root)!, "outside.lcr");
    Expect<IOException>(() => archive.CompleteSegment(segment, 0, false, null));
});

static void RunCompletion() => WithTemp(root =>
{
    var archive = new ReplayArchive(root);
    var clean = archive.BeginRun(Now());
    var crashed = archive.BeginRun(Now().AddSeconds(1));
    Check(archive.Scan().Runs.All(r => r.Status == "recording"), "both active launches initially recording");
    archive.EndRun(clean);
    Check(archive.Scan().Runs.Single(r => r.Id == clean.Id).Status == "complete", "current index sees clean completion");
    var reopened = new ReplayArchive(root).Scan();
    Check(reopened.Runs.Single(r => r.Id == clean.Id).Status == "complete", "clean shutdown survives fresh process index");
    Check(reopened.Runs.Single(r => r.Id == crashed.Id).Status == "interrupted", "missing shutdown remains interrupted");
});

static void LongProfileRoot() => WithTemp(root =>
{
    string profileRoot = Path.Combine(root, "com.kesomannen.gale", "lethal-company", "profiles", "rec", "BepInEx", "replays");
    var archive = new ReplayArchive(profileRoot);
    var run = archive.BeginRun(Now());
    var session = archive.BeginSession(run, Now(), "Gale profile", "host");
    var day = archive.BeginDay(session, 1, Now(), "Experimentation", 1);
    var first = archive.AllocateSegment(day, 1); Write(archive, first, 10);
    archive.UpdateDay(day, "Assurance", "recording");
    var second = archive.AllocateSegment(day, 2); Write(archive, second, 20);
    archive.EndDay(day, "complete"); archive.EndSession(session); archive.EndRun(run);
    Check(run.Id.Length == 32 && session.Id.Length == 32 && day.Id.Length == 32, "compact names preserve full metadata identity");
    Check(Path.GetFileName(run.DirectoryPath).EndsWith(run.Id[..12]) && Path.GetFileName(day.DirectoryPath).EndsWith(day.Id[..12]), "directories use compact random suffixes");
    string oldDay = Path.Combine(profileRoot, "Run-20260923-120000-" + run.Id, "Session-001-" + session.Id, "Day-001-" + day.Id);
    Check(Path.Combine(oldDay, "day.json.tmp-" + Guid.NewGuid().ToString("N")).Length >= 260, "fixture reproduces former Mono path overflow");
    Check(Path.Combine(day.DirectoryPath, "~000000000000.tmp").Length < 260, "atomic save has room for its short sibling temporary file");
    Check(Directory.EnumerateFileSystemEntries(profileRoot, "*", SearchOption.AllDirectories).All(path => path.Length < 260), "all persisted paths fit Windows Mono");
    Check(!Directory.EnumerateFiles(profileRoot, "*.tmp", SearchOption.AllDirectories).Any(), "atomic updates leave no temporary files");
    var index = new ReplayArchive(profileRoot).Scan();
    Check(index.Warnings.Count == 0 && AllSegments(index).Count() == 2, "complete long-root archive reloads");
    Check(index.FindNextSegment(first.FilePath)?.FilePath == second.FilePath, "long-root multipart continuation");
    var replay = ReplayReader.Read(second.FilePath);
    Check(replay.IsComplete && replay.Duration == 20 && replay.Header.Metadata["runId"] == run.Id && replay.Header.Metadata["dayId"] == day.Id,
        "full replay data and original metadata IDs survive long-root saves");
});

static void OriginalFolderLayout() => WithTemp(root =>
{
    string runId = Guid.NewGuid().ToString("N"), sessionId = Guid.NewGuid().ToString("N"), dayId = Guid.NewGuid().ToString("N");
    string oldDay = Path.Combine(root, "Run-20260923-120000-" + runId, "Session-001-" + sessionId, "Day-007-" + dayId);
    Directory.CreateDirectory(oldDay);
    string oldFile = Path.Combine(oldDay, "part-0001.lcr");
    RawReplay(oldFile, new ReplayHeader
    {
        SessionId = sessionId, Metadata = new() { ["runId"] = runId, ["sessionId"] = sessionId, ["dayId"] = dayId, ["dayNumber"] = "7", ["part"] = "1", ["moon"] = "Titan" }
    });
    byte[] original = File.ReadAllBytes(oldFile);
    var archive = new ReplayArchive(root);
    var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "New", "host");
    var day = archive.BeginDay(session, 1, Now(), "Assurance"); Write(archive, archive.AllocateSegment(day, 1), 1);
    var index = archive.Scan();
    Check(index.Runs.Count == 2 && AllSegments(index).Count() == 2, "original and compact archive layouts coexist");
    var recovered = index.Runs.SelectMany(r => r.Sessions).SelectMany(s => s.Days).Single(d => d.DirectoryPath == oldDay);
    Check(recovered.DayNumber == 7 && recovered.Moon == "Titan" && recovered.Segments.Single().HeaderReadable, "old full GUID folders recover normally");
    Check(original.SequenceEqual(File.ReadAllBytes(oldFile)), "old recordings are never renamed or rewritten");
});

static void ExcessiveRoot() => WithTemp(root =>
{
    int padding = 170 - root.Length - 1;
    Check(padding > 0, "test root supports a deterministic long configured path");
    string configured = Path.Combine(root, new string('p', padding));
    var archive = new ReplayArchive(configured);
    var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "Long", "host");
    Expect<PathTooLongException>(() => archive.BeginDay(session, 1, Now(), "Titan"));
    Check(!Directory.EnumerateDirectories(session.DirectoryPath).Any(), "invalid long day is rejected before creating a partial folder");
});

static void ExactPathBoundary() => WithTemp(root =>
{
    string configured = Path.Combine(root, new string('b', 162 - root.Length - 1));
    var archive = new ReplayArchive(configured);
    var run = archive.BeginRun(Now()); var session = archive.BeginSession(run, Now(), "Boundary", "host");
    var day = archive.BeginDay(session, 1, Now(), "Titan");
    string foreignTemporary = Path.Combine(day.DirectoryPath, "~000000000000.tmp");
    Check(foreignTemporary.Length == 259, "fixture reaches MAX_PATH minus null terminator");
    File.WriteAllText(foreignTemporary, "pre-existing temporary file from another writer");
    var segment = archive.AllocateSegment(day, 1); Write(archive, segment, 5);
    archive.UpdateDay(day, "Rend", "recording");
    archive.EndDay(day, "complete"); archive.EndSession(session); archive.EndRun(run);
    Check(File.ReadAllText(foreignTemporary) == "pre-existing temporary file from another writer", "manifest cleanup never deletes a temporary file it did not create");
    Check(ReplayReader.Read(segment.FilePath).IsComplete, "recording at boundary remains readable");
    Check(archive.Scan().Warnings.Count == 0, "unrelated temporary files do not break indexing");
    Check(Directory.GetFiles(day.DirectoryPath, "*.tmp").Length == 1, "only the pre-existing temporary file remains");
});

static void QuotaHierarchy() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var quota = new ArchiveQuotaSnapshot { Target = 150, Fulfilled = 20, DeadlineDaysRemaining = 3, DeadlineDaysTotal = 4, QuotaCycle = 2 };
    Check(quota.IsKnown && quota.Remaining == 130, "remaining quota reflects submitted profit rather than total target");
    var group = archive.BeginQuota(run, Now(), quota, "host");
    Check(group.IsQuotaGroup && group.QuotaRemaining == 130 && Path.GetFileName(group.DirectoryPath) == "130", "quota numeric folder");
    var clips = new List<ArchiveDay>();
    for (int remaining = 3; remaining >= 1; remaining--)
    {
        quota.DeadlineDaysRemaining = remaining;
        var clip = archive.BeginQuotaDay(group, 4 - remaining, Now().AddMinutes(3 - remaining), "Titan", quota, 7);
        Check(clip.DirectoryPath == group.DirectoryPath && clip.RecordingStem == remaining.ToString(), "deadline is the single-file stem");
        var savedFile = archive.AllocateSegment(clip, 1);
        Check(Path.GetFileName(savedFile.FilePath) == remaining + ".lcr", "quota/day.lcr layout");
        Write(archive, savedFile, 6);
        Expect<InvalidOperationException>(() => archive.AllocateSegment(clip, 2));
        archive.EndDay(clip, "saved");
        clips.Add(clip);
    }
    archive.EndSession(group); archive.EndRun(run);
    Check(!Directory.EnumerateDirectories(run.DirectoryPath, "Session-*", SearchOption.AllDirectories).Any()
        && !Directory.EnumerateDirectories(run.DirectoryPath, "Day-*", SearchOption.AllDirectories).Any(), "new recordings do not create session or day folders");
    var index = archive.Scan(); var saved = index.Runs.Single().Sessions.Single();
    Check(index.Warnings.Count == 0 && saved.IsQuotaGroup && saved.Days.Count == 3, "quota index round trip");
    Check(saved.Days.Select(day => day.DeadlineDaysRemaining).OrderBy(value => value).SequenceEqual(new int?[] { 1, 2, 3 }), "all remaining deadlines preserved");
    Check(saved.Days.All(day => day.QuotaRemaining == 130 && day.QuotaTarget == 150 && day.QuotaFulfilled == 20 && day.DeadlineDaysTotal == 4 && day.QuotaCycle == 2), "quota metadata round trip");
    var header = ReplayReader.ReadHeader(clips[0].Segments[0].FilePath);
    Check(header.Metadata["archiveLayout"] == "quota-deadline" && header.Metadata["quotaRemaining"] == "130"
        && header.Metadata["quotaTarget"] == "150" && header.Metadata["quotaFulfilled"] == "20"
        && header.Metadata["deadlineDaysRemaining"] == "3" && header.Metadata["deadlineDaysTotal"] == "4" && header.Metadata["quotaCycle"] == "2", "recoverable header metadata");
    Check(saved.Days.All(clip => clip.Segments.Count == 1), "each day has one physical replay file");
    Check(index.FindNextSegment(clips[0].Segments[0].FilePath) == null, "continuation never crosses a deadline recording");
});

static void QuotaReconnects() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var quota = new ArchiveQuotaSnapshot { Target = 130, Fulfilled = 0, DeadlineDaysRemaining = 3 };
    var group = archive.BeginQuota(run, Now(), quota, "host");
    var first = archive.BeginQuotaDay(group, 1, Now(), "Titan", quota); var part = archive.AllocateSegment(first, 1); Write(archive, part, 1);
    var original = File.ReadAllBytes(part.FilePath);
    archive.EndDay(first, "saved"); archive.EndSession(group);
    var reopened = archive.BeginQuota(run, Now().AddSeconds(1), quota, "client");
    Check(ReferenceEquals(group, reopened) && run.Sessions.Count == 1 && group.Status == "recording", "reconnect reopens the same remaining quota group");
    var second = archive.BeginQuotaDay(group, 1, Now(), "Titan", quota); Write(archive, archive.AllocateSegment(second, 1), 2);
    Check(first.DirectoryPath == second.DirectoryPath && first.RecordingStem == "3" && second.RecordingStem == "3-2" && first.Id != second.Id,
        "same-deadline reconnects use collision-safe file stems");
    Check(original.SequenceEqual(File.ReadAllBytes(part.FilePath)), "earlier clip bytes preserved");
    quota.Fulfilled = 50;
    Expect<ArgumentException>(() => archive.BeginQuotaDay(group, 1, Now(), "Titan", quota));
    var reduced = archive.BeginQuota(run, Now(), quota, "host");
    Check(Path.GetFileName(reduced.DirectoryPath) == "80" && reduced.Id != group.Id, "quota progress has its own numeric group");
    quota.Fulfilled = 999; quota.DeadlineDaysRemaining = 0;
    var paid = archive.BeginQuota(run, Now(), quota, "host");
    var due = archive.BeginQuotaDay(paid, 1, Now(), "Company", quota);
    Check(quota.Remaining == 0 && quota.IsKnown && Path.GetFileName(paid.DirectoryPath) == "0" && due.RecordingStem == "0", "fulfilled quota and deadline-now use real zero");
});

static void UnknownQuota() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var unavailable = new ArchiveQuotaSnapshot { Target = 130 };
    Check(!unavailable.IsKnown && unavailable.Remaining == null, "missing submitted profit does not imply zero");
    var group = archive.BeginQuota(run, Now(), unavailable, "client");
    var clip = archive.BeginQuotaDay(group, 1, Now(), "Unknown", unavailable);
    var segment = archive.AllocateSegment(clip, 1); Write(archive, segment, 2);
    Check(Path.GetFileName(group.DirectoryPath) == "unknown" && clip.RecordingStem == "unknown", "unavailable values have explicit unknown names");
    Check(!segment.Metadata.ContainsKey("quotaRemaining") && !segment.Metadata.ContainsKey("deadlineDaysRemaining"), "headers never invent unknown values");
    var stored = archive.Scan().Runs.Single().Sessions.Single();
    Check(stored.IsQuotaGroup && stored.QuotaRemaining == null && stored.Days.Single().DeadlineDaysRemaining == null, "unknown state survives scan");
    Check(new ArchiveQuotaSnapshot { Target = -1, Fulfilled = 0 }.Remaining == null, "invalid values stay unavailable");
    Check(new ArchiveQuotaSnapshot { Target = int.MaxValue, Fulfilled = 0 }.Remaining == int.MaxValue, "remaining arithmetic does not overflow");
});

static void QuotaRecovery() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var quota = new ArchiveQuotaSnapshot { Target = 150, Fulfilled = 20, DeadlineDaysRemaining = 2, DeadlineDaysTotal = 4, QuotaCycle = 3 };
    var group = archive.BeginQuota(run, Now(), quota, "host");
    var clip = archive.BeginQuotaDay(group, 7, Now(), "Dine", quota, 11);
    var one = archive.AllocateSegment(clip, 1); Write(archive, one, 3);
    File.Delete(Path.Combine(group.DirectoryPath, "quota.json"));
    File.WriteAllText(Path.Combine(clip.DirectoryPath, clip.RecordingStem + ".day.json"), "{bad");
    File.Delete(Path.ChangeExtension(one.FilePath, ".json"));
    var index = new ReplayArchive(root).Scan(); var recoveredGroup = index.Runs.Single().Sessions.Single();
    var recovered = recoveredGroup.Days.Single();
    Check(recoveredGroup.IsQuotaGroup && recoveredGroup.QuotaRemaining == 130, "numeric folder recovers quota grouping");
    Check(recovered.DeadlineDaysRemaining == 2 && recovered.QuotaRemaining == 130 && recovered.QuotaTarget == 150 && recovered.QuotaFulfilled == 20
        && recovered.DeadlineDaysTotal == 4 && recovered.QuotaCycle == 3 && recovered.DayNumber == 7 && recovered.CampaignDay == 11 && recovered.Moon == "Dine", "recording header recovers missing metadata");
    Check(recovered.Segments.Single().HeaderReadable && index.FindNextSegment(one.FilePath) == null, "single-file clip recovers from its header");
    var limited = archive.Scan(new ArchiveScanLimits { MaxDirectories = 2 });
    Check(limited.IsTruncated && !AllSegments(limited).Any(), "quota directory obeys directory budget");
});

static void MixedQuotaArchive() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var old = archive.BeginSession(run, Now(), "Legacy", "host");
    var day = archive.BeginDay(old, 1, Now(), "Titan"); var oldPart = archive.AllocateSegment(day, 1); Write(archive, oldPart, 2);
    string loose = Path.Combine(root, "old-loose.lcr"); RawReplay(loose, new ReplayHeader { SessionId = "old" });
    var before = File.ReadAllBytes(oldPart.FilePath); var looseBefore = File.ReadAllBytes(loose);
    var quota = new ArchiveQuotaSnapshot { Target = 130, Fulfilled = 0, DeadlineDaysRemaining = 3 };
    var group = archive.BeginQuota(run, Now(), quota, "host");
    var clip = archive.BeginQuotaDay(group, 1, Now(), "Experimentation", quota); Write(archive, archive.AllocateSegment(clip, 1), 3);
    var index = archive.Scan();
    Check(index.Warnings.Count == 0 && AllSegments(index).Count() == 3, "legacy and quota entries coexist");
    Check(index.Runs.SelectMany(item => item.Sessions).Count(item => item.IsQuotaGroup) == 1, "legacy sessions are not relabeled quota groups");
    Check(before.SequenceEqual(File.ReadAllBytes(oldPart.FilePath)) && looseBefore.SequenceEqual(File.ReadAllBytes(loose)), "old data never rewritten or migrated");
});

static void QuotaPathSafety() => WithTemp(root =>
{
    string profileRoot = Path.Combine(root, "com.kesomannen.gale", "lethal-company", "profiles", "rec", "BepInEx", "replays");
    var archive = new ReplayArchive(profileRoot); var run = archive.BeginRun(Now());
    var foreign = Path.Combine(run.DirectoryPath, "130"); Directory.CreateDirectory(foreign);
    var marker = Path.Combine(foreign, "foreign.txt"); File.WriteAllText(marker, "preserve this");
    var quota = new ArchiveQuotaSnapshot { Target = 130, Fulfilled = 0, DeadlineDaysRemaining = 3 };
    var group = archive.BeginQuota(run, Now(), quota, "host");
    Check(group.DirectoryPath != foreign && Path.GetFileName(group.DirectoryPath).StartsWith("130-"), "foreign numeric folder uses a collision suffix");
    var clip = archive.BeginQuotaDay(group, 1, Now(), "Titan", quota); var part = archive.AllocateSegment(clip, 1); Write(archive, part, 5);
    archive.EndDay(clip, "saved"); archive.EndSession(group); archive.EndRun(run);
    Check(File.ReadAllText(marker) == "preserve this", "foreign folder untouched");
    Check(Directory.EnumerateFileSystemEntries(profileRoot, "*", SearchOption.AllDirectories).All(path => path.Length < 260), "new quota layout remains compatible with Unity Mono MAX_PATH");
    Check(ReplayReader.Read(part.FilePath).IsComplete, "new long-root recording readable");
    Check(archive.Scan().Runs.SelectMany(item => item.Sessions).Single(item => item.Days.Count > 0).QuotaRemaining == 130, "collision suffix keeps quota identity");
});

static void LoadedRecordingCorruptSibling() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Recovery", "host"); var day = archive.BeginDay(group, 1, Now(), "Titan");
    var parts = Enumerable.Range(1, 4).Select(number => archive.AllocateSegment(day, number)).ToArray();
    foreach (var part in parts) Write(archive, part, 10);
    File.WriteAllBytes(parts[2].FilePath, Array.Empty<byte>());
    var index = archive.Scan();
    var before = LoadedRecording.Read(index, parts[1].FilePath, CancellationToken.None);
    Check(before.Timeline.Parts.Select(part => part.FilePath).SequenceEqual(parts.Take(2).Select(part => part.FilePath)) && before.PartIndex == 1,
        "complete prefix opens at selected part without joining the later intact part");
    Check(before.Session.IsComplete && before.Session.Warnings.Any(warning => warning.Contains("Part 3")) &&
        before.Session.Header.Warnings.Any(warning => warning.Contains("Part 3")), "boundary warning reaches runtime log and header");
    var after = LoadedRecording.Read(index, parts[3].FilePath, CancellationToken.None);
    Check(after.Timeline.Parts.Count == 1 && after.PartIndex == 0 && after.Session.IsComplete,
        "valid suffix opens independently after a corrupt earlier sibling");
    Check(after.Session.Warnings.Any(warning => warning.Contains("Part 3")), "suffix reports excluded boundary");
});

static void LoadedRecordingMissingPart() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Missing part", "host"); var day = archive.BeginDay(group, 1, Now(), "Titan");
    var first = archive.AllocateSegment(day, 1); var third = archive.AllocateSegment(day, 3);
    Write(archive, first, 10); Write(archive, third, 10);
    foreach (var selected in new[] { first, third })
    {
        var loaded = LoadedRecording.Read(archive.Scan(), selected.FilePath, CancellationToken.None);
        Check(loaded.Timeline.Parts.Count == 1 && loaded.Timeline.Parts[0].FilePath == selected.FilePath && loaded.PartIndex == 0,
            "missing part two splits both playback directions");
        Check(loaded.Session.Warnings.Any(warning => warning.Contains("missing or duplicated")), "number gap is explained");
    }
});

static void LoadedRecordingRecovery() => WithTemp(root =>
{
    var archive = new ReplayArchive(root); var run = archive.BeginRun(Now());
    var group = archive.BeginSession(run, Now(), "Recovery", "host"); var day = archive.BeginDay(group, 1, Now(), "Titan");
    var one = archive.AllocateSegment(day, 1); var two = archive.AllocateSegment(day, 2);
    Write(archive, one, 10); Write(archive, two, 12);
    using (var file = new FileStream(two.FilePath, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 5);
    var index = archive.Scan();
    var loaded = LoadedRecording.Read(index, one.FilePath, CancellationToken.None);
    Check(loaded.Timeline.Parts.Count == 2 && loaded.Timeline.Parts[1].Duration == 12,
        "a torn footer with recoverable frames remains in the continuous recording");
    var recovered = LoadedRecording.Read(index, two.FilePath, CancellationToken.None);
    Check(!recovered.Session.IsComplete && recovered.Session.Frames.Count == 1 && recovered.PartIndex == 1,
        "selected partial segment retains recovered frames and warning");
    Check(recovered.Session.Warnings.Any(warning => warning.Contains("no end marker")), "partial-file warning preserved");
    File.WriteAllBytes(two.FilePath, Array.Empty<byte>());
    Expect<InvalidDataException>(() => LoadedRecording.Read(index, two.FilePath, CancellationToken.None));
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Expect<OperationCanceledException>(() => LoadedRecording.Read(index, one.FilePath, cancelled.Token));
});

static IEnumerable<ArchiveSegment> AllSegments(ArchiveIndex index) => index.Runs.SelectMany(r => r.Sessions).SelectMany(s => s.Days).SelectMany(d => d.Segments);
static DateTimeOffset Now() => DateTimeOffset.Parse("2026-09-23T12:00:00Z");
static void Write(ReplayArchive archive, ArchiveSegment segment, double duration)
{
    var header = new ReplayHeader { SessionId = segment.SessionId, StartedUtc = segment.StartedUtc.ToString("O"), Metadata = segment.Metadata };
    using var writer = new ReplayWriter(segment.FilePath, header);
    Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = duration, Frame = new ReplayFrame { Time = duration } }), "frame accepted");
    writer.Dispose(); Check(writer.Error == null, "replay writer success");
    archive.CompleteSegment(segment, duration, true, null);
}
static void RawReplay(string path, ReplayHeader header) { using var writer = new ReplayWriter(path, header); }
static void WithTemp(Action<string> action)
{
    string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string root = Path.GetFullPath(Path.Combine(temp, "LCReplayArchiveTests-" + Guid.NewGuid().ToString("N")));
    if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test temporary path.");
    Directory.CreateDirectory(root);
    try { action(root); } finally { Directory.Delete(root, true); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
