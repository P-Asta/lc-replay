# LC Replay

LC Replay **automatically records and saves Lethal Company gameplay** and lets you watch it from a free camera through the main menu's **Replay** button. The current version is the **0.11.0 development preview** for BepInEx 5.

New recordings include bounded map and character meshes, shader parameters, textures, HDRP sky and fog settings, active lights, moving ship poses, state snapshots and events. Supported characters use their recorded body mesh and bone poses. Playback draws a separate, render-only scene; it does not restart the game's AI, physics or network commands. This is a state replay, not a video recording with identical baked lighting, effects and audio.

**Validation:** 55 automated test groups pass. An isolated v81 / BepInEx 5.4.23.5 expedition saved one 124 MiB `.lcr`, then loaded and sought through its replay in the native UI; see [testing status](docs/TESTING.md) for details and limitations.

## Install and use

1. Install **BepInEx 5** in the game or the mod-manager profile you actually launch. Previous releases were exercised with BepInEx 5.4.21 and 5.4.23.5; BepInEx 6 is not verified.
2. Extract `artifacts/LCReplay-0.11.0.zip` and copy its `BepInEx/plugins/LCReplay` folder into that game/profile. Replace **both** `LCReplay.dll` and `LCReplay.Core.dll`, remove duplicate older copies, and restart the game.
3. Host or join a game. Recording starts automatically; `AUTO REC / SAVING` appears without a record or save button.
4. Disconnect and select **Replay**, between Settings and Credits in the main menu. Select a run, quota and deadline recording, then **[Play]**. **F9** also opens the archive.

The English archive and playback controls are native **Unity uGUI/TextMeshPro**, with a three-column dark red and orange layout inspired by LethalConfig. They require neither a browser nor LethalConfig. **[Folder]** opens the recording's save location. The playback bar has a single timeline for the complete selected recording; **[Details]** opens recorded state, events and optional diagnostic labels/bones. **[Settings]** changes replay render resolution and gamma, preserving both values for later playback.

Files stay in the active profile's **`BepInEx/replays`** directory. Settings are created at **`BepInEx/config/io.lcreplay.recorder.cfg`**. Nothing is uploaded automatically. Recordings may contain player names and observed game state.

If installing directly from a build, copy the two DLLs from `src/LCReplay.Plugin/bin/Release/netstandard2.1`. Game, Unity and BepInEx assemblies are not distributed. The game supplies Newtonsoft.Json 13.

## Quota and deadline organization

The numeric folders refer to the game's **remaining profit quota** and **days remaining until the quota deadline**. They are not connection numbers or a count of expeditions.

```text
BepInEx/replays/
  Run-20260924-210000-<short-id>/
    130/                         remaining quota
      3.lcr                      three days until the deadline
      3.lci                      optional fast playback index
      3.json                     recording status/index metadata
      3.day.json                 day metadata
      2.lcr                      two days until the deadline
```

Remaining quota is `max(target quota - fulfilled quota, 0)`. Missing values use `unknown`. Each day has **one physical `.lcr` file** directly inside its quota folder. A small `.lci` sidecar speeds seeking; the `.lcr` remains readable without it. If the same deadline is recorded again within a run, the next file uses a safe suffix such as `3-2.lcr` rather than overwriting `3.lcr`. Changes in quota/deadline create a new recording context. Existing multipart and `Session-.../Day-...` archives remain readable without being moved or renamed. See [archive layout](docs/ARCHIVE.md).

Recording continues in the same `.lcr` through the day and is finalized when the observed quota/deadline context changes, on disconnect, or on shutdown. F8 writes a checkpoint event and a fresh frame without creating another file. Playback indexes the one file into bounded in-memory windows while retaining **one clock and one seek bar**. Playback stops at the selected recording's end rather than entering an unrelated recording.

Serialization and disk writes run on a background worker; shutdown waits for accepted writes to finish. Complete records are written throughout the day so an interrupted file can recover its readable prefix. Scene geometry capture uses short main-thread steps across multiple frames when a level loads or finishes generating. Level generation and individual Unity mesh/texture reads can still pause a frame. Playback cannot recreate movement during an unrecorded gap.

## Controls

| Control | Action |
| --- | --- |
| F8 | Add a checkpoint frame/event to the same file, or start a separate retry file after a save error |
| F9 | Open or close the archive |
| F10 | Add a recording bookmark |
| F11 | Open the archive, or exit playback |
| Esc | Close the archive, or return from playback to the archive |
| Hold right mouse + WASD | Move and look with the free camera |
| Hold right mouse + Q / E | Move down / up |
| Shift | Move faster |
| Space / Left arrow / Right arrow | Pause / seek back 5 seconds / seek forward 5 seconds |
| F / Tab | Focus the selected player / select the next player |

The playback bar also supports speed selection from 0.25× to 4× and player following. Diagnostic labels and bone overlays start disabled. The archive does not pause a live game, and playback requires disconnecting first.

## What is recorded

| Source | Recorded data |
| --- | --- |
| Players | Transforms, health/death/movement state, inventory references, selected slot, supported local bone poses |
| Enemies | Transforms, health/death, behavior state, target references and supported primitive fields |
| Items | Transforms, held/use state, value, holder/slot references and supported battery state |
| Environment | Doors/locks, facility devices, mines, turrets, teleporters, vehicles and bodies; sampled ship/elevator roots |
| Round | Available map seed, ship phase, time, weather, quota, credits and related fields |
| Events | Observed calls, sampled state changes, discovery/disappearance, scene changes, bookmarks and capture omissions |
| Appearance | Meshes, UVs, submeshes, shader names/parameters, ground color layers up to 2048 pixels per edge, other primary natural and skinned textures up to 1024 pixels per edge, supported skinning, spectator-selected natural LODs, procedural grass, looping particle emitters, sampled Unity Terrain with normals and layer texture, bounded lights, sky cubemap and HDRP fog/exposure settings |
| Other mods | Installed plugin versions, configured extra component types and explicitly registered state providers |

Full state snapshots default to **10 Hz**; entity discovery runs approximately once a second. Fast changes can occur between samples. A `call` event means a method call was observed, not that the action necessarily succeeded; RPC paths can produce duplicates. Use snapshots and `state` events to inspect outcomes.

Generated interior renderers are captured without requiring the player to enter each room, within the configured budgets. Repeated static room meshes share one stored payload. Exterior geometry can span up to three bounded capture records; the interior has its own record so distant outside detail cannot consume its budget. Playback joins each capture set before drawing it. Outside, replay shows the captured exterior and hides the interior; inside, it hides the exterior and renders nearby recorded rooms within a **105 m range**. Ground surfaces keep their high-detail geometry at any outside distance; vegetation stays detailed for at least 100 m, with larger recorded LOD ranges extended up to 300 m. The recorded ship/elevator roots move with the player during takeoff and landing. Replay uses captured sky, fog, shader parameters, lit materials, bounded lights and shadows, plus a modest spectator lamp inside dark rooms. Supported player bodies use recorded meshes and bones. Interaction-only fallback shapes and unreadable environment bounds stay hidden by default.

**Complete capture of every game datum is not guaranteed.** Voice and game audio, exact short-lived/GPU particle states, baked lighting, unsupported post-processing, complete physics/RNG execution, other clients' private state and arbitrary mod internals are not captured. Looping particles and particle-only bee/locust enemies are visual approximations, not frame-exact recordings. Recorded sky/fog and dynamic lights approximate the game's appearance; unreadable custom shader assets, transparency, terrain materials and unsupported skins may still differ. Some skins fall back to a mesh baked at capture time. Text chat capture is disabled by default. Host recordings generally observe more authoritative state; other players do not need this mod to connect.

Geometry, fields, arrays, bones and textures have explicit limits. Textures are capped at 256 entries and 32 MiB of PNG data per world. New recordings allocate higher resolution to the color layers of ground materials and sample Unity Terrain at up to a 128×128 cell grid instead of 32×32. Ground maps keep their captured base resolution at all distances; other maps use mipmaps, with a bias toward higher-detail vegetation and preserved alpha-test coverage. The game source texture may itself be only 256×256; capture cannot invent missing detail. First-person visor effects are excluded from new captures and hidden when replaying older archives. Old recordings cannot gain image detail, missing vegetation or 3D fog masks that were never saved; record again to use the new capture behavior. There is no automatic deletion or retention policy.

Each capture record has a conservative 45 MiB geometry estimate, leaving headroom for textures and other world data in the reader's 96 MiB expanded-record limit. Exterior capture prioritizes actor skins and nearby objects, then fills further capture records with the remaining outside geometry. Interior geometry is captured separately. An unreadable mesh, excluded gameplay layer or a final exhausted budget can still omit an object. Earlier recordings cannot gain sky, fog or previously omitted geometry retroactively.

## Settings and performance

| Setting | Default | Meaning |
| --- | --- | --- |
| SampleRate | 10 | 1–60 snapshots per second, also limited by the game frame rate |
| CaptureBones / CaptureWorld | true | Record supported bone poses and render geometry |
| CaptureChat | false | Record locally displayed text chat; restart after changing |
| FieldsPerEntity | 192 | Primitive field budget per tracked component |
| WorldObjects | 4000 | Render-object budget per world snapshot |
| WorldVertices | 500000 | Unique mesh vertex budget; accepted range 1000–1000000 |
| ReplayDirectory | replays | BepInEx-relative or absolute storage directory; restart after changing |
| ExtraTrackedTypes | empty | Comma-separated full Unity Component type names |
| RenderResolutionScale | 1.0 | Replay scene resolution, 25–100% of the window; playback UI stays at full resolution |
| Gamma | 1.0 | Replay image gamma, 0.5–2.0; 1.0 is neutral |

The 0.4 migration changes the old exact `WorldVertices=120000` value to `500000` once and records `CaptureSettingsVersion=4`. The 0.8 migration changes an existing exact `RenderResolutionScale=0.5` value to `1.0` once; other configured resolution values are preserved. Repeated mesh instances do not each consume another full vertex payload.

Lower `SampleRate`, `WorldObjects` or `WorldVertices`, or disable `CaptureBones`/`CaptureWorld`, if capture is too expensive. World capture is spread across main-thread frames with a 3 ms step target; individual Unity calls and game level generation can exceed that target. Serialization and disk writes use a background worker. A full recording queue or disk failure stops recording with a visible error rather than silently dropping accepted records. Resolve the cause and use F8 to retry; initialization failures require restarting the game.

## Compatibility and troubleshooting

- Runtime reflection locates game components, fields and optional hooks without a compile-time `Assembly-CSharp.dll` dependency. Missing optional members are logged and skipped.
- The target is Unity 2022.3, .NET Standard 2.1 and BepInEx 5. Input System and HDRP integration are detected at runtime.
- Version 0.11 was tested on v81 with native automatic single-file saving, archive playback and a separate synthetic Unity Terrain capture. Older recordings remain readable but need a new recording to gain higher-detail ground capture. Multiple-day campaigns, remote-client perspectives, every old/new game build and all modpacks remain unverified.
- Check BepInEx logs for `Replay runtime active`, `Replay keyboard input ready`, `Replay button attached below Settings`, `Replay archive directory` and `Saved replay`. Menu attachment failures include a reason and are retried.
- A missing or torn JSON manifest can be recovered from bounded file headers. An execution that never wrote any `.lcr` data cannot be reconstructed.

## Build and test

Use .NET SDK 8 or later and the installed game's `UnityEngine.UI.dll` and `Unity.TextMeshPro.dll`. The default Steam path is used; another install can be selected with `-p:GameManagedPath="D:\SteamLibrary\steamapps\common\Lethal Company\Lethal Company_Data\Managed"`. These reference assemblies are not packaged.

```powershell
dotnet build LCReplay.sln -c Release
dotnet run --project tests/LCReplay.Core.Tests -c Release
dotnet run --project tests/LCReplay.Archive.Tests -c Release
dotnet run --project tests/LCReplay.Lifecycle.Tests -c Release
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -NoBuild
```

If only SDK 10 is installed, add `-p:ReplayTestFramework=net10.0` to solution/test/CLI commands. The mod remains targeted at `netstandard2.1`. See [testing procedures](docs/TESTING.md) for runtime checks and the distinction between automated tests and real-game evidence.

## Inspect and export

```powershell
dotnet run --project tools/LCReplay.Cli -- inspect "C:\replays\example.lcr"
dotnet run --project tools/LCReplay.Cli -- export "C:\replays\example.lcr" "C:\replays\example.jsonl"
dotnet run --project tools/LCReplay.Cli -- demo "C:\replays\synthetic-demo.lcr"
```

`demo` creates 12 seconds of synthetic test data, not a real game recording. `export` and `demo` refuse to overwrite existing outputs. JSONL contains one record per line. The optional packaged `examples/synthetic-demo.lcr` can be copied to `BepInEx/replays` to inspect the viewer without recording a game. See [format and limits](docs/FORMAT.md).

## Integrate another mod

Reference `LCReplay.dll` and `LCReplay.Core.dll` from an integration plugin with an optional BepInEx dependency. State providers run on Unity's main thread; keep them inexpensive and unregister on shutdown.

```csharp
ReplayApi.RegisterStateProvider("my.mod", () => new Dictionary<string, string>
{
    ["customScore"] = currentScore.ToString()
});
ReplayApi.LogEvent("my.mod", "special-action", new Dictionary<string, string>
{
    ["target"] = targetName
});
// OnDestroy:
ReplayApi.UnregisterStateProvider("my.mod");
```

The namespace is `LCReplay.Plugin`. Files contain known value schemas, not arbitrary object graphs or executable game objects.

## References

- [BepInEx plugin development](https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/2_plugin_start.html)
- [Official BepInEx templates](https://github.com/BepInEx/BepInEx.Templates)
- [LethalConfig menu reference](https://github.com/AinaVT/LethalConfig)

MIT licensed. Game and third-party assets, source and assemblies remain subject to their own licenses. No LethalConfig code, prefab or bundled font asset is distributed.
