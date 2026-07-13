# Feature Specification: On-the-Fly Document Page Translation

**Feature Branch**: `001-document-page-translation`

**Created**: 2026-07-12

**Status**: Draft

**Input**: User description: "A desktop application, with dotnet 10, that can open pdf or epub and on the fly generate a ai generated translation of the current page (by page), based on MS Agent Framework so the model can be changed by the user"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Read a translated page while browsing a document (Priority: P1)

A reader opens a PDF or EPUB file in the application and navigates through it page by page.
For the page currently in view, the application produces an AI-generated translation into the
reader's chosen target language so the reader can understand content written in a language they
do not read fluently.

**Why this priority**: This is the core value of the product — turning a document the reader
cannot read into one they can, without leaving the reading flow. Without it there is no product.

**Independent Test**: Open a sample foreign-language PDF and EPUB, select a target language, and
confirm that the translated text of the currently viewed page appears and updates as the reader
moves to the next/previous page.

**Acceptance Scenarios**:

1. **Given** a PDF written in a language other than the target language is open, **When** the
   reader views a page, **Then** a translation of that page's readable text is presented in the
   target language.
2. **Given** an EPUB is open, **When** the reader navigates to a different page, **Then** the
   translation updates to reflect the newly viewed page.
3. **Given** a page has already been translated, **When** the reader returns to it, **Then** the
   previously produced translation is shown without re-translating from scratch.

---

### User Story 2 - Choose and switch the AI model used for translation (Priority: P2)

A reader opens application settings and selects which AI model performs the translation, and can
switch to a different model later. Subsequent page translations use the selected model.

**Why this priority**: User-selectable models are an explicit requirement and the product's key
differentiator — it lets readers trade off quality, cost, speed, and privacy. It builds on P1 but
is not required to demonstrate the core reading experience.

**Independent Test**: With a document open, change the configured model in settings, translate a
page, and confirm the translation is produced by the newly selected model (verifiable via a model
indicator and by differing output).

**Acceptance Scenarios**:

1. **Given** more than one model is available, **When** the reader selects a different model in
   settings, **Then** later page translations are produced by the newly selected model.
2. **Given** a selected model is unavailable or misconfigured, **When** the reader triggers a
   translation, **Then** the application reports the problem clearly and does not silently fail.

---

### User Story 3 - Choose the target (and source) language (Priority: P2)

A reader selects the language they want pages translated into. The application detects the source
language automatically, and the reader may override it when detection is wrong.

**Why this priority**: Readers need results in their own language; a fixed language pair would make
the product unusable for most people. It complements P1.

**Independent Test**: Open a document, set the target language to two different languages in turn,
and confirm the same page is translated into each selected language.

**Acceptance Scenarios**:

1. **Given** a document is open, **When** the reader selects a target language, **Then** page
   translations are produced in that language.
2. **Given** automatic source-language detection is wrong, **When** the reader overrides the source
   language, **Then** subsequent translations use the specified source language.

---

### Edge Cases

- What happens when a page contains no extractable text (e.g., a scanned image-only PDF page or a
  full-page illustration)? The application MUST clearly indicate that there is nothing to translate
  rather than appearing stuck.
- How does the system handle a page whose source language is the same as the target language? It
  MUST indicate no translation is needed rather than producing a meaningless round-trip.
- What happens when the model call fails, times out, or the network is unavailable? The reader MUST
  see an actionable error and be able to retry.
- How does the system handle very large pages or long documents so that translation of the current
  page remains responsive?
- What happens when the reader navigates to a new page before the current translation finishes? The
  stale translation MUST NOT overwrite the translation for the page now in view.
- What happens when an opened file is corrupt, password-protected, or in an unsupported format?
- What happens when the reader jumps around (skips pages, jumps backward) so the prefetched next
  page is not the page they land on? The prefetch is simply unused for that turn; the landed-on
  page is translated on demand, and prefetching resumes from the new position.
- What happens when the reader turns the page before the prefetch of that next page has finished?
  The reader sees normal in-progress feedback for the remainder, not a hang, and no page is shown
  a translation that belongs to a different page (FR-010).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Application MUST allow the reader to open and view local PDF and EPUB documents.
- **FR-002**: Application MUST let the reader navigate a document one page at a time (next/previous
  and jump to a page).
- **FR-003**: Application MUST identify the readable text of the page currently in view.
- **FR-004**: Application MUST produce an AI-generated translation of the current page's text into
  the reader's selected target language.
- **FR-005**: Application MUST present the translation for the current page and keep it in sync as
  the reader navigates between pages.
- **FR-006**: Application MUST let the reader select a target language and MUST automatically detect
  the source language, allowing the reader to override the detected source language.
- **FR-007**: Application MUST let the reader choose which AI model performs translation and switch
  models at any time, with later translations using the newly selected model.
- **FR-008**: Application MUST avoid redundant work by reusing an already-produced translation
  (whether produced on demand or via prefetch) for a page the reader revisits within the same
  session.
- **FR-009**: Application MUST clearly report translation failures (model unavailable, timeout,
  network error, unsupported/empty page) and allow the reader to retry.
- **FR-010**: Application MUST ensure that a translation belongs to the page it was requested for,
  so navigating away does not display a mismatched translation.
- **FR-011**: Application MUST indicate translation progress so the reader knows a translation is in
  progress, complete, or failed.
- **FR-012**: Application MUST persist the reader's model choice, language preferences, and any model
  configuration between sessions.
- **FR-013**: Application MUST present the translation in the same reading pane as the original page,
  with a single control that toggles the pane between the original page and its translation, so the
  reader sees one at a time and can flip between them.
- **FR-014**: Application MUST let the reader configure both cloud-hosted models (using
  reader-supplied provider credentials) and models that run locally on the reader's machine, and
  MUST let the reader choose between them when selecting the active model. Consumer chat
  subscriptions (e.g. GitHub Copilot, claude.ai Pro/Max) are NOT supported as translation
  backends, because they are not usable as an application model endpoint; the "no account,
  no per-use cost" experience is served by locally-run models.
- **FR-015**: To keep local models (which are noticeably slower than cloud models) usable, the
  application MUST prefetch the translation of the next page (in the reader's current direction of
  travel) in the background while the reader is reading the current page, so that moving to the
  next page shows its translation immediately in the common case rather than starting a
  translation from scratch.
- **FR-016**: Prefetching MUST NOT degrade the current page: an in-progress prefetch MUST yield to
  an on-demand translation the reader is actively waiting for, and a prefetched result MUST be
  reused (not re-translated) when the reader arrives at that page, subject to the page-matching
  rule in FR-010.

### Key Entities *(include if feature involves data)*

- **Document**: An opened PDF or EPUB file; has a title/source path, an ordered set of pages, and a
  detected source language.
- **Page**: A single unit of the document as navigated by the reader; has extractable readable text
  and a position within the document.
- **Translation**: AI-generated target-language text for a specific page, produced by a specific
  model into a specific target language; associated with its originating page.
- **Model Configuration**: The reader's selected AI model and the settings needed to use it; the
  active configuration determines how translations are produced, including whether the model is
  cloud-hosted (reader-supplied credentials) or local. Consumer chat subscriptions are not a valid
  configuration.
- **Reader Preferences**: Persisted target language, source-language overrides, selected model
  (cloud or local), and the original/translation toggle state.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A reader can go from launching the application to reading a translated page of a
  foreign-language document in under 2 minutes without external instructions.
- **SC-002**: For a typical text page, the translation of the current page is presented within 10
  seconds of the page coming into view (or of the reader requesting it).
- **SC-003**: Navigating to a previously translated page within a session shows its translation in
  under 1 second (no re-translation).
- **SC-008**: During continuous forward reading with a local model, at least 80% of page turns
  show the next page's translation immediately (already prefetched) rather than making the reader
  wait for a fresh translation.
- **SC-004**: A reader can change the active AI model and confirm the change took effect within 30
  seconds, without restarting the application.
- **SC-005**: 100% of translation failures result in a clear, actionable message; none leave the
  reader with an indefinite or ambiguous state.
- **SC-006**: The application successfully opens and paginates at least 95% of a representative set
  of valid PDF and EPUB files.
- **SC-007**: In usability testing, at least 90% of readers successfully translate and read a page
  on their first attempt without assistance.

## Assumptions

- Target platform is a desktop application; mobile and web delivery are out of scope for v1.
- The reader supplies documents from local storage; content acquisition, purchasing, or a library
  catalog is out of scope.
- Translation is performed page-by-page for the page in view rather than translating the entire
  document up front, consistent with the "on the fly, by page" intent. Prefetching the immediate
  next page (FR-015) is a bounded lookahead of this, not whole-document pre-translation.
- Local models are assumed to be materially slower than cloud models, which is the reason
  next-page prefetching is a v1 requirement rather than an optimization deferred to later.
- Consumer chat subscriptions (GitHub Copilot, claude.ai Pro/Max, and similar) are explicitly out
  of scope as translation backends: they are not usable as an application model endpoint. Keyless,
  no-per-use-cost operation is provided by locally-run models; cloud use requires reader-supplied
  provider API credentials.
- Source language is auto-detected by default, with a reader override available; the reader
  explicitly selects the target language.
- The reader is responsible for any credentials, accounts, or resources their chosen AI model
  requires; the application stores this configuration securely. Both cloud-hosted models (via
  reader-supplied credentials) and locally-run models are supported in v1.
- Local models require the reader's machine to have sufficient resources; provisioning or bundling
  local model runtimes/weights is the reader's responsibility unless decided otherwise at planning.
- Layout-faithful re-rendering of translated text back into the original page's exact visual layout
  is out of scope for v1; presenting readable translated text of the page is sufficient.
- Editing, exporting, or saving translations as a new document is out of scope for v1.
