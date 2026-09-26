# Replay file tools

The CLI uses the same validated reader as the in-game viewer. It requires the .NET 8 SDK to build. On a machine with only a newer SDK targeting pack, set `-p:ReplayTestFramework=net10.0` (or the installed target) when building/running.

```powershell
dotnet run --project tools/LCReplay.Cli -- inspect recording.lcr
dotnet run --project tools/LCReplay.Cli -- export recording.lcr recording.jsonl
dotnet run --project tools/LCReplay.Cli -- demo synthetic-demo.lcr
```

`inspect` prints metadata, counts, duration, completion status and warnings. `export` writes ordinary UTF-8 JSON lines in timestamp order, including the header and, for a complete source recording, an end marker. Recovered incomplete input stays explicitly incomplete: its JSONL export has no end marker and prints warnings. Output paths must not already exist. Exit codes are 0 for success, 1 for read/write/validation failures and 2 for invalid command usage.

`demo` creates clearly labelled synthetic data to exercise the viewer without starting a game recording: 12 seconds at 10 Hz, a floor mesh, two players, one enemy, one moving scrap item, and fabricated pickup/damage/death events. It is a demonstration of the file format and playback, not a Lethal Company capture or a compatibility test.
