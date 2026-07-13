# Phase 1 Data Model: On-the-Fly Document Page Translation

Domain types live in `BabelRead.Core/Domain`. All are UI-agnostic. Fields are conceptual; exact C#
shapes (records vs classes, immutability) are an implementation detail for tasks.

## Document

Represents an opened PDF or EPUB.

| Field | Type | Notes |
|---|---|---|
| `Id` | DocumentId (opaque) | Stable for the open session; part of cache keys. |
| `Title` | string | From metadata or file name. |
| `SourcePath` | string | Local file path. |
| `Format` | enum { Pdf, Epub } | Selects the `IDocumentReader`. |
| `PageCount` | int | Total navigable pages. |
| `DetectedSourceLanguage` | LanguageCode? | Auto-detected; may be null until known. |

Rules: opening a corrupt / password-protected / unsupported file fails with a typed error surfaced
to the reader (spec edge case), not a crash.

## Page

A single navigable unit (PDF page, or an EPUB spine reading-order unit).

| Field | Type | Notes |
|---|---|---|
| `Index` | int (0-based) | Position within the document. |
| `ExtractableText` | string | May be empty for image-only/illustration pages. |
| `HasText` | bool | False → "nothing to translate" empty state. |

## Translation

AI-generated target-language text for one specific page.

| Field | Type | Notes |
|---|---|---|
| `PageIndex` | int | Origin page (enforces FR-010 page-matching). |
| `TargetLanguage` | LanguageCode | Language translated into. |
| `SourceLanguage` | LanguageCode | Detected or reader-overridden. |
| `ModelId` | string | Which model produced it. |
| `Text` | string | The translated text. |
| `Origin` | enum { OnDemand, Prefetch } | Provenance (FR-008/FR-015). |
| `Status` | enum { Pending, Completed, Failed } | Drives progress UI (FR-011). |

Rules: a Translation is only displayed for the page whose `PageIndex` matches the page in view
(FR-010). Cache key = `(DocumentId, PageIndex, TargetLanguage, ModelId)`.

## ModelConfiguration / ModelProfile

The reader's selected model and how to reach it. A valid configuration is cloud or local; consumer
chat subscriptions are not valid.

| Field | Type | Notes |
|---|---|---|
| `ProfileId` | string | User-facing identifier for the saved profile. |
| `Kind` | enum { Cloud, Local } | |
| `ModelId` | string | e.g. provider model name or local model tag. |
| `Endpoint` | Uri? | Base URL (local endpoint or Azure resource). |
| `CredentialRef` | SecretRef? | Reference into `ISecretStore` (cloud only); never the raw key. |

Rules: switching the active profile causes subsequent translations to use the new model (FR-007) and
does not serve cache entries keyed to a different `ModelId`.

## ReaderPreferences

Persisted settings (JSON; secrets excluded).

| Field | Type | Notes |
|---|---|---|
| `TargetLanguage` | LanguageCode | Reader-selected (FR-006). |
| `SourceLanguageOverrides` | map<DocumentId, LanguageCode> | Optional per-document override. |
| `ActiveModelProfileId` | string | Currently selected model (FR-012). |
| `PaneToggleDefault` | enum { Original, Translation } | Default view for the toggle (FR-013). |

## State: per-page translation lifecycle

```
                 navigate to / request page
                          │
                          ▼
     ┌───────────────► Pending ──────────────┐
     │ (prefetch or       │ model call        │ model error / empty page
     │  on-demand)        ▼                    ▼
     │                Completed             Failed ──(retry, FR-009)──┐
     │                    │                                          │
     └────────────────────┴──────────  reused from cache  ◄──────────┘
```

- A `Pending` prefetch for a page the reader navigates away from is **cancelled** (FR-016).
- `Completed` results (either origin) are cached and reused on revisit (FR-008).
- `Failed` exposes an actionable retry; it never leaves an indefinite state (SC-005).
