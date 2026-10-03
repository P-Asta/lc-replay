using System;
using System.Collections.Generic;
using System.Linq;

namespace LCReplay.Core
{
    /// <summary>Align an initial moon capture with the recorded landing phase.
    /// World payloads are captured over many game ticks, so their write times
    /// do not necessarily describe when the already generated map became visible.</summary>
    public static class ReplayMapTiming
    {
        public static bool TryFindFirstLandingMap(ReplaySession session, out string captureSetId, out double time)
        {
            captureSetId = "";
            time = 0;
            if (session == null) throw new ArgumentNullException(nameof(session));
            var sets = session.Worlds.Where(record => record.World != null && record.World.CaptureSetId.Length != 0)
                .GroupBy(record => record.World!.CaptureSetId, StringComparer.Ordinal)
                .OrderBy(group => group.Min(record => record.Time)).ToArray();
            if (sets.Length < 2) return false;
            var opening = sets[0].Select(record => record.World!).ToArray();
            // A ship-only capture has the moving elevator/cabin but no moon
            // scene or generated rooms. A completed first map needs no shift.
            if (!opening.SelectMany(world => world.Geometry).Any(geometry =>
                    geometry.AnchorId == "ship-elevator" || geometry.Name == "ShipInside") ||
                opening.Any(world => world.Rooms.Count != 0 || world.AssetScene.Length != 0)) return false;
            var next = sets[1].ToArray();
            if (!next.Any(record => record.World!.Rooms.Count != 0 ||
                record.World.AssetScene.Length != 0 &&
                record.World.AssetRendererPaths.Count + record.World.AssetTerrainPaths.Count != 0)) return false;
            var landing = session.Frames.FirstOrDefault(frame =>
                (frame.State.TryGetValue("StartOfRound.inShipPhase", out var phase) &&
                    bool.TryParse(phase, out var inShipPhase) && !inShipPhase) ||
                (frame.State.TryGetValue("StartOfRound.shipHasLanded", out var landed) &&
                    bool.TryParse(landed, out var shipHasLanded) && shipHasLanded));
            if (landing == null || !next.Any(record => record.Time > landing.Time)) return false;
            captureSetId = sets[1].Key;
            time = landing.Time;
            return true;
        }

        /// <summary>Move only the initial exterior/interior capture burst.
        /// Later updates in the same set retain their own recorded times.</summary>
        public static void AlignInitialCapture(ReplaySession session, string captureSetId, double time)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrEmpty(captureSetId)) throw new ArgumentException("A capture set is required.", nameof(captureSetId));
            if (double.IsNaN(time) || double.IsInfinity(time) || time < 0)
                throw new ArgumentOutOfRangeException(nameof(time));
            var worlds = session.Worlds;
            var first = worlds.FindIndex(record => record.World?.CaptureSetId == captureSetId);
            if (first < 0 || worlds[first].World!.Layer != "exterior") return;
            var last = first;
            var hasInterior = false;
            for (var i = first; i < worlds.Count && worlds[i].World?.CaptureSetId == captureSetId; i++)
            {
                last = i;
                if (worlds[i].World!.Layer == "interior") { hasInterior = true; break; }
            }
            if (!hasInterior) return;
            for (var i = first; i <= last; i++)
                worlds[i].Time = Math.Min(worlds[i].Time, time);
            session.Worlds = worlds.OrderBy(record => record.Time).ToList();
        }
    }
}
