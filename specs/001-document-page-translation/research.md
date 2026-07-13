# Phase 0 Research: On-the-Fly Document Page Translation

All decisions resolve the Technical Context. No open `NEEDS CLARIFICATION` remain.

## 1. UI framework — cross-platform desktop on .NET 10

- **Decision**: Avalonia UI 11 with MVVM (`CommunityToolkit.Mvvm`).
- **Rationale**: The spec says "desktop" without an OS constraint, and the developer's machine is
  macOS. WPF and WinUI 3 are Windows-only and cannot even run locally for development. Avalonia is a
  mature, actively maintained .NET 10-compatible desktop UI that runs on macOS, Windows, and Linux
  from one codebase, has a Fluent theme (supports Constitution III consistency), headless test host
  (`Avalonia.Headless`) for Constitution II, and first-class MVVM.
- **Alternatives considered**: WPF/WinUI 3 (Windows-only — rejected); .NET MAUI (mobile-first, weaker
  desktop story, no macOS-parity for this use case); Uno Platform (heavier, web/mobile focus).

## 2. Model abstraction — Microsoft Agent Framework

- **Decision**: Use Microsoft Agent Framework (`Microsoft.Agents.AI`) built on
  `Microsoft.Extensions.AI`'s `IChatClient`. A translation is one chat completion. Model swapping =
  constructing a different `IChatClient` from the reader's active `ModelProfile`.
- **Rationale**: The user explicitly chose Microsoft Agent Framework "so the model can be changed by
  the user." `IChatClient` is the framework's single provider-neutral seam; every supported provider
  (OpenAI, Azure OpenAI, Ollama, etc.) implements it, so the app depends on the interface, not any
  provider. This isolates provider details to one factory and makes the pipeline testable with a fake
  `IChatClient`.
- **Alternatives considered**: Calling each provider SDK directly (rejected — defeats the swap
  requirement, duplicates plumbing); Semantic Kernel directly (Agent Framework supersedes/merges it
  and is what the user named).

## 3. Model providers in scope for v1 (FR-014)

- **Decision**: (a) **Cloud** — OpenAI and Azure OpenAI via `IChatClient`, using reader-supplied API
  credentials. (b) **Local** — an OpenAI-compatible local endpoint, with **Ollama** as the reference
  runtime (also compatible with Foundry Local / LM Studio, which expose the same API shape).
- **Rationale**: Both categories are required by FR-014. Ollama is the most common no-key, offline
  local runtime and speaks the OpenAI-compatible API, so it reuses the same client path as cloud with
  a different base URL and no key. Foundry Local drops in via the same endpoint contract.
- **Explicitly out of scope**: Consumer chat subscriptions — GitHub Copilot and claude.ai Pro/Max —
  are **not** usable as an application model endpoint and are not offered (FR-014, spec Assumptions).
  GitHub Models (GitHub-token auth, rate-limited) is noted as a possible future cloud option, not v1.

## 4. PDF text extraction

- **Decision**: `UglyToad.PdfPig`.
- **Rationale**: MIT-licensed, pure-managed .NET (no native deps → clean cross-platform), extracts
  per-page text with word positions. Page model maps directly to the app's `Page` entity.
- **Alternatives considered**: PDFium wrappers (native binaries per-OS — packaging friction);
  iText (AGPL/commercial licensing — rejected for an open desktop app).
- **Edge handling**: Image-only/scanned pages yield no extractable text → surfaced as the "nothing to
  translate" empty state (spec edge case). OCR is out of scope for v1.

## 5. EPUB parsing

- **Decision**: `VersOne.Epub` (EpubReader).
- **Rationale**: Popular, MIT-licensed, pure .NET; reads spine/reading order and per-item HTML/text,
  and detects the document language when present. Content is HTML — plain reading text is extracted
  for translation.
- **Pagination note**: EPUB is reflowable and has no intrinsic fixed "pages." v1 defines a "page" as
  a spine reading-order unit (one content document, or a bounded chunk of a large one) so navigation
  and per-page translation stay meaningful. Exact chunking size is an implementation detail of the
  `EpubDocumentReader`, covered in tasks.

## 6. Prefetch strategy (FR-015 / FR-016 / SC-008)

- **Decision**: A `PrefetchCoordinator` that, whenever the current page settles, kicks off a
  background translation of the **next page in the current direction of travel** (default: forward),
  writing the result into the session cache keyed by page identity. On-demand translation of the page
  the reader is actively waiting for takes priority; a prefetch in flight for a now-abandoned page is
  cancelled (`CancellationToken`). Lookahead depth in v1 is exactly **one page** (the minimum that
  makes slow local models usable; deeper lookahead deferred).
- **Rationale**: Meets SC-008 (≥80% of forward turns instant) without whole-document pre-translation.
  Single-page lookahead bounds memory and model load. Cancellation + page-identity keys satisfy
  FR-010/FR-016 (never show a translation belonging to a different page; never starve the on-demand
  request).
- **Alternatives considered**: Translate whole document up front (contradicts "on the fly", huge
  local-model cost); no prefetch (fails SC-008 on slow local models — the reason this is a v1 req).

## 7. Session translation cache (FR-008)

- **Decision**: In-memory dictionary keyed by `(DocumentId, PageIndex, TargetLanguage, ModelId)`,
  holding the produced translation; lifetime = the open-document session. Optional bounded size with
  LRU eviction to cap memory (Constitution IV).
- **Rationale**: Spec scopes reuse to "within the same session," so no persistence/DB is needed.
  Keying on target language + model means switching either correctly produces a fresh translation
  rather than serving a stale one.

## 8. Preferences & secure credential storage (FR-012)

- **Decision**: Non-secret preferences (target language, source overrides, selected model, toggle
  state) in a JSON file via `System.Text.Json` under the per-user app-data directory. Secrets (cloud
  API keys) go through an `ISecretStore` abstraction backed by OS-native secure storage: Windows
  DPAPI, macOS Keychain, Linux libsecret.
- **Rationale**: Satisfies "stores this configuration securely" cross-platform without inventing
  crypto. The abstraction keeps the platform specifics behind one interface (Constitution I) and lets
  tests use an in-memory fake.
- **Alternatives considered**: Plaintext JSON for keys (rejected — insecure); a full DB (overkill).

## 9. Concurrency / responsiveness (Constitution IV)

- **Decision**: All parsing and inference run on background threads via `async`/`await` and
  `Task.Run` where needed; the UI thread only binds observable state. Each translation carries a
  `CancellationToken` tied to page navigation.
- **Rationale**: Guarantees the < 100ms interaction budget and non-blocking page turns even when a
  local model takes many seconds.

## 10. Testing approach (Constitution II)

- **Decision**: xUnit throughout. A `FakeChatClient : IChatClient` returns canned/delayed responses
  for deterministic pipeline, cache, prefetch, and page-matching tests. View-model logic tested with
  `Avalonia.Headless.XUnit`. Document readers tested against small committed sample PDF/EPUB fixtures.
- **Rationale**: No real model or network → deterministic, fast, CI-friendly, and covers the critical
  paths the constitution names.
