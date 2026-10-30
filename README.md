# Commander4K

[![CI](https://github.com/4kprojekt/Commander4K/actions/workflows/ci.yml/badge.svg)](https://github.com/4kprojekt/Commander4K/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Built with AI](https://img.shields.io/badge/built%20with-AI%20(Claude%20Code)-8A2BE2.svg)](#built-with-ai)

A fast, keyboard-driven, dual-pane file manager for Windows 11 in the tradition of Norton Commander, Midnight Commander and Total Commander. Written in C# on .NET 10 with Avalonia UI (no XAML), native on x64 and ARM64.

> **Status: early preview (0.x).** It works for daily browsing, copying and searching, but expect rough edges and breaking changes.

![Commander4K with the Norton Commander skin](docs/images/norton-commander.png)

## Features

- **Never freezes.** Disk, network, registry and database work runs off the UI thread. Listings stream in as they arrive, so the first files appear almost immediately, even in folders with hundreds of thousands of entries. `Esc` drops a hung network call at once.
- **One interface for many places:**
  - local disks;
  - `net://` network neighbourhood: finds SMB, SSH and FTP hosts on your LAN by itself;
  - SMB shares, with offline cached browsing;
  - `sftp://` and `ftp://` servers;
  - `reg://`, the Windows Registry.
- **Total Commander-style workflow:**
  - `F5`/`F6` copy and move in the background, with an `F2` queue;
  - `F3` viewer, `F4` your editor, `F7` new folder, `F8` delete;
  - `Alt+F7` parallel search that also looks inside files;
  - `Space` shows folder sizes, `Ctrl+T` opens a tab, `Ctrl+G` jumps to a recently used folder.
- **Skins:** Modern dark/light, Norton Commander, Midnight Commander and Total Commander classic. You can also make your own as JSON, or let AI design one (see below).
- **Easy to configure:**
  - `Ctrl+,` opens a settings window that searches settings, shortcuts and config files;
  - `Ctrl+Shift+P` opens a command palette for every command, skin and setting;
  - every setting stays in plain JSON, so you can still edit the files by hand.
- **Self-updating:** checks GitHub Releases once a day. Before installing, it checks the download against the SHA-256 checksum published with the release.

| Modern | Midnight Commander | Total Commander classic |
|---|---|---|
| ![](docs/images/modern-dark.png) | ![](docs/images/midnight-commander.png) | ![](docs/images/total-commander-classic.png) |

## Install

Download `Commander4K-win-x64.exe` (Intel/AMD) or `Commander4K-win-arm64.exe` (Snapdragon and other ARM64 PCs) from [Releases](https://github.com/4kprojekt/Commander4K/releases) and run it. You don't need to install anything else.

The executables are not code-signed yet, so Windows SmartScreen may warn on first start ("More info" → "Run anyway").

Settings live in a `config` folder next to the exe. If that folder isn't writable, they go to `%AppData%\Commander4K` instead.

## Custom skins and AI-designed skins

Skins are JSON files in `config/skins/`. The schema is written to `config/skins/skin.schema.json` on every start. You can make a skin in three ways:

1. **Export and edit:** press `F2` (skin picker), choose "Eksportuj do edycji", edit the JSON, then click "Przeładuj".
2. **Claude.ai or Claude Design:** "Kopiuj prompt dla Claude" copies a ready prompt with the schema and an example. Describe your idea, save the JSON you get into `config/skins/`, then reload.
3. **In the app:** "Generuj z AI…" describes a skin in words and Claude designs it, with a live preview and contrast check before you save. This option needs an Anthropic API key in `ANTHROPIC_API_KEY`, and each generation is billed by the API.

## Build from source

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet build Commander4K.sln
dotnet test --solution Commander4K.sln
dotnet run --project src/Commander4K.App
./build-release.ps1            # single-file ReadyToRun builds in dist/win-x64 and dist/win-arm64
```

To publish a release, push a tag such as `v0.2.0`. The [release workflow](.github/workflows/release.yml) tests, builds both architectures and publishes the release assets the updater expects.

Architecture: `Commander4K.PluginSdk` (plugin contracts) → `Commander4K.Core` (VFS router, background jobs, SQLite index, keymap, updates) → `Commander4K.Plugins.BuiltIn` (local, network, registry, SFTP/FTP, search) → `Commander4K.App` (Avalonia UI). See [CLAUDE.md](CLAUDE.md) for the details.

## Built with AI

Commander4K is developed with substantial help from AI. Most of the code, tests and documentation were written by Anthropic's Claude (via [Claude Code](https://claude.com/claude-code)) under the direction of the maintainer. Commits produced this way carry a `Co-Authored-By: Claude …` trailer.

AI-written code can contain mistakes that look plausible. Review changes as you would any contribution, and please [report issues](https://github.com/4kprojekt/Commander4K/issues).

## License

[MIT](LICENSE) © 2026 4Kprojekt

---

### Po polsku

Commander4K to szybki, dwupanelowy menedżer plików dla Windows 11 w stylu Norton, Midnight i Total Commandera, na licencji MIT.

- **Interfejs:** po polsku.
- **Konfiguracja:** `Ctrl+,` otwiera ustawienia z wyszukiwarką, a `Ctrl+Shift+P` paletę komend.
- **Skórki:** są w `config/skins`. Możesz też wygenerować je z pomocą AI.
- **Aktualizacje:** pobierane z GitHub Releases.

Projekt powstaje z dużym udziałem AI (Claude Code). Błędy zgłaszaj w zakładce Issues.
