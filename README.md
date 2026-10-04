# LC Replay

LC Replay **automatically records and saves Lethal Company gameplay** and lets you watch it from a free camera through the main menu's **Replay** button or while connected to a game. The current version is the **0.25.38 development preview** for BepInEx 5.

New recordings reference installed moon scenery and native actor meshes, rigs and materials. They store bounded generated geometry, shader settings, world HDRP sky/fog/post-processing, lights, moving ship poses, state snapshots, transient particle/line samples and action events. Level/map/dungeon identities are stored once with the world. Player and enemy animation uses the installed Animator and IK hierarchy with changed states/parameters. Enemy visual bones, rigid model ancestors and blend-shape weights are also sampled after animation and procedural scripts, including inactive transformation forms, without a list of supported enemy classes. This covers ordinary vanilla and modded enemy rigs when their matching assets are installed; arbitrary shader deformation, runtime mesh replacement and unobserved states are not guaranteed. New default recordings do not learn player bone tracks. Game audio waveforms and voice are never recorded: playback resolves native sound clip/action identifiers against the installed game. TZP, fear and other personal screen filters are excluded. Playback uses inert scenery without executing live AI, physics or network commands. This is a state replay, not a pixel-exact video recording.

**Validation:** 114 automated test groups pass. Muted isolated v81 / BepInEx 5.4.23.5 runs check the supplied replay's procedural bones, native animation transitions, held items, Cruiser cargo, opening visibility and backward seeking, plus a synthetic mod-style rig with two Animators and changing forms. A separate v73 fixture reproduces and verifies the supplied file's oversized player fix. Multiplayer replay still needs a live multi-client check. See [testing status](docs/TESTING.md) for measurements and limits.

## Install and use

1. Install **BepInEx 5** in the game or the mod-manager profile you actually launch. Previous releases were exercised with BepInEx 5.4.21 and 5.4.23.5; BepInEx 6 is not verified.
2. Extract `artifacts/LCReplay-0.25.38.zip` and copy its `BepInEx/plugins/LCReplay` folder into that game/profile. Replace **both** `LCReplay.dll` and `LCReplay.Core.dll`, remove duplicate older copies, and restart the game.
3. Host or join a game. Recording starts and saves automatically without a record or save button. The top-left `AUTO REC / SAVING` indicator appears only if `Debug.ShowOverlay` is enabled.
4. Select **Replay** below Settings on the main menu, select **Replay** in the in-game pause menu, or press **F9**. Select a run, quota and deadline recording, then **[Play]**. In-game replay takes over the local view and controls until you close it; the live session continues.

The English archive and playback controls are native **Unity uGUI/TextMeshPro**, styled after [LethalConfig's in-game menu](https://github.com/AinaVT/LethalConfig): solid dark-red backgrounds, flat red rows, orange text and selected-item outlines, and the game's pixel-style font. The main-menu Replay entry copies the game's Settings button appearance. The F9 archive, playback HUD, settings, details, loading and confirmation windows, recording indicator and diagnostic labels share the same palette. LethalConfig does not need to be installed; the replay UI uses its own controls and no third-party UI asset. In the F9 archive, choose a recording to see its date, moon, length and size. **PLAY** stays at the lower right; **Folder** opens the selected save location. An **X** beside a recording, quota or run opens a confirmation with the affected recording and file counts. Confirming permanently removes those recordings' `.lcr` files and their `.lci`/JSON metadata, leaving unselected quota/day recordings intact. The active recording cannot be deleted. Esc or F9 closes the confirmation first. During playback, the top dock contains Settings, Details and X. The bottom bar contains the clock, timeline, Play, speed and one Camera selector for Freecam or a living player. A followed player who dies switches to Freecam at the recorded death position, which is marked on the timeline. Zooming close to a followed player uses their recorded gameplay camera when available. **Details** opens recorded state, events and optional diagnostic labels/bones. **Settings** changes replay render resolution, gamma, Fog / Fake Fog, No shadow, interior tile culling, cinematic camera movement and camera speed, preserving these values for later playback. With **Disable culling: ON**, recorded interior tiles remain visible when the spectator camera moves outside. **No shadow: ON** keeps exterior illumination active inside the facility and disables replay light shadows. Left-click while following a player cycles through living players; in Freecam it selects the actor under the pointer.

Replay adds menu space upward and can coexist with LethalConfig's button. Closing or caching playback restores the game and mod menu controls.

Playback defaults follow the [HDLethalCompany vanilla settings](https://github.com/Sligili/HDLethalCompany): resolution multiplier 1 (860×520), original installed textures, native LOD distances, native shadow pipeline, AA off, fog/post-processing/foliage on. The replay camera copies the installed player's HDRP settings. Volumetric fog uses the game's authored vanilla quality budget while preserving recorded weather density and color. Existing recordings remain state replays with their original capture limits; this does not replace missing textures or baked lighting. Resolution is fixed relative to the vanilla raster, not the desktop window; its slider displays the actual dimensions.

In-game playback blocks the game's polled action assets as well as performed callbacks, while replay reads the raw devices and its own UI actions. Closing restores actions that were enabled on entry and the game's current camera. Held-action cancellation remains available. Entity discovery registers at most two components per game frame; native player skins reference the shared installed prefab when its mesh and renderer path match, with fallback for custom meshes/materials.

Static ambience in new recordings uses actual source start/stop and changed gain/pitch, with native emitter positions and distance limits. Audio data is never saved. Playback no longer attaches rain, campfire or entrance sounds from generic prefab mesh/name matches or default scene templates. Older recordings lack those emitter actions, so unverified static ambience is omitted; their explicit native breaker-box fallback remains available.

Files stay in the active profile's **`BepInEx/replays`** directory. Settings are created at **`BepInEx/config/io.lcreplay.recorder.cfg`**. Nothing is uploaded automatically. Recordings may contain player names and observed game state.

If installing directly from a build, copy the two DLLs from `src/LCReplay.Plugin/bin/Release/netstandard2.1`. Game, Unity and BepInEx assemblies are not distributed. The game supplies Newtonsoft.Json 13.

## Quota and deadline organization

The numeric folders refer to the game's **remaining profit quota** and **days remaining until the quota deadline**. They are not connection numbers or a count of expeditions.

```text
BepInEx/replays/
  Lobby-Friends-<short-id>/
    130/                         remaining quota
      3.lcr                      three days until the deadline
      3.lci                      optional fast playback index
      3.json                     recording status/index metadata
      3.day.json                 day metadata
      2.lcr                      two days until the deadline
```

Remaining quota is `max(target quota - fulfilled quota, 0)`. Missing values use `unknown`. Each day has **one physical `.lcr` file** directly inside its quota folder. A small `.lci` sidecar speeds seeking; the `.lcr` remains readable without it. If the same deadline is recorded again within a run, the next file uses a safe suffix such as `3-2.lcr` rather than overwriting `3.lcr`. The quota/deadline values at the start of a day name its file; temporary deadline display changes during an expedition do not split it. Existing multipart and `Session-.../Day-...` archives remain readable without being moved or renamed. See [archive layout](docs/ARCHIVE.md).

Recording normally continues in the same `.lcr` through the day and is finalized when the crew returns to orbit, disconnects, or shuts down. F8 writes a checkpoint event and a fresh frame without creating another file. If capture or output fails during a day, automatic recovery keeps the readable prefix and starts a numbered continuation file after a short backoff; a disk-full error pauses recording until space is available and F8 is pressed. Playback combines the day's parts under **one clock and one seek bar**. Playback stops at the selected recording's end rather than entering an unrelated recording. The library shows indexing and read progress, then the viewer shows scene-building progress. Completed older recordings with a missing or outdated `.lci` receive a reusable sidecar from metadata scanning; future frame arrays and world textures are decoded and validated only when their section is opened. The first replay window uses a 4 MiB expanded frame/event budget, with 48 MiB windows afterward and a separate bounded world payload.

From the main menu, closing a replay back to the archive keeps its built scene in memory so reopening the same recording resumes at its camera and timeline position. Closing the archive releases that scene before the game menu is available. In-game replay releases its scene when closed, so reopening it loads the recording again. The cache does not persist across game restarts.

During a live session, replay uses scenery from the matching moon scene already loaded by the game. If a recording references a different moon, its installed-scene scenery may be absent; recorded geometry and actors still appear. Main-menu playback can load the installed moon scenery.

Serialization and disk writes run on background workers; shutdown waits for accepted writes to finish. Complete records are written throughout the day so an interrupted file can recover its readable prefix. Scene asset indexing begins when the moon loads and inspects components in short main-thread steps while the level generates. Stable installed-scene renderer references can be written during generation; generated exterior and interior objects are captured after completion. The initial transform membership walk, game level generation and individual Unity mesh/texture reads can still pause a frame. Playback cannot recreate movement during an unrecorded gap.

## Controls

| Control | Action |
| --- | --- |
| F8 | Add a checkpoint frame/event to the same file, or manually retry after a disk-full save error |
| F9 | Open or close the archive |
| Backtick (`) | Add a recording bookmark; configurable through InputUtils Controls or Input.BookmarkKey |
| F11 | Open the archive, or exit playback |
| Esc | Close the archive, or return from playback to the archive |
| WASD + mouse | Move and look with the free camera while the cursor is hidden |
| Q / E | Move down / up |
| B | Toggle the cursor for replay UI interaction |
| Right mouse button with cursor visible | Temporarily look and move with the free camera |
| Left mouse button in Freecam | Follow the visible player or enemy under the cursor or center reticle |
| Mouse wheel in Freecam | Change camera movement speed (also updates Settings) |
| Mouse wheel while following | Change follow distance; scroll close for first-person view |
| Shift | Move faster |
| C (during playback) | Toggle smooth cinematic camera movement |
| Space / Left arrow / Right arrow | Pause / seek back 5 seconds / seek forward 5 seconds |
| F / Tab | Focus the selected player / select the next player |
| L (during playback) | Hide or show the replay UI |

The playback bar also supports speed selection from 0.25× to 4× and player following. First-person follow uses the recorded gameplay camera position and direction in new recordings; older recordings use the reconstructed head and body direction. Diagnostic labels and bone overlays start disabled. The top-left recording status appears only when `Debug.ShowOverlay = true` in the BepInEx configuration (off by default). Errors appear in the top-right corner for 3.5 seconds, even with that setting off; full details remain in the BepInEx log and, when available, the F9 archive. The archive and playback do not pause a live game. Connected players remain in the session and should close the viewer to resume control.

## What is recorded

| Source | Recorded data |
| --- | --- |
| Players | Transforms, selected health/death/movement and held-item state, selected slot, Animator state and parameter changes; optional local bone poses |
| Enemies | Transforms, health/death, behavior/target state, separate native Animator states and changed parameters per rig; small procedural facing, torso/head-target and spider-joint poses; transient webs |
| Items | One pose while resting, game-curve inputs for ordinary drops, impact and drop sound events; frame transforms for held or irregular items, plus changing use/scrap/battery state |
| Environment | Doors/locks, facility devices, mines, turrets, teleporters, vehicles and bodies; sampled ship/elevator roots |
| Round | Level ID, map seed, dungeon-flow/seed settings, ship phase, time, weather, quota, credits and related fields |
| Events | Observed calls, sampled state changes, discovery/disappearance, scene changes, bookmarks and capture omissions |
| Appearance | Built-in moon-scene renderer/terrain references; bounded generated and moving meshes, UVs, submeshes, shader names/parameters, textures, supported skinning, spectator-selected natural LODs, generated room high-detail meshes, procedural grass, looping particle emitters, sampled Unity particles and line renderers, fallback sampled Unity Terrain, lights with color temperature, sky cubemap and HDRP fog/exposure settings |
| Audio | Installed effect clip names and play/stop actions for tracked players, enemies, items and hazards, including footsteps and spray use; no waveform or voice capture |
| Other mods | Installed plugin versions, configured extra component types and explicitly registered state providers |

Full state snapshots default to **10 Hz**. Entity discovery scans one component type per game frame, then pauses roughly one second between completed passes; particle and line lists refresh once per pass. Built-in entities record a small set of changing gameplay fields. Basic item definitions come from the installed game and are not copied into every frame; resting items and ordinary falls use sparse pose events instead of repeated frame transforms. The map seed and level identity are stored once with the world. Mod-provided extra types retain bounded general field capture. Fast changes and effects born and destroyed between discovery passes can be missed. A `call` event means a method call was observed, not that the action necessarily succeeded; RPC paths can produce duplicates. Use snapshots and `state` events to inspect outcomes.

Generated interior renderers are captured without requiring the player to enter each room, within the configured budgets. Repeated static room meshes share one stored payload. Built-in exterior surfaces already present in the installed moon scene, including ground and rocks, are saved as small references instead of copied mesh/texture payloads. Other exterior geometry can span up to three bounded capture records; the interior has its own record. The map seed, level ID, dungeon seed and dungeon-flow ID are saved for provenance, while generated rooms remain recorded rather than regenerated from those values. Playback joins each capture set before drawing it. Outside, replay shows the recorded exterior plus referenced moon scenery and normally hides the interior; inside, it hides the exterior and renders nearby recorded rooms within a **105 m range**. The Disable culling playback option keeps all recorded interior tiles visible from either side. Ground surfaces keep their high-detail geometry at any outside distance; vegetation stays detailed for at least 100 m, with larger recorded LOD ranges extended up to 300 m. The recorded ship/elevator roots move with the player during takeoff and landing. Replay uses captured sky, fog, shader parameters, lit materials, bounded lights and shadows, plus a modest spectator lamp inside dark rooms. Local item lights follow their recorded items, and strongly emissive scrap can supply a small fallback light. The ship cabin retains exterior fog; generated dungeon rooms use local fog. Supported player bodies use recorded skinned meshes and sparse animation events. Interaction-only fallback shapes and unreadable environment bounds stay hidden by default.

**Complete capture of every game datum is not guaranteed.** Voice chat is disabled by default. Player effects such as footsteps are recorded, tagged and spatialized at the recorded player position; the playback mute option needs a new recording with those tags. Unity-virtualized distant AudioSources are additionally sampled from readable `DecompressOnLoad` clips in bounded batches, so the spectator can hear those effects when approaching their recorded positions. Compressed or streamed clips, source timing that Unity does not expose, and sounds that bypass Unity AudioSource may be missing. Particles emitted entirely on the GPU, baked lighting, custom post-processing textures, complete physics/RNG execution, other clients' private state and arbitrary mod internals are not captured. Short-lived Unity particles are sampled at the frame rate and limited to 256 per frame, so effects between frames can be missed. Line renderers are sampled at up to 128 per frame. Transparent particle maps are used where their alpha can be recovered; an alpha-feathered texture is used otherwise, including for older recordings. Rain, lightning and bee/locust VFX remain visual approximations rather than exact original effects. Recorded sky, fog, HDRP filters and dynamic lights approximate the game's appearance; the outline pass is currently suppressed because it produces black surface artifacts in the replay camera; unreadable custom shader assets, transparency, terrain materials and unsupported skins may still differ. Some skins fall back to a mesh baked at capture time. Text chat capture is disabled by default. Host recordings generally observe more authoritative state; other players do not need this mod to connect.

Geometry, fields, arrays, bones and textures have explicit limits. Textures are capped at 256 entries and 32 MiB of PNG data per world. Built-in moon scenery uses the game's original mesh/material/texture, so its ground is not limited by the capture mesh budget. During recording, unsupported scenes and surfaces with invalid or ambiguous scene paths use embedded geometry instead of a scene reference. For generated terrain without a built-in scene reference, capture can sample Unity Terrain at up to a 128×128 cell grid and ground color layers at up to 2048 pixels per edge. Ground maps keep their captured base resolution at all distances; other maps use mipmaps, with a bias toward higher-detail vegetation and preserved alpha-test coverage. The game source texture may itself be only 256×256; capture cannot invent missing detail. First-person visor effects are excluded from new captures and hidden when replaying older archives. Old recordings cannot gain image detail, missing vegetation or 3D fog masks that were never saved; record again to use the new capture behavior. There is no automatic deletion or retention policy.

Each capture record has a conservative 45 MiB geometry estimate, leaving headroom for textures and other world data in the reader's 96 MiB expanded-record limit. Exterior capture prioritizes actor skins and nearby objects, then fills further capture records with the remaining outside geometry. Interior geometry is captured separately. An unreadable mesh, excluded gameplay layer or a final exhausted budget can still omit an object. For older recordings with a missing spider body, playback reads the installed spider mesh, materials and rig. Existing recordings without web trap events or entities cannot recover the web's timing or position.

## Settings and performance

| Setting | Default | Meaning |
| --- | --- | --- |
| SampleRate | 10 | 1–60 snapshots per second, also limited by the game frame rate |
| CaptureBones | false | Also sample full player/item bone poses; enemy visual poses and bounded blend-shape weights are always sampled |
| CaptureWorld | true | Record bounded render geometry and appearance |
| CaptureChat | false | Record locally displayed text chat; restart after changing |


| FieldsPerEntity | 192 | Primitive field budget per tracked component |
| WorldObjects | 4000 | Render-object budget per world snapshot |
| WorldVertices | 500000 | Unique mesh vertex budget; accepted range 1000–1000000 |
| ReplayDirectory | replays | BepInEx-relative or absolute storage directory; restart after changing |
| ExtraTrackedTypes | empty | Comma-separated full Unity Component type names |
| ResolutionMultiplier | 1.0 | Replay raster size relative to vanilla 860×520; range 0.25–4.5; UI stays at window resolution |
| Gamma | 1.0 | Replay image gamma, 0.5–2.0; 1.0 is neutral |
| DisableInteriorCulling | false | Show every recorded interior tile when the replay camera is indoors; also available in playback Settings |
| ShowFog | true | Show recorded global/local fog and fog-named scenery; also available in playback Settings |
| Debug.ShowOverlay | false | Show the top-left recording status during gameplay; errors appear briefly at the top-right regardless |

The 0.4 migration changes the old exact `WorldVertices=120000` value to `500000` once. The lean-capture migration sets `CaptureBones=false` once and records `CaptureSettingsVersion=5`; you can enable it again afterward. The 0.25.37 migration replaces the window-relative `RenderResolutionScale` setting with `ResolutionMultiplier=1`, restores neutral gamma, enables fog and shadows, and enables interior culling once (`PlaybackSettingsVersion=3`). Later settings edits persist normally. Repeated mesh instances do not each consume another full vertex payload.

Lower `SampleRate`, `WorldObjects` or `WorldVertices`, or disable `CaptureWorld`, if capture is too expensive. The migration turns off `CaptureBones` in existing profiles once; set it back to `true` after the migration if full player/item bone poses matter more than recording size. Enemy visual bones and bounded blend-shape weights are always captured from 0.25.27 onward. New recordings save active actors' Animator state and changed float/int/bool parameters, including movement and sprint controls, plus the original player/enemy Animator and IK hierarchy during playback. Historical observed tracks remain readable as a fallback. Native skin transforms prevent an animated mesh offset from shifting the whole model. Unchanged moving-scene and entity-child renderer poses are omitted from frame records; playback uses the world snapshot as their baseline, while an explicit hidden pose records a destroyed child renderer. World and room-visibility capture are spread across main-thread frames with a 3 ms step target, and scene references use 1 ms steps while the dungeon generates; individual Unity calls and game level generation can exceed those targets. Serialization and disk writes use a background worker. A full output queue temporarily pauses sampling while accepted data drains. Transient file writes and flushes retry, and other recording exceptions restart capture in another part after a bounded backoff. Persistent storage faults can still leave an incomplete part; disk-full errors require freeing space and pressing F8. Initialization failures require restarting the game.


New recordings store sound actions rather than audio buffers. Enemy/item effects include their owning prefab, clip field/array identity and source path, avoiding collisions between unrelated same-named clips. Playback restores native source attenuation/custom rolloff and silences world effects past the native maximum distance. The removed `CaptureAudio` and `CaptureVoiceChat` options in older configuration files are ignored. Older files containing waveform blocks remain playable. While watching a replay inside a connected game, live audio remains active and replay effects also use the original player listener at the real player position.

Long recordings open with a 4 MiB frame/event window, then use 48 MiB windows plus their current world, with one recently used window and one prefetched window. Prior state is reduced to the latest value per actor/Animator parameter/item instead of rereading the entire event history. Destroyed actor tracking is removed; window audio objects and expired actor models are destroyed; native mesh and static-batch caches each have a 64 MiB ceiling. Player and enemy playback keep the native Animator hierarchy and IK constraints; all animation graphs are evaluated manually so pausing/seeking does not let them run independently. Initial world loads still show progress; spawning another known entity within that world does not show a Loading overlay.

## Compatibility and troubleshooting

- Runtime reflection locates game components, fields and optional hooks without a compile-time `Assembly-CSharp.dll` dependency. Missing optional members are logged and skipped.
- The target is Unity 2022.3, .NET Standard 2.1 and BepInEx 5. Input System and HDRP integration are detected at runtime.
- Scene paths that exceed the file-format limit or collide use embedded geometry instead of stopping automatic saving. Each recording that references baked moon scenery needs that same Lethal Company game version for playback; a missing/mismatched scene produces a clear error instead of silently omitting the ground. Older recordings remain readable. Multiple-day campaigns, remote-client perspectives, every old/new game build and all modpacks remain unverified.
- Check BepInEx logs for `Replay runtime active`, `Replay keyboard input ready`, `Replay pause-menu hook ready`, `Replay archive directory` and `Saved replay`. Menu attachment failures include a reason and are retried.
- A missing or torn JSON manifest can be recovered from bounded file headers. An execution that never wrote any `.lcr` data cannot be reconstructed.

## Build and test

Use .NET SDK 8 or later and the installed game's `UnityEngine.UI.dll`, `Unity.TextMeshPro.dll` and `Unity.InputSystem.dll`. The default Steam path is used; another install can be selected with `-p:GameManagedPath="D:\SteamLibrary\steamapps\common\Lethal Company\Lethal Company_Data\Managed"`. These reference assemblies are not packaged.

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
- [LethalConfig in-game menu reference](https://github.com/AinaVT/LethalConfig)

MIT licensed. Game and third-party assets, source and assemblies remain subject to their own licenses. No LethalConfig code, prefab or bundled font asset is distributed.


## Recording bookmarks and daylight

Backtick adds a bookmark during recording. [InputUtils](https://github.com/Rune580/LethalCompanyInputUtils) adds Replay / Add replay bookmark to the game Controls; otherwise configure Input.BookmarkKey. Archive rows show bookmark counts and cyan timeline markers seek when clicked. The replay dock shows the game clock; Details includes game start time and the local recording timestamp. New captures save only changed sunlight scalars once per second, along with sky changes. Native moon sun curves also restore daytime progression for older installed-scene files with recorded game time.
