---
description: |
  Attributes each pull request's changed lines to agent or human and makes every
  AI-generated line visible in colour. Separates recorded provenance (commit trailers,
  bot authors, git AI notes) from inferred style signals, paints each hunk at its
  confidence tier, and surfaces the result four ways: a colour-coded label on the PR,
  severity-coloured annotations on the exact diff lines, a sticky comment listing the
  painted lines, and a downloadable full-diff report where every line is tinted.

on:
  # Attribution is read-only analysis, so it must run for every contributor.
  # The default (write access only) would skip exactly the outside-contributor
  # PRs this is most useful on. Writes still go through safe outputs.
  roles: all
  pull_request:
    types: [opened, synchronize, reopened]
    forks: "*"
  schedule: daily
  workflow_dispatch:
  permissions:
    issues: write
  steps:
    - name: Bootstrap tier labels
      env:
        GH_TOKEN: ${{ github.token }}
      run: |
        # add-labels cannot create labels and cannot set colours. Do it here.
        # Never gate activation on this - a repo may withhold issues:write.
        create() { gh label create "$1" --color "$2" --description "$3" --force || true; }
        create "ai:none"     "0E8A16" "AI attribution: no AI signal in the changed lines"
        create "ai:low"      "FBCA04" "AI attribution: weak AI signal (20-44%)"
        create "ai:medium"   "FB8C00" "AI attribution: moderate AI signal (45-74%)"
        create "ai:high"     "D73A4A" "AI attribution: strong AI signal (75%+)"
        create "ai:recorded" "6F42C1" "AI attribution: agent authorship on record"
        exit 0

permissions:
  contents: read
  pull-requests: read
  # The agent job stays read-only. The code-scanning write happens in the
  # create-code-scanning-alert safe-output job, under its own scoped token.
  security-events: read

network: defaults

concurrency:
  group: "gh-aw-ai-attribution-${{ github.event.pull_request.number || github.run_id }}"
  cancel-in-progress: false

steps:
  - name: Checkout repository
    uses: actions/checkout@v7
    with:
      fetch-depth: 0
      persist-credentials: false

  - name: Collect recorded provenance and the diff
    if: github.event_name == 'pull_request'
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ github.event.pull_request.number }}
      BASE_SHA: ${{ github.event.pull_request.base.sha }}
      HEAD_SHA: ${{ github.event.pull_request.head.sha }}
    run: |
      mkdir -p /tmp/gh-aw/agent
      git diff "$BASE_SHA...$HEAD_SHA" > /tmp/gh-aw/agent/pr.diff || true
      bash "$GITHUB_WORKSPACE/scripts/collect-recorded.sh" \
        "$BASE_SHA" "$HEAD_SHA" "$PR_NUMBER" "$GITHUB_REPOSITORY" \
        > /tmp/gh-aw/agent/recorded-evidence.json 2>/dev/null || echo '{}' > /tmp/gh-aw/agent/recorded-evidence.json
      wc -l /tmp/gh-aw/agent/pr.diff
      cat /tmp/gh-aw/agent/recorded-evidence.json

post-steps:
  - name: Render the full painted diff
    if: always()
    run: |
      if [ ! -s /tmp/gh-aw/agent/findings.json ] || [ ! -s /tmp/gh-aw/agent/pr.diff ]; then
        echo "no findings or no diff - nothing to render"
        exit 0
      fi
      # Fails loudly if the rendered body is not the diff character for character.
      python3 "$GITHUB_WORKSPACE/scripts/build_report.py" \
        --diff /tmp/gh-aw/agent/pr.diff \
        --findings /tmp/gh-aw/agent/findings.json \
        --out /tmp/gh-aw/agent/ai-attribution-report.html

  - name: Upload the report
    if: always()
    uses: actions/upload-artifact@v7
    with:
      name: ai-attribution-report
      path: /tmp/gh-aw/agent/ai-attribution-report.html
      retention-days: 30
      if-no-files-found: ignore

safe-outputs:
  create-code-scanning-alert:
    driver: "AI Attribution"
    max: 50
  add-labels:
    allowed: ["ai:none", "ai:low", "ai:medium", "ai:high", "ai:recorded"]
    target: "*"
    max: 2
  remove-labels:
    allowed: ["ai:none", "ai:low", "ai:medium", "ai:high", "ai:recorded"]
    target: "*"
    max: 5
  add-comment:
    target: "*"
    hide-older-comments: true
  messages:
    footer: "> 🎨 *Attributed by [{workflow_name}]({run_url})  -  inferred signals are not proof of authorship*"

tools:
  bash: true
  cache-memory: true
  github:
    toolsets: [pull_requests, repos]
    read-only: true
    min-integrity: none # reads any PR, including from forks; writes go through safe outputs only

imports:
  - shared/ai-attribution-rules.md

timeout-minutes: 20
---

# AI Attribution 🎨

You attribute the changed lines of a pull request to agent or human, and make every line
that reads as AI-generated **visible in colour**. Apply the imported attribution rules
exactly  -  their IDs, weights, scopes and false-positive notes are the specification.

## What you must never do

- **Never merge the recorded and inferred lanes into one number.** They answer different
  questions and fail in different ways.
- **Never paint a line whose hunk cites no rule.** If a hunk feels generated but matches no
  rule, that is a rule nobody has written yet  -  list it as a *suggested rule*, not as paint.
- **Never call an empty recorded lane "human-written."** It means *no provenance recorded*.
- **Never claim certainty.** Cap every score at 0.95.

## Mode

- `pull_request` event → attribute the triggering PR. Full fidelity: the repository is
  checked out with complete history, so E1, E2 and E3 are blame-level.
- `schedule` or `workflow_dispatch` → **backfill**. Search open PRs carrying no `ai:*`
  label, take at most **5**, oldest first, and attribute each. Lane A is commit-level there
  rather than blame-level, because there is no local checkout of those branches  -  say so in
  the comment rather than degrading silently.

## Step 1  -  Read the recorded evidence

For the triggering PR, read `/tmp/gh-aw/agent/recorded-evidence.json` and
`/tmp/gh-aw/agent/pr.diff`, both written by a deterministic step before you ran.

For a backfill PR, gather the equivalent yourself:

```bash
gh pr view <n> --repo "$GITHUB_REPOSITORY" --json commits,author,body,title,headRefName
gh pr diff <n> --repo "$GITHUB_REPOSITORY" > /tmp/gh-aw/agent/pr.diff
```

Apply E1 (agent trailers), E3 (bot authors) and E4 (declaration in body, title or branch) to
what you get back. E2 is unavailable in backfill mode; note that.

**These are facts.** Do not re-score them, do not second-guess them, and do not let an
inferred score contradict them. Note E1's own caveat: a trailer covers the *commit*, not the
line, so report trailer-only evidence as commit-level.

## Step 2  -  Establish the baseline before scoring anything

This is the load-bearing step. Skip it and every well-written codebase reads as AI.

For each changed file, read the **unchanged** code around the hunks with the GitHub tools and
record: comment density and whether comments explain *why* or restate *what*; identifier
verbosity and casing; error-handling posture; function length and nesting; test naming and
assertion style. For a new file, use the nearest sibling in the same directory.

Check at the same time whether the repo runs a **formatter or linter in CI**. If it reformats
on merge, H4 can never fire and its absence is not evidence.

Identify generated and vendored files  -  lockfiles, migrations, `vendor/`, `node_modules/`,
minified assets, `*.lock.yml` compiled from an agentic workflow source. **Exclude them from
scoring entirely** and record their line counts for the report.

If no baseline is obtainable, say so and drop every inferred finding by one tier.

## Step 3  -  Segment into hunks and score

Score each **hunk**, never a line on its own. Then paint every line in the hunk at the
hunk's tier. Follow the scoring formula, the tier table and the PR-level log-odds
aggregation in the imported rules exactly.

## Step 4  -  Write `findings.json`

Write `/tmp/gh-aw/agent/findings.json`. A deterministic post-step renders it into the full
painted diff and fails the run if the rendering is not the diff character for character  -
so you cannot tidy, elide or summarise the code being shown. Typos and ragged formatting must
survive; they are H1 and H4 evidence.

```json
{
  "title": "owner/repo #123  -  Refactor auth and add rate limiting",
  "verdict": "No provenance on record; style diverges sharply from the file baseline",
  "meta": "3 files · +310 / -42 · 1 commit",
  "recorded": "0 of 310",
  "inferred": 82,
  "excluded": ["package-lock.json  -  4180 lines, generated"],
  "findings": [
    {
      "lines": [12, 30],
      "tier": "t3",
      "score": 0.85,
      "rules": "C2, D1, N1",
      "where": "src/auth.ts:41-58",
      "why": "Full docblock restating the signature on a file whose other methods carry none; guards on values that cannot be null here; identifiers markedly more verbose than the file's own."
    }
  ]
}
```

Field rules, all mandatory:

- `lines` is a **0-based inclusive index pair into the raw diff file**, not file line numbers.
  Count lines in `pr.diff` to get them right. A finding out of range aborts the render.
- `where` is `path:startline-endline` in the **file**, and must be accurate  -  the code
  scanning alerts in Step 5 are derived from it.
- `tier` is `t3` (high, >= 0.75), `t2` (medium, 0.45-0.74), `t1` (low, 0.20-0.44), or `rec`
  for recorded evidence. Hunks scoring below 0.20 get no entry at all.
- `rules` is the comma-separated rule IDs that fired. Never empty.
- `recorded` is `"N of M added lines"`  -  a count, never a percentage.
- `inferred` is an integer percentage, or the string `"n/a"` when there are zero scoreable
  added lines.

## Step 5  -  Paint the lines inside the PR

For each painted hunk, emit one `create_code_scanning_alert`. GitHub renders these as
severity-coloured annotations on the exact lines in the Files-changed tab.

```json
{
  "rule_id": "ai-attribution/high",
  "message": "Painted high (0.85)  -  C2, D1, N1",
  "severity": "error",
  "file_path": "src/auth.ts",
  "start_line": 41,
  "description": "Full docblock restating the signature on a file whose other methods carry none; guard density above this file's baseline.\n\nRules: C2 (+0.40), C2 measures divergence from the file's own comment style; D1 (+0.40); N1 (+0.35).\n\nThis is an inferred style signal, not proof of authorship. Thorough, uniform, well-commented code is often just a careful developer."
}
```

Severity mapping: `t3` → `error`, `t2` → `warning`, `t1` → `note`, `rec` → `error` with
`rule_id: ai-attribution/recorded`.

The cap is 50. If more hunks are painted, emit the 50 highest-scoring and record how many
were omitted  -  Step 6 must state the number and point at the artifact, which has all of them.

If the alert upload fails (fork PRs restrict code scanning), do not fail the run. Say so in
the comment and rely on the other three surfaces.

## Step 6  -  Post the sticky comment

One comment, replacing the previous one. This is the surface that needs no extra permission
and no download, so it must stand on its own.

````markdown
## 🎨 AI attribution

| | |
|---|---|
| **Recorded** | 0 of 310 added lines  -  *no provenance recorded* |
| **Inferred** | **82%**  -  inference from style, not a record of authorship |

Painted 218 of 264 scoreable added lines. [Full painted diff ↓](RUN_URL) · 34 hunks
annotated inline, 0 omitted.

| File | Added | Painted | Heat | Rules |
|---|---:|---:|---|---|
| `src/auth.ts` | 142 | 131 | 🟥🟥🟥🟧🟨 | C2 D1 N1 N4 |
| `src/limiter.ts` | 96 | 61 | 🟧🟧🟨 | N2 I4 |
| `tests/auth.spec.ts` | 72 | 26 | 🟥🟥🟥 | T1 T2 |

Excluded as generated: `package-lock.json` (4180 lines).

<details><summary><b>🟥 src/auth.ts  -  131 painted lines</b></summary>

```
🟥 41  export function validateToken(token: string): boolean {
🟥 42    if (!token || typeof token !== 'string') {
🟧 51    const decoded = decode(token) ?? null;
```

</details>

---

**How to read this.** Scores are computed per hunk against each file's own conventions, then
every line in the hunk is painted at the hunk's tier  -  a single line carries too few tokens
to attribute on its own. Thorough, uniform, well-commented code is often just a careful
developer, a house style, or a formatter in CI. Treat this as a pointer for where to look,
never as a verdict about a person.
````

Rules for the comment:

- Two numbers in the header, always. Never add or average them.
- One `<details>` block per file containing **the painted lines themselves**, each prefixed
  🟥 / 🟧 / 🟨 / 🟪 and its file line number. This is what makes the lines visible when code
  scanning is unavailable.
- Comments cap at 65535 characters. Emit files in descending painted-line order and truncate
  the tail, stating exactly how many lines were dropped and pointing at the artifact.
- Link the artifact as `${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}`.
- Keep the "How to read this" block. It is not decoration  -  it is what stops the tool being
  used against people.
- If `inferred` is `n/a`, say so plainly: a pure-deletion or lockfile-only PR carries no
  authorship signal, because removing code has no style.

## Step 7  -  Label the PR

Set exactly one tier label, plus `ai:recorded` when lane A fired.

1. `remove_labels` for every `ai:` tier label currently on the PR that is not the one you are
   about to set. Re-runs on `synchronize` must replace, not accumulate.
2. `add_labels` with the tier label: `ai:none` (< 20%), `ai:low`, `ai:medium`, `ai:high`.
3. `add_labels` with `ai:recorded` when any E rule fired. It coexists with a tier.
4. When `inferred` is `n/a`, set no tier label at all.

If a label is missing from the repository, the bootstrap step could not run  -  note it in the
comment and point the maintainer at the `gh label create` block in the docs.

## Deduplication

Use `/tmp/gh-aw/cache-memory/` to record `{pr, head_sha, run_id, timestamp}` per attributed
PR. If this exact head SHA was attributed within the last 10 minutes, stop immediately  -  a
duplicate invocation.

## Finishing

**If nothing needed doing**  -  the PR is empty, every file was excluded, or it was already
attributed at this SHA  -  you **MUST** call the `noop` safe-output tool. Failing to call any
safe-output tool is the most common cause of safe-output workflow failures.

```json
{"noop": {"message": "No action needed: [what was analysed and why nothing was emitted]"}}
```
