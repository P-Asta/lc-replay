# LC Replay

Record your Lethal Company runs automatically, then watch them again from a new angle.

**Version 1.0.0** · BepInEx 5 · Windows

![Watching a recorded expedition inside the mansion](https://raw.githubusercontent.com/P-Asta/lc-replay/main/docs/images/replay-freecam.png)

## Your runs, ready to replay

- **Automatic recording:** host or join a game and play as usual.
- **Free camera:** explore a recorded scene or follow players and enemies.
- **Playback controls:** pause, jump along the timeline, and watch at 0.25×–4× speed.
- **Bookmarks:** mark a moment during a run so you can find it later.
- **Local library:** browse recordings by lobby, quota, and day.

## Install

### With a mod manager

Install **Replay** and its dependencies in the Gale, r2modman, or Thunderstore profile you use to launch Lethal Company. Launch the game with mods enabled.

### Manually

1. Install **BepInEx 5** and **LethalCompany InputUtils**, including its dependencies.
2. Extract `LCReplay-1.0.0.zip` into your game folder. The three files `LCReplay.dll`, `LCReplay.Core.dll`, and `LCReplay.Skinning.dll` belong in `BepInEx/plugins/LCReplay`.
3. Remove duplicate older copies of Replay and restart the game.

## Record and watch

1. **Host or join a game.** Recording starts automatically. There is no record or save button to press; recordings are saved as you play and finalized when you return to orbit, disconnect, or quit.
2. **Mark your favorite moments.** Press the backtick key (`` ` ``) during gameplay to add a bookmark. You can change the binding through InputUtils or Replay's configuration.
3. **Open your library.** Choose **Replay** from the main menu or pause menu, or press **F9**.
4. **Choose a recording and press Play.** Use the timeline to seek, click bookmark markers to revisit a moment, and use the Camera selector to switch views.

**The live game keeps running while you watch a replay in a session.** Close the viewer to return to your character.

![A recorded view of the ship interior](https://raw.githubusercontent.com/P-Asta/lc-replay/main/docs/images/replay-ship.png)

## Camera and playback controls

| Control | Action |
| --- | --- |
| F9 | Open or close the replay library |
| Esc | Leave playback or close the library |
| WASD + mouse | Move and look around in free camera |
| Q / E | Move down / up |
| Shift | Move faster |
| B | Show or hide the cursor |
| Right mouse button | Look around while the cursor is visible |
| Left mouse button | Follow a player or enemy in free camera |
| Mouse wheel | Change free-camera speed or follow distance |
| Space | Pause or resume |
| Left / right arrow | Jump back / forward 5 seconds |
| C | Toggle smooth camera movement |
| L | Show or hide the playback interface |

Zoom close to a followed player to use their recorded first-person camera when available. The playback bar also lets you change speed and select a camera.

## Make it look right

Open **Settings** during playback to adjust resolution, brightness, fog, shadows, and camera movement. For better performance, lower the replay resolution first. Frame rate depends on your PC, the scene, and installed mods.

Use the **same game version and mod pack** that you recorded with for the closest match. Replay recreates the scene from saved game state and installed assets, so lighting, fog, particles, and some modded visuals can differ from the original game. Older recordings cannot restore details that were never captured.

## Where are my recordings?

Recordings stay in your active game or mod-manager profile:

- **Recordings:** `BepInEx/replays`
- **Settings:** `BepInEx/config/pasta.replay.cfg`

The library's **Folder** button opens the selected recording's location. Use the **X** beside an entry to delete recordings after confirmation. Keep a backup of recordings you want to save.

Nothing is uploaded automatically. Recording files may include player names. Voice chat is not recorded, and Replay does not export video; use your usual screen recorder to share a clip.

## Need help?

- **Replay is missing from the menu:** check that you launched the correct modded profile and installed the dependencies.
- **Scenery or characters look different:** check that the original game's mods and assets are installed.
- **Playback runs slowly:** lower resolution and adjust fog and shadow settings in the replay viewer.

Report problems on [GitHub Issues](https://github.com/P-Asta/lc-replay/issues). Include your game version, mod list, and what happened.

Licensed under the [MIT License](https://github.com/P-Asta/lc-replay/blob/main/LICENSE).
