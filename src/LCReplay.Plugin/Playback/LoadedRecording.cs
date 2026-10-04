using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LCReplay.Core;
using LCReplay.Core.Archive;

namespace LCReplay.Plugin.Playback
{
    internal sealed class LoadedRecording
    {
        internal ReplaySession Session = null!;
        internal ReplayRecordingTimeline Timeline = null!;
        internal int PartIndex;

        internal static LoadedRecording Read(ArchiveIndex index, string path, CancellationToken cancellation,
            Action<string, double>? progress = null)
        {
            progress?.Invoke("Reading header", 0.01);
            cancellation.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            var clip = index.Runs.SelectMany(run => run.Sessions).SelectMany(group => group.Days)
                .FirstOrDefault(day => day.Segments.Any(part => string.Equals(part.FilePath, full, StringComparison.OrdinalIgnoreCase)));
            var parts = clip?.Segments.OrderBy(part => part.Part).ThenBy(part => part.FilePath, StringComparer.OrdinalIgnoreCase).ToArray()
                ?? new[] { new ArchiveSegment { FilePath = full, Part = 1 } };
            var selected = Array.FindIndex(parts, part => string.Equals(part.FilePath, full, StringComparison.OrdinalIgnoreCase));
            selected = Math.Max(0, selected);
            var headerForLayout = ReplayReader.ReadHeader(full);
            if (headerForLayout.Metadata.TryGetValue("singleFile", out var singleFile) && singleFile == "true")
            {
                progress?.Invoke("Indexing recording", 0.03);
                // Read a full first playback window before opening the viewer.
                // A tiny opening window deferred actor, vehicle and item state
                // until playback was already visible.
                var fileIndex = ReplayReader.IndexSingleFile(full, cancellation, 48L * 1024 * 1024,
                    value => progress?.Invoke("Indexing recording", 0.03 + value * 0.17), initialWindowExpandedBytes: 48L * 1024 * 1024,
                    deferPayloadValidation: true);
                fileIndex.ReuseWorldPayloads = true;
                fileIndex.TrimInactiveActorState = true;
                var windows = fileIndex.Windows.Select(window => new ReplayRecordingPart
                {
                    FilePath = full,
                    StartedUtc = DateTimeOffset.TryParse(fileIndex.Header.StartedUtc, out var started)
                        ? started.AddSeconds(window.Start) : default,
                    Duration = window.Duration, Window = window
                }).ToArray();
                progress?.Invoke("Reading first section", 0.20);
                var firstWindow = ReplayReader.ReadWindow(fileIndex.Windows[0], cancellation,
                    value => progress?.Invoke("Reading first section", 0.20 + value * 0.65), preloadInitialMap: true);
                progress?.Invoke("Preparing scene", 0.85);
                return new LoadedRecording { Session = firstWindow,
                    Timeline = new ReplayRecordingTimeline(windows), PartIndex = 0 };
            }
            // A corrupt sibling must not prevent opening this independently recoverable file.
            // Read the selected file first so its errors and cancellation always propagate.
            progress?.Invoke("Reading recording", 0.03);
            var session = ReplayReader.Read(full, cancellationToken: cancellation,
                progress: value => progress?.Invoke("Reading recording", 0.03 + value * 0.82));
            var metadata = new List<ReplayRecordingPart> { Metadata(full, session.Header, session.Duration) };
            var warnings = new List<string>();
            var first = selected;
            for (var candidate = selected - 1; candidate >= 0; candidate--)
            {
                cancellation.ThrowIfCancellationRequested();
                if ((long)parts[candidate + 1].Part - parts[candidate].Part != 1)
                {
                    warnings.Add("Playback starts at part " + parts[candidate + 1].Part + " because earlier part numbers are missing or duplicated. Earlier files remain separate.");
                    break;
                }
                if (!TryMetadata(parts[candidate], cancellation, warnings, out var value)) break;
                metadata.Insert(0, value!);
                first = candidate;
            }
            for (var candidate = selected + 1; candidate < parts.Length; candidate++)
            {
                cancellation.ThrowIfCancellationRequested();
                if ((long)parts[candidate].Part - parts[candidate - 1].Part != 1)
                {
                    warnings.Add("Playback ends at part " + parts[candidate - 1].Part + " because later part numbers are missing or duplicated. Later files remain separate.");
                    break;
                }
                if (!TryMetadata(parts[candidate], cancellation, warnings, out var value)) break;
                metadata.Add(value!);
            }
            if (parts[0].Part > 1 && first == 0)
                warnings.Add("Playback begins at part " + parts[0].Part + "; earlier recording parts are missing.");
            session.Warnings.AddRange(warnings);
            session.Header.Warnings.AddRange(warnings);
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke("Preparing scene", 0.85);
            return new LoadedRecording { Timeline = new ReplayRecordingTimeline(metadata), PartIndex = selected - first, Session = session };
        }

        private static bool TryMetadata(ArchiveSegment part, CancellationToken cancellation, List<string> warnings, out ReplayRecordingPart? value)
        {
            try
            {
                var header = ReplayReader.ReadHeader(part.FilePath);
                value = Metadata(part.FilePath, header, ReplayReader.ReadDuration(part.FilePath, cancellation));
                return true;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
            {
                cancellation.ThrowIfCancellationRequested();
                value = null;
                warnings.Add("Playback is limited to the continuous readable parts around the selected file. Part " + part.Part +
                    " (" + Path.GetFileName(part.FilePath) + ") could not be read: " + error.Message);
                return false;
            }
        }

        private static ReplayRecordingPart Metadata(string path, ReplayHeader header, double duration)
        {
            DateTimeOffset.TryParse(header.StartedUtc, out var started);
            return new ReplayRecordingPart { FilePath = path, StartedUtc = started, Duration = duration };
        }
    }
}
