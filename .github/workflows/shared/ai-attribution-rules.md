---
# AI attribution rules  -  imported by ai-attribution.md
# Retune weights here without touching the workflow. After editing run 'gh aw compile'.
---

## Attribution Rules

Categories: **E** recorded evidence · **C** comments · **N** naming & shape ·
**D** defensive scaffolding · **I** idiom divergence · **T** tests ·
**V** PR metadata · **H** human tells (negative weight).

Every rule declares its own **Scope**  -  `hunk`, `file`, or `pr`. Do not infer scope from
the category.

- **Scope: hunk**  -  the weight enters that hunk's score; its lines get painted.
- **Scope: file**  -  applies to every hunk in that file.
- **Scope: pr**  -  enters the PR-level adjustment only; nothing is painted.

### The two lanes

**E rules are facts. Everything else is inference.** Never combine them into one number,
and never let an inferred score contradict a recorded one.

| | E rules | C/N/D/I/T/V/H |
|---|---|---|
| Answers | *who wrote this* | *what does this look like* |
| Fails by | being absent | being wrong |
| Good for | audit, compliance | directing review attention |

**Absence of an E rule means "no record", never "human-written."** Report an empty recorded
lane as `no provenance recorded`. It is not a finding.

### Baseline first  -  read before you score

C, N, D, I and T rules score **divergence from the file's own conventions**, not absolute
style. Before scoring any hunk, read the *unchanged* code in the same file (and a sibling
file if the changed file is new) and establish:

- comment density  -  comments per 10 lines, and whether they explain *why* or restate *what*
- naming  -  length and verbosity of identifiers, casing, abbreviation habits
- error handling  -  does this codebase guard, throw, or let it fail?
- structure  -  function length, nesting depth, early-return vs nested-if
- test shape  -  naming pattern, assertions per test, use of mocks

A hunk written in the file's own idiom scores low even if that idiom is verbose and heavily
commented. A hunk that is *cleaner and more thorough than its surroundings* is the signal.
Without a baseline, every well-written codebase reads as AI and the tool is worse than nothing.

Check for a **formatter or linter in CI** at the same time. If the pipeline reformats on
merge, H4 can never fire and its absence is not evidence.

**Never score generated or vendored files**  -  migrations, lockfiles, `vendor/`,
`node_modules/`, `*.g.*`, minified assets, scaffolder output, `*.lock.yml` compiled from an
agentic workflow source. They are machine-written and always were. List them as excluded,
with their line counts.

---

## E  -  Recorded evidence (facts, not weights)

These bypass scoring. A hunk matching any E rule is marked **Recorded** and needs no score.
They are collected deterministically by `scripts/collect-recorded.sh`, not by judgement.

### E1  -  Commit trailer
**Look for:** the commit that introduced the line carrying `Co-Authored-By: Claude`,
`Co-authored-by: Copilot`, `Co-authored-by: Cursor`, or similar.
**Verdict:** agent-authored, recorded.
**False positives:** a human may add a trailer to a commit they largely wrote themselves; the
trailer covers the commit, not the line. Report as *commit-level*, not line-level.
**Scope:** hunk

### E2  -  Git AI note
**Look for:** `refs/notes/ai` on the introducing commit.
**Verdict:** agent-authored, recorded, line-level and exact.
**False positives:** none  -  this is the only true line-level record. Absent on every commit
predating adoption, and unavailable in backfill mode where there is no local git repo.
**Scope:** hunk

### E3  -  Bot author
**Look for:** commit author or committer is a bot  -  `Copilot Autofix`, `copilot-swe-agent`,
`dependabot[bot]`, `github-actions[bot]`, `renovate[bot]`, any `*[bot]@users.noreply.github.com`.
**Verdict:** machine-authored, recorded.
**False positives:** dependabot and renovate write config and lockfile changes, which are
machine-generated but not *LLM*-generated. Distinguish the two in the report.
**Scope:** hunk

### E4  -  Declared in the PR
**Look for:** the PR body, title, or branch name stating agent involvement  -  "generated with",
"via Claude Code", `copilot/`, `cursor/`, a Claude Code PR footer.
**Verdict:** agent involvement declared, scope unclear.
**False positives:** a declaration covers the PR, not any particular line. Never paint lines
from E4 alone; show it in the header.
**Scope:** pr

---

## C  -  Comments

### C1  -  Comment restates the code
**Look for:** a comment carrying no information the line below doesn't already carry  - 
`// Increment the counter`, `// Loop through items`, `// Return the result`.
**Weight:** +0.45
**False positives:** teaching repos, and codebases with a genuine house rule requiring them  - 
check the baseline before firing.
**Scope:** hunk

### C2  -  Docstring on everything
**Look for:** every method carrying a full docblock including trivial getters and setters,
with `@param` lines that only restate the type signature, in a file whose existing methods
have none.
**Weight:** +0.40
**False positives:** codebases with a documented public API, or a linter rule requiring
docblocks. If the unchanged methods in the same file have them, this is house style  -  do not fire.
**Scope:** hunk

### C3  -  Comment density above baseline
**Look for:** the hunk's comments-per-line materially exceeding the file's established rate.
**Weight:** +0.30
**False positives:** genuinely subtle code deserves more comment than its neighbours. Read
whether the comments explain *why*; if they do, do not fire.
**Scope:** hunk

### C4  -  No loose ends
**Look for:** across a substantial PR, zero `TODO`, `FIXME`, `XXX`, `HACK`, no commented-out
code, no debug statements, no scratch variable left behind.
**Weight:** +0.25
**False positives:** a disciplined author, a pre-commit hook, or a CI lint rule that bans
them. Weak alone; meaningful only beside C1/C2.
**Scope:** pr

### C5  -  Section-banner comments
**Look for:** decorative separators introducing obvious blocks  -  `// ===== Helpers =====`,
`# --- Validation ---`, `// Main logic starts here`  -  where the file has none.
**Weight:** +0.25
**False positives:** a house style that uses banners; check the unchanged file.
**Scope:** hunk

---

## N  -  Naming & shape

### N1  -  Verbose identifiers against local convention
**Look for:** `calculateTotalPriceWithDiscountApplied` in a file of `calcTotal` and `sum`;
loop variables named `currentIterationIndex` where the file uses `i`.
**Weight:** +0.35
**False positives:** a deliberate move away from bad legacy naming; a new module with no
established convention.
**Scope:** hunk

### N2  -  Uniform function shape
**Look for:** every added function the same length and the same internal shape  -  guard
clauses, then body, then a single return  -  with no outliers.
**Weight:** +0.30
**False positives:** genuinely repetitive work (CRUD handlers, mappers, adapters) is uniform
by nature.
**Scope:** file

### N3  -  Exhaustive enumeration
**Look for:** every branch of an enum, every status code, every possible case handled  - 
including ones the feature cannot reach  -  where the codebase handles only the live paths.
**Weight:** +0.30
**False positives:** a language or linter requiring exhaustive matches (Rust, TS strict
switch). Do not fire where the compiler demands it.
**Scope:** hunk

### N4  -  Symmetric abstraction
**Look for:** an interface, an abstract base, a factory, and a concrete implementation all
arriving together for a single call site.
**Weight:** +0.35
**False positives:** a planned refactor; a genuine plugin point with a second implementation
already in flight.
**Scope:** file

---

## D  -  Defensive scaffolding

### D1  -  Guard density above baseline
**Look for:** null checks, `isset`, `empty`, `?->`, type assertions at a rate the file does
not otherwise use  -  especially guards on values that cannot be null at that point.
**Weight:** +0.40
**False positives:** hardening work, or a bug fix whose whole point is the missing guard.
Read the PR title before firing.
**Scope:** hunk

### D2  -  Try/catch on a happy-path file
**Look for:** wrapped exception handling introduced into a file that otherwise lets
exceptions propagate, particularly `catch` blocks that log and rethrow unchanged.
**Weight:** +0.40
**False positives:** the PR is explicitly about error handling.
**Scope:** hunk

### D3  -  Validation the codebase does not do
**Look for:** input validation, range checks, or sanitisation on an internal call path where
every comparable function trusts its caller.
**Weight:** +0.30
**False positives:** a public API boundary, where validating is correct.
**Scope:** hunk

### D4  -  Defensive default
**Look for:** `?? []`, `?: null`, `default:` returning a benign value, swallowing the case
where the codebase would fail loudly.
**Weight:** +0.25
**False positives:** a legitimate fix for a crash. Check the linked issue.
**Scope:** hunk

---

## I  -  Idiom divergence

### I1  -  Unused-elsewhere API
**Look for:** a language feature, standard-library call, or framework method appearing
nowhere else in the repo, where an established local equivalent exists.
**Weight:** +0.40
**False positives:** the first legitimate use of a newly available API after a version bump.
**Scope:** hunk

### I2  -  Modern idiom in legacy code
**Look for:** arrow functions, named arguments, match expressions, enums, or generics dropped
into a file written in a markedly older style.
**Weight:** +0.35
**False positives:** an intentional, announced modernisation; new files in an old repo.
**Scope:** hunk

### I3  -  Import style break
**Look for:** an `import`/`use` block sorted, grouped or fully-qualified differently from
every other file in the module.
**Weight:** +0.25
**False positives:** an IDE's organise-imports on save. Weak alone.
**Scope:** file

### I4  -  Reimplemented helper
**Look for:** an inline implementation of something the repo already provides  -  a local
`array_flatten`, a hand-rolled date format, a private `slugify` beside an existing utility.
**Weight:** +0.35
**False positives:** the existing helper is genuinely unsuitable, or lives in a module this
one cannot depend on.
**Scope:** hunk

---

## T  -  Tests

### T1  -  Exhaustive edge-case naming
**Look for:** `testReturnsNullWhenInputIsEmpty`, `testThrowsExceptionWhenUserIsNotFound`  -  a
full matrix of cases, uniformly named, where the suite's existing tests are named loosely.
**Weight:** +0.40
**False positives:** a codebase with a documented test-naming standard.
**Scope:** file

### T2  -  AAA comments
**Look for:** literal `// Arrange`, `// Act`, `// Assert` (or Given/When/Then) comments in
every test.
**Weight:** +0.40
**False positives:** a team convention  -  check the untouched tests in the same suite.
**Scope:** hunk

### T3  -  Mock everything
**Look for:** every collaborator mocked including value objects and pure functions, with
expectations asserted on calls rather than outcomes.
**Weight:** +0.30
**False positives:** a suite whose established style is interaction testing.
**Scope:** hunk

### T4  -  Tests that assert the implementation
**Look for:** tests that restate the function body  -  mirroring its branches one-to-one, or
asserting a mock was called with exactly the arguments the code passes it.
**Weight:** +0.30
**False positives:** contract tests, where verifying the call *is* the point.
**Scope:** hunk

---

## V  -  PR metadata

### V1  -  Large diff, single commit
**Look for:** several hundred added lines across multiple files in one commit, with no
follow-up fixes.
**Weight:** +0.30
**False positives:** a squashed branch, a vendored update, a generated file, a mechanical
rename. Check whether the repo squash-merges before firing.
**Scope:** pr

### V2  -  Implausible authoring rate
**Look for:** consecutive commits minutes apart each carrying substantial hand-written-looking
code; a large feature landing in a single short session.
**Weight:** +0.35
**False positives:** rebasing and cherry-picking rewrite commit timestamps. Use author date,
not commit date, and treat this as weak on any rebased branch. **Never fire on
deletion-dominant commits**  -  deleting a file takes a human seconds, so a rapid burst of
removals is the expected human rate, not an implausible one.
**Scope:** pr

### V3  -  Whole-file rewrite
**Look for:** a file replaced nearly end-to-end where the linked issue describes a narrow change.
**Weight:** +0.30
**False positives:** reformatting, a linter run, a license-header change, line-ending churn.
Always diff with whitespace ignored before firing.
**Scope:** pr

### V4  -  Commit message register
**Look for:** commit messages in a uniform generated register  -  a conventional-commit prefix,
a tidy summary line, and a bulleted body listing every change  -  against a repo whose history
reads `fix`, `wip`, `PROJ-12848`.
**Weight:** +0.25
**False positives:** a commit-message template or a `commitlint` hook.
**Scope:** pr

---

## H  -  Human tells (subtract)

### H1  -  Typos in comments or identifiers
**Look for:** misspellings, wrong homophones, a variable named `recieved`, a comment with a
missing word.
**Weight:** -0.45
**False positives:** models reproduce existing misspellings when editing surrounding code, and
will happily extend a misspelled identifier. Only count a typo the hunk *introduces*.
**Scope:** hunk

### H2  -  Commented-out code
**Look for:** dead code left in place, an old implementation kept beside the new one, a
commented `dd()`, `var_dump`, `console.log`, `printf`.
**Weight:** -0.40
**False positives:** a deliberately preserved reference implementation with an explanation.
**Scope:** hunk

### H3  -  Debug leftovers
**Look for:** stray logging, a hardcoded test value, a temporary early return, a `sleep(1)`.
**Weight:** -0.35
**False positives:** legitimate structured logging that the codebase uses everywhere.
**Scope:** hunk

### H4  -  Formatting the linter would fix
**Look for:** inconsistent indentation, a line well over the project limit, missing trailing
comma, alignment that drifts, mixed quote styles  -  in a repo that has a formatter.
**Weight:** -0.40
**False positives:** if CI formats on merge, this signal is destroyed and the rule is
meaningless. Check for a formatter in CI first.
**Scope:** hunk

### H5  -  Matches local idiom
**Look for:** the hunk is indistinguishable in naming, comment density, error handling and
structure from the unchanged code around it.
**Weight:** -0.40
**False positives:** an agent given the file as context matches its style well. This is
evidence, not proof  -  which is why it subtracts rather than deciding.
**Scope:** hunk

### H6  -  TODO with an owner
**Look for:** `// TODO(penka):`, `// FIXME PROJ-12848`, a comment naming a person, a ticket,
or a date.
**Weight:** -0.35
**False positives:** a template TODO copied from a scaffold.
**Scope:** hunk

### H7  -  Narrow, surgical change
**Look for:** a one-to-five-line change in the middle of a large file that leaves everything
else alone.
**Weight:** -0.30
**False positives:** agents make small edits too, and increasingly do. Weak on its own.
**Scope:** hunk

### H8  -  Idiosyncratic choice
**Look for:** an unusual but working approach, a personal abbreviation, humour, profanity, a
domain shortcut only someone who knows the system would take.
**Weight:** -0.45
**False positives:** a widely-known idiom you happen not to recognise.
**Scope:** hunk

---

## Rules that contradict each other

**N2 (uniform shape) vs H5 (matches local idiom).** A hunk can be internally uniform *and*
match its surroundings  -  that is a consistent codebase, not evidence. Fire N2 only where the
uniformity is tighter than the file's own.

**D1/D2 (defensive scaffolding) vs the PR's purpose.** A hardening or bug-fix PR adds guards
because that is the job. Read the title and linked issue first; where the guard is the point,
do not fire.

**H4 (bad formatting) requires no formatter in CI.** If the pipeline formats on merge, H4 can
never fire and its absence means nothing. Check before relying on it.

---

## Scoring

**The hunk is the scoring unit; the line is only the display unit.** A line of code carries
too few tokens to attribute on its own. Score each hunk, then paint every one of its lines at
the hunk's tier.

```
score = SUM(weights of matching hunk-scope rules)
      + SUM(weights of matching file-scope rules for that file)
      - SUM(weights of matching H rules)
clamp to [0, 0.95]
```

**Never paint:** blank lines, lone braces and brackets, bare imports, pure-deletion lines, and
anything in a generated or vendored file. `build_report.py` enforces this independently.

### Tiers

| Tier | Score | `findings.json` | Label | Alert severity | Marker |
|---|---|---|---|---|---|
| high | >= 0.75 | `t3` | `ai:high` | `error` | RED |
| medium | 0.45 - 0.74 | `t2` | `ai:medium` | `warning` | ORANGE |
| low | 0.20 - 0.44 | `t1` | `ai:low` | `note` | YELLOW |
| unpainted | < 0.20 | -  | `ai:none` | -  | -  |
| recorded | n/a | `rec` | `ai:recorded` | `error` | PURPLE |

**Never output 1.0.** No detector is certain, and a report claiming certainty is lying about
its own method.

### PR score

Additive log-odds, not an average. Averaging lets unchanged and neutral lines count as
evidence *for* a human author, which makes a large PR score lower than a small one carrying
identical tells.

```
rate = painted_lines / scoreable_added_lines      # excludes generated + never-paint lines
damp = min(1, sqrt(rate / 0.15))

pos  = SUM(distinct hunk/file-scope rule weights) * damp  +  SUM(pr-scope positive) / 2
neg  = SUM(distinct H rule weights)

L    = -1.2 + 2 * (pos - neg)
inferred = clamp(sigmoid(L), 0.02, 0.95)
```

**Count each rule once**, at its highest weight, however many hunks it fired on.

`sigmoid(L) = 1 / (1 + e^-L)`:

| L | pct | L | pct | L | pct |
|---|-----|---|-----|---|-----|
| -3.00 | 5% | -0.75 | 32% | +1.50 | 82% |
| -2.75 | 6% | -0.50 | 38% | +1.75 | 85% |
| -2.50 | 8% | -0.25 | 44% | +2.00 | 88% |
| -2.25 | 10% | 0.00 | 50% | +2.25 | 90% |
| -2.00 | 12% | +0.25 | 56% | +2.50 | 92% |
| -1.75 | 15% | +0.50 | 62% | +2.75 | 94% |
| -1.50 | 18% | +0.75 | 68% | +3.00 | 95% |
| -1.25 | 22% | +1.00 | 73% | | |
| -1.00 | 27% | +1.25 | 78% | | |

### Report two numbers, never one

- **Recorded**  -  `N of M added lines` provably agent-authored. A count, not a percentage of
  confidence. Often zero.
- **Inferred**  -  the percentage above, labelled as inference.

Do not add them. Do not average them. A PR can be 0 recorded and 80% inferred; that means
"no record exists and the code looks generated", which is a different statement from either
number alone.

### Hard guards

- **No rule citation, no paint.** A hunk that "feels AI" but matches no rule is a rule nobody
  has written yet. Put it in the report as a *suggested rule*, never as a painted line.
- **Zero scoreable added lines**  -  the inferred lane does not run. Report `n/a`, not a number.
  A pure-deletion or lockfile-only PR carries no authorship signal; removing code has no style.
- **Under 30 added lines**  -  report the score and say the sample is too small to be reliable.
- **No baseline obtainable**  -  say so, and drop the confidence of every inferred finding by
  one tier.

---

## Calibration

The false-positive direction is the dangerous one. A tool that flags a careful developer's
work is worse than no tool  -  it will be used against people. Painting a specific line is a
sharper accusation than a PR-level percentage, because it points at a person's work.

| Looks generated | Often isn't |
|---|---|
| Thorough, uniform, well-commented | A senior developer with standards |
| Full docblocks, full error handling | A house style or a lint rule |
| Clean formatting throughout | A formatter in CI |
| Exhaustive tests | A team test convention |
| Big single commit | A squash merge |
| Unfamiliar idiom | Someone who just learned it |

State the method's limits in every report. The caveat block is not decoration.

## Adding new rules

1. Next free ID in the category. Never renumber  -  reports cite them.
2. Weight `0.10`-`0.55`. Reserve `0.50+` for near-conclusive. No rule may exceed 0.55.
3. At least one false positive. A rule without one hasn't been thought through.
4. State the scope.
5. State what the rule is measured *against*  -  the file baseline, the repo, or nothing.
6. If it contradicts an existing rule, add the pair above.

Next free IDs: **E5 · C6 · N5 · D5 · I5 · T5 · V5 · H9**. Update this line when you add one.
