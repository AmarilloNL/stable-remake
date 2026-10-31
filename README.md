<div align="center">

# osu!stable remake

**An offline, from-scratch rewrite of osu!stable — same feel, lower frame times.**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-raylib--cs%207-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)
![Offline](https://img.shields.io/badge/online-never-lightgrey)
![Replay accuracy](https://img.shields.io/badge/stable%20replay%20accuracy-983%2F1088%20(90.3%25)-ff66aa)
![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)

</div>

---

osu!stable remake plays your existing **osu!stable** library — songs, skins, replays, scores and settings —
in a new client written from the ground up in C#. The goal is to judge, look and feel like stable,
while running with sub-millisecond frame times and handling frames better.

It is **offline only**: it never talks to any osu! server, and it never writes to your osu! folder.
Everything it saves (its own settings, replays) goes next to its own executable.

> [!NOTE]
> This is a personal hobby project. It is not affiliated with, endorsed by, or connected to ppy Pty Ltd
> or osu!. No osu! code or assets are included here: skins, fonts and sounds are read from your own
> installation at runtime.

## ✨ Highlights

| | |
|---|---|
| 🎯 **Stable judging** | Hit windows, notelock, slider tracking (stable's button rules and segmented ball), slider curves, stacking, the momentum spinner and HP drain are re-implemented to behave like stable. Checked against real stable replays: **983 of 1088** re-judge to the exact same 300/100/50/miss counts and max combo. |
| ⚡ **Fast** | ~0.2 ms average frame time during gameplay, almost no per-frame allocation, sprites decoded in the background while you pick a map. Judging runs in fixed 1 ms steps, so results never depend on your frame rate. |
| 🎨 **Your skin, your way** | Reads your skins (with stable's default skin as fallback) and recreates stable's HUD: score, combo, accuracy pie, hit error meter, key overlay, health bar and the animated in-game leaderboard. |
| 🎬 **Storyboards** | Sprites, animations, all commands and 35 easings, loops, triggers, variables, layers and pass/fail states. |
| 📼 **Replays** | Its own exact replay format, plus import and playback of your stable `.osr` replays, with stable's replay overlay and speed control. |
| 🎵 **Low-latency audio** | BASS audio with a clock that stays within ~0.15 ms of the music. |

## 🕹️ What's in

<details open>
<summary><b>Gameplay</b></summary>

- osu!standard with all of stable's mods and hotkeys: EZ NF HT HR SD PF DT NC HD FL RX AP SO, Auto and Score V2 (stable's V2 scoring and slider-head judging)
- Stable's flashlight, countdown, breaks (pass/fail), warning arrows, skip, the fail animation and sound
- Pause and resume exactly like stable (click the orange cursor to resume), hold-to-retry, Visual Settings bar (dim, storyboard, video, skin, hitsound toggles, per map)
- Follow points, snaking sliders, judgement animations and particles, old- and new-style spinners
- Loved/Qualified map warning, local beatmap offset with `+` / `-` (read from stable's `osu!.db`)
- Stable's keyboard bindings (Options → Change keyboard bindings), F7 frame limiter, F10 mouse buttons, F12 screenshots
</details>

<details>
<summary><b>Song select</b></summary>

- Reads stable's `osu!.db` directly, in the background (a 100k-map library is ready in a few seconds)
- Stable's carousel, search with filters (`stars>5 ar=9 length<120`), sorting and grouping (collections, recently played, artist, difficulty)
- Mod-adjusted star ratings, preview music, local leaderboard and personal best
- Mod select (`F1`) and beatmap options (`F3`) as stable's dialogs, random (`F2`) and rewind (`Shift+F2`), play with Auto (`Ctrl+Enter`), watch a local score (`Ctrl+1`–`9`)
</details>

<details>
<summary><b>Menus & UI</b></summary>

- Main menu with the beating cookie, audio visualizer, seasonal backgrounds, tips and music controls
- Ranking screen with stable's layout, HP graph and replay watching
- Options panel as stable's: all nine sections with dropdowns, buttons, stable's tooltips and the skin preview (online parts are shown but never connect; changes go to `remake.cfg`)
- Menu snow, kiai star fountains, break stars, combo bursts and kiai beat pulses, ported from stable
- Volume overlay, stable's FPS / frame time counter, notifications
</details>

<details>
<summary><b>Replays</b></summary>

- Every play is saved as a replay next to the executable (`Replays/`); failed plays can be saved with `F2`
- Stable `.osr` replays from your osu! folder show up in the leaderboards marked `[stable]`
- Stable's replay overlay with speed control (`F`); `H` hides it and the leaderboard
</details>

## 📋 Roadmap

Progress at a glance. Checked items are done; everything else is still open.

**Core**
- [x] osu!standard gameplay with stable's judging, scoring and HP
- [x] All stable mods and mod hotkeys
- [x] Storyboards
- [x] Skins with stable's default skin fallback, and beatmap-specific skin elements
- [x] Song select reading `osu!.db`, collections, search and grouping
- [x] Own replays (exact playback) and stable `.osr` import
- [x] Main menu, options, ranking screen, pause/fail menus
- [x] Background videos (decoded by Windows Media Foundation, no extra libraries; the Video option turns them off)
- [ ] Other modes: osu!taiko, osu!catch, osu!mania
- [ ] Linux / macOS (currently uses Windows fonts and Windows BASS builds)

**Accuracy vs stable**
- [x] Hit windows, notelock, stacking, slider curves and timing ported from stable's behaviour
- [x] Slider tracking with stable's button rules
- [x] Judging identical to a running stable (300/100/50/miss, max combo and score) on 39 maps with generated sloppy replays, and with DoubleTime, HardRock, HalfTime, Easy and Score V2 (scores too)
- [ ] The last ~10% of mismatches on real 60Hz replays (mostly cursor right on the slider follow-radius edge)
- [x] HP drain calibration identical to stable's, with and without HardRock or Easy (checked object by object against a running stable: 39 maps without mods, 32 with HR or EZ)
- [x] Slider curves, timing, ticks, repeats and stacking bit-identical to stable on 39 maps (18,602 objects, also with HR and EZ; one HR slider ends a float step off)
- [x] Song select, main menu, mod select, dialogs, HUD, pause/fail and ranking screens measured pixel by pixel against stable at 720p, 900p and 1080p
- [x] Stable's rule that ignores clicks on a stacked circle while the one below it is still unhit

**Missing pieces**
- [ ] Hit lighting (stable draws it only under a condition not yet identified)
- [ ] Beatmap options (`F3`) actions: the dialog is there, but deleting maps and editing collections are left to stable
- [x] Visual Settings per map (stable's from `osu!.db`, changes kept in `remake.cfg`)
- [ ] Faster fallback when `osu!.db` is missing (folder scan is slow on big libraries)
- [x] Verify guessed details: the Qualified warning text, notice texts and menu tips (read from a running stable)

**Not planned**
- Online features of any kind (login, multiplayer, online leaderboards, score submission)
- The beatmap editor

## 🚀 Getting started

**You need**
- Windows 10 or 11 (x64)
- The [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An osu!stable installation with your songs (the game reads its `osu!.db`, `Songs`, `Skins` and settings)

**Build and run**

```powershell
git clone https://github.com/AmarilloNL/osu-stable-remake.git
cd osu-stable-remake
dotnet build -c Release
& ".\bin\Release\net10.0\osu!stable remake.exe"
```

The game looks for osu!stable in its default location (`%LOCALAPPDATA%\osu!`). If yours is somewhere else,
create `remake.cfg` next to `osu!stable remake.exe`:

```ini
OsuDir = D:\Games\osu!
```

## ⚙️ Configuration

Your osu! settings (`osu!.<username>.cfg`) are read as-is: keys, skin, dim, volumes, offset and so on.
Anything you change inside osu!stable remake is written to `remake.cfg` next to the executable instead, and
overrides the osu! value. Your osu! files are never modified.

## 🧪 Developer tools

The executable doubles as a test harness:

| Command | What it does |
|---|---|
| `--check` | Parses every map in the library and validates the results |
| `--verify-osr N` | Re-judges up to N of your stable replays headlessly and compares them with what stable recorded (`VERIFY_LOG=1` for a per-judgement log, `VERIFY_MD5=<hash>` for one map) |
| `--auto-check [filter] [--mods HR]` | Plays maps with Auto at several frame times, lists any run that isn't all 300s, and prints each score |
| `--play <file.osu> [--auto] [--mods HDDT]` | Starts a map directly |
| `--watch <replay>` | Watches a replay |
| `--fuzz-osr <file.osu> <seed> <out.osr> [mods]` | Writes a deliberately sloppy replay as a stable `.osr` (NoFail) and prints our judgement of it; stable can watch the same file, so the two judgements can be compared |
| `--shots 5,10 --exit-after 12` | Takes screenshots at the given seconds and quits; prints frame time stats |
| `--input "3:key Enter, 4:click 640 360"` | Scripted input for UI tests |

## 📜 Credits & licenses

- [raylib](https://www.raylib.com/) via [Raylib-cs](https://github.com/ChrisDill/Raylib-cs) for graphics and input
- [BASS](https://www.un4seen.com/) and BASS_FX by un4seen developments for audio (free for non-commercial use; the DLLs in `lib/` are covered by their license)
- Menu tips are from the [osu! wiki](https://osu.ppy.sh/wiki/en/Client/Menu_tips) (CC BY-NC 4.0)
- osu! is a game by ppy Pty Ltd. All osu! names, skins and sounds belong to their respective owners.

<div align="center">
<sub>Made by AmarilloNL · offline, for the love of stable</sub>
</div>
