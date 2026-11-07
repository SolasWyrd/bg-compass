# Third-party notices

## bgtracker (MIT)

https://github.com/BattlegroundsHelp/bgtracker

Used here:

- `src/BgCompass.Core/Sim/` is a C# port of `sim/engine.py`.
- `src/BgCompass.Core/Data/hero_tips.ru.json` is a Russian translation of `data/hero_tips.json`.
- `src/BgCompass.Core/Data/archetypes.json` adapts the role definitions of `data/comp_roles.json`.
- `tests/BgCompass.Tests/fixtures/real_2026_heroselect.power.log` is `tests/sample_heroselect.log`.
- Log-reading rules documented in `docs/ARCHITECTURE.md` informed `src/BgCompass.Core/Tracking/`.

```
MIT License

Copyright (c) 2026 the bgtracker contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Avalonia (MIT)

https://github.com/AvaloniaUI/Avalonia. UI framework.

## Onest font (SIL OFL 1.1)

https://github.com/googlefonts/onest. Copyright 2021 The Onest Project Authors. Bundled in `src/BgCompass.App/Assets/Fonts/`; the licence text is `OFL.txt` there.

## HearthstoneJSON

https://hearthstonejson.com. Card data downloaded at runtime and embedded as an offline snapshot; card art tiles fetched at runtime.

Hearthstone, its card names, text and art are (c) Blizzard Entertainment, Inc. This project is not affiliated with or endorsed by Blizzard Entertainment.

## Looked at and deliberately not used

- Firestone and `@firestone-hs/simulate-bgs-battle`: no open licence ("All rights reserved").
- `static.zerotoheroes.com/api/bgs/*`: public URL, but bgtracker's notes report that its terms forbid scraping and redistribution.
- HDT BobsBuddy and HearthMirror: closed or memory-reading.
- HearthLens, TavernLens: no licence granted.
