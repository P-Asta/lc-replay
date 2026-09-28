# Verification status and runtime checks

## 0.23.0 status

- Release solution build: zero warnings/errors. Core, Archive and Lifecycle suites pass 26/26, 33/33 and 6/6 groups. Existing Core groups now cover separate entrance volumes, linear texture metadata, emitter-specific particle/line state, strict streaming UTF-8 decoding, ordered parallel reads, cached-world validation and file-change detection.
- Installed v81 / BepInEx 5.4.23.5 / Unity 2022.3.62 / DX11 probes verified native normal/mask/base-map identity, recorded smoothness, zero unnecessary PNG decodes, separate entrance-volume containment without filling the gap, particle stretch mode, 2x2 texture-sheet settings, alpha 0.4, 3D size and a simulated turret-style loop. The final probe also verified zero gameplay Awake calls from cloned particle objects and the transparent fallback when no source template exists. These are engine-level fixture checks, not a full turret encounter or every weather effect. Evidence: `artifacts/recording-test/fidelity-v023-final.log`.
- Isolated playback-only native runs opened the real 81,964,291-byte recording and sought to 75 seconds, with no LC Replay exception. Comparing the packaged 0.22.2 baseline with the two candidate runs on the same file/configuration: file-open stage 6.670 -> 4.710-4.760 seconds; initial scene ready 8.862 -> 6.245-6.657 seconds; seek to 75 seconds ready 6.232 -> 2.227-2.459 seconds. These are local runs with a warm file cache at a 30 FPS test cap, not a general loading-time guarantee. Evidence: `artifacts/recording-test/loading-v0222-baseline.log`, `loading-v023-player.log` and `fidelity-v023-final.log`.
- A separate .NET 10 file-decoding benchmark (20 logical CPUs) reduced first-window managed allocations from about 1,324 MiB to 912 MiB; bounded world reuse reduced adjacent-window allocations from about 643 MiB to 320 MiB. Managed allocation totals are not peak working-set size. Results in `artifacts/loading-benchmark/results-81mb.txt` and `results-161mb.txt` exclude Unity scene construction.
- Source-specific short-lived particles and mine entrance bounds need fresh recordings. GPU VisualEffect bee/locust swarms remain approximate. Actual turret laser appearance, all weather shaders and every mineshaft entrance variant still require gameplay visual comparison; the fixture checks do not establish pixel-identical reproduction.

## 0.22.2 status

- The release solution builds with zero warnings. Core, Archive and Lifecycle suites pass 26/26, 33/33 and 6/6 groups.
- Archive tests verify that later runs follow earlier ones, sessions and days retain save order, and quota/deadline recordings appear in the order they were created (3, 2, then 1 days remaining). Multipart playback order remains numeric by part.
- A live in-game pass is still needed to confirm the archive's visual order after refreshing the F9 list.

## 0.22.1 status

- The release solution builds with zero warnings. Core, Archive and Lifecycle suites pass 26/26, 33/33 and 6/6 groups.
- The archive's full-screen backdrop, main surface, columns and confirmation background, plus the playback bars and pop-up windows, now use fully opaque colors. The scroll viewport keeps an invisible mask image because it is a clipping control rather than a visible background.
- The colors and three-column arrangement were compared with [LethalConfig's menu example](https://github.com/AinaVT/LethalConfig/blob/main/.github/images/menu-example.gif). Live-game visual checks at different resolutions are still needed.

## 0.22.0 status

- The release solution builds with zero warnings and the Core, Archive and Lifecycle suites pass 26/26, 33/33 and 6/6 groups. The UI is drawn with native Unity controls; there is no new runtime dependency on Imperium.
- The interface colors and floating-window layout were checked against Imperium's [default theme image](https://github.com/giosuel/imperium/blob/main/assets/themes/imperium.png). A fresh live-game check is still needed for text clipping, scaling and menu hover at different resolutions.

## 0.21.0 status

- The release solution builds with zero warnings. Core, Archive and Lifecycle suites pass 26/26, 33/33 and 6/6 groups. The new Core test compares the playback sampler against independent samples while repeatedly seeking forward and backward across entity, bone, renderer, anchor and discrete-state changes.
- The 4× and timeline-drag paths now avoid repeated activation resets, full audio-event scans and full sparse-visual event replays. World reconstruction is budgeted across frames; adjacent bounded file windows can be reused from memory. These changes have not yet been timed or visually verified in the installed game.

## 0.20.0 status

- The release solution builds and the Core, Archive and Lifecycle suites pass 25/25, 33/33 and 6/6 groups. The Core single-file test now checks that sparse renderer and spray events survive later playback windows and an indexed sidecar.
- ILSpy inspection of the installed game's `SprayPaintItem` confirmed that spray marks are pooled HDRP `DecalProjector` instances. The plugin records their changes as sparse events and reconstructs render-only projectors.
- Audio distance, destroyed scenery, spray marks, self-lit rooms and the redesigned native UI still need a fresh live-game visual/audio check. Existing recordings lack the new global-audio and sparse-visual metadata, so these corrections primarily apply to new captures.

## 0.19.0 status

- The release solution builds with zero warnings. Core, Archive and Lifecycle suites pass 25/25, 33/33 and 6/6 groups. Archive tests cover deleting a quota folder with completed and empty prepared days while preserving another quota, plus refusal of a batch containing an active recording.
- ILSpy inspection of the installed game assembly confirmed that `HangarShipDoor.PlayDoorAnimation` drives `shipDoorsAnimator.SetBool("Closed", closed)`. Capture now includes that animator's renderers and stores their frame poses relative to the moving ship anchor.
- The particle blending and Circuit Bee visual changes are code-verified but have **not** been checked visually in a live game session. VFX Graph swarms remain an approximation; a fresh recording is needed to include newly tracked ship-door motion.

## 0.18.0 status

- The solution builds with zero warnings. Core, Archive and Lifecycle suites pass 25/25, 31/31 and 6/6 groups. New archive tests delete one quota recording while preserving its neighbor and shared quota metadata, delete a legacy multipart recording, and reject active/outside paths.
- The F9 archive now has a selected-recording Delete confirmation and clearer column/action hierarchy. A native game run has not yet verified the new layout or mouse/keyboard interaction; deletion behavior has been verified against real temporary archive files in automated tests.

## 0.17.0 status

- The solution builds with zero warnings. Core, Archive and Lifecycle suites pass 25/25, 29/29 and 6/6 groups. New Core checks cover progress callbacks, world revisions and regenerated sidecar indexes.
- A complete older 161 MB `.lcr` without a sidecar took 10.15 seconds to index on the first CLI run and 0.55 seconds on a second CLI run using the generated `.lci` (both timings include `dotnet run` process startup). Initial playback reads a 48 MiB expanded frame/event window, then builds scene assets across main-thread frames with a visible progress bar.
- A hidden-window isolated v81 game launch did not start BepInEx, so the new particle material behavior and loading bars have **not** been visually verified in-game. Rain, lightning and other particle effects still require a direct live-game check.

## 0.16.0 status

- The solution builds with zero warnings. Core, Archive and Lifecycle automated suites pass 60/60 groups.
- Disable culling now bypasses the exterior-camera test for recorded interior renderers, lights, fog and sampled effects. The replay Settings panel offers Player sounds OFF for files tagged with player/held-item audio ownership; older files show an unavailable-state label.
- A bounded main-thread fallback samples readable `DecompressOnLoad` clips from virtualized `AudioSource`s and one-shot calls while the existing audio worker compresses blocks off-thread. Streaming or compressed clips and non-AudioSource sound paths are still unavailable. A fresh native v81 game run has not yet verified the fallback's audible range, player-sound classifications or interior visibility from outdoors.

## 0.15.0 status

- The solution builds with zero warnings. Core, Archive, and Lifecycle suites pass 25/25, 29/29, and 6/6 groups. New Core coverage checks bounded line path round trips and malformed position rejection.
- The game assembly was inspected with ILSpy for `TimeOfDay`, `Turret`, `ItemDropship`, `MineshaftElevatorController` and player voice-source fields. The first quota/deadline naming now waits for a positive quota and derives days from `timeUntilDeadline / totalTime`; newly spawned visual entities and active line renderers are sampled after the main world snapshot.
- A native v81 expedition has not yet been run for the new room tagging, dropship/elevator meshes, turret effects, bee lightning approximation or far-audio behavior. Unity can virtualize sources outside the current listener's range, so their PCM cannot be guaranteed by an AudioSource filter.

## 0.14.0 status

- The release solution builds with zero warnings. Core, Archive, and Lifecycle suites pass 24/24, 29/29, and 6/6 groups. Core now covers moving unowned furniture poses, hidden baselines, and spatial audio metadata round trips.
- A new recording stores the position and distance settings of each captured sound source, moving generated furniture poses, larger enemy skeletons and skin root bones. It also captures geometry for enemies spawned after the main map snapshot in a bounded incremental world record. These fields are optional so earlier schema-1 recordings remain readable, but they cannot gain motion or spatial information that was never recorded.
- A fresh isolated game process was attempted for this build, but BepInEx did not start in that launch environment; no new native rendering or audio result can be claimed yet. The particle material change and new capture paths still need direct visual and listening checks in v81 and any other targeted game build.

## 0.13.1 status

- Audio callbacks now queue bounded PCM blocks; a dedicated worker encodes ADPCM and base64 data. Destroyed audio taps are pruned, and short effects flush when their source stops or is destroyed. The recording format is unchanged from 0.13.0.
- In an isolated v81 / BepInEx 5.4.23.5 loopback expedition, a **81,964,291-byte**, **133.2-second** `130/3.lcr` saved as one complete file and reopened through the native archive. Before the final save, the probe read **523 audio blocks**, including one from a **0.12-second effect whose source was destroyed**, plus **81 frames containing short-lived particles**. The completed file contains **758 audio blocks and zero reported dropped blocks**. It also verified that replay during the live session preserved the recorder and restored player input on close. Evidence: `artifacts/recording-test/audio-worker-v0131-expedition.log`; file statistics from `tools/inspect_recording_stats.py`.
- This verifies the new worker and short-effect flush path in one real game run. Four capture sample gaps remained, with a maximum of **1.162 seconds** during the run. It does not establish a controlled reduction in game-frame stalls across maps, hardware or modpacks.

## 0.13.0 status

- The Core, Archive and Lifecycle suites pass **58/58 groups** (23, 29 and 6). New Core checks cover color-temperature and short-lived particle round trips, audio ADPCM waveform recovery, and malformed block rejection.
- An isolated Lethal Company v81 / BepInEx 5.4.23.5 loopback expedition wrote one complete **87,401,021-byte `130/3.lcr`** with **1,217 frames**. Before entering the facility, the probe read five world records, 641 generated interior meshes with 2,191 resolved mesh references, **550 sound blocks** and **99 frames with sampled burst particles**. The strongest decoded sound block had a 0.282 normalized peak. It opened an older replay while still connected; the live recorder's clock advanced, local movement/look input was disabled in the viewer and restored on close. The subsequent normal disconnect saved cleanly and reopened the archive recording. Evidence: `artifacts/recording-test/expedition-v013-verify.log`.
- A native procedural test reconstructed sampled burst particles, a non-silent decoded audio clip and a visible landmine fallback alongside existing instanced grass, looping particles, local fog and Unity Terrain. It ended with `passed=True`; evidence: `artifacts/recording-test/procedural-audio-v013-verify.log`.
- A separate loopback host test changed the live deadline display from three to two days and restored it. The active recording kept the same archive day and finished as one file after normal disconnect; evidence: `artifacts/recording-test/deadline-v013-verify.log`. This tests deadline display stability, while an actual complete multi-day return-to-orbit sequence remains covered by lifecycle tests rather than a native three-day playthrough.
- A final loopback host check verified that player movement effects are included while a voice-chat source is excluded by default, then saved and reopened a complete recording (`artifacts/recording-test/audio-filter-v013-verify2.log`). Audio is recorded from observed Unity AudioSources at 22.05 kHz mono. This is not a pixel or waveform-identical video capture; GPU-only effects, exact spatialization, other moons and every mod combination remain unverified.

## 0.12.1 status

- A user run of 0.12.0 stopped automatic saving with `Invalid scene renderer path` after an exterior capture reported 251 installed-scene surfaces. That message could mean an empty, overlong or duplicate path; the original offending path was not present in the log.
- The 0.12.1 recorder filters scene paths longer than 256 characters, duplicate paths and reference-list overflow before they reach the background writer. Rejected surfaces use embedded geometry. The three automated suites pass **56/56 groups** (21 Core, 29 Archive, 6 Lifecycle).
- In isolated v81 / BepInEx 5.4.23.5 tests, a synthetic 316-character renderer path was excluded from scene references. A loopback expedition then recorded that fixture as ordinary mesh geometry and saved a complete **71,044,163-byte**, **108.8-second** `130/3.lcr` with 911 frames. Its exterior world records had 383 scene references, maximum path length 30 and no duplicate paths; the fixture appears in the embedded geometry. The native archive opened and replayed the saved file, including original `terrainMap` scenery, generated rooms and a skinned player. Both probes ended with `passed=True`. Evidence is `artifacts/recording-test/sceneasset-v0121-path-bepinex.log`, `sceneasset-v0121-expedition-bepinex.log` and their result files.
- The exact moon/mod combination from the failed user run has not been reproduced locally. The source-path guards cover all three conditions reported by the validator, while other unrelated capture or disk errors can still stop a recording.

## 0.12.0 status

- The three automated suites pass **56/56 groups** (21 Core, 29 Archive, 6 Lifecycle). The new Core group round-trips bounded moon-scene component paths and level/map/dungeon generation settings and rejects malformed references.
- An isolated v81 / BepInEx 5.4.23.5 loopback LAN expedition saved one complete **65,849,017-byte** `130/3.lcr` containing 905 frames and five world records. A prior 0.11 test file was 124,058,148 bytes with 1,110 frames and eight world records; the different durations and generated layouts mean the 47% total-size difference is an observation, not a controlled compression benchmark. World-record bytes fell from 98,276,608 to 46,400,362. The new final sky/fog update is only 80,575 bytes on disk and did not duplicate static geometry.
- While recording Experimentation, 386 built-in moon-scene renderer paths were saved in each exterior capture instead of embedding those surfaces. The game still recorded generated rooms, generated rocks, moving objects, player bodies and state frames. Playback in a separate isolated launch loaded `Level1Experimentation` and rendered **389 selected original-scene renderers**, including `terrainMap` and `OutOfBoundsTerrain`. The native probe also verified a skinned player, generated rooms, seven natural LOD groups, two visible swarms, and return of 207 captured exterior renderers after indoor/outdoor transitions. A further offscreen render of the original `terrainMap` produced `artifacts/recording-test/sceneasset-ground-v012.png`. Both playback probes ended with `passed=True`; evidence is `sceneasset-v012-playback-bepinex.log`, `sceneasset-v012-ground-bepinex.log` and their result files in the same directory.
- The plugin records the current level ID, map seed and dungeon-flow/seed values. The native player uses the installed game scene for baked exterior scenery; it does **not** regenerate procedural dungeon rooms from seed. Those rooms still cost file space and are bounded by the existing capture limits. A recording using scene references requires the same game version for playback. Other moons, modded scenery, weather and cross-version playback are not verified.

## 0.11.0 status

- The three automated suites pass **55/55 groups** (20 Core, 29 Archive, 6 Lifecycle). New checks cover one physical quota/deadline file, collision-safe repeated deadline names, sidecar/fallback indexing, bounded playback windows and carried world state.
- An isolated Lethal Company v81 / BepInEx 5.4.23.5 LAN expedition wrote one **124,058,148-byte `130/3.lcr`** with a 32,088-byte `.lci` offset index. The native archive loaded and sought within the recording; the probe verified 1,111 frames, exterior/interior scene reconstruction, a native HUD, and return of 527 exterior renderers after indoor/outdoor transitions. It ended with `passed=True`. The `.lci` contains indexing data; deleting it triggers a slower `.lcr` scan, not loss of the recording.
- A separate isolated native synthetic Terrain test captured **16,641 height-field vertices** with sampled normals and UVs, a source terrain-layer texture, and its 8× tiling. Native playback reconstructed the same 16,641-vertex mesh and its texture; the procedural/fog checks in that run also passed. This validates the Unity Terrain fallback but does not compare its image pixel-for-pixel with a live moon.
- On Experimentation, the live `terrainMap` mesh used a source 256×256 base-color map and a 512×512 normal map. The recorder preserves their source dimensions and material UV tiling; it cannot increase source detail. Other moons, weather and modded ground shaders are not comprehensively verified, and old recordings cannot gain newly captured terrain details.

## 0.10.0 status

- The three automated suites pass **54/54 groups** (19 Core, 29 Archive, 6 Lifecycle). Core now round-trips bounded 3D local-fog masks and rejects an invalid mask byte count.
- In an isolated Lethal Company v81 / BepInEx 5.4.23.5 loopback expedition, a generated level recorded **813 frames in two complete parts**. Four CPU-readable 3D fog masks totaling 524,288 RGBA bytes were stored across the captured worlds; the real `DustStorm` and `LocalRollingFog` sources used 32³ density textures. The probe returned to the main menu, opened the archive, rendered exterior and interior replay targets, checked room visibility and restored all 562 exterior renderers after two indoor/outdoor transitions. It ended with `passed=True`.
- Native replay inspection of a user recording confirmed the affected ground material has one texture level, detailed tree geometry remains active at 70 m and switches to distant detail at 400 m, and the existing local-fog color values were restored. A synthetic capture/replay probe separately reconstructed a 4³ density mask. The fog hue compensation was compared in native offscreen images; visible color improved, but this is not a pixel-matched comparison against live gameplay.
- Weather transitions, every custom fog shader/mask and all game versions remain unverified. Older recordings gain playback color and detail changes but cannot recover unsaved 3D masks.

## 0.9.0 status

- The automated suites pass **54/54 groups** (19 Core, 29 Archive, 6 Lifecycle). The Core round trip now includes bounded local fog data and rejects invalid density and count.
- In isolated Lethal Company v81 with BepInEx 5.4.23.5, replayed a real user recording containing the affected HDRP/Lit `Leaves1 2` material. The recorded PNG retained transparent pixels; a native render after material validation showed separate leaves without the blue texture background.
- The replay camera enabled its HDRP atmosphere and volumetric frame overrides. A synthetic local fog in the native renderer visibly filled the air around the isolated bush, and the fog component had the expected density, size and enabled state.
- A separate native synthetic capture test created a `LocalVolumetricFog`, captured it alongside grass and particles, and reconstructed an enabled local fog in playback. That test finished with `passed=True`. The old user recording cannot contain local fog it never saved; a new recording is needed for fake fog. Weather transitions and custom fog masks remain unverified.

## 0.8.0 status

- The three automated suites pass **54/54 groups**: 19 Core, 29 Archive and 6 Lifecycle. The new Core group round-trips procedural grass matrices, natural LOD metadata and looping particle emitters, and rejects invalid data.
- Two isolated v81 / Unity 2022.3.62 / BepInEx 5.4.23.5 loopback expeditions saved complete two-part recordings under `130/3`. The final run with complete-world reuse saved **813 frames**; its second part's largest recorded frame gap was **0.136 s** (99th percentile **0.134 s**) and made no new world scan after the storage-part boundary. The first part's **0.958 s** largest gap occurred during game landing/generation. These are sampled recording intervals, not a universal frame-time benchmark.
- Native replay reconstructed 151–156 mipmapped textures, a skinned player body, **5–12 paired natural LOD groups** that switched detail with spectator distance, and **2 Red Locust swarm visuals**. Exterior geometry returned after two indoor/outdoor transitions. Both expedition probes finished with `passed=True`.
- A separate disconnected playback-only run on the final viewer build passed whole-recording seeking, menu/HUD controls, indoor/outdoor restoration, natural LOD switching, swarm reconstruction and sky/interior render-target checks.
- A separate in-game synthetic scene captured a `SpawnGrassOnMesh` instance and a looping ParticleSystem, recreated both in replay, and rendered both into an offscreen target. The grass and particles are visible in `artifacts/recording-test/procedural-replay-target.png`; this verifies the rendering path but not every moon's procedural effects.
- The v81 game and probe used hidden-window, isolated ES3 saves and a 127.0.0.1 LAN host. Pixel-identical graphics, all weather and custom shaders, transient GPU VFX state and other game versions remain unverified. Source texture resolution can be lower than the recorder's 1024-pixel cap.

## 0.7.0 status

- The three automated suites pass **53/53 groups**: 18 Core, 29 Archive and 6 Lifecycle. Core's texture-boundary test now checks the 1024-pixel reader limit.
- An isolated v81 / Unity 2022.3.62 / BepInEx 5.4.23.5 loopback LAN expedition saved 102.51 seconds in two complete parts under `130/3`, with 752 frames. Its generated facility was captured while the player remained outside.
- The captured worlds contained **104 texture maps above the old 256-pixel limit** among 250 maps inspected. The new capture contained no first-person visor geometry. Native playback loaded 153 textures with mipmaps and trilinear filtering, reconstructed the skinned player body, kept nearby interior geometry visible, and restored 576 exterior renderers after two indoor/outdoor transitions. The probe ended with `passed=True`.
- The game ran in a hidden window with isolated ES3 saves and a 127.0.0.1 LAN host. Offscreen render-target captures show the ship and an interior wall, but they cannot establish visual parity with a normally displayed game window on every map or mod shader. Existing 256-pixel recordings gain distance filtering but cannot recover missing close-up detail.

## 0.6.0 status

- The three automated suites pass **53/53 groups**: 18 Core, 29 Archive and 6 Lifecycle. The new Core group covers round trips and rejection of invalid sky, fog, room and shader-property data.
- An isolated v81 / Unity 2022.3.62 / BepInEx 5.4.23.5 loopback LAN expedition generated the real facility while the recorded player remained outside. The automatic `130/3` recording saved **100.84044 seconds in two complete parts**, with 762 frames and 67,821 entity snapshots. Test game saves stayed under the workspace, separate from the user's game saves.
- The saved records held six sky cubemap faces and blended HDRP sky, fog, exposure and color-grading parameters. Exterior geometry spanned two bounded chunks; a separate interior record contained 1,463 render objects and raw bounds for 87 generated tiles. The replay ignores any room bound without captured interior geometry; one such bound overlapped the player outside the facility.
- In a new disconnected process, the native archive Play button loaded that recording. The playback Settings sliders changed the render target to 35% of the 2560×1440 test window (896×504) and saved gamma 1.25. Offscreen render-target captures showed the ship scene, a room and outdoor sky/fog/terrain; see `artifacts/recording-test/replay-target-*.png`. The test used explicit `Camera.Render()` calls because a hidden Windows game process did not continuously render its target.
- Native playback preserved camera, pause, speed and player selection across the two storage parts and backward seeking. A recorded skinned body and nearby interior renderers were reconstructed without gameplay behaviours/colliders or visible debug bounds. **All 588 exterior renderers returned after each of two interior→exterior transitions**, and hid again inside. The final playback probe reported `passed=True`.
- The visual captures confirm the new environment appears in this tested scene; they do not establish pixel-identical appearance on every moon, weather or mod shader. Sky cubemap capture, textures and geometry remain bounded. Unsupported particles, baked lighting and audio still differ from live play.

## 0.5.0 status

- The three automated suites pass: 17 Core, 29 Archive and 6 Lifecycle groups. Core includes moving-anchor interpolation, lit-world data round trips and duplicate-anchor rejection.
- An isolated v81 / Unity 2022.3.62 / BepInEx 5.4.23.5 LAN expedition recorded 91.075 seconds in **one** complete file under `130/3`: 717 frames and 53,235 entity snapshots. A comparable 0.4 run needed eight parts. Only three world captures occurred (initial ship, scene load and generated facility). The median frame interval was 0.133 seconds; the maximum was 1.259 seconds during one of those scene captures. This supports removal of recurring save stalls, but does not claim a zero-stutter recording.
- The ship-elevator anchor was present in all 717 frames and moved from y=70.34 to y=0.28; the recorded player covered y=70.36 to y=0.29. The world contained 96 ship-anchored renderers at the final capture, so playback can move the hull with the actor through landing.
- The final facility snapshot contained 1,008 interior renderers and 86 captured lights. The live UI loaded the saved file with a skinned player body and camera-controlled room visibility. A follow-up screenshot exposed HDRP light intensity being zero; the replay light setup now sets the HDRP-specific intensity explicitly. A low-power spectator lamp and local light culling make nearby walls and floor visible without lighting the whole facility.
- A disconnected spectator test entered a recorded room and verified that all 141 reconstructed exterior renderers were hidden; moving the camera outside made all 141 visible again. Only five nearby lights remained active in the test room. `artifacts/recording-test/inspect-latest.png` shows the resulting scene at 1280×720.
- In a separate isolated host, changing the game's `daysUntilDeadline` field from 3 to 2 caused the `130/3` recording to finalize with `saved` status and a new recording to start in `130/2`. Both parts completed, the archive opened the new recording, and native playback loaded it. This was a controlled field change, not a full three-day campaign.
- A new-process playback test loaded an older eight-part, 91.150-second recording with no captured light list. The viewer kept its unlit fallback, advanced from the first to second part, sought backward while preserving camera/pause/speed/player selection, reached the final pose and restored native menu input. The probe ended with `passed=True`.
- Previous 0.4 evidence remains below. Baked lightmaps, post-processing and effects audio are still outside this replay format, and each world capture remains bounded by its geometry/texture budgets.

## 0.4.0 status

**Automated checks: 51 groups pass (16 Core, 29 Archive, 6 Lifecycle).** The Release test assemblies were executed with .NET 10. Both a playback-only run on older recordings and an isolated v81 expedition capture/save/replay run passed.

| Suite | Coverage |
| --- | --- |
| Core — 16 groups | Compression round trips; every truncated byte boundary; schema, number, array and size limits; bounded writer draining/failure; lifecycle-aware sampling; quaternion and renderer interpolation; texture/material/skin round trips and validation; legacy defaults; stable equal-time ordering; whole-recording gaps, overlaps, end boundaries and backward seeks; duration recovery/cancellation; static mesh instance references |
| Archive — 29 groups | Legacy launch/session/day layouts and ordered parts; collision safety; missing/torn manifest recovery; loose and multipart legacy files; bounded header-only scans; concurrent scans; path safety and clean/interrupted runs; long Windows paths; numeric remaining-quota/deadline hierarchy; reconnects; explicit unknown values; quota recovery and mixed layouts; damaged neighboring parts; matching duration offsets; atomic manifest replacement/read-sharing and permanent-lock preservation |
| Lifecycle — 6 groups | Initial orbit; late joining; three observed expeditions; repeated observations/deaths; no empty trailing expedition; reconnect behavior |

The Core duration tests validate real headers before exercising malformed record lengths and aggregate expansion limits. Mesh-instance tests reject missing/forward/self/cyclic/chained references, skinned sources and duplicate payload arrays. Automated archive tests do not establish that a particular game version exposes the expected quota/deadline fields; that requires the runtime checks below.

## 0.4 playback-only result

A real-game run replayed six older parts as one **71.2247926-second** recording. The probe verified English native controls, full-duration timeline range, natural forward part transitions, backward seeks across parts, the complete final pose and camera preservation in a persistent viewer. This demonstrates the new playback path on existing data, not the new generated-interior capture path.

Local playback evidence: `artifacts/recording-test/playback-v040.log`.

A fresh process then replayed the new eight-part expedition recording as **91.1500432 seconds** using the final build. The archive duration matched the playback clock within the 0.25-second manifest/header tolerance. Pause/resume, full-range slider, natural forward transition, backward cross-part seek, end seeking, persistent camera/HUD and restoration of the original menu input all passed. Final-build evidence: `artifacts/recording-test/playback-v040-verified.log` and `playback-result.txt`.

## 0.4 expedition result

The passing run is `artifacts/recording-test/expedition-v040-verified.log`: game v81 / Unity 2022.3.62 / BepInEx 5.4.23.5, D3D11 at 1280×720, with ShipLoot 1.1. Test saves stayed inside the workspace, and the LAN host bound to 127.0.0.1. The probe invoked the game's actual landing/generation path and kept the player outside the facility.

- Automatic saving produced eight complete parts, 656 frames and 107,272,048 bytes under `Run-20260924-014232-16a0e683cf23/130/3/164255-4320ee1f104a`. The continuous timeline spans about 91 seconds, including storage-boundary gaps.
- A generated snapshot retained 1,104 interior renderers. The pre-entry file check validated recorded interior data, repeated-mesh references and 506 completed-part frames showing the player outside. Capture remained bounded: large or distant objects could still be omitted, and this is not evidence of capturing every surface.
- The world-space vertices of 38 unowned static-batch slices fell within the corresponding live Unity renderer bounds. Testing had exposed an additional nonuniform-scale/shear error, fixed by saving these slices directly in world coordinates.
- After normal disconnect, the production archive opened and replayed the saved files. Native UI checks passed. The viewer had a native skinned player body without gameplay behaviours/colliders, no visible default proxy/debug surfaces, and an interior renderer that hid far from the camera and appeared nearby.
- Inspected screenshots: `saved-archive.png` (English quota/deadline library), `recorded-default-view.png` (unaltered initial camera), and `expedition-interior.png` (probe-positioned free camera inside a recorded room). The probe ended with `passed=True`.

An earlier attempt exposed a transient Windows `File.Replace` failure; its external cause was not established. Bounded retries and previous-manifest preservation tests were added, and the final run completed without that error. Older failed diagnostic logs are retained but do not represent the passing result. These checks do not cover a multi-day campaign, remote clients, every enemy, vehicle, map or modpack.

## 0.4 real-game checklist

1. Launch a clean BepInEx 5 profile. Confirm `Settings → Replay → Credits`, English archive/HUD captions, native Canvas/TMP/Button/Slider/ScrollRect controls, F9 operation, and restoration of the original menu EventSystem after closing.
2. Host or join and verify automatic recording. Compare the displayed target quota, fulfilled amount and deadline with the header metadata. Confirm the path is `Run-.../<max(target-fulfilled,0)>/<days-left>/<HHmmss-shortid>/part-xxxx.lcr`, and unavailable values remain `unknown`.
3. Start a real expedition and wait for procedural dungeon generation. Keep the local player outside the facility while a production recording is saved. Read that `.lcr` and verify interior mesh data was captured before entry, with valid `MeshSourceId` references for repeated static geometry.
4. Reopen the recording from the main menu in a fresh process. Check exterior and interior surfaces, body mesh/texture and supported bone movement. Move the replay camera into recorded rooms; nearby rooms should appear, including rooms never entered by the recording player. Confirm that proximity culling does not leave the camera's current room hidden.
5. Check that interaction-only fallback shapes, debug/trigger geometry and unreadable bounds do not appear as solid walls. Exercise doors and moving child renderers; verify normal visible objects still render.
6. Obtain multiple storage parts. Verify a single total-duration slider spans them all. Let playback naturally cross a part boundary, then seek backward across it while paused. Assert the same viewer/HUD/camera survive and camera pose, playback speed, pause state and selected player remain consistent.
7. Seek to the complete recording's end. Confirm the final pose and paused state, and that another quota/deadline recording is not opened. Close during a pending part load and confirm a late background result does not reopen playback or restore disposed objects.
8. Finish an expedition, change the quota/deadline, reconnect and record again. Confirm distinct recordings under the correct numeric groups, no accidental overwrites, and preserved legacy session/day entries.
9. Test F10 bookmarks and F8 checkpoints/retry. Confirm the old exact `WorldVertices=120000` migrates once to 500000 while a different custom value is retained within the allowed range.

The ignored integration probe uses isolated ES3 saves and a 127.0.0.1 LAN host. Its playback branch exercises native button callbacks and the persistent `PartIndex`/global-time contract. It saves an **unaltered default camera screenshot before test-only framing**. Future runs should retain both their result and inspected screenshots. Probe code and test saves are not shipped.

## Historical real-game evidence

The results in this section belong to earlier releases. They are not a 0.4 expedition or continuous-timeline test.

### 0.2.3 — initialization and menu

On game v81 / Unity 2022.3.62 / BepInEx 5.4.21, the first scene load was observed destroying the initial plugin/runtime Unity objects before their first Update. The fix retained managed scene callbacks and created the runner after scene load, with cleanup at application shutdown.

A separate D3D11 profile also loaded the existing REC profile's Imperium 1.4.0, FontPatcher 1.2.4/resources, InputUtils 0.7.13, LethalNetworkAPI 3.3.3, ShipLootCruiser 1.0.5 and MonoMod debug patcher. Actual Unity button callbacks and Input System F9 presses opened/closed the archive. Screenshots confirmed the 1280×720 menu order and game-style font. The original user's plugin profile was not modified, and this menu probe did not host or join a game.

Historical local evidence: `artifacts/runtime-test/main-menu.png`, `archive.png`, `full-profile-evidence.log`.

### 0.2.4 — automatic saving and recovery

An isolated v81 / BepInEx 5.4.23.5 / ShipLoot 1.1 process reproduced a 279-character temporary manifest path failing in Unity Mono. Compact path names then saved successfully under a 94-character root matching the affected mod-manager path length.

About 70 seconds in a loopback LAN lobby produced six complete parts and 598 frames with world snapshots. The size estimate caused rotation before the 60-second maximum. The measured largest within-part sample gap was 0.441 seconds, with boundary gaps of 0.033–0.037 seconds. A fresh process loaded the archive and advanced to the next part. Empty player slots were excluded from default camera/player selection. These recordings used the earlier simple appearance pipeline.

The original failed user run contained manifests and an empty recording directory but no `.lcr`; no gameplay data existed to recover. It was left unchanged. Historical local evidence includes `artifacts/recording-test/result.txt`, `recording-player.log`, `frame-statistics.json`, `playback-result.txt` and playback screenshots. Microphone initialization errors occurred, but the tested recorder did not report a storage error.

### 0.3.0 — native controls and embedded appearance

About 71 seconds in the isolated v81 / BepInEx 5.4.23.5 lobby produced six complete parts and 593 frames. The first part contained 105 appearance entries, 73,966 vertices, 39 PNGs (about 1.57 MiB), 87 materials and a player skin with 7,998 vertices and 43 bone references. A fresh menu-only process reconstructed the stored ship textures, meshes and player body without loading playable level prefabs.

The probe verified native Canvas, TMP, ScrollRect, Button, Slider and EventSystem components; archive play; pause/frozen clock; seek; resume; details; next-part continuation; return to the archive; and original menu input restoration. Korean caption glyphs and visible trigger/debug regions found in earlier attempts were fixed for that release. Version 0.4 changes fixed product captions to English while retaining Unicode name support.

Historical evidence: `artifacts/recording-test/native-filtered-player.log`, `native-final-player.log`, `archive-ready.png` and `playback-default-view.png`. The last file shows the product's initial view; `playback-view.png` may use probe-only camera framing and is not evidence of default placement. These results covered ship/player appearance, not all enemies, generated facilities or mod materials.

## Additional compatibility and failure checks

- Record independently as host and client, compare observed states, and connect to peers without this mod.
- Exercise joining/leaving mid-expedition, death, wipes, repeated landing/departure, vehicles and configured mod component types.
- Verify missing optional fields/hooks on other game versions are handled without stopping the game.
- Test other UI/camera mods, multiple resolutions and Unicode player names; ensure menu navigation does not activate hidden game buttons.
- Measure capture stalls, allocation and playback frame rate on large facilities. Check world/texture limits and repeated-mesh sharing.
- Truncate only a copied recording and verify complete-prefix recovery. Reject corrupt complete records and unknown schemas.
- Test low disk space/permission failures, persistent visible errors, F8 retry, and clean disposal during background loading.
- Check that generation/capture never modifies live dungeon renderer activation or the game's occlusion state.

Voice is opt-in; observed game effects and music use bounded mono audio blocks. Sources outside Unity AudioSource, exact spatial sound, GPU-only particles, baked lighting and unsupported post-processing remain unavailable. Recorded HDRP sky, fog, selected color settings and dynamic lights improve the approximation but do not establish complete visual reproduction of every game environment.
