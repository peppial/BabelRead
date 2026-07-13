---
description: "Task list for On-the-Fly Document Page Translation"
---

# Tasks: On-the-Fly Document Page Translation

**Input**: Design documents from `/specs/001-document-page-translation/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/service-interfaces.md

**Tests**: REQUIRED. Constitution v1.0.0 Principle II (Testing Standards) is NON-NEGOTIABLE — every
feature ships with tests written Red-Green-Refactor. Test tasks precede implementation in each phase.

**Organization**: Grouped by user story so each is independently implementable and testable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: US1 / US2 / US3 (from spec.md)
- Paths follow the layered layout in plan.md: `src/BabelRead.App`, `src/BabelRead.Core`, `tests/*`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution scaffolding and tooling.

- [X] T001 Create solution and projects: `BabelRead.sln` with `src/BabelRead.App` (Avalonia), `src/BabelRead.Core` (classlib), and `tests/BabelRead.Core.Tests`, `tests/BabelRead.App.Tests`, `tests/BabelRead.Integration.Tests` (xUnit), targeting .NET 10
- [X] T002 Add NuGet dependencies: Avalonia 11 + `CommunityToolkit.Mvvm` (App), `Microsoft.Agents.AI` / `Microsoft.Extensions.AI` + `UglyToad.PdfPig` + `VersOne.Epub` (Core), `xUnit` + `Avalonia.Headless.XUnit` (tests)
- [X] T003 [P] Configure code-quality gates in `.editorconfig` and `Directory.Build.props`: nullable enabled, `TreatWarningsAsErrors=true`, Roslyn analyzers, `dotnet format` (Constitution I)
- [X] T004 [P] Add `Directory.Build.props` test/coverage settings and a `dotnet test` script so CI can run the full suite green (Constitution II)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain, service seams, and app host that every user story depends on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T005 [P] Define `Document` and `Page` entities in `src/BabelRead.Core/Domain/Document.cs` and `src/BabelRead.Core/Domain/Page.cs` (per data-model.md)
- [X] T006 [P] Define `Translation`, `TranslationKey`, and status/origin enums in `src/BabelRead.Core/Domain/Translation.cs`
- [X] T007 [P] Define `ModelProfile`/`ModelConfiguration`, `LanguageCode`, and `ReaderPreferences` in `src/BabelRead.Core/Domain/`
- [X] T008 Define all service interface contracts (`IDocumentReader`, `IChatClientFactory`, `ITranslationService`, `ITranslationCache`, `IPrefetchCoordinator`, `IPreferencesStore`, `ISecretStore`) per `contracts/service-interfaces.md`, placed in their respective `src/BabelRead.Core/{Documents,Models,Translation,Preferences}/` folders
- [X] T009 [P] Implement `FakeChatClient : IChatClient` test double (canned + optionally delayed responses) in `tests/BabelRead.Integration.Tests/Fakes/FakeChatClient.cs`
- [X] T010 Implement `IChatClientFactory` over Microsoft Agent Framework with the **local** OpenAI-compatible provider (Ollama/Foundry Local) in `src/BabelRead.Core/Models/ChatClientFactory.cs`
- [X] T011 [P] Implement `IPreferencesStore` (JSON via `System.Text.Json`, per-user app-data) in `src/BabelRead.Core/Preferences/JsonPreferencesStore.cs`
- [X] T012 [P] Implement `ISecretStore` (OS-native: DPAPI/Keychain/libsecret) with an in-memory fake for tests in `src/BabelRead.Core/Preferences/SecretStore.cs`
- [X] T013 Configure DI/app host, structured logging, and top-level error handling in `src/BabelRead.App/Program.cs` and `src/BabelRead.App/App.axaml.cs`
- [X] T014 [P] Create shared UI state controls (loading / empty / error-with-retry) and the Fluent theme in `src/BabelRead.App/Controls/` and `src/BabelRead.App/Assets/` (Constitution III)

**Checkpoint**: Foundation ready — user stories can begin.

---

## Phase 3: User Story 1 - Read a translated page while browsing (Priority: P1) 🎯 MVP

**Goal**: Open a PDF/EPUB, navigate page-by-page, and see the current page's AI translation in the
same pane via an original/translation toggle — with next-page prefetch so local-model reading is
smooth.

**Independent Test**: Open a foreign-language PDF and EPUB with the default local model and target
language; confirm the current page translates, the toggle flips original⇄translation, translations
stay in sync on navigation, revisits are instant, and forward turns are served from prefetch.

### Tests for User Story 1 (write first, must fail)

- [X] T015 [P] [US1] Unit tests for `PdfDocumentReader` (paginate, extract text, empty page for image-only) using a sample fixture in `tests/BabelRead.Core.Tests/Documents/PdfDocumentReaderTests.cs`
- [X] T016 [P] [US1] Unit tests for `EpubDocumentReader` (spine reading-order pages, text extraction, language detection) in `tests/BabelRead.Core.Tests/Documents/EpubDocumentReaderTests.cs`
- [X] T017 [P] [US1] Unit tests for `TranslationService` (Completed/Failed, `PageIndex` matches source per FR-010, source==target short-circuit) using `FakeChatClient` in `tests/BabelRead.Core.Tests/Translation/TranslationServiceTests.cs`
- [X] T018 [P] [US1] Unit tests for `TranslationCache` reuse + key includes language & model (FR-008, SC-003) in `tests/BabelRead.Core.Tests/Translation/TranslationCacheTests.cs`
- [X] T019 [P] [US1] Unit tests for `PrefetchCoordinator` (next-page scheduled, cancelled on navigation, yields to on-demand — FR-015/FR-016, SC-008) in `tests/BabelRead.Core.Tests/Translation/PrefetchCoordinatorTests.cs`
- [X] T020 [P] [US1] Headless view-model tests for `ReaderViewModel` (navigate, toggle, in-sync translation, loading/empty/error states) in `tests/BabelRead.App.Tests/ReaderViewModelTests.cs`
- [X] T021 [US1] Integration test: open → translate current page → prefetch → instant next turn, end-to-end with `FakeChatClient` in `tests/BabelRead.Integration.Tests/ReadFlowTests.cs`

### Implementation for User Story 1

- [X] T022 [P] [US1] Implement `PdfDocumentReader` (PdfPig) in `src/BabelRead.Core/Documents/PdfDocumentReader.cs`
- [X] T023 [P] [US1] Implement `EpubDocumentReader` (VersOne.Epub, spine chunking) in `src/BabelRead.Core/Documents/EpubDocumentReader.cs`
- [X] T024 [US1] Implement `TranslationService` (one page → one chat completion, page-matching, off-UI-thread) in `src/BabelRead.Core/Translation/TranslationService.cs`
- [X] T025 [P] [US1] Implement `TranslationCache` (session-scoped, bounded LRU) in `src/BabelRead.Core/Translation/TranslationCache.cs`
- [X] T026 [US1] Implement `PrefetchCoordinator` (one-page lookahead, `CancellationToken`, writes to cache) in `src/BabelRead.Core/Translation/PrefetchCoordinator.cs` (depends on T024, T025)
- [X] T027 [US1] Implement `ReaderViewModel` (open/next/prev/jump, toggle, progress, background translation + prefetch trigger) in `src/BabelRead.App/ViewModels/ReaderViewModel.cs` (depends on T022–T026)
- [X] T028 [US1] Implement `ReaderView` (single pane + toggle control, loading/empty/error states, keyboard navigation, WCAG labels) in `src/BabelRead.App/Views/ReaderView.axaml` and `.axaml.cs` (Constitution III)
- [X] T029 [US1] Wire open-file dialog + default local model (from T010) + default target language into the reader flow in `src/BabelRead.App/ViewModels/ReaderViewModel.cs`

**Checkpoint**: US1 fully functional and independently testable — this is the MVP.

---

## Phase 4: User Story 2 - Choose and switch the AI model (Priority: P2)

**Goal**: Let the reader configure cloud (reader-supplied credentials) and local models, choose the
active one, and switch at any time — with consumer chat subscriptions explicitly not offered.

**Independent Test**: With a document open, switch between a local and a cloud model in Settings,
translate a page, and confirm the newly selected model produced it; verify no Copilot/claude.ai
subscription options appear (FR-014).

### Tests for User Story 2 (write first, must fail)

- [X] T030 [P] [US2] Unit tests for `ChatClientFactory` building cloud + local clients and switching active profile in `tests/BabelRead.Core.Tests/Models/ChatClientFactoryTests.cs`
- [X] T031 [P] [US2] Unit tests for `SecretStore` round-trip (set/get, never in plaintext prefs) in `tests/BabelRead.Core.Tests/Preferences/SecretStoreTests.cs`
- [X] T032 [P] [US2] Headless VM tests for `SettingsViewModel` (list/select/switch model, persists choice, no subscription options) in `tests/BabelRead.App.Tests/SettingsViewModelTests.cs`
- [X] T033 [US2] Integration test: switching model changes which client produces the next translation (FakeChatClient A→B) in `tests/BabelRead.Integration.Tests/ModelSwitchTests.cs`

### Implementation for User Story 2

- [X] T034 [P] [US2] Extend `ChatClientFactory` with cloud providers (OpenAI / Azure OpenAI, credentials via `ISecretStore`) in `src/BabelRead.Core/Models/ChatClientFactory.cs`
- [X] T035 [US2] Implement `ModelProfileService` (add / select / switch active profile; only Cloud|Local kinds) in `src/BabelRead.Core/Models/ModelProfileService.cs`
- [X] T036 [US2] Implement `SettingsViewModel` (model list, credential entry via `ISecretStore`, switch action) in `src/BabelRead.App/ViewModels/SettingsViewModel.cs`
- [X] T037 [US2] Implement `SettingsView` model-configuration UI, offering only cloud/local (no consumer subscriptions) in `src/BabelRead.App/Views/SettingsView.axaml` and `.axaml.cs`
- [X] T038 [US2] Persist active model profile + retrieve on launch, and wire live model switch into `ReaderViewModel` (later translations use the new model; cache keyed by model) in `src/BabelRead.App/ViewModels/ReaderViewModel.cs`

**Checkpoint**: US1 and US2 both work independently.

---

## Phase 5: User Story 3 - Choose the target (and source) language (Priority: P2)

**Goal**: Let the reader pick the target language, auto-detect the source language, and override the
source when detection is wrong.

**Independent Test**: Set the target language to two different languages and confirm the same page
translates into each; override a wrong source language and confirm subsequent translations honor it.

### Tests for User Story 3 (write first, must fail)

- [X] T039 [P] [US3] Unit tests for source-language resolution (auto-detect + reader override precedence) in `tests/BabelRead.Core.Tests/Translation/LanguageResolutionTests.cs`
- [X] T040 [P] [US3] Headless VM tests: changing target language re-translates the current page into the new language in `tests/BabelRead.App.Tests/LanguageSelectionTests.cs`
- [X] T041 [US3] Integration test: target change → new-language translation; source override honored on next translations in `tests/BabelRead.Integration.Tests/LanguageFlowTests.cs`

### Implementation for User Story 3

- [X] T042 [P] [US3] Implement `LanguageResolver` (detected source vs per-document override, target selection) in `src/BabelRead.Core/Translation/LanguageResolver.cs`
- [X] T043 [US3] Add target-language selection + source-language override UI to `src/BabelRead.App/ViewModels/SettingsViewModel.cs` and `src/BabelRead.App/Views/SettingsView.axaml`
- [X] T044 [US3] Wire selected target + source override into translation requests and persist target language via `IPreferencesStore` in `src/BabelRead.App/ViewModels/ReaderViewModel.cs`

**Checkpoint**: All three user stories independently functional.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Constitution-driven quality gates across all stories.

- [X] T045 [P] Accessibility pass across all views: WCAG 2.1 AA keyboard nav, contrast, screen-reader labels, and RTL target-language layout (Constitution III) in `src/BabelRead.App/Views/`
- [X] T046 [P] Performance validation tests asserting budgets SC-002 (<10s), SC-003 (<1s revisit), SC-004 (<30s model switch), SC-008 (≥80% prefetched turns) in `tests/BabelRead.Integration.Tests/PerformanceTests.cs` (Constitution IV)
- [X] T047 [P] Write `README.md` and `docs/` covering build/run, local model (Ollama) setup, and cloud key entry
- [X] T048 Run `quickstart.md` manual validation scenarios 1–6 and record results
- [X] T049 Final code-quality gate: `dotnet format --verify-no-changes` and `dotnet build -warnaserror` green; remove dead code (Constitution I)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies — start immediately.
- **Foundational (Phase 2)**: depends on Setup — **blocks all user stories**.
- **User Stories (Phase 3–5)**: all depend on Foundational; can then proceed in parallel or in
  priority order P1 → P2 → P2.
- **Polish (Phase 6)**: depends on all targeted user stories being complete.

### User Story Dependencies

- **US1 (P1)**: after Foundational. Uses the default local model + default target language provided
  by Foundational, so it is a self-contained MVP.
- **US2 (P2)**: after Foundational. Extends model configuration; integrates with the US1 reader but
  US1 remains functional without it.
- **US3 (P2)**: after Foundational. Adds language choice; integrates with the US1 reader but US1
  works with the default language without it.

### Within Each User Story

- Tests written and failing before implementation (Constitution II).
- Models/entities → services → view-models → views.
- Core (`BabelRead.Core`) before UI (`BabelRead.App`) wiring.

### Parallel Opportunities

- Setup: T003, T004 in parallel.
- Foundational: T005, T006, T007 (distinct entity files), then T009, T011, T012, T014 in parallel.
- US1 tests T015–T020 in parallel; implementations T022, T023, T025 in parallel (T024→T026→T027 chain).
- US2 tests T030–T032 in parallel; US3 tests T039, T040 in parallel.
- With capacity, US1/US2/US3 can be built by different developers once Foundational is done.

---

## Parallel Example: User Story 1

```bash
# US1 tests together (all fail first):
Task: "Unit tests for PdfDocumentReader in tests/BabelRead.Core.Tests/Documents/PdfDocumentReaderTests.cs"
Task: "Unit tests for EpubDocumentReader in tests/BabelRead.Core.Tests/Documents/EpubDocumentReaderTests.cs"
Task: "Unit tests for TranslationService in tests/BabelRead.Core.Tests/Translation/TranslationServiceTests.cs"
Task: "Unit tests for TranslationCache in tests/BabelRead.Core.Tests/Translation/TranslationCacheTests.cs"
Task: "Unit tests for PrefetchCoordinator in tests/BabelRead.Core.Tests/Translation/PrefetchCoordinatorTests.cs"

# US1 independent implementations together:
Task: "Implement PdfDocumentReader in src/BabelRead.Core/Documents/PdfDocumentReader.cs"
Task: "Implement EpubDocumentReader in src/BabelRead.Core/Documents/EpubDocumentReader.cs"
Task: "Implement TranslationCache in src/BabelRead.Core/Translation/TranslationCache.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1 Setup → 2. Phase 2 Foundational (blocks everything) → 3. Phase 3 US1 →
4. **STOP and VALIDATE** US1 independently (open, translate, toggle, prefetch) → demo the MVP.

### Incremental Delivery

- Setup + Foundational → foundation ready.
- + US1 → read + translate + prefetch (MVP).
- + US2 → configurable/switchable models.
- + US3 → language choice.
- Each story adds value without breaking the previous.

---

## Notes

- [P] = different files, no dependency on incomplete tasks.
- Tests are required (Constitution II) — verify they fail before implementing.
- Keep `BabelRead.Core` free of UI dependencies so services stay headless-testable.
- Commit after each task or logical group; stop at any checkpoint to validate a story.
