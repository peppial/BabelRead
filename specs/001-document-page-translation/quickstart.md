# Quickstart & Validation Guide

How to build, run, and validate BabelRead end-to-end. Proves the spec's user stories and success
criteria against the real app. Implementation details live in `data-model.md`,
`contracts/service-interfaces.md`, and (later) `tasks.md`.

## Prerequisites

- **.NET 10 SDK** installed (`dotnet --version` ≥ 10.x).
- A local model runtime for the offline/no-key path: **Ollama** running with a translation-capable
  model pulled (e.g. `ollama pull <model>`), reachable at its default OpenAI-compatible endpoint.
- (Optional, cloud path) an OpenAI or Azure OpenAI **API key** to enter in Settings.
- Sample documents: one foreign-language **PDF** and one **EPUB** (small fixtures also live under
  `tests/` for automated tests).

## Build & test

```bash
dotnet restore
dotnet build -warnaserror        # Constitution I: warnings are errors
dotnet format --verify-no-changes
dotnet test                      # Constitution II: all unit/integration/UI tests green
```

Run the app:

```bash
dotnet run --project src/BabelRead.App
```

## Automated validation (runs in CI, no network/model)

The test suites assert the critical paths using a `FakeChatClient`:

- **Document parsing** — `PdfDocumentReader` / `EpubDocumentReader` open sample fixtures, paginate,
  and return empty-text pages for image-only pages (spec edge cases, SC-006).
- **Translation pipeline** — `ITranslationService` returns `Completed`/`Failed` correctly and always
  stamps `PageIndex` == source page (FR-004/FR-009/FR-010).
- **Cache** — revisiting a page reuses the cached translation; switching model or target language
  does not serve a stale entry (FR-008, SC-003).
- **Prefetch** — after a page settles, the next page is translated in the background and served from
  cache on the next turn; navigating away cancels the pending prefetch; on-demand is never starved
  (FR-015/FR-016, SC-008).

## Manual end-to-end scenarios

Each maps to a spec user story / success criterion.

1. **Read a translated page (US1, SC-001, SC-002)** — Launch → open the sample PDF → set a target
   language → confirm the current page's translation appears within ~10s and that the toggle flips
   between original and translation (FR-013). Repeat with the EPUB; turn pages and confirm the
   translation stays in sync (FR-005).
2. **Instant forward turns via prefetch (SC-008)** — With a **local (Ollama)** model selected, read
   forward through several pages. After the first page, each *next* page's translation should appear
   effectively immediately on turn (already prefetched) rather than showing a fresh multi-second wait.
3. **Switch model (US2, SC-004)** — In Settings, switch between a local model and a cloud model
   (enter a key for the cloud one) → translate a page → confirm the active model changed within ~30s
   with no app restart (FR-007). Confirm consumer subscriptions are not offered as options (FR-014).
4. **Choose languages (US3)** — Change the target language and confirm the same page re-translates
   into the new language; override a wrongly-detected source language and confirm subsequent
   translations honor it (FR-006).
5. **Failure & edge states (SC-005)** — Point the cloud model at a bad key / disconnect the network:
   confirm an actionable error with retry (FR-009), not a hang. Open an image-only page: confirm the
   "nothing to translate" state. Open a corrupt/protected file: confirm a clear error, no crash.
6. **Persistence (FR-012)** — Set language + model, close and relaunch: preferences persist; the
   cloud key is retrieved from OS secure storage, not a plaintext file.

## Expected outcomes

- All automated tests pass; build is warning-free and formatted.
- Manual scenarios 1–6 behave as described, meeting SC-001…SC-008.
- UI interactions stay responsive (< 100ms) while translation/parsing run in the background, and the
  local-model reading experience is smooth because of next-page prefetch.
