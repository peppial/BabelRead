#!/usr/bin/env python3
"""Aggregate the per-stage verdicts into one report and decide whether the run fails.

Each stage of the pipeline drops a JSON file into a findings directory. This reads them
all, renders one markdown table for the job summary, and applies the fail policy. Keeping
the policy here rather than in each job means the gate is one file to read and change.

Stage file shape:
    {"stage": "sast", "status": "pass|fail|error|skipped",
     "high": 0, "medium": 0, "summary": "one line", "details": ["..."]}
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

STAGE_ORDER = ["sast", "dependencies", "secrets", "tests", "security-tests"]

ICON = {"pass": "✅", "fail": "❌", "error": "💥", "skipped": "⏭️"}


def load_stages(findings_dir: Path) -> list[dict]:
    stages: list[dict] = []
    for path in sorted(findings_dir.rglob("*.json")):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            stages.append({
                "stage": path.stem,
                "status": "error",
                "summary": f"could not read {path.name}: {exc}",
                "high": 0, "medium": 0, "details": [],
            })
            continue
        data.setdefault("stage", path.stem)
        stages.append(data)

    def key(stage: dict) -> tuple:
        name = stage["stage"].split("/")[0]
        return (STAGE_ORDER.index(name) if name in STAGE_ORDER else len(STAGE_ORDER), stage["stage"])

    return sorted(stages, key=key)


def render(stages: list[dict], fail_on: str, failing: list[dict]) -> str:
    lines = [
        "# 🔒 AI-generated code security scan",
        "",
        f"Policy: **fail_on = {fail_on}**",
        "",
        "| | Stage | High | Medium | Result |",
        "|---|---|---:|---:|---|",
    ]
    for s in stages:
        lines.append(
            f"| {ICON.get(s.get('status'), '❔')} | `{s['stage']}` | {s.get('high', 0)} "
            f"| {s.get('medium', 0)} | {s.get('summary', '')} |"
        )

    lines += ["", "---", ""]
    if failing:
        lines.append("## What blocks this run")
        lines.append("")
        for s in failing:
            lines.append(f"### ❌ `{s['stage']}`")
            lines.append("")
            lines.append(s.get("summary", ""))
            for detail in s.get("details", [])[:40]:
                lines.append(f"- {detail}")
            lines.append("")
    else:
        lines.append("**Nothing blocking.** Advisory findings, if any, are listed below.")
        lines.append("")

    advisory = [s for s in stages if s not in failing and s.get("details")]
    if advisory:
        lines.append("<details><summary>Advisory findings</summary>")
        lines.append("")
        for s in advisory:
            lines.append(f"**`{s['stage']}`**")
            lines.append("")
            for detail in s.get("details", [])[:40]:
                lines.append(f"- {detail}")
            lines.append("")
        lines.append("</details>")
        lines.append("")

    lines += [
        "---",
        "",
        "**How to read this.** The scanners answer *is anything known to be wrong*; the package-identity "
        "check answers *is this dependency who it claims to be*, which is the question AI-generated code "
        "raises and which no vulnerability database can answer. Neither replaces the human review stage — "
        "they narrow what the reviewer has to look at.",
    ]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--findings", type=Path, required=True)
    parser.add_argument("--fail-on", choices=["high", "any", "none"], default="high")
    parser.add_argument("--out", type=Path, help="write the rendered markdown here as well")
    args = parser.parse_args()

    stages = load_stages(args.findings)
    if not stages:
        print("no stage verdicts found - every scan job must upload one", file=sys.stderr)
        return 2

    if args.fail_on == "none":
        failing = []
    elif args.fail_on == "any":
        failing = [s for s in stages if s.get("status") in ("fail", "error") or s.get("medium", 0)]
    else:
        failing = [s for s in stages if s.get("status") in ("fail", "error")]

    report = render(stages, args.fail_on, failing)
    print(report)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(report, encoding="utf-8")

    return 1 if failing else 0


if __name__ == "__main__":
    sys.exit(main())
