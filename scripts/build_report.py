#!/usr/bin/env python3
"""Render an AI-code-attribution report from a diff plus findings.

    python3 build_report.py --diff pr.diff --findings f.json --out report.html

Findings address diff lines by INDEX (0-based, into the raw diff), never by
substring match, so the diff is reproduced byte-exact by construction.

f.json:
{
  "title":    "scaleforce/tixets #1920 — ...",
  "verdict":  "No provenance on record; style reads human",
  "meta":     "2 files · +88 / −47 · 1 commit",
  "recorded": "0 of 88",
  "inferred": 6,
  "excluded": ["db/migrations/2026_01_01_x.sql — 412 lines, generated"],
  "findings": [
    {"lines": [12, 30], "tier": "t2", "score": 0.55, "rules": "C1, D1",
     "why": "...", "where": "app/Service/Foo.php:41-58"}
  ]
}

tier is one of t3 | t2 | t1 | rec. Use "rec" for recorded evidence; its score is
rendered as "recorded" rather than a number.
"""
import argparse, html, json, pathlib, re, sys

HERE = pathlib.Path(__file__).parent
TPL = HERE / "report-template.html"

# Lines that carry no authorship signal and are never painted.
NEVER_PAINT = re.compile(
    r"^[+-]\s*$"                     # blank
    r"|^[+-]\s*[}\]);,]+\s*$"        # lone closers
    r"|^[+-]\s*(<\?php|\?>)\s*$"
    r"|^[+-]\s*(use|import|from|#include|require|require_once|include)\b"
)


def classify(line):
    """Structural class for a raw diff line."""
    if line.startswith(("diff --git", "index ", "--- ", "+++ ", "new file", "deleted file",
                        "similarity index", "rename from", "rename to", "old mode", "new mode")):
        return "fh"
    if line.startswith("@@"):
        return "hh"
    if line.startswith("+"):
        return "add"
    if line.startswith("-"):
        return "del"
    return "ctx"


def build(diff_text, spec):
    lines = diff_text.split("\n")
    # Trailing newline in the file yields a final "" — keep it so we can rejoin exactly.
    paint = {}   # index -> finding
    for f in spec.get("findings", []):
        lo, hi = f["lines"]
        if not (0 <= lo <= hi < len(lines)):
            sys.exit(f"finding out of range: {f['lines']} (diff has {len(lines)} lines)")
        for i in range(lo, hi + 1):
            cls = classify(lines[i])
            if cls in ("add", "del") and not NEVER_PAINT.match(lines[i]):
                paint[i] = f

    out = []
    for i, raw in enumerate(lines):
        esc = html.escape(raw, quote=False)
        cls = classify(raw)
        f = paint.get(i)
        if f:
            score = "recorded" if f["tier"] == "rec" else f"{f['score']:.2f}"
            out.append(
                f'<span class="l {cls} {f["tier"]}" data-score="{score}" '
                f'data-rules="{html.escape(f["rules"], quote=True)}" '
                f'data-why="{html.escape(f["why"], quote=True)}" tabindex="0">{esc}</span>'
            )
        else:
            out.append(f'<span class="l {cls}">{esc}</span>')

    body = "\n".join(out)

    # Verbatim guarantee: stripping every tag must return the input exactly.
    stripped = html.unescape(re.sub(r"<[^>]+>", "", body))
    if stripped != diff_text:
        sys.exit("VERBATIM CHECK FAILED — refusing to write the report")

    scoreable = sum(
        1 for l in lines if classify(l) == "add" and not NEVER_PAINT.match(l)
    )
    painted = len(paint)
    pct = round(100 * painted / scoreable) if scoreable else 0

    rows = []
    for f in sorted(spec.get("findings", []),
                    key=lambda x: (x["tier"] != "rec", -x.get("score", 0))):
        pill = "recorded" if f["tier"] == "rec" else f"{f['score']:.2f}"
        rows.append(
            f'        <tr><td class="c">{html.escape(f.get("where", ""))}</td>'
            f'<td class="n"><span class="pill {f["tier"]}">{pill}</span></td>'
            f'<td class="n">{html.escape(f["rules"])}</td>'
            f'<td>{f["why"]}</td></tr>'
        )
    findings = "\n".join(rows) or (
        '        <tr><td colspan="4" class="empty">Nothing met the threshold.</td></tr>')

    excluded = "\n".join(
        f"    <li>{html.escape(e)}</li>" for e in spec.get("excluded", [])
    ) or '    <li class="empty">None — every changed file was scoreable.</li>'

    tpl = re.sub(r"^<!--.*?-->\n", "", TPL.read_text(encoding="utf-8"), flags=re.S)
    out_html = (tpl
                .replace("{{TITLE}}", html.escape(spec["title"]))
                .replace("{{RECORDED}}", html.escape(str(spec["recorded"])))
                .replace("{{INFERRED}}", str(spec["inferred"]))
                .replace("{{VERDICT}}", spec["verdict"])
                .replace("{{META}}", html.escape(spec["meta"]))
                .replace("{{PAINTED_PCT}}", str(pct))
                .replace("{{DIFF}}", body))
    out_html = re.sub(r"\s*<!-- example row.*?-->\s*\{\{FINDINGS\}\}", "\n" + findings,
                      out_html, flags=re.S)
    out_html = out_html.replace("{{EXCLUDED}}", "\n" + excluded + "\n  ")

    # Check only OUR slot names. The diff body legitimately contains {{...}} —
    # application string files use that syntax for their own placeholders.
    for slot in ("TITLE", "RECORDED", "INFERRED", "VERDICT", "META",
                 "PAINTED_PCT", "DIFF", "FINDINGS", "EXCLUDED"):
        if "{{" + slot + "}}" in out_html:
            sys.exit(f"unfilled slot: {{{{{slot}}}}}")
    return out_html, dict(scoreable=scoreable, painted=painted, pct=pct, lines=len(lines))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--diff", required=True)
    ap.add_argument("--findings", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    diff_text = pathlib.Path(a.diff).read_text(encoding="utf-8")
    spec = json.loads(pathlib.Path(a.findings).read_text(encoding="utf-8"))
    out_html, stats = build(diff_text, spec)
    pathlib.Path(a.out).write_text(out_html, encoding="utf-8")
    print(f"verbatim OK · {stats['lines']} diff lines · "
          f"{stats['painted']}/{stats['scoreable']} scoreable added lines painted "
          f"({stats['pct']}%)")
    print("wrote", a.out)


if __name__ == "__main__":
    main()
