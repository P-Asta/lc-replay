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
