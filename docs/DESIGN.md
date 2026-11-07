# BG Compass: design

Windows overlay for Hearthstone Battlegrounds. Log-only: no memory reads, no injection, no input automation.

## Stack: .NET 10 + Avalonia 12

| Option | RAM (typical) | Download | Overlay | Build from macOS | Decision |
|---|---|---|---|---|---|
| Electron | 200-300 MB | ~100 MB | good | easy | rejected: heaviest at runtime, the game is the priority |
| Tauri 2 | 120-200 MB (WebView2) | ~10 MB | needs cursor polling for click-through | needs MSVC SDK or mingw | rejected: no way to smoke-test WebView2 without Windows |
| WPF | 60-100 MB | ~70 MB | native | compiles only | rejected: UI cannot run or render anywhere but Windows |
| Avalonia | 60-120 MB | ~40-60 MB | one small native window per panel, nothing covers the game outside a panel | publishes win-x64 directly, UI renders on macOS and headless | chosen |

RAM figures are expectations, not measurements of this app.

## What the log gives us

- `Power.log` (`Logs/Hearthstone_<date>/Power.log`, needs `[Power]` in `%LOCALAPPDATA%\Blizzard\Hearthstone\log.config`).
- `GameState.*` is written ahead of the animation and contains combat outcomes in advance. `PowerTaskList.*` replays in sync with the screen. The app reads `PowerTaskList.DebugPrintPower` for all state, like HDT, and `GameState.DebugPrintEntityChoices` / `DebugPrintGame` for choices and the build number. It must never use the ahead-of-time copy to show a result early.
- Phase: `GameEntity TURN` parity (even = combat), as HDT does. `BACON_CURRENT_COMBAT_PLAYER_ID` names the fighter; some fights never write its `value=0`, so it must not decide the phase.
- Available, reliably: hero offers, own board, shop, tier, leaderboard places, opponents' tiers via `PLAYER_TECH_LEVEL`, next opponent id, both boards at combat start, final place.
- Never in the log: lobby tribe list (only sightings prove a tribe present; absence is never proof), MMR, other players' shops and hands.
- Tag names may appear as numbers (`tag=4901`) for tags newer than the client's enum dump. Never crash on them.

## Data sources

- Cards: HearthstoneJSON (`api.hearthstonejson.com/v1/{build|latest}/{enUS,ruRU}/cards.json`), CC0 data, refreshed when the log's `BuildNumber` changes. Snapshot embedded for offline first run. Stale data is shown as stale.
- Updates: GitHub Releases of SolasWyrd/bg-compass, exe verified by SHA-256 (`Runtime/Updater.cs`). No personal data is sent.
- Statistics: no reliable free source that permits use.
  - HSReplay/Tier7: paid, closed.
  - Firestone `static.zerotoheroes.com`: public URL, but its ToS bans scraping and redistribution.
  - bgtracker community feed: permitted but thin (95 games, 0 heroes with n >= 30 on 2026-11-06) and served over plain HTTP from a bare IP.
  - So: the player's own games only, with sample size, and dated expert rules labelled as opinions.
- Rules text reuses MIT-licensed work from bgtracker (see THIRD_PARTY.md).

## Simulator

`@firestone-hs/simulate-bgs-battle` is "All rights reserved"; HDT's BobsBuddy is closed. The only compatible open implementation found is bgtracker's `sim/engine.py` (MIT), ported to C#. It stays labelled BETA, names unmodelled cards, widens odds for them, and shows nothing when a board is incomplete.

## Blizzard rules

The EULA section 1.C bans software that "facilitates the gameplay" or grants an advantage; there is no written approval for overlays. Log-reading trackers have been tolerated for years, which is not a promise. The README states this plainly.
