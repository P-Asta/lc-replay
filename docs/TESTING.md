# Verification status and runtime checks

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

Voice, effects audio, baked lighting and unsupported post-processing remain unavailable. Recorded HDRP sky, fog, selected color settings and dynamic lights improve the approximation but do not establish complete visual reproduction of every game environment.
