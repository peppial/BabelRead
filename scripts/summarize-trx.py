#!/usr/bin/env python3
"""Turn VSTest .trx results into one stage verdict for the security report."""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def summarize(trx_dir: Path) -> tuple[int, int, list[str]]:
    passed = failed = 0
    details: list[str] = []
    for path in sorted(trx_dir.rglob("*.trx")):
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exc:
            details.append(f"could not parse {path.name}: {exc}")
            continue
        for result in root.iterfind(".//t:UnitTestResult", NS):
            outcome = result.get("outcome")
            if outcome == "Passed":
                passed += 1
            elif outcome == "Failed":
                failed += 1
                message = result.findtext(".//t:Message", default="", namespaces=NS).strip()
                first = message.splitlines()[0] if message else "no message"
                details.append(f"`{result.get('testName', '?')}` — {first}")
    return passed, failed, details


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trx", type=Path, required=True)
    parser.add_argument("--stage", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    if not args.trx.exists():
        # The test command died before writing results; that is a stage error, not a pass.
        verdict = {
            "stage": args.stage, "status": "error", "high": 1, "medium": 0,
            "summary": "no .trx results were produced — the test run did not complete",
            "details": [],
        }
    else:
        passed, failed, details = summarize(args.trx)
        verdict = {
            "stage": args.stage,
            "status": "fail" if failed else ("error" if passed == 0 else "pass"),
            "high": failed,
            "medium": 0,
            "summary": f"{passed} passed, {failed} failed" if passed or failed else "no tests ran",
            "details": details,
        }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(verdict, indent=2), encoding="utf-8")
    print(json.dumps(verdict, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
