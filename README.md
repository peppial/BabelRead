# BabelRead

A cross-platform .NET 10 desktop reader that opens a **PDF or EPUB** and produces an **on-the-fly,
page-by-page AI translation** of the page you're reading. The translation shows in the same pane via
an original ⇄ translation toggle. The AI model is **reader-selectable at runtime** — both local
models (no key, offline) and cloud models (your own API key) — through the Microsoft Agent Framework
/ `Microsoft.Extensions.AI` `IChatClient` seam. Because local models are slower, BabelRead
**prefetches the next page** in the background so forward reading stays smooth.

> Consumer chat subscriptions (GitHub Copilot, claude.ai Pro/Max) **cannot** be used as a
> translation backend — they aren't usable as an application model endpoint. Use a local model for a
> no-key experience, or a cloud provider with your own API key.

## Requirements

- **.NET 10 SDK** (`dotnet --version` ≥ 10)
- For the offline / no-key path: a local model runtime exposing an OpenAI-compatible endpoint —
  **[Ollama](https://ollama.com)** is the reference (`ollama pull llama3.1`, served at
  `http://localhost:11434/v1`). Foundry Local and LM Studio work the same way.
- For the cloud path: an OpenAI or Azure OpenAI API key, entered in **Settings**.

## Download

Prebuilt, self-contained packages are on the
[releases page](https://github.com/peppial/BabelRead/releases/latest) — **no .NET install needed**.

| Platform | File |
| --- | --- |
| Windows (Intel/AMD) | `BabelRead-<version>-win-x64.zip` |
| Windows (ARM) | `BabelRead-<version>-win-arm64.zip` |
| Linux (Intel/AMD) | `BabelRead-<version>-linux-x64.tar.gz` |
| Linux (ARM) | `BabelRead-<version>-linux-arm64.tar.gz` |
| macOS (Apple silicon) | `BabelRead-<version>-osx-arm64.dmg` |
| macOS (Intel) | `BabelRead-<version>-osx-x64.dmg` |

Check a download against the release's `SHA256SUMS.txt`.

The macOS bundle is ad-hoc signed rather than notarised, so Gatekeeper refuses it after download
("BabelRead is damaged"). Drag it to Applications, then clear the quarantine flag once:

```bash
xattr -dr com.apple.quarantine /Applications/BabelRead.app
```

On **Windows and Linux, cloud API keys are not persisted** — the secure store is macOS Keychain only
today, and other platforms fall back to in-memory storage, so a key must be re-entered each launch.
The local Ollama path needs no key and is unaffected.

Releases are produced by [`.github/workflows/release.yml`](.github/workflows/release.yml): push a
`v*` tag and every platform is built, packaged and attached to the release.

## Build, test, run

```bash
dotnet build -warnaserror          # warnings are errors (code-quality gate)
dotnet test                        # unit, view-model, and integration tests
dotnet run --project src/BabelRead.App
```

## Using it

1. **Open** a PDF or EPUB (toolbar → *Open…*).
2. The current page is translated into your target language; use **◀ / ▶** (or the arrow keys) to
   turn pages, and the toggle button (or **T** / **Space**) to flip between original and translation.
3. **Settings** (⚙):
   - Pick a **model** — the built-in *Local (Ollama)* profile, or add a **cloud** model with your key.
   - Set the **target language** (BCP-47 code, e.g. `en`, `fr`, `de`) and, if auto-detection is
     wrong, override the **source language**.

Preferences (language, active model, toggle default) persist between sessions. Cloud API keys are
stored in the OS secure store (macOS Keychain), never in the preferences file.

## Architecture

Layered solution (`BabelRead.slnx`):

- **`src/BabelRead.Core`** — UI-agnostic domain and services: document readers (PdfPig, VersOne.Epub),
  the translation pipeline, session cache, prefetch coordinator, model-provider factory, preferences,
  and secret store. Fully unit-testable headless.
- **`src/BabelRead.App`** — Avalonia UI (MVVM): reader and settings views/view-models, composition root.
- **`tests/`** — `BabelRead.Core.Tests`, `BabelRead.App.Tests` (headless view-model), and
  `BabelRead.Integration.Tests`, plus `BabelRead.TestSupport` (fakes + fixture generators).

The single `IChatClientFactory` seam is the only place model providers are swapped, so the UI never
depends on any provider.

## License

[MIT](LICENSE) — © 2026 Penka Alexandrova.

Third-party dependencies are all permissive and impose no additional conditions:
PdfPig and xunit are Apache-2.0, VersOne.Epub is Unlicense, and Avalonia,
CommunityToolkit.Mvvm and the `Microsoft.Extensions.*` packages are MIT.
