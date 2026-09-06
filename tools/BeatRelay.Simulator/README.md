# BeatRelay Simulator

Runs the overlay core without Beat Saber, BSIPA, Unity, or network calls.

Use this on macOS to inspect projected-rank behavior before Windows in-game integration:

```bash
dotnet run --project tools/BeatRelay.Simulator/BeatRelay.Simulator.csproj
```

Run the editable file-backed sample:

```bash
dotnet run --project tools/BeatRelay.Simulator/BeatRelay.Simulator.csproj -- \
  --leaderboard tools/BeatRelay.Simulator/scenarios/ranked-sample-leaderboard.json \
  --ticks tools/BeatRelay.Simulator/scenarios/ranked-sample-ticks.json
```

The simulator creates a fake ranked leaderboard and a fake song timeline. It exercises:

- early `Calculating...` suppression
- collapsed gameplay overlay
- expanded pause / note-gap overlay
- late-song rank movement
- projected local row insertion

`ranked-sample-leaderboard.json` uses the same shape as the BeatLeader score response. `ranked-sample-ticks.json` contains simulated in-song snapshots.
