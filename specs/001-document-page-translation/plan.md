# Implementation Plan: On-the-Fly Document Page Translation

**Branch**: `001-document-page-translation` | **Date**: 2026-07-12 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-document-page-translation/spec.md`

## Summary

BabelRead is a cross-platform .NET 10 desktop reader that opens a local PDF or EPUB, extracts the
text of the page currently in view, and produces an AI-generated translation into the reader's
chosen language — shown in the same pane via an original/translation toggle. The AI model is
reader-selectable at runtime through the Microsoft Agent Framework's `IChatClient` abstraction,
covering both cloud models (reader-supplied credentials) and local models (Ollama / Foundry Local).
Because local models are slow, the app prefetches the next page's translation in the background so
forward reading feels instant. Consumer chat subscriptions (Copilot, claude.ai) are explicitly not
backends.

## Technical Context

**Language/Version**: C# 13 on .NET 10 (LTS)

**Primary Dependencies**:
- **UI**: Avalonia UI 11 (cross-platform desktop — runs on the developer's macOS machine plus
  Windows/Linux; WPF/WinUI were rejected as Windows-only) with the MVVM pattern via
  `CommunityToolkit.Mvvm`.
- **AI**: Microsoft Agent Framework (`Microsoft.Agents.AI`) over `Microsoft.Extensions.AI`
  `IChatClient`. Cloud via the OpenAI / Azure OpenAI clients; local via an OpenAI-compatible
  endpoint (Ollama / Foundry Local). Model swapping = swapping the active `IChatClient`.
- **Document parsing**: `UglyToad.PdfPig` (PDF text extraction), `VersOne.Epub` (EPUB parsing).

**Storage**: Reader preferences persisted as a JSON file (`System.Text.Json`) under the per-user
app-data directory; model credentials in OS-native secure storage (Windows DPAPI / macOS Keychain /
libsecret) behind an `ISecretStore` abstraction. Translation cache is in-memory, session-scoped
(per FR-008).

**Testing**: xUnit for unit/integration; `Avalonia.Headless.XUnit` for view-model / UI-logic tests.
A fake `IChatClient` drives deterministic translation-pipeline tests with no network or model.

**Target Platform**: Desktop — macOS (primary dev target), Windows, and Linux from one codebase.

**Project Type**: Desktop application (single solution, layered: App / Core / Tests).

**Performance Goals** (from Constitution IV + spec SC): interactive view usable < 2s; UI
interactions respond < 100ms (all translation work off the UI thread); current-page translation
presented < 10s (SC-002); revisited/prefetched page shown < 1s (SC-003); ≥ 80% of forward page
turns served from prefetch under a local model (SC-008).

**Constraints**: Fully functional offline when a local model is used; UI thread never blocks on I/O
or inference; prefetch must yield to on-demand translation and never show a mismatched page (FR-010,
FR-016).

**Scale/Scope**: Single-user desktop app; documents up to typical book length (hundreds–thousands of
pages); one document open at a time in v1; ~4–6 primary screens/panes.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Mapped to the four principles in `.specify/memory/constitution.md` v1.0.0:

- **I. Code Quality** — Solution ships with `.editorconfig`, nullable reference types enabled,
  `TreatWarningsAsErrors=true`, and Roslyn analyzers; `dotnet format` verifies style. Layering
  (App → Core; Core has no UI dependency) keeps complexity contained and logic testable. **PASS**
- **II. Testing (NON-NEGOTIABLE)** — Every feature ships with xUnit tests written Red-Green-Refactor.
  Critical paths get unit + integration coverage: PDF/EPUB text extraction, the translation pipeline
  (via a fake `IChatClient`), the session cache, page-matching (FR-010), and the prefetch
  coordinator (FR-015/FR-016). Tests are deterministic — no real model or network. **PASS**
- **III. User Experience Consistency** — One Avalonia Fluent-based theme and shared components;
  every user-facing state is designed: loading (translation in progress), empty (no extractable
  text), error (with retry), and offline (local model). WCAG 2.1 AA: keyboard navigation for
  open/turn/toggle, sufficient contrast, screen-reader labels; RTL target languages supported. **PASS**
- **IV. Performance Requirements** — Budgets above are enforced: translation and parsing run off the
  UI thread; the prefetch coordinator exists specifically to meet the interactive budget on slow
  local models. Benchmarks/timers assert SC-002/SC-003/SC-008 on representative inputs; resource use
  (memory, cache size) is bounded. **PASS**

No violations → Complexity Tracking left empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-document-page-translation/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (internal service contracts)
└── tasks.md             # Phase 2 output (/speckit-tasks — NOT created here)
```

### Source Code (repository root)

```text
BabelRead.sln
src/
├── BabelRead.App/                 # Avalonia UI host (MVVM)
│   ├── Views/                     # ReaderView, SettingsView, dialogs
│   ├── ViewModels/                # ReaderViewModel, SettingsViewModel, ...
│   ├── Controls/                  # shared UI components (states: loading/empty/error)
│   └── Assets/                    # theme, icons
└── BabelRead.Core/                # UI-agnostic domain + services
    ├── Documents/                 # IDocumentReader, PdfDocumentReader, EpubDocumentReader
    ├── Translation/               # ITranslationService, TranslationCache, PrefetchCoordinator
    ├── Models/                    # IChatClientFactory, ModelProfile, provider config (Agent Framework)
    ├── Preferences/               # IPreferencesStore, ISecretStore
    └── Domain/                    # Document, Page, Translation, ModelConfiguration entities

tests/
├── BabelRead.Core.Tests/         # unit tests (parsing, cache, prefetch, page-matching)
├── BabelRead.App.Tests/          # headless view-model / UI-logic tests
└── BabelRead.Integration.Tests/  # end-to-end pipeline with a fake IChatClient
```

**Structure Decision**: Single-solution layered desktop app. `BabelRead.Core` holds all domain
logic and service interfaces with **no UI dependency**, so the translation pipeline, cache, and
prefetch coordinator are unit-testable headless (Constitution II). `BabelRead.App` is a thin
Avalonia MVVM layer binding to Core services. The `IChatClient` seam (Microsoft Agent Framework) is
the single place model providers are swapped, satisfying FR-007/FR-014 without leaking provider
details into the UI.

## Complexity Tracking

> No constitution violations — nothing to justify.
