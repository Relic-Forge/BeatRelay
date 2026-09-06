# BeatRelay

BeatRelay is a PC Beat Saber mod that shows your projected leaderboard placement while you play. It is built for players who want a clean, readable in-game overlay that keeps rank context visible without turning the whole run into a spreadsheet.

BeatRelay supports both BeatLeader and ScoreSaber, including source selection from the mod settings.

This release targets **Beat Saber 1.40.8 on Windows PC**. Newer versions will be added eventually.

## Features

- Live projected leaderboard rank during gameplay.
- BeatLeader and ScoreSaber leaderboard support.
- In-game overlay with familiar Counters+ position presets.
- Optional desktop overlay for recording, streaming, or other means.
- Collapsed and expanded display modes that react to breaks/blank space in maps.
- Customizable visible player count, row details, scale, opacity, and everything inbetween.
- Separate display controls for compact and expanded overlay states.
- Background leaderboard discovery so the overlay can refine nearby ranks over time.

## Dependencies

BeatRelay depends on:

- BSIPA
- BeatSaberMarkupLanguage

Install compatible versions of these mods and their dependencies through BSManager, another mod manager, or their GitHub releases. They are not included in the BeatRelay ZIP. The ZIP includes BeatRelay's required .NET support libraries in `Libs`.

## Installation

Install BeatRelay like a normal Beat Saber mod:

1. Close Beat Saber and install the required dependencies for 1.40.8.
2. Extract the ZIP contents directly into your Beat Saber installation folder (the folder containing `Beat Saber.exe`), merging the included folders.
3. Start Beat Saber and open BeatRelay from the mod menu.

The ZIP contains `Plugins/BeatRelay.dll` and supporting DLLs in `Libs/`. Extract both folders into your Beat Saber installation folder; not inside `Plugins`.

Your existing settings in `UserData` are preserved when updating.

## Configuration

BeatRelay can be configured from its in-game settings page.

- `Leaderboard source` switches between BeatLeader and ScoreSaber.
- `Update interval` controls how often the live overlay refreshes.
- `Visible players` changes how many nearby leaderboard rows are shown.
- `Position preset` moves the overlay between familiar in-game locations.
- `Desktop overlay` enables or disables the overlay drawn in the desktop game window.
- ... and more

## Notes

BeatRelay projects your final leaderboard position from the current stats and the leaderboard data it has available. On very large leaderboards, or while the mod is still discovering the relevant rank range, the projected rank may become more accurate as more data is loaded.

Some maps, leaderboard sources, modifiers, or score states may expose less information than others. BeatRelay will still try to show the best available estimate.

## FAQ

### Why not make it a Custom Counter?

Early in development, Counters+ integration caused a few small but frustrating problems: UI could end up in a half-broken state, settings did not always apply properly, and the overlay was more limited than intended.

BeatRelay instead reuses the position presets you already know and love (from Counters+), while keeping the overlay purpose-built enough to give it the power it deserves.

### Can BeatRelay rate limit me on ScoreSaber or BeatLeader?

Normal use should be fine. BeatRelay has built-in request pacing, so it is not trying to spam leaderboard APIs.

The update interval mostly controls how often the overlay refreshes, but setting it very low can make BeatRelay check for leaderboard data more often. Leaving it near the default is the safest choice.

## License

BeatRelay is licensed under the MIT License. See [LICENSE](LICENSE) for details.
