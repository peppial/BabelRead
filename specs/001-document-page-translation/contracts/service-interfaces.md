# Phase 1 Contracts: Internal Service Interfaces

BabelRead is a desktop app with no public network API. Its "contracts" are the `BabelRead.Core`
service interfaces the UI (and tests) depend on. Signatures below are conceptual C#; exact shapes
are finalized in tasks. Every interface is designed to be implemented by both a real class and a
test fake (Constitution II).

## IDocumentReader

Opens a file and exposes its pages. One implementation per format.

```csharp
Task<Document> OpenAsync(string path, CancellationToken ct);
Task<Page> GetPageAsync(Document doc, int index, CancellationToken ct);
```

- Implementations: `PdfDocumentReader` (PdfPig), `EpubDocumentReader` (VersOne.Epub).
- Contract: throws a typed `DocumentOpenException` for corrupt / protected / unsupported files;
  returns a `Page` with `HasText == false` for pages lacking extractable text.

## IChatClientFactory  (Microsoft Agent Framework seam)

Builds the active `IChatClient` from a `ModelProfile`. The single point where providers are swapped.

```csharp
IChatClient Create(ModelProfile profile);   // resolves secrets via ISecretStore for Cloud kind
```

- Contract: `Cloud` profiles attach reader-supplied credentials; `Local` profiles target an
  OpenAI-compatible endpoint (Ollama/Foundry Local) with no key. Unknown/unsupported kinds throw.

## ITranslationService

Produces one page's translation. Provider-agnostic — depends only on `IChatClient`.

```csharp
Task<Translation> TranslateAsync(
    Page page, LanguageCode target, LanguageCode? sourceOverride, CancellationToken ct);
```

- Contract: returns `Completed` with text on success; `Failed` (with a reason) on model
  unavailable / timeout / network error; short-circuits to a "no translation needed" result when
  source == target; the returned `Translation.PageIndex` always equals `page.Index` (FR-010).

## ITranslationCache  (session-scoped)

```csharp
bool TryGet(TranslationKey key, out Translation value);
void Set(TranslationKey key, Translation value);
```

- `TranslationKey = (DocumentId, PageIndex, TargetLanguage, ModelId)`.
- Contract: reuse across on-demand and prefetch (FR-008); optional bounded size with LRU eviction
  (Constitution IV). Cleared when the document/session closes.

## IPrefetchCoordinator

Drives next-page background translation (FR-015/FR-016).

```csharp
void OnPageSettled(Document doc, int currentIndex, ReadingDirection dir);  // schedule next-page prefetch
void CancelPending();                                                       // on navigation change
```

- Contract: schedules exactly one page of lookahead in `dir`; writes results into
  `ITranslationCache`; **cancels** an in-flight prefetch when the reader moves elsewhere; must not
  delay an on-demand translation the reader is waiting on (yields priority).

## IPreferencesStore

```csharp
Task<ReaderPreferences> LoadAsync();
Task SaveAsync(ReaderPreferences prefs);
```

- Contract: JSON file under per-user app-data; never stores secrets. Missing file → sensible
  defaults (FR-012).

## ISecretStore

```csharp
Task<SecretRef> SetAsync(string name, string secret);
Task<string?> GetAsync(SecretRef reference);
```

- Contract: backed by OS-native secure storage (DPAPI / Keychain / libsecret). The rest of the app
  handles only `SecretRef`, never raw keys.
