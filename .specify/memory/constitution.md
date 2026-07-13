<!--
Sync Impact Report
==================
Version change: TEMPLATE (unversioned) → 1.0.0
Bump rationale: Initial ratification of the project constitution (MAJOR baseline).

Modified principles:
  - [PRINCIPLE_1_NAME] → I. Code Quality
  - [PRINCIPLE_2_NAME] → II. Testing Standards (NON-NEGOTIABLE)
  - [PRINCIPLE_3_NAME] → III. User Experience Consistency
  - [PRINCIPLE_4_NAME] → IV. Performance Requirements
  - [PRINCIPLE_5_NAME] → (removed; user requested four focused principles)

Added sections:
  - Quality Gates (formerly [SECTION_2_NAME])
  - Development Workflow (formerly [SECTION_3_NAME])

Removed sections:
  - Fifth principle slot from the template (intentional; four principles requested)

Templates requiring updates:
  - ✅ .specify/templates/plan-template.md — Constitution Check uses a dynamic
       placeholder ("[Gates determined based on constitution file]"); no hardcoded
       gates conflict. Reviewer maps the four principles into concrete gates when planning.
  - ✅ .specify/templates/spec-template.md — no constitution references; no change needed.
  - ⚠ .specify/templates/tasks-template.md — states "Tests are OPTIONAL". This
       constitution makes automated testing mandatory (Principle II). The generic
       framework template was left unchanged; feature tasks.md MUST include test
       tasks regardless of the template's default wording.

Follow-up TODOs: none. RATIFICATION_DATE set to initial adoption date (2026-07-12).
-->

# BabelRead Constitution

## Core Principles

### I. Code Quality

Code MUST be readable, consistent, and maintainable before it is considered complete.

- All code MUST pass the project linter and formatter with zero errors before merge;
  configuration is version-controlled and is the single source of truth for style.
- Every change MUST be reviewed and approved by at least one other maintainer; the author
  MUST NOT be the sole approver.
- Public functions, modules, and exported types MUST have clear names and documented intent;
  code MUST read like the surrounding code (naming, idioms, comment density).
- Complexity and duplication MUST be actively reduced: prefer reusing existing utilities over
  introducing new ones. Any deliberate exception MUST be justified in review.
- No dead code, commented-out blocks, or TODOs without a tracking reference may be merged.

**Rationale**: BabelRead is a long-lived codebase; readability and low complexity are what keep
change safe and cheap. Quality enforced at review time is far cheaper than quality retrofitted.

### II. Testing Standards (NON-NEGOTIABLE)

Automated tests are mandatory and MUST prove behavior, not merely execute code.

- Every feature and bugfix MUST ship with automated tests. A bugfix MUST include a test that
  fails before the fix and passes after it.
- Tests MUST follow Red-Green-Refactor: write a failing test, make it pass with the minimal
  change, then refactor with tests green.
- The full test suite MUST pass in CI before merge; a red build MUST NOT be merged.
- Critical paths (content parsing, translation/rendering pipeline, data persistence) MUST have
  both unit and integration coverage. Contract and inter-component boundaries MUST be tested.
- Tests MUST be deterministic and isolated: no reliance on network, wall-clock time, or shared
  mutable state unless explicitly mocked or fixtured.

**Rationale**: Tests are the executable specification and the safety net for refactoring. Making
them non-negotiable prevents regressions from accumulating as the project grows.

### III. User Experience Consistency

The user experience MUST be predictable, coherent, and accessible across the whole product.

- Shared UI patterns (navigation, typography, spacing, controls, error and empty states) MUST
  come from a single design system / component library; ad-hoc one-off variants are prohibited.
- Interaction behavior MUST be consistent across platforms and screens: the same action MUST
  produce the same result and feedback everywhere it appears.
- All user-facing surfaces MUST meet WCAG 2.1 AA (contrast, keyboard navigation, screen-reader
  labels) and MUST support the product's declared locales and reading directions.
- Every user-facing state MUST be designed, including loading, empty, error, and offline states;
  errors MUST be actionable and written in plain language.

**Rationale**: BabelRead serves readers across languages and devices; consistency and
accessibility are core to trust and usability, not optional polish.

### IV. Performance Requirements

Performance is a feature and MUST be treated as a measurable, enforced budget.

- User-facing performance budgets MUST be defined and enforced in CI: interactive views MUST
  reach usable state within 2 seconds on the target baseline device/connection, and primary
  interactions MUST respond within 100 ms.
- Performance-sensitive paths MUST be measured with benchmarks or profiling before and after
  change; regressions beyond the agreed budget MUST block merge.
- Resource usage (memory, bundle/artifact size, and network payloads) MUST be bounded and
  monitored; unbounded growth MUST be treated as a defect.
- Optimization MUST be evidence-driven: profile first, optimize the demonstrated bottleneck, and
  avoid speculative complexity that is not justified by measurement.

**Rationale**: Reading is a sustained, latency-sensitive experience. Explicit budgets prevent slow
regressions that individually seem minor but collectively degrade the product.

## Quality Gates

The following gates are mandatory and automated wherever possible:

- Lint, format, and type checks MUST pass with zero errors.
- The full automated test suite MUST pass; coverage MUST NOT decrease on critical paths.
- Performance budgets (Principle IV) MUST be verified for changes touching sensitive paths.
- Accessibility checks (Principle III) MUST pass for changes to user-facing surfaces.
- A pull request MUST NOT merge with any failing gate or unresolved review comment.

## Development Workflow

- All work happens on feature branches; direct commits to the main branch are prohibited.
- Every pull request MUST be reviewed for compliance with all four core principles, not only for
  functional correctness. Reviewers explicitly verify code quality, tests, UX consistency, and
  performance impact.
- Any deviation from a principle MUST be documented in the pull request with an explicit rationale
  and, where applicable, recorded in the plan's Complexity Tracking section.
- Feature specs and plans MUST map each principle to concrete gates during planning; see
  `.specify/templates/plan-template.md`.

## Governance

This constitution supersedes all other development practices. When guidance conflicts, the
constitution wins.

- **Amendments**: Proposed changes MUST be documented in a pull request describing the change and
  its rationale, reviewed and approved by the maintainers, and accompanied by any required
  migration of dependent templates and docs.
- **Versioning**: This constitution uses semantic versioning. MAJOR for backward-incompatible
  governance or principle removals/redefinitions; MINOR for a new principle or materially expanded
  guidance; PATCH for clarifications and non-semantic refinements.
- **Compliance review**: Every pull request and review MUST verify compliance with the four core
  principles. Complexity and deviations MUST be justified; unjustified violations block merge.
- Runtime and agent development guidance lives alongside the repository docs; those docs MUST stay
  consistent with this constitution.

**Version**: 1.0.0 | **Ratified**: 2026-07-12 | **Last Amended**: 2026-07-12
