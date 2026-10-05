using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LCReplay.Core
{
    public sealed class ReplayEnemyLife
    {
        public string EntityId { get; set; } = "";
        public string Name { get; set; } = "";
        public double SpawnTime { get; set; }
        public Vec3 SpawnPosition { get; set; }
        public double? DeathTime { get; set; }
        public Vec3 DeathPosition { get; set; }
    }

    public static partial class ReplayReader
    {
        // Retain only a small summary per actor, never the full recording's frames.
        public static IReadOnlyList<ReplayEnemyLife> ReadEnemyHistory(ReplayFileIndex index,
            CancellationToken cancellation = default)
        {
            using var input = new FileStream(index.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            if (input.Length != index.FileLength || File.GetLastWriteTimeUtc(index.FilePath).Ticks != index.LastWriteUtcTicks)
                throw new InvalidDataException("Replay changed after its index was created.");
            IEnumerable<ReplayRecord> Records()
            {
                var limits = new ReplayReadLimits();
                foreach (var entry in index.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entry.Kind != "frame" && (entry.Kind != "event" || entry.EventCategory.Length != 0)) continue;
                    input.Position = entry.Offset;
                    if (reader.ReadInt32() != entry.Compressed || reader.ReadInt32() != entry.Expanded)
                        throw new InvalidDataException("Replay changed after its index was created.");
                    var payload = reader.ReadBytes(entry.Compressed);
                    if (payload.Length != entry.Compressed) throw new InvalidDataException("Truncated replay record.");
                    var record = ReplayFormat.Decode(payload, entry.Expanded, cancellation);
                    ReplayValidation.Record(record, limits);
                    yield return record;
                }
            }
            return EnemyHistory(Records());
        }

        public static IReadOnlyList<ReplayEnemyLife> EnemyHistory(IEnumerable<ReplayRecord> records)
        {
            var lives = new Dictionary<string, ReplayEnemyLife>();
            var positions = new Dictionary<string, Vec3>();
            foreach (var record in records)
            {
                if (record.Frame != null)
                    foreach (var entity in record.Frame.Entities)
                    {
                        if (entity.Kind != "enemy") continue;
                        if (!lives.TryGetValue(entity.Id, out var life))
                        {
                            if (!entity.Active) continue;
                            lives[entity.Id] = life = new ReplayEnemyLife { EntityId = entity.Id, Name = entity.Name,
                                SpawnTime = record.Frame.Time, SpawnPosition = entity.Position };
                        }
                        positions[entity.Id] = entity.Position;
                        if (!life.DeathTime.HasValue && entity.State.TryGetValue("isEnemyDead", out var dead) &&
                            string.Equals(dead, "True", StringComparison.OrdinalIgnoreCase))
                        { life.DeathTime = record.Frame.Time; life.DeathPosition = entity.Position; }
                    }
                var evt = record.Event;
                if (evt != null && evt.Category == "state" && evt.Name == "isEnemyDead" &&
                    evt.Data.TryGetValue("to", out var to) && string.Equals(to, "True", StringComparison.OrdinalIgnoreCase) &&
                    lives.TryGetValue(evt.EntityId, out var died) && (!died.DeathTime.HasValue || evt.Time < died.DeathTime))
                { died.DeathTime = evt.Time; died.DeathPosition = positions[evt.EntityId]; }
            }
            return lives.Values.OrderBy(life => life.SpawnTime).ToArray();
        }
    }
}
