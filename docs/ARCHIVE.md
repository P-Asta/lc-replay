# Automatic recording archive

Open the native archive through the main menu's **Replay** button or **F9**. Recording begins automatically when a game is hosted or joined. New recordings are organized by **run → remaining quota → deadline file**.

```text
BepInEx/replays/
  Run-20260924-210000-<short-id>/
    run.json
    130/
      quota.json
      3.lcr
      3.lci                optional fast record-offset index
      3.json
      3.day.json
      2.lcr
      2.json
      2.day.json
      3-2.lcr              repeated deadline after reconnect
```

The numeric folder is **remaining profit quota**, calculated as `max(target - fulfilled, 0)`. The `.lcr` stem is **days remaining until the quota deadline**. `130/3.lcr` means 130 credits still needed with three days left. Missing values use `unknown`, never an invented zero.

A run belongs to one game process. A quota directory can be reused within that run. Each day writes one physical `.lcr` directly in the quota directory. Repeated deadline values use a collision-safe suffix (`3-2.lcr`, `3-3.lcr`) so reconnecting cannot overwrite an earlier recording. A changed quota or deadline starts a new recording context. Preparation, departure, return and observed expedition number remain metadata. The `.lci` sidecar contains record offsets and timestamps, not another recording. Keep it when copying an archive for fast opening; if missing or invalid, playback can rebuild the index by scanning the `.lcr` at a slower startup cost.

`ArchiveSession` remains the compatibility model for a quota group. `ArchiveDay` identifies one recording, its deadline and its file stem. Each day has one `ArchiveSegment`; this retains older archive APIs while new files never rotate into numbered parts. Nullable quota/deadline fields preserve unavailable information.

## Recording and playback

The recorder finalizes the `.lcr` when quota/deadline changes, on disconnect, or on shutdown. It appends independent compressed records to the same file throughout that day. **F8** adds a checkpoint event and frame. If saving fails, the incomplete file remains visible and F8 retry allocates another collision-safe file.

Playback scans the one file's record offsets, then loads bounded time windows into memory on demand. A window carries the preceding frame and latest world capture set, preserving the scene when seeking across a window boundary. These windows do not create more `.lcr` files. The viewer, HUD, camera, one timeline, speed, pause state and selected player persist while the active window changes. The next window is prefetched in the background.

Disk serialization runs on a background worker. At shutdown, accepted records are drained before the file closes. A physically truncated final write leaves a readable complete prefix. A complete malformed record is rejected rather than silently skipped. The single-file index has explicit file, record, duration and expanded-data limits; an extreme recording that reaches them fails visibly rather than producing a hidden second file.

## Metadata, recovery and compatibility

New `.lcr` headers mark `singleFile=true` and include run/session/day IDs, quota/deadline values and game version. JSON companions are replaceable index metadata. They use short temporary siblings and atomic replacement; recordings are created without overwriting an existing path. An index scan reads bounded headers and manifests rather than every frame or texture payload. Missing or damaged JSON can be recovered from filenames and `.lcr` headers. The default scan bounds include 10,000 replay files and 4,096 directories; reaching a bound is reported as a partial listing. Symbolic links and junctions are not followed.

Old multipart quota/deadline folders, `Run-.../Session-.../Day-...` archives and loose 0.1 `.lcr` files remain readable without being moved, renamed or deleted. Older multipart playback still uses its contiguous readable parts and does not bridge missing or corrupt neighbors. Numeric quota groups and legacy layouts can coexist.

Copy an entire run directory when moving recordings to another machine. Paths are reconstructed from their current disk location; a stale absolute path in a manifest is not followed. Recordings are never automatically uploaded or deleted.
