# 0.25.41

- Rename the BepInEx plugin ID to pasta.replay. Import the previous io.lcreplay.recorder.cfg on first launch only when the new config does not exist, preserving both the old file and any existing new settings.

# 0.25.40

- Carry the replay free camera with the ship while inside its cabin. Preserve local camera position, ship-relative turning and smooth movement; release on leaving the cabin and avoid double movement while following a player. Keep the camera outside the disposable ship hierarchy.
- Show the recorded turret targeting beam during charging even when native bullet effects are available. Suppress the duplicate straight ray only while the turret is actually firing; retain the native glowing bullet trails.
- Shade the targeting beam through its soft HDR base-color texture instead of constant emission that can fill the entire line quad in installed shader variants.

# 0.25.39

- Share player skin positions and outline work only when both the original position and bone weights match exactly. Retain separate surface normals, tangents and topology at seams and hard edges.
- Calculate body and outline in one worker pass and compute mesh bounds once after uploading each mesh, retaining normal renderer notifications.
- Add an optional Windows x64 numeric skinning accelerator using the same four-influence matrix and outline equations. Validate its ABI and indices, pin arrays only during the synchronous calculation, and retain managed fallback when unavailable or incompatible. Native Unity object access remains on the main thread.
- Cache unchanged local poses only for owned renderer clones; animated rig nodes and reparented rigid parts retain their authoritative updates. Remove the redundant second held-item placement per frame.
- Retain the vanilla graphics baseline, native lighting/material restoration and full animation evaluation on every playback frame.

# 0.25.38

- Remove the camera-facing outdoor fill in normal playback and stop inventing point lights for emissive materials. Keep the explicit NoShadow visibility assist and refresh native night vision after player assets finish loading.
- Record bounded HDRP light shapes, attenuation, diffuse/specular response and volumetric settings. Restore native intensity units and preserve area light types; supplement older files only from an unambiguous matching installed light.
- Restore installed baked/custom reflection probes on the replay layer without realtime cubemap rendering or changing the source scene.
- Preserve captured shader keyword sets, UV transforms when reusing native textures, additional material slots, and renderer/material property-block overrides in new captures.
- Retain the vanilla graphics baseline and 0.25.37 animation/per-frame optimizations. Missing historical material overrides and baked GI are not reconstructed from guesses; this release does not establish a 200 FPS result.

# 0.25.37

- Save a vanilla graphics baseline: 860×520 at resolution multiplier 1, neutral gamma, fog/shadows on and normal interior culling. Migrate once; subsequent Settings changes persist. Show actual dimensions in the resolution control.
- Use installed player-camera HDRP frame settings, no AA, native custom fog quality and recorded LOD distances without the previous 3× distance extension. Preserve recorded weather properties and complete ground surfaces.
- Index teleport/capture-gap events per immutable playback window instead of scanning every sound/animation event for every player and display frame. Preserve general mutable sampler behavior.
- Reuse renderer, actor and hazard lookup tables; avoid redundant hierarchy traversal, component lookup, activation and transform writes. Invalidate caches on world rebuilds and keep late mod asset retries.
- Reuse exact bone-weight matrices and parallelize skin/outline numeric work without changing the evaluated animation poses or mesh channels.

# 0.25.36

- Retain isolated native storm particle templates, including the lightning warning's authored texture-sheet animation, before unloading the round asset scene.
- Preserve recorded realtime light shadows in every room instead of enabling only the first four exported indoor fixtures.
- Parallelize large player skin calculations over bounded numeric-array chunks; keep Unity object access and mesh uploads on the main thread with unchanged skinning equations.
- Capture animated exterior scene renderers as moving geometry, including main entrance doors, and observe entrance open/finish calls in new recordings.
- Let the bounded capture buffer absorb motion/state records while a large map is compressed and while its tail catches up. Raise the bulk-tail count watermark from 48 records to three quarters of the configured buffer capacity, retaining byte limits and disk-failure handling.
- Publish disconnect metadata in the background after accepted recording data is saved. Retire isolated player-scene Harmony guards over bounded main-thread turns instead of unpatching hundreds on one close/cancel frame.

# 0.25.35

- Show top-right notices for eight real-time seconds, with 0.35-second SmoothStep/Lerp slides in from the right and back out to the right.
- Avoid patching existing BepInEx plugin bootstraps during isolated player-asset loading. Skip optional mod types whose unavailable dependencies prevent type inspection, while retaining guards on scene lifecycle callbacks and failing closed for game assembly errors.
- Include the failing method and underlying error when a lifecycle guard cannot be installed.

# 0.25.34

- Prioritize due live snapshots over queued call events, animation polling and map maintenance. Keep a maintenance turn after two consecutive snapshots if the requested sampling rate exceeds available Unity frames.
- Remove the 128-entity output traversal bottleneck. Frozen output remains ordered and memory-bounded; overdue samples allow up to 1.5 ms of output work (0.4 ms otherwise), with 32 records/4096 work units as hard caps. Completed output no longer forces an extra idle frame before a due snapshot.
- Keep sampling deadlines on the configured cadence instead of adding each Unity scheduling delay to every subsequent deadline. Skip missed deadlines without inventing observations. Disk backpressure and accepted-record preservation remain in effect.

# 0.25.33

- Stop drawing a synthetic straight aim ray over the turret's installed native bullet particles. Preserve recorded aim data for particle orientation; warm native particles when seeking into a paused firing state.
- Capture mechanism renderer poses before mesh export supplies a baseline, including the first moving-platform/elevator ride. Keep complete bounded mechanism poses so a later baseline cannot change the meaning of earlier frames.
- Do not bridge known missing indoor player samples with straight-line motion through facility walls. These old gaps hold the last observation and resume at the next sample; the missing path is not recoverable. Outdoor short-gap interpolation remains available.
- Respect observed short player teleports below the old distance threshold. New call records identify the affected player; legacy calls require a matching destination and observed relocation.

# 0.25.32

- Prevent the replay-only exterior fill from illuminating volumetric fog. Preserve its surface lighting and the recorded fog color, density and world lighting.
- Restore the native night-vision light's volumetric dimmers instead of HDRP defaults, avoiding additional indoor fog illumination.

# 0.25.31

- Bridge short player sampling gaps when the recorded endpoints allow plausible continuous movement. Preserve discontinuities for death, excessive distance, changed indoor/elevator state, observed teleport calls and gaps longer than five seconds. Missing intervals are interpolated, not recovered observations.
- Correct delayed tree visibility in older recordings when a later confirmed tree removal matches a positional native break sound. Match the recorded trunk base, retain unmatched events and leave the source recording untouched. Forward playback and backward seeks use the corrected timeline.
- Continue sparse visual-state scans while exporting a map, preventing world capture from postponing tree destruction/visibility observations until the entire map export finishes.

# 0.25.30

- Keep rigid enemy parts on the same evaluated native hierarchy as their skins. Legacy flattened renderer poses no longer detach Sapsucker eyes/eyelids from an animated head; complete newer procedural bone captures remain authoritative. The rule applies to matching vanilla and modded rigs.
- Append new actors, anchors and visual resources within an unchanged capture set while retaining the built map. Streaming-window transitions no longer tear down the interior and ship just to add actor geometry. Hide the loading overlay after a completed world build; genuinely different maps still rebuild.
- Restore legacy embedded terrain from the installed moon only when its level identity, terrain name, transform, dimensions and sampled heights match. This uses native terrain layers, holes and full height data instead of the old single-layer coarse mesh, with the recorded mesh retained when no match is available.

# 0.25.29

- Reuse installed rigid mesh assets without copying their GPU-only draw buffers. This restores separate teeth, jaws and weapon parts through the common prefab path, including matching mod assets. Track borrowed meshes separately so seeking and closing playback cannot destroy game assets.
- Attach an unambiguous visible held item to its recorded owner when utility-slot mods leave the ordinary selected-item field empty. New captures also retain `ItemOnlySlot` and `isPocketed`; hidden pocket contents remain hidden.
- Preserve the original availability time of static world records carried into later streaming windows. Large backward seeks no longer defer the ship, exterior and interior until the later window's timestamp.
- Honor each recorded particle render mode. Invisible bullet emitters no longer produce an extra red stretched tracer; their captured visible flare keeps its original renderer and material.

# 0.25.28

- Smooth native animation state changes in older recordings that contain only partial procedural bone poses. Cache each outgoing pose using the recorded inputs at its transition boundary, including interrupted transitions and backward seeks. Complete visual pose recordings retain their captured transitions.
- Record the actual held-item owner, attachment transform and runtime item offsets. Follow enemy and player attachment points after native animation and recorded procedural poses have been evaluated; older enemy recordings can resolve an unambiguous nearby installed holder.
- Clear retained native animation state when navigating forward or backward on the timeline, fixing history-dependent player hand/item offsets. Ordinary playback and paused updates keep their reusable graphs.
- Discover vehicles before the bulk of other scene entities and preserve their initially disabled renderer branches. Restore an older recording's missing opening Cruiser pose only when its opening geometry and subsequent parked ship-relative motion establish a usable attachment. Hide the inactive destroyed body and vehicle debug bounds.
- Record vehicle cargo relative to its vehicle and evaluate both at the same playback time. Older closely spaced rest poses can recover a vehicle-relative path when their recorded motion establishes that they travelled together; held, falling, ambiguous and stationary ground items retain their own paths.
- Rebuild GPU-only player meshes into readable geometry before CPU skinning. This prevents an unreadable lower-detail body from remaining as an oversized, unanimated mesh in v73 recordings.
- Preserve inert copies of the ship magnet's native particle templates before their scene unloads, including the original sprite-sheet animation and material. Main-menu playback therefore retains the electrical effect instead of drawing the full texture atlas as square billboards.

# 0.25.27

- Capture enemy visual bones and rigid model ancestors generically, including inactive transformation forms and assets loaded by mods after startup. Sample after Animator and ordinary LateUpdate movement, with bounded immutable pose snapshots and omission reporting.
- Apply recorded procedural outputs after the native Animator/IK graph so spider legs and enemy facing survive evaluation and backward seeks. Keep hidden native visual branches available while recorded renderer states select visible forms.
- Rebuild retained controller streams on backward seeks so Nutcracker patrol restores its lowered head after inspection. Release owned animation graphs when switching replay windows.
- Preserve installed mesh blend-shape channels and capture bounded procedural blend-shape weights. Restore weights deterministically when seeking.
- Restore cold-menu player bodies from the actual installed player prefab and local/remote controllers. Load only in an inactive network session, guard the private asset scene's callbacks, and unload it before playback. Overlap this work with file decoding and retain the assets for later replays.
- Reduce replay material preparation work with incremental resource indexing, reused texture lookups, and an exact alpha-coverage histogram instead of repeatedly scanning each texture mip.
- Reuse each blended player skin matrix and its outline direction across vertex channels, reducing body processing without lowering model detail or animation update frequency.
- Remove native particle-template gameplay components in dependency order and disable their audio before activation. An unstrippable template falls back to recorded visual properties.
- Pass 109 automated groups, native construction/binding checks for 34 vanilla enemy prefabs, a synthetic mod-style transformation rig, and the supplied replay's player/Spider/Nutcracker/Jester regression checks. Instrumented playback CPU work at 90 seconds decreases from 27.1 to 15.2 ms with all four player bodies visible; this is not a total FPS measurement.
- Retain the reusable playback sampler and single-LOD rendering optimizations from 0.25.26. Older files cannot supply procedural motion or transformation states they never recorded; arbitrary custom shaders and runtime mesh deformation remain outside this capture stream.

# 0.25.26

- Evaluate enemy controllers and RigBuilder constraints in the same manual graph, including the Jester's closed and opened forms. Enable animation branches that start inactive in the installed prefab.
- Preserve Nutcracker torso turns independently of its feet. New recordings capture the torso directly; older recordings can recover its single-axis turn from the recorded native shotgun transform. New Jester captures also retain its procedural head target.
- Advance animation phase without multiplying state speed twice. New observations store normalized cycles per second; older matching observations interpolate their recorded phase, with the already speed-adjusted duration as fallback.
- Select one complete native LOD level per actor instead of drawing overlapping high/low-detail bodies. Rebuild that selection on map/window changes, and register late-spawn enemy LODs before recording their meshes.
- Reuse playback pose storage and held-item lookup tables, removing roughly 58 KB of temporary allocations per sample in the supplied recording. Cache camera properties outside the player-outline vertex loop.
- Release compilation has zero warnings/errors; 107 automated groups and muted native Jester/Nutcracker pose, torso and LOD checks pass. Internal replay frame processing is faster in the supplied-file fixture; total game FPS has not been measured.

# 0.25.25

- Continue an interrupted quota-day recording in a numbered `-p2.lcr` file instead of retrying the occupied first filename every 60 seconds. The archive keeps all parts in one replay and recovers their link from headers if sidecar manifests are lost.
- Queue one completed large map snapshot behind a busy writer and pause capture until it drains. This addresses the observed capture backlog error that preceded the repeated quota-day alert.
- Release build has zero warnings/errors; 105 automated Core/archive/lifecycle groups pass. Live game recording after restart remains to be checked.

# 0.25.24

- Open a full first playback window and finish its map/actor build before advancing playback, so the first visible section includes player, vehicle, ship, item and enemy data already recorded there.
- Attach native rigid enemy parts such as heads, eyes and teeth to their animated prefab transforms; recorded per-renderer poses still take priority for procedural motion. Keep the live menu's input modules enabled during playback so buttons work again after leaving replay.
- Bound item/player effects, including TZP inhalation, to a nearby 3D range for spectators in both native and older waveform playback. Preserve the recorded runtime range instead of replacing it with the prefab's default range.
- Accept Unity UI's passive `LayoutElement` on the main Settings button template. The Gale `van host and visual` profile used this component, and rejecting it prevented the Replay menu button from attaching.
- Preserve a recording after a transient capture exception by closing its readable prefix and automatically starting a numbered continuation after a bounded delay. Freeze late-entity owner lists before yielding across game frames; this fixes the observed `Collection was modified` stop in the supplied Artifice recording.
- Retry transient primary file writes, rollback and flushes without duplicating records. Disk-full errors stop promptly; an optional sidecar failure does not stop the replay file. Hide `Player #<number>` placeholders from both new and existing crew lists.
- Keep actor meshes found in the opening map ready across playback windows, hiding their roots until the actor appears. The supplied Artifice cruiser mesh is recorded at the start but its first frame appears in the second window; this avoids a delayed or transparent car.
- Validate the built scene when seeking across windows and resolve the target world again. A matching file revision alone no longer skips a needed map rebuild when seeking backward.
- Record a new sparse item pose whenever its Y position or fall target height changes, even within the former 0.02 m tolerance. Keep the existing X/Z and scale tolerance.
- Keep replay-created scenes out of Netcode's new-client synchronization. Connected playback reuses render-only copies from a matching loaded moon, and releases its scene on close; menu playback waits for scene cleanup before returning to game controls.
- Stop changing the game's button interactable states for replay UI input. Block the HUD's separate right-click PingScan action during playback and clear its spectator vote hold timer when entering and leaving replay.
- Restore a visible fallback sky when an older HDRI recording contains six black, quantized cubemap faces. New recordings store RGBE-encoded sky faces so dim HDR sky colors survive PNG capture; replay keeps the restored cubemap when later sky settings change.
- Hide the moon's recorded exterior fog while replaying the prelanding ship phase, then restore it after departure or on a seek back and forth. This prevents the detached free camera from showing the orbit scene through dense white surface fog.
- Schedule live snapshots, frozen snapshot output and scene maintenance on different game frames. Keep player, held-item and camera poses at one observation time. Under pressure, do not starve new frames behind the entire method-event backlog.
- Drain at most eight hook events with a cooperative 0.3 ms budget. Pending output transfers at most four records/0.15 ms; snapshot publication processes at most eight records/128 comparison units with a cooperative 0.4 ms budget. Unchanged entities still yield comparison work.
- Cycle sparse animation bindings with their actual observation times. Reuse clip lists, skip unchanged native actor clip queries, and allocate parameter change dictionaries only when values change.
- Reuse one recorder-owned HDRP volume stack, updating and reading it in separate steps; dispose it on stop. Slice visual renderer/decal scans and late-geometry discovery/verification. Limit world/scene work on each maintenance frame.
- Avoid exception-driven NetworkObjectId reads for unspawned player slots. Stream the final frozen snapshot and queued events asynchronously instead of building a final main-thread queue burst.
- Defer texture export from material inspection. Allow one GPU readback/worker PNG at a time, copy transient GPU data in its completion callback, reuse pooled RGBA storage, and perform PNG compression outside Unity's main thread. Preserve completed geometry/texture data when stopping without waiting for GPU work.
- Keep a bounded motion tail while a large world record is being written; the world alone does not pause every new motion sample. The tail still pauses/resumes at its count/byte watermarks.
- Apply these small work slices only to recording. Replay keeps its separate fast scene-building loop, asynchronous file loading and full per-render animation updates.
- Replace the player outline's 2.5% whole-body enlargement with a thin offset along the animated surface. Weld duplicate vertex normals, preserve body transforms and cap protrusion at 5 mm; retain opaque black outlines.
- Release build has zero warnings/errors; 103 automated groups pass. Native test measurements and the limitations of cooperative work budgets are documented in docs/TESTING.md. The Artifice cruiser/seek changes need an in-game visual check; multiplayer replay and spectator vote fixes still need a two-client game check.

# 0.25.23

- Reduce the production writer from 256 records/64 MiB to 64 records/16 MiB and overflow from 64 MiB to 8 MiB. Pause capture at 48 queued records or 4 MiB; resume below 12 records and 1 MiB with no overflow.
- Reuse estimated record weights during retries. Drain at most 16 pending records per tick with a cooperative 0.75 ms budget, and process at most 48 hook events per tick. Preserve an initial frame and asynchronously finish accepted records.
- Run compression and disk output on a dedicated worker with optional below-normal priority.
- Release compilation passes without warnings/errors; all 94 automated groups and the muted native pressure test pass. Sampling density can temporarily decrease under pressure; these limits are not a whole-process memory cap or a guarantee against every game hitch.

# 0.25.22

- Treat a full disk queue as temporary backpressure. Preserve an ordered bounded overflow, pause new capture sampling while saving catches up, then resume on the same file. Async completion drains accepted overflow without blocking Unity's main thread on disk writes.
- Account for queued/in-flight snapshot memory as well as record count, reduce the writer queue from 1024 to 256, and cap the optional whole-world reuse cache at 32 MiB. Release queued payloads after output failure. Bounded event overflow records an explicit storage gap instead of aborting the day; unaccepted bookmarks are not counted.
- Stream JSON directly into gzip. Remove expanded UTF-16/UTF-8 record copies and the final compressed ToArray copy, retaining the existing replay schema and incremental size limits.
- Preserve readable completed records after write/allocation failure and torn final writes. Refresh failed-save archive metadata from the physical prefix. Failure to create/write an optional index does not stop the primary replay; playback rebuilds it.
- Flush stream buffers periodically, finish accepted records even if capture cleanup fails, and use actual written duration/bookmark totals for final metadata.
- Release build has zero warnings/errors; 92 automated groups pass, including stalled/full-disk and injected allocation failure. A muted native recorder test verifies pause/resume, event overflow reporting, bookmark preservation and released queue memory. See docs/TESTING.md for measurements and limits.
# 0.25.21

- Compute deadline days with the native TimeOfDay floor formula. Fractional deadlines now label newly saved quota recordings Day 1, Day 2 and Day 3 instead of 1, 1 and 2. Preserve the existing remaining-deadline file naming and previously written files.
- Evaluate the installed player controller and RigBuilder outputs in one manually sampled PlayableGraph. The previous separate PreviousInputs stream left crouching leg IK in a default pose and buried the feet. Keep sparse animation states/parameters; add no bone recording.
- Select the recorded local/remote player controller from verified player assets. Apply view rotation to gameplayCamera, matching PlayerControllerB, and reset both sampled transition poses before blending.
- Set the expanded player outline material to opaque black. Release owned animation graphs when an actor retires or playback is disposed, including cached reactivation.
- Release solution compilation succeeds without warnings/errors; 85 automated test groups pass. Muted native comparisons verify crouch idle/walk/down, looking, pause and reactivation. See docs/TESTING.md for the native reference measurements and limits.
# 0.25.20

- Spread each discovered type's component registration over multiple frames, with at most two registrations per step and a 2 ms cooperative work budget. Skip unused player-slot animation setup. Retain discovery membership correctly through partial and repeated scans.
- Reference matching native player skins instead of repeatedly exporting their meshes, bindings and default maps for every lobby member. Keep custom mesh/material capture as fallback. Share the installed player sound catalog across players.
- Disable and retain the exact enabled state of both globally polled gameplay actions and the local player's separate look asset while the archive/loading/viewer owns controls. Keep replay raw-device and UI input active, and allow cancellation to release held item actions.
- Reassert movement/look flags before the player's Update. Release input in cleanup finally paths, reacquire it on cached resume/archive transitions, and restore the live round's current camera rather than the camera from entry.
- Muted native tests cover 32 player clones, device-injected WASD/click isolation, restored live movement, camera switching, cache/archive/eviction and continuing live recording. Release build has zero warnings/errors; 84 automated groups pass. This fixture does not establish multiplayer FPS or zero startup pauses.

# 0.25.19

- Remove generic prefab renderer/mesh matching and default scene-source copying for ambience. Rain, mine-entrance and campfire sounds cannot be authorized by an unrelated matching object. Older recordings retain only the narrow native BreakerBoxHum fallback, rejecting ambiguous or inactive geometry.
- Capture actual static spatial source actions and changed loop state, gain and pitch. Store installed clip references, emitter points, native range/rolloff and ship-relative anchors; never audio samples. Stopped/inactive configured sources do not start replay loops.
- Isolate one-shots from configured looping clips, deduplicate forwarded Play calls and remove static ownership when an actor owns the source. Restore loop/stop state on seek without replaying historical one-shots.
- Bound static tracking to 256 sources, discover at most 32 transform nodes and check at most eight cached emitters per frame. Clear unloaded/destroyed sources and guard reused Unity instance IDs.
- Validate 84 automated groups plus muted native emitter, legacy false-match, seek, distance and cleanup checks. Existing files cannot recover unrecorded ambience actions.

# 0.25.18

- Exclude every GrabbableObject and EnemyAI audio source from passive structure ambience, including LungProp's component-local hum. Stop stale apparatus loops after undocking, including older ship/held-item state, while retaining action one-shots.
- Gate derived rain ambience on the recorded level weather. Dry and unknown weather cannot start template rain loops; rain matching respects word boundaries so terrain and drains retain their ordinary ambience.
- Keep exterior direct/indirect lighting enabled while the camera is in the ship, matching TimeOfDay's factory check. Preserve factory night vision and ship floodlight/fog handling.
- Connect native circuit-bee VFX targets to the reconstructed hive pose. Record a small hive/self/override decision so the game's line-of-sight choice survives playback; older recordings can find a nearby hive.
- Restore bee electricity using the installed ThunderAndLightning path script and materials. Use the native zap modes, intervals and four-unit target box; clear bolts on pause/seek and isolate their render layer without extra live-world lights or retained scene subscriptions.

# 0.25.17

- Restrict derived structure ambience to active passive sources. Exclude controller-owned turret, mine, item and enemy sounds, inactive scene branches and distant/unbounded prefab attachment scopes; keep native breaker-box hum and source attenuation.
- Check recorded turret audio against current Detection/Charging/Firing/Berserk state. Stop stale firing loops even when an older recording lacks its stop event; backward seeks cannot restore a firing loop while idle.
- Reconstruct the installed landmine Animator, fire its native startIdle trigger and advance it with the replay clock. Keep pause/seek behaviour deterministic, hide overlapping snapshot meshes and stop the idle visual after explosion.
- Record only mask attachment flags and mine activation state. Gate Comedy/Tragedy eye meshes and attached lights on attachment, with legacy Animator-parameter fallback. Exclude conditional mask emission and moving entities from synthesized room illumination. Audio waveforms remain unrecorded.

# 0.25.16

- Apply PlayerControllerB's script-driven camera/arm IK weights as well as native Animator states. Use the cloned ServerItemHolder and the game's unscaled item offsets for third-person holding; store essential item offsets for modded items.
- Copy the native night-vision radius, attenuation and specular settings into the spectator light. Exclude duplicate player/radar night-vision fixtures, including older recordings, to prevent concentrated highlights on nearby walls.
- Cycle living players with left-click while following. Remove Player sounds from Settings/config, rename Fog / Fake Fog, and add persistent No shadow to keep outside illumination inside while disabling light shadows.
- Interpolate recorded particles by emitter/seed/birth, extrapolate retiring particles only while alive, and interpolate native emitter clocks. Reuse particle/style buffers during playback.
- Copy the turret's complete native bullet/flare hierarchy, restore state-driven detection/firing sounds with native distance limits, and soften the replay aim beam with a transparent glow profile. Resolve sibling hazard sound references without substituting unrelated clips.
- Play installed loop ambience at matching static structures and inert moon scenery, including BreakerBoxHum. Preserve native range, pause on replay pause, and destroy playback sources during world/viewer cleanup. No audio waveform is recorded.
- Replace mesh-sphere swarm substitutes with the installed bees/locusts FlyingBugs VFX Graph, textures, native targets and behaviour parameters; disable gameplay scripts and retain pause/speed controls.

# 0.25.15

- Reset native animation defaults before sampling so spawn-only transform changes cannot bury the Giant during locomotion or after seeking.
- Render player bodies from their original bone/bind-pose matrices, using reusable buffers. Remove the center-to-pelvis position guess and Unity BakeMesh offset/scale errors.
- Suppress every original renderer in native animation copies even when animations or LOD enable it. Exclude radar MapDot animations from new recordings and legacy playback.
- Draw clickable cyan bookmarks as the same centered 6-by-34 bar used for player deaths. Show a bookmark-saved confirmation at the top right for 3.5 seconds.

# 0.25.14

- Record bookmarks with backtick by default. InputUtils exposes Replay / Add replay bookmark in Controls; otherwise Input.BookmarkKey is configurable. Ignore bookmarks while chatting, using the terminal or menus/viewer. Persist accepted/written counts in small archive manifests and show clickable timeline marks.
- Use the complete installed player Animator/IK hierarchy, as for enemies, instead of replaying a short learned player pose. Stop learning per-bone player tracks in new default captures. Retain legacy tracks and explicit CaptureBones playback.
- Preserve the short animation transition by evaluating both poses through the native rig, and feed recorded player view rotation into the native camera-look target.
- Parent restored skinned meshes to their native renderer transform. Avoid applying a flattened animated mesh offset a second time, which buried monsters and shifted player skins.
- Display the native game clock in playback and game/recording start times in Details. Save changed direct/indirect sun values at most once per second and interpolate them while seeking/playing; retain sky changes. Older installed-scene captures drive the isolated moon's original timeOfDay sun curves.
- Upgrade the compact index to LCIX0007 with bookmark times and independent sunlight state. Old indexes rebuild once using the fast metadata scanner; the .lcr schema stays compatible.

# 0.25.13

- Rebuild missing, outdated or damaged replay indexes from record metadata. Avoid decompressing and allocating every future frame's entities/bones and every world's meshes/textures before opening playback. Keep the existing sidecar format and bounded startup windows.
- Validate complete payloads when their playback window is decoded. Retain strict scanning for inspection callers, cancellation, physical truncation recovery, state carry and record/index consistency checks. Archive duration queries also use metadata scanning.

# 0.25.12

- Keep each enemy's complete native animation hierarchy, IK targets and RigBuilder constraints. Drive its original Animator states and blend-tree parameters manually at replay time; fix frozen Sapsucker legs and the Giant's T pose without adding per-frame bone recording. Suppress cloned gameplay, animation callbacks, audio, colliders and particle emission.
- Resolve sound clips using the owning enemy/item prefab and serialized clip field/array entry. Avoid selecting another enemy's same-named clip; older action recordings also search within their owning enemy before any global lookup.
- Restore the original AudioSource attenuation and custom distance curve, spatialize world effects and silence them beyond the original maximum distance. Connected replay uses the retained live player listener; standalone replay uses the spectator camera. Apply maximum-distance suppression to older spatial audio and item drops as well.
- Decode a 4 MiB startup frame/event window before opening playback, then continue using bounded 48 MiB windows and prefetch. Reuse native prefab/audio lookups instead of repeatedly scanning hierarchies for every effect or world rebuild.

# 0.25.11

- Remove waveform and voice capture. Save installed clip identifiers and native play/stop actions; resolve footsteps, monster, item-use and hazard effects during playback, including automatic looping effects. Older waveform recordings remain readable.
- Destroy native sound objects when replacing a playback window; prune destroyed actor/render/animation/audio tracking and expired window actor models, and bound native mesh caches to 64 MiB. Reduce frame/event windows to 48 MiB and carry only the latest independent state into later windows. Rebuild older sidecars using LCIX0006. Consume skipped one-shot sounds during fast forwarding to prevent bursts on resume.
- Reference native item/enemy assets without exporting their mesh, skin or textures at spawn. Restore native bone scales, controllers, UV channels and tangents, repairing Butler, Thumper and Giant skins in matching older recordings. Select native materials by their actual asset identity to avoid shared-name collisions; preserve custom runtime materials.
- Reconstruct native one-hand/two-hand rig poses, including the game's arm rig constraints; replace the earlier finger-only correction. Preserve real live-game effects/voice and the original player listener while connected replay input stays isolated.
- Exclude TZP, fear, poison, flashbang, underwater and death-screen filters from world post-processing capture. Ignore personal-filter changes in older replay files while retaining world fog and grading.
- Hide repeated Loading overlays for entity additions within the same world, reuse known actor visuals, and present quota days as Day 1–3.

# 0.25.10

- Rebuild the one-hand item grip from the installed game's observed `HoldOneHandedItem` finger pose while the player's locomotion controls both arms. This repairs older recordings where the learned one-hand track had captured a two-hand arm pose.
- Click a visible player or enemy in Freecam to follow them. Mouse wheel changes Freecam movement speed; while following, it continues to change camera distance and enters first person when close.
- Expand the main and pause menus upward when adding Replay, preserving LethalConfig's row, button spacing and click listeners. Restore only Replay's own position offsets so later mod injections keep their layout; use automatic navigation for the added row.
- Release the cached playback HUD's menu input lock when playback closes. Reacquire it on resume and avoid restoring stale input state when the cache is evicted, keeping LethalConfig and other game buttons usable after replay playback.

# 0.25.9

- Keep the most recently loaded replay scene and decoded recording in memory after closing playback. Reopening that recording restores its camera, timeline position and world immediately, without a second file read, world build or Loading Replay screen.
- Hide the parked replay's camera, HUD, installed scenery and audio while the archive or live game is visible. Evict the cache when another recording is selected, the source file changes, the game scene/connection changes or the game exits.

# 0.25.8

- Attach third-person held items to the reconstructed right hand using the installed game's `Item.positionOffset` and `rotationOffset` and the native `ServerItemHolder` placement. Existing recordings that stored the recorder's first-person item pose now show that item in the player's hand from outside; first-person follow restores the recorded view-model pose immediately when the camera changes.
- Preserve the existing sparse replay format: the hand and Item asset supply the third-person pose without recording another per-frame item transform.

# 0.25.7

- Choose the player's one-hand or two-hand replay pose from the held item's `twoHandedAnimation` flag. Earlier recordings resolve this flag from the installed game item asset; new recordings save the player flag directly.
- Restore locomotion and the free left arm beneath one-hand item poses. Avoid learning a reusable one-hand track while the game's two-hand layer is still active.

# 0.25.6

- Continue recording a named lobby in its existing archive folder across launches. Reuse its quota folder and allocate a new deadline file suffix without overwriting earlier recordings.
- Capture short monster spawn states at `EnemyAI.Start`, prefetch their later mesh for playback before the first world snapshot, and retain native mesh/animator facing poses.
- Capture turret bullet flare subemitters and the turret's aim ray. Show a replay tracer and beam in older recordings when their bullet particle samples are available.
- Restore a player silhouette on the replay mesh without the broken fullscreen outline pass. Smooth cinematic camera look as well as movement and enforce cursor lock just before rendering.

# 0.25.5

- Reconstruct held scrap between capture samples from its position relative to the holder. New recordings include the holder ID; older recordings can use a nearby player when the match is unambiguous.
- Record the game's player camera position and rotation for first-person following. Older recordings retain the head/body fallback.
- Put Replay in the in-game pause menu. While an in-game replay is open, keep the live player's audio listener and voice sources at the player's actual position, and block local gameplay input callbacks.
- Move Replay Settings below the top dock, scroll its controls, and align the top and bottom bars to the Details panel's 28-pixel side margin.
- Replace Left Alt with B for cursor toggle; holding right mouse while the cursor is visible temporarily enables replay-camera movement.

# 0.25.4

- Keep the baked player mesh at its recorded size by removing duplicate rig scaling, and align its center with the animated spine.
- Attach the recorded ship fog-exclusion volume to the moving cabin. With Fog enabled, retain exterior fog inside that volume instead of disabling all cabin fog; turning Fog off still hides both global and local effects.

# 0.25.3

- Narrow the replay dock and bottom bar. Keep Settings, Details and X above; place Play, speed and one Camera selector below. Dead players are dimmed and cannot be selected.
- Switch a followed player to free camera at the recorded death position, including after seeking past death, and mark that death on the timeline. Place first person at the recorded head bone.
- Restore landmine visuals from the installed game while the recorded world mesh is unavailable. Render the recorded player bone pose through a bounded baked mesh with refreshed visibility bounds so HDRP shows the body.

# 0.25.2

- Replace the replay dock's Archive button with X. Keep only time and seeking in the bottom bar; click the top controls to play/pause, change speed, choose free/follow camera, and select a player to focus.
- Add a persisted playback Fog setting, enabled by default. Turning it off hides recorded HDRP fog, local volumetric fog, and fog-named mesh, particle, and installed scenery renderers.
- Reapply the replay cursor lock and visibility after game updates and UI drawing so movement mode keeps the cursor hidden until the UI cursor is requested.

# 0.25.1

- Show the selected player's body in free and third-person cameras; hide it only when follow zoom enters first person.
- Hide the turret placeholder when its recorded model is present. Capture its actual `bulletParticles` even when the emitter is a sibling of `TurretScript`, and replay sampled bullets only while firing.
- Stop adding realtime shadows from lights in older recordings that never saved bake mode. New recordings keep the source light's bake mode, shadow strength and HDRP light/shadow dimmers. Keep the ship's exterior floodlights and sun out of its cabin view.
- Restore recorded local fog color directly, classify dungeon fog by room bounds, and track changes to HDRP fog, visual environment and shadow settings alongside other sparse post-processing events.
- Suppress the game's `LethalSponge` fullscreen pass on the detached replay camera: its scene-buffer sampling painted large black patches across surfaces even with the original material and color-fetch setting. Other captured passes remain available; the outline effect needs a separate replay-safe implementation.

# 0.25.0

- Save new recordings under the game's lobby name and show participant names in the archive before playback.
- Move the replay camera without holding right mouse. Left Alt toggles the UI cursor; the follow camera wheel changes distance and enters first person up close.
- Restore each recorded renderer's shadow settings and prevent camera fill light shadows from blocking areas incorrectly.
- Remove empty lobby and quota folders after deleting their final recording.

# 0.24.1

- Keep exterior height fog and the spectator fill light out of the ship cabin so its interior is no longer washed out.
- Attach native spider fangs to their animated head bone in old recordings and in new captures. Preserve the spider's script-driven mesh root and leg joints as the only extra per-frame bone poses; its other motion still uses the game's Animator states and reusable clip track. Older files use their available native spider track.
- Settle to a neutral stance when an old player recording lacks an idle track, instead of freezing one stride of a walk clip. Capture settled clips with `Idle` anywhere in their name, including `SpiderIdle`.
- Add a persistent Cinematic movement setting and C shortcut for smooth replay camera acceleration and deceleration, plus a camera-speed slider. Shift still increases movement speed.

# 0.24.0

- Keep captured spider body meshes even when game visibility temporarily suppresses their renderers. Retry incomplete late actor geometry and scan promptly after a spider spawns a web, tracking the trap as a transient entity with its installed mesh. For older files missing the body, reconstruct its native render mesh and rig from the installed spider prefab.
- Anchor item lights to moving items, add a small local light for strongly emissive scrap without a native light, and reconstruct that light from material data in older recordings. Cull local lights by spectator distance as items cross indoor boundaries.
- Keep recorded local fog for generated dungeon rooms and preserve the exterior fog outside the ship cabin.
- Show errors in the top-right corner for 3.5 seconds, including recording, save, archive and playback failures. Keep the top-left recording status behind `Debug.ShowOverlay` (off by default). During playback, press L to hide or restore the replay UI, including its dock and open panels.
- Restore the game's active HDRP volume effects, bounded color curves and `LethalSponge` fullscreen outline pass using the recorded shader and outline settings. Save later filter changes only when their blended values change, and apply them at their recorded times. Older recordings use the installed game's outline shader when available.
- Carry the latest changed value for each HDRP effect across bounded seek windows. The `LCIX0005` sidecar stores effect keys; older sidecars are rebuilt from the recording.
- Keep an upright fallback pose when an old recording has no idle motion track; capture a settled idle pose for new recordings. Blend adjacent recorded actor poses over 0.18 seconds instead of snapping between clips.
- Capture short pose tracks for active upper Animator layers, including player spawn and emotes, and give those layers priority during replay. Do not learn a reusable idle or walk while an overlay is active; reject interrupted base loops and record overlay fade-out across the active-weight boundary. Existing recordings without upper-layer poses still rely on a working installed Animator for those actions.
- Replay observed player and monster bone motion directly from the game's recorded clip output when a controller does not animate the rebuilt rig. Rebind installed controllers after world bones are created for states without an observed track.
- Store resting item poses once and reconstruct ordinary drops from the game's start/target and sampled fall curve. Record player drop actions and floor impacts as events and play the installed drop sound when no matching audio block was captured. Keep frame transforms for held or irregularly moving items. The `LCIX0004` sidecar carries item-pose state across seek windows.

- Capture and replay each active Animator on an enemy separately. Skin rigs now match the Animator that drives their bones, so monsters with separate adult, baby, mask or effect animation no longer collapse to the first controller. State, parameter and reusable motion-track records carry the Animator's path; older recordings remain readable.
- Capture changed Animator parameters and apply them to matching installed player and enemy controllers. Choose the dominant weighted clip for controller-free motion tracks and identify each track by state hash. Resolve enemy controllers that share a name by their recorded clip set and paired avatar.
- Spread entity and effect discovery across frames, cache stable item names, and capture only selected gameplay fields for built-in objects. Extra tracked mod components retain their bounded general field capture.
- Index installed moon-scene assets in small steps and write stable renderer and terrain references while the dungeon generates. Capture generated geometry after generation completes.
- Record only active player and enemy Animator layer changes, keeping one bounded rig pose with each skinned mesh. Learn short motion tracks from observed clips, so menu replay can animate an actor even before its original controller has loaded. Per-frame bone capture is off by default, with a one-time migration for existing recording profiles.
- Omit unchanged moving-scene renderer poses from frames, spread room/LOD visibility discovery across frames, and group late spawned entities into fewer world records.
- Skip unused player slots and unchanged entity-child renderer poses in frame snapshots, while retaining explicit visibility tombstones for destroyed renderers. Carry prior visual events only from the current map capture set. The `LCIX0004` sidecar distinguishes animation, item poses and visual state; older sidecars are regenerated from the replay file.
- Tolerate sub-nanosecond JSON rounding differences between replay records and their binary sidecar timestamps.

# 0.23.0

- Reconstruct Unity particle effects with their installed source renderer, material, atlas, mesh and vertex streams instead of combining different effects into a generic white billboard. New frame data retains emitter identity, velocity, lifetime and 3D size/rotation; turret tracers replay their firing loop so short-lived bullets are not missed between samples.
- Keep native line/beam materials and capture animated renderers even when the live player's room culler hides them. Refresh effect resources across replay-world changes and wait for installed moon assets before resolving native materials.
- Reuse installed material normal/mask maps and shader state, prioritize surface properties during capture, preserve linear data textures and reconstruct mesh tangents to reduce incorrect gloss and missing surface detail.
- Record the mine entrance's special room bounds separately from its start tile, matching the game's indoor culling classification without filling the space between them.
- Stream and parallelize bounded file decoding, reuse validated world payloads between windows, skip redundant texture decoding and remove the fixed 24-object-per-frame build ceiling. Retain all installed-scene surface references so later world updates can reveal additional recorded scenery.
- Source-specific particles and special entrance bounds require a new recording. Older recordings remain readable; GPU VisualEffect insect swarms still use the existing approximation.

# 0.22.2

- Show replay runs, quota groups and day recordings in the order they were saved, with the earliest recording at the top of the archive.
- Use start time and stable tie-breakers without changing multipart playback order.

# 0.22.1

- Replace the transparent crimson replay backgrounds with fully opaque red archive, playback, settings and dialog surfaces inspired by LethalConfig's in-game menu.
- Use flat red buttons with orange pixel-font labels, and let the main-menu Replay entry retain the game's own Settings-button styling and hover animation.

# 0.22.0

- Restyle the main-menu Replay entry, F9 archive, playback dock and timeline, Settings/Details windows, loading and delete dialogs, recording indicator and diagnostic labels around Imperium's default crimson/orange window theme.
- Give archive columns independent translucent frames with title bars, tighten recording rows and controls, and keep the selected recording and destructive action visually distinct.
- Keep the UI self-contained with native Unity controls; use an already loaded Imperium font when available, otherwise fall back to the game's pixel-style font.

# 0.21.0

- Reduce 4× playback frame churn by reusing timeline interpolation lookups, avoiding redundant entity and renderer activation and transform writes, and skipping linear audio seeks while high-speed audio is muted.
- Coalesce timeline dragging, debounce jumps across world or file-window boundaries, keep the previous bounded playback window in memory, and restore sparse visual state directly on backward seeks.
- Spread replay-world teardown across frames and reject distant geometry before the follow camera tests recorded triangles for obstructions.

# 0.20.0

- Re-spatialize world sound effects at the replay spectator camera while retaining true music/UI sources as global audio. New recordings store that distinction explicitly.
- Record sparse visibility changes for captured static scenery and installed moon-scene renderers, and keep destroyed moving renderers inactive. Carry visual changes across bounded playback windows and backward seeks.
- Record spray-paint decals as sparse projector events, including pool reuse, color, pose, and ship-relative placement. Playback uses the installed game's decal material where available, with a simple projected fallback.
- Preserve emission properties on HDRP tile materials, include more room and area lights, and add bounded glow lighting for emissive rooms without an active captured fixture.
- Restyle the native archive and playback controls with a compact charcoal-and-amber game palette, lighter framing, clearer metadata and less visual clutter.

# 0.19.0

- Put an X beside each recording, quota and run in the F9 archive. Folder X deletes its recordings after one confirmation; Play stays at the far right of the bottom bar, while Refresh, Folder and Close sit above the list.
- Track the HangarShipDoor animator's renderers as moving exterior geometry, including their poses relative to the moving ship anchor. Fresh recordings can now replay the door's full motion instead of freezing at its initial mesh pose.
- Prefer a transparent HDRP material already loaded by the game for replay particle alpha blending, and recover luminance masks from opaque black-backed particle sprites. Circuit Bees and locust approximations use small shaded meshes rather than alpha-ignoring white billboards.

# 0.18.0

- Add Delete to the F9 replay library, with a recording-specific confirmation showing date, file count and size. Delete only the selected replay's data and sidecars, refuse active files and paths outside the archive, and refresh the list afterward.
- Clarify the archive's selection and actions with numbered columns, a wider details pane, visible file sizes, a prominent Play action and a distinct Delete action. Esc and F9 dismiss the confirmation before closing the archive.

# 0.17.0

- Draw looping and short-lived particles with transparent HDRP-compatible materials. Prefer captured alpha maps and fall back to a soft transparent texture, avoiding opaque white billboards for rain, lightning and other effects.
- Show native progress bars while indexing, reading the first replay section and constructing the replay scene. Spread texture/material and geometry construction across frames instead of blocking the first viewer frame.
- Load a smaller first playback window, cache a sidecar index for complete older recordings, and keep the reconstructed scene across windows that refer to the same world capture.

# 0.16.0

- Keep recorded interior tiles, their lights and local effects visible when Disable culling is enabled, even when the spectator moves outside.
- Add a persistent Player sounds replay switch that mutes tagged player and held-item audio; recordings without source ownership tags show an older-recording notice.
- Sample readable `DecompressOnLoad` clips from Unity-virtualized distant AudioSources and one-shot effects in bounded batches, retaining their recorded positions for spectator-relative playback. Streaming, compressed and non-AudioSource effects remain outside this path.

# 0.15.0

- Defer the first archive until quota data is initialized and derive the deadline file from remaining time, avoiding a premature `0/2.lcr` when the first day is `130/3.lcr`.
- Classify generated props and entity meshes against room bounds when they are not parented to a dungeon tile; include every generated tile in spectator room detection and extend interior light visibility.
- Capture geometry added after the initial map scan for items, bodies, hazards, dropships and mineshaft elevators, as well as enemies. Track dropship and elevator controllers explicitly.
- Record bounded LineRenderer paths for lasers, beams and ropes; preserve bones on skinned hazard/mechanism meshes, sample turret muzzle particles while firing and render bee swarms with transparent particles and approximate lightning.
- Include non-voice player sound sources by default and spatialize their effects at the recorded player position.

# 0.14.0

- Capture per-source sound position and distance settings; replay sound effects from the spectator camera while keeping 2D music global. Older mono recordings continue to play.
- Render short particle bursts with a transparent soft texture instead of opaque white quads.
- Preserve animated enemy skin root bones and raise the skeletal capture limit to 512 so larger creatures can animate in replay.
- Add small incremental geometry captures for enemies that spawn after the moon snapshot, avoiding a full map rescan for each new creature.
- Record generated mansion shelves, bookcases, doors and other independently moving scene renderers, including initial hidden state and later motion.

# 0.13.1

- Move replay audio compression and base64 conversion off Unity's audio callback to a bounded worker. Audio callbacks now copy a small PCM block and return without compressing or writing replay events.
- Flush short sound effects when their source stops or is destroyed, and discard destroyed tap entries so long sessions can keep capturing new sources.

# 0.13.0

- Keep each daily recording open through temporary quota/deadline display changes, including entering generated rooms; finalize it after the actual return to orbit.
- Capture dynamic light color temperature and use a brighter ship-cabin spectator light while reducing replay fog indoors. Add a persistent playback option to disable interior tile culling.
- Preserve high-detail generated tile meshes when the live camera has disabled their LOD; render bounded Unity particle bursts and visible fallback shapes for unmeshed hazards.
- Record observed game effects, known player effects and music as bounded mono ADPCM blocks. Voice chat and unknown player-attached sources are opt-in through `CaptureVoiceChat`.
- Allow the replay archive and free-camera viewer while connected to a game, locking and restoring local movement/look input and isolating replay scenes from the active recorder.

# 0.12.1

- Prevent automatic saving from stopping when a moon scene contains renderer or terrain paths longer than the 256-character record limit, duplicate paths, or more than 4,096 references. Such surfaces fall back to embedded geometry; the writer also reports the specific invalid-path condition if one reaches validation.

# 0.12.0

- Store stable references to built-in moon-scene renderers and Unity Terrain instead of repeatedly serializing their meshes and textures. Playback loads the matching installed game scene as inert visual scenery, preserving original ground, rocks and materials. Generated rooms and moving objects remain recorded as bounded snapshots.
- Record the level ID, map seed, dungeon seed and dungeon-flow ID alongside the scene references. These values identify generation settings; playback does not rerun the procedural dungeon generator.
- Prioritize generated ground and rock geometry when a scene cannot supply it. After landing, update sky/fog without saving another full copy of the unchanged map.
- Require the same game version when replaying recordings that depend on installed moon-scene assets; report a missing/mismatched scene clearly.

# 0.11.0

- Save each quota/deadline day in one `quota/day.lcr` file instead of rotating numbered `.lcr` parts. A repeated deadline uses a collision-safe suffix. Preserve older multipart archives and allow F8 to add a checkpoint without splitting the file.
- Add a small `.lci` offset index beside new recordings so a long single file opens quickly. Playback reads bounded time windows from that file while keeping one continuous timeline; an absent index can be rebuilt from the `.lcr`.
- Capture larger ground color layers, and reconstruct Unity Terrain with a finer height grid, sampled normals, UVs and a terrain-layer texture where available. Terrain sampling yields between rows to limit gameplay stalls.

# 0.10.0

- Carry recorded fog color through HDRP sky-fog tint and compensate for chroma lost by the bounded 8-bit sky capture. Capture and restore supported 3D local-fog density masks, tiling and scroll so weather fog does not become a uniform white volume. Existing recordings retain their stored fog colors; masks require a new recording.
- Keep ground textures at their full captured resolution instead of selecting distant mip levels. Extend high-detail tree/plant LOD range to at least 100 m, retain large trees up to 300 m, and keep procedural grass visible within 180 m.
- Increase spectator-visible interior rooms from 35 m to 105 m, while retaining bounded indoor distance culling.

# 0.9.0

- Validate reconstructed HDRP materials after applying recorded properties, restoring alpha-clipped foliage without the colored texture background around leaves.
- Override the replay camera's HDRP atmosphere and volumetric frame settings and keep recorded global fog active across indoor/outdoor camera transitions.
- Capture bounded active local volumetric fog volumes, including fake fog, and reconstruct their density, color, size and falloff in replay. Older files still load; local fog requires a new recording.

# 0.8.0

- Spread map and appearance capture across bounded game-frame steps, remove repeated room/LOD scans from every entity-discovery tick, and carry a completed world capture into safety parts instead of rescanning the same map. Disk serialization remains on the background worker.
- Capture procedural GPU-instanced grass and active looping ParticleSystem emitters. Recreate their visible effects in the private replay scene; show particle-based Circuit Bees and Red Locusts with bounded swarm effects.
- Preserve higher-detail natural-object LOD meshes and switch to lower-detail meshes only as the spectator moves away. Preserve alpha-tested foliage coverage through generated mip levels.
- Default replay render resolution to 100% and migrate an existing exact 50% value once; keep other custom values and gamma settings.

# 0.7.0

- Capture higher-resolution primary texture maps within bounded image and world budgets, preserving close-up detail in new recordings.
- Generate trilinear mipmaps in replay playback so distant surfaces use lower-resolution levels without making nearby surfaces uniformly blurry.
- Exclude first-person helmet and visor effects from new world captures, and hide them when loading older recordings.

# 0.6.0

- Save the blended HDRP sky and fog with bounded cubemap faces; restore them in a private replay volume so main-menu lighting does not color the recording.
- Record supported shader parameters, render queues and keywords, and reuse supported game shaders during replay, including shaders already loaded as Unity resources. Preserve recorded room bounds for indoor detection; ignore bounds without captured interior geometry so an exterior entrance cannot hide the outdoor world.
- Capture exterior geometry in bounded chunks and interior geometry separately, then merge each completed capture set. Outdoor surfaces return when the spectator leaves a recorded room.
- Add native playback Settings controls for render resolution (25–100%, default 50%) and gamma (0.5–2.0), saved in the BepInEx configuration file.
- Update the native archive Play control every frame so it becomes available as soon as an asynchronous archive scan completes.
- Keep older recordings readable, with a fallback sky when the new environment data is absent. Increase the bounded compressed-record limit to 32 MiB for the additional visual data.

# 0.5.0

- Record moving ship/elevator roots every frame and store ship geometry relative to them, so the hull descends with the players.
- Capture bounded scene lights and use lit replay materials and shadows. Separate exterior and interior visibility by spectator position; show nearby interior rooms and use a low-power spectator light where baked illumination is unavailable.
- Stop periodic world scans and 60-second file rotation. Capture the world on scene/generation changes, keep a recording through its quota/deadline context, and close safety parts on a background worker. Use actual expanded bytes for bounded reader rotation.
- Retain unlit playback for older recordings without light data.

# 0.4.0

- Read the renderer's own submesh slices from static batches, compact their vertices and preserve world coordinates, including nonuniformly scaled hierarchies. Bake unowned skinned props such as the ship's suit rack into their visible pose.
- Leave space for gameplay frames after large facility snapshots with a conservative 192 MiB storage-part estimate; retain the 60-second and entity-count limits.

- Change new archive folders to `Run-.../remainingQuota/deadline/HHmmss-shortid/part-xxxx.lcr`. Remaining quota is `max(target - fulfilled, 0)`; deadline is the game's remaining days, with explicit `unknown` values. Older session/day and loose-file archives remain readable.
- Present each recording as one continuous clock and seek bar across storage parts. Keep the viewer, HUD, camera, pause state and speed while switching parts, prefetch the next part in the background, and support backward seeks across boundaries.
- Use English throughout the native archive, playback controls and product status messages. Keep Unicode fallback glyphs for player names and recorded text.
- Capture generated interior geometry before player entry within configured budgets. Share repeated static mesh payloads through validated references and show interior rooms around the replay camera within an 80 m proximity range.
- Reconstruct supported actors with recorded body meshes and bone poses. Hide interaction-only fallback shapes and unreadable environment bounds by default; bake unowned skins such as suit-rack footwear when needed.
- Increase the default `WorldVertices` budget to 500,000, with a maximum of 1,000,000. Migrate the old exact 120,000 value once; preserve other configured values within the allowed range. Apply a separate conservative 40 MiB geometry budget to leave space for textures and other world data.
- Recover usable recording timelines around damaged neighboring parts. Retry transient Windows manifest replacement failures without deleting the prior complete manifest.
- **51 automated groups pass: 16 Core, 29 Archive, 6 Lifecycle.** Add timeline, duration recovery/cancellation, mesh reference, quota/deadline, damaged-neighbor, matching archive durations and atomic manifest tests.
- A real-game playback-only run verified a continuous 71.2247926-second timeline across six older parts, English native controls, forward/backward seeks, final pose and camera preservation. An isolated v81 expedition saved about 91 seconds across eight parts under `130/3`, captured interiors before entry, and passed body, interior-proximity and 38 static-batch coordinate checks. See `docs/TESTING.md` for scope and evidence.

# 0.3.0

- 보관함을 Unity uGUI/TMP로 교체. LethalConfig의 적갈색 3열 패널·주황색 게임 글꼴·각진 버튼과 선택 테두리를 참고한 독립 UI.
- 재생 조작을 하단 바에 모으고, 기록 정보·관절·이름 표시는 선택해서 표시. Settings 아래 Replay 진입과 실행/세션/일차 구조 유지.
- 새 녹화에 UV·노멀·서브메시·저해상도 PNG·재질·지원되는 스킨 외형 저장. 읽기 불가 메시를 GPU 버퍼에서 수집하고, 지원되지 않는 스킨은 수집 시점 메시로 대체.
- 재생 시 렌더링 전용 메시·텍스처·스킨 생성. 읽지 못한 환경의 경계 상자는 숨김. 녹화된 지형을 기준으로 초기/따라가기 카메라가 벽 뒤로 빠지는 현상 완화.
- 외형 데이터의 크기·차원·참조·배열·인덱스 검증 추가. 기존 스키마 1 녹화는 계속 읽으며, 과거 녹화에 없는 외형은 복원하지 않음.
- 빌드에 게임의 UnityEngine.UI와 Unity.TextMeshPro 참조 필요. 배포는 기존과 같이 플러그인 DLL 두 개이며 LethalConfig 설치는 불필요.
- 자동 테스트 35개 그룹 통과. v81의 격리된 LAN 로비에서 약 71초 자동 녹화·6구간 저장 후, 재실행하여 외형 재생과 네이티브 버튼·시간 탐색·다음 구간 연결·원래 메뉴 입력 복원 검증.

# 0.2.4

- Gale 프로필에서 일차 목록의 임시 경로가 279자로 늘어나 녹화 시작 전 `DirectoryNotFoundException`이 발생하던 문제 수정.
- 폴더 ID와 임시 파일명을 줄이고, 전체 ID와 기존 폴더 형식의 읽기 호환성 유지.
- 새 폴더·임시 파일의 충돌 시 기존 데이터를 덮어쓰지 않고 재시도. 지나치게 긴 루트는 명확한 경로 오류로 안내.
- 저장 위치·구간 저장 완료 로그 추가. 보관함에서도 녹화 실패 안내가 다른 상태나 마우스 도움말에 가려지지 않도록 수정.
- 재생기의 플레이어 목록과 초기 카메라 대상에서 참가자가 없는 비활성 슬롯 제외.
- 게임 v81 / BepInEx 5.4.23.5에서 약 70초 LAN 로비 자동 녹화·6구간 저장·598프레임 읽기·재생 및 다음 구간 자동 전환 확인. 자동 테스트 31개 그룹 통과.

# 0.2.3

- ILSpy 역컴파일과 실제 게임 프로세스 실행으로 첫 장면 로딩 중 초기 플러그인·실행기 오브젝트가 파괴되는 현상 재현.
- 실행기를 `sceneLoaded` 이후 생성하고, 초기 플러그인 오브젝트 파괴 시 관리형 콜백과 메뉴 후크 유지.
- 실제 `Application.quitting`에서만 녹화 종료·저장·후크 정리.
- 이미 파괴된 Unity 실행기를 정리할 때 발생하던 NullReferenceException 수정.
- 버전별 선택 필드·속성 검색을 캐시하고, 존재하지 않는 필드를 매 프레임 경고하던 처리 수정.
- 게임 v81 / Unity 2022.3.62 / BepInEx 5.4.21 격리 실행에서 프레임 처리·Settings 아래 Replay 생성·실제 버튼 이벤트와 Input System F9 입력으로 보관함 열기와 닫기 확인.

# 0.2.2

- 메뉴 초기화 후 한 프레임 기다려 `MenuContainer/MainButtons`를 직접 찾는 방식으로 변경.
- 비활성 메뉴 관리자에 의존하는 검색 조건과 한 번 실패하면 재시도하지 않던 처리 제거.
- 녹화·단축키·보관함 GUI를 독립적인 영구 Unity 오브젝트에서 실행.
- 프레임 실행 시작, 키보드 준비, F9 입력, 메뉴 초기화·버튼 연결 결과와 실행 예외 로그 추가.

0.2.1 실행 로그에서는 플러그인 로드 이후 자동 녹화 로그가 없었고, F9도 반응하지 않았다는 사용자 보고가 있었습니다. 기존 프레임 처리 미실행의 정확한 원인은 확정하지 못했습니다. 0.2.2의 실제 게임 실행 검증은 아직 수행하지 않았습니다.

# 0.2.1

- 첫 화면의 Settings 바로 아래, Credits 바로 위에 `> Replay` 메뉴 항목 배치.
- 기존 메뉴의 글꼴·색상·선택 효과를 사용하고, 누르면 녹화 보관함 열기.
- 우측 상단의 별도 `[ Replays ]` 버튼 제거. F9 단축키 유지.

실제 게임에서의 메뉴 표시·클릭·다른 메뉴 모드와의 배치는 아직 실행 검증하지 않았습니다.

# 0.2.0

- 수동 시작/정지 없이 게임 참가부터 자동 녹화·자동 저장.
- 게임 실행 → 방 접속 세션 → 관측 일차 → 저장 구간의 실제 폴더 구조.
- 귀환 시 자동 저장 체크포인트, 다음 탐사 시작 시 일차 전환.
- 메인 메뉴 `[ Replays ]` 버튼과 LethalConfig를 참고한 독립 보관함.
- 일차별 정보·처음부터 재생·선택 구간 재생·저장 폴더 열기.
- 같은 일차 내 구간 이어 보기, 비동기 목록 로딩, 이전 파일 및 손상된 목록 복구.
- 자동 저장·일차 경계·목록 복구 테스트 추가.

실제 게임 내 메뉴 렌더링·귀환 자동 저장·여러 버전 호환성은 아직 실행 검증하지 않았습니다.

# 0.1.0

- BepInEx 5용 로컬 상태 녹화 및 분할 파일 저장.
- 플레이어, 적, 아이템, 문, 함정, 차량, 라운드 상태와 관찰 이벤트 기록.
- 렌더링 전용 리플레이 장면, 자유 카메라, 플레이어 따라가기, 일시 정지, 배속, 시간 탐색.
- 압축 기록, 잘린 파일의 완료된 구간 복구, JSONL 내보내기.
- 다른 모드의 상태 공급자 및 이벤트 확장 API.

개발 미리보기입니다. 실제 게임 내 녹화/렌더링 및 여러 게임 버전의 호환성 검증은 아직 수행하지 않았습니다.
