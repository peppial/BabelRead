#!/usr/bin/env python3
"""Package-identity check for AI-generated code.

Vulnerability scanners answer "is this package known to be bad?". They cannot answer
"is this package who it claims to be?" -- and that is the question AI-generated code
raises, because a model asked for a dependency will happily invent a plausible id
(usually the *namespace*) that a squatter has already registered.

For every PackageReference in the repo this asks nuget.org who owns the package, how
long it has existed and whether a far more popular near-name package exists, then
scores the answer. Findings are advisory heuristics, not proof: use the allowlist.

Exit codes: 0 clean or medium-only, 1 at least one HIGH, 2 the check itself failed.
"""

from __future__ import annotations

import argparse
import gzip
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

FLAT = "https://api.nuget.org/v3-flatcontainer"
SEARCH = "https://azuresearch-usnc.nuget.org/query"
REGISTRATION = "https://api.nuget.org/v3/registration5-gz-semver2"

PLACEHOLDER_DESCRIPTIONS = {"", "package description", "description", "no description"}

HIGH = "HIGH"
MEDIUM = "MEDIUM"
OK = "OK"

# Scoring weights. Tuned so a package failing several independent signals reaches HIGH,
# while a small-but-honest package (few versions, low downloads, but a real description
# and project URL) stays at MEDIUM and can be allowlisted.
W_FEW_VERSIONS = 2
W_PLACEHOLDER_DESC = 3
W_NO_PROJECT_URL = 2
W_LOW_DOWNLOADS = 2
W_PUBLISH_ORDER = 2
W_NEAR_NAME = 4

THRESHOLD_HIGH = 5
THRESHOLD_MEDIUM = 3

MIN_VERSIONS = 5
MIN_DOWNLOADS = 100_000
NEAR_NAME_DOWNLOAD_RATIO = 10


def http_json(url: str, timeout: float = 20.0) -> dict | None:
    # urllib will happily dereference file:// and ftp://. Every caller builds its URL from a
    # constant https prefix, and this makes that a checked invariant rather than a convention.
    if not url.startswith("https://"):
        raise ValueError(f"refusing a non-https URL: {url!r}")
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "babelread-package-identity", "Accept-Encoding": "gzip"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read()
            if resp.headers.get("Content-Encoding") == "gzip" or body[:2] == b"\x1f\x8b":
                body = gzip.decompress(body)
            return json.loads(body.decode("utf-8"))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise


def find_references(root: Path) -> dict[str, set[str]]:
    """Every PackageReference in the repo, mapped id -> {versions}."""
    refs: dict[str, set[str]] = {}
    candidates = list(root.rglob("*.csproj")) + list(root.rglob("Directory.*.props"))
    for path in candidates:
        if "/obj/" in str(path) or "/bin/" in str(path):
            continue
        try:
            if b"<!DOCTYPE" in path.read_bytes()[:4096].upper():
                raise ValueError("project file carries a DOCTYPE declaration")
            tree = ET.parse(path)
        except (ET.ParseError, ValueError, OSError) as exc:
            print(f"warning: could not parse {path}: {exc}", file=sys.stderr)
            continue
        for node in tree.iter():
            if not node.tag.endswith("PackageReference"):
                continue
            pkg = node.get("Include") or node.get("Update")
            if not pkg:
                continue
            version = node.get("Version") or (node.findtext("Version") or "").strip()
            refs.setdefault(pkg, set())
            if version and "$(" not in version:
                refs[pkg].add(version)
    return refs


def load_allowlist(path: Path) -> dict[str, str]:
    """id -> reason. A package here is reported but never fails the run."""
    allowed: dict[str, str] = {}
    if not path.exists():
        return allowed
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        pkg, _, reason = line.partition("#")
        allowed[pkg.strip().lower()] = reason.strip() or "no reason recorded"
    return allowed


def edit_distance(a: str, b: str) -> int:
    if abs(len(a) - len(b)) > 3:
        return 99
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def parse_version_key(version: str) -> tuple:
    """Sortable key for a semver-ish NuGet version. Prerelease sorts below its release."""
    core, _, pre = version.partition("-")
    parts = []
    for piece in core.split(".")[:4]:
        parts.append(int(piece) if piece.isdigit() else 0)
    while len(parts) < 4:
        parts.append(0)
    return (*parts, 0 if pre else 1)


def fetch_flat_versions(pkg: str) -> list[str] | None:
    data = http_json(f"{FLAT}/{urllib.parse.quote(pkg.lower())}/index.json")
    return data.get("versions", []) if data else None


def fetch_search_entry(pkg: str) -> dict | None:
    query = urllib.parse.urlencode({"q": f"packageid:{pkg}", "prerelease": "true", "take": "1"})
    data = http_json(f"{SEARCH}?{query}")
    if not data:
        return None
    for item in data.get("data", []):
        if item.get("id", "").lower() == pkg.lower():
            return item
    return None


def fetch_publish_dates(pkg: str) -> list[tuple[str, str]]:
    """[(version, publishedIso)] straight from the registration index, in listed order."""
    data = http_json(f"{REGISTRATION}/{urllib.parse.quote(pkg.lower())}/index.json")
    if not data:
        return []
    out: list[tuple[str, str]] = []
    for page in data.get("items", []):
        for item in page.get("items", []):
            entry = item.get("catalogEntry", {})
            version, published = entry.get("version"), entry.get("published")
            if version and published:
                out.append((version, published))
    return out


def find_near_name(pkg: str, own_downloads: int, own_owners: set[str]) -> dict | None:
    """A far more popular package with a confusingly similar id and no shared owner.

    This is the signal that catches a namespace-squat: the model guesses the namespace,
    the real package lives under a shorter id, and the two share no maintainer.
    """
    seen: dict[str, dict] = {}
    terms = {pkg, pkg.replace(".", " "), pkg.split(".")[-1]}
    for term in terms:
        query = urllib.parse.urlencode({"q": term, "prerelease": "true", "take": "20"})
        data = http_json(f"{SEARCH}?{query}")
        for item in (data or {}).get("data", []):
            seen.setdefault(item.get("id", "").lower(), item)

    lower = pkg.lower()
    best = None
    for other_id, item in seen.items():
        if other_id == lower:
            continue
        similar = (
            edit_distance(other_id, lower) <= 2
            or other_id in lower
            or lower in other_id
            or other_id.split(".")[-1] == lower.split(".")[-1]
        )
        if not similar:
            continue
        downloads = item.get("totalDownloads", 0) or 0
        if downloads < max(own_downloads, 1) * NEAR_NAME_DOWNLOAD_RATIO:
            continue
        if own_owners & {o.lower() for o in item.get("owners", [])}:
            continue  # same maintainer publishing under two ids is not a squat
        if best is None or downloads > (best.get("totalDownloads", 0) or 0):
            best = item
    return best


def inspect(pkg: str, versions_used: set[str]) -> dict:
    """Score one package. Returns a finding dict regardless of verdict."""
    finding = {
        "package": pkg,
        "versions_referenced": sorted(versions_used),
        "verdict": OK,
        "score": 0,
        "signals": [],
        "likely_intended": None,
    }

    published_versions = fetch_flat_versions(pkg)
    if published_versions is None:
        finding.update(
            verdict=HIGH,
            score=99,
            signals=["package does not exist on nuget.org - almost certainly a hallucinated id"],
        )
        return finding

    missing = sorted(v for v in versions_used if v not in published_versions)
    if missing:
        finding.update(
            verdict=HIGH,
            score=99,
            signals=[f"referenced version(s) {', '.join(missing)} are not published on nuget.org"],
        )
        return finding

    entry = fetch_search_entry(pkg) or {}
    if entry.get("verified"):
        # A reserved id prefix means nuget.org verified the owner controls the namespace.
        finding["signals"].append("reserved id prefix (verified owner) - trusted")
        return finding

    owners = {o.lower() for o in entry.get("owners", [])}
    downloads = entry.get("totalDownloads", 0) or 0
    description = (entry.get("description") or "").strip()
    project_url = entry.get("projectUrl") or ""
    score = 0
    signals: list[str] = []

    if len(published_versions) < MIN_VERSIONS:
        score += W_FEW_VERSIONS
        signals.append(f"only {len(published_versions)} published version(s)")

    if description.lower() in PLACEHOLDER_DESCRIPTIONS:
        score += W_PLACEHOLDER_DESC
        signals.append(f"placeholder description ({description!r})")

    if not project_url:
        score += W_NO_PROJECT_URL
        signals.append("no project or repository URL")

    if downloads < MIN_DOWNLOADS:
        score += W_LOW_DOWNLOADS
        signals.append(f"only {downloads:,} total downloads")

    dates = fetch_publish_dates(pkg) if len(published_versions) < 20 else []
    if len(dates) > 1:
        ordered = sorted(dates, key=lambda d: parse_version_key(d[0]))
        if [d[1] for d in ordered] != sorted(d[1] for d in ordered):
            score += W_PUBLISH_ORDER
            signals.append(
                "publish dates run backwards against version order "
                + ", ".join(f"{v} @ {p[:10]}" for v, p in ordered)
            )

    near = find_near_name(pkg, downloads, owners)
    if near:
        score += W_NEAR_NAME
        near_downloads = near.get("totalDownloads", 0) or 0
        ratio = near_downloads / max(downloads, 1)
        signals.append(
            f"'{near['id']}' has {near_downloads:,} downloads ({ratio:.0f}x this package) "
            f"under different owners ({', '.join(near.get('owners', [])) or 'unknown'})"
        )
        finding["likely_intended"] = near["id"]

    finding["score"] = score
    finding["signals"] = signals
    finding["owners"] = sorted(owners)
    finding["downloads"] = downloads
    finding["verdict"] = HIGH if score >= THRESHOLD_HIGH else MEDIUM if score >= THRESHOLD_MEDIUM else OK
    return finding


def render_table(findings: list[dict]) -> str:
    icon = {HIGH: "❌", MEDIUM: "⚠️", OK: "✅"}
    lines = ["| | Package | Score | Signals |", "|---|---|---:|---|"]
    for f in findings:
        signals = "<br>".join(f["signals"]) or "nothing anomalous"
        if f.get("allowlisted"):
            signals = f"_allowlisted: {f['allowlisted']}_<br>{signals}"
        score = "" if f["score"] >= 99 else str(f["score"])
        lines.append(f"| {icon[f['verdict']]} | `{f['package']}` | {score} | {signals} |")
    return "\n".join(lines)


def render_text(findings: list[dict]) -> str:
    out = []
    for f in findings:
        head = f"{f['verdict']:<6} {f['package']}"
        if f["score"] and f["score"] < 99:
            head += f"  (score {f['score']})"
        if f.get("allowlisted"):
            head += f"  [allowlisted: {f['allowlisted']}]"
        out.append(head)
        for signal in f["signals"]:
            out.append(f"         - {signal}")
        if f["likely_intended"]:
            out.append(f"         => did you mean '{f['likely_intended']}'?")
    return "\n".join(out)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--format", choices=["text", "table", "json"], default="text")
    parser.add_argument("--out", type=Path, help="also write the findings as JSON here")
    parser.add_argument("--allowlist", type=Path, default=None)
    parser.add_argument("--no-fail", action="store_true", help="always exit 0; report only")
    args = parser.parse_args()

    allowlist_path = args.allowlist or (args.root / "scripts" / "package-identity-allowlist.txt")
    allowlist = load_allowlist(allowlist_path)

    refs = find_references(args.root)
    if not refs:
        print("no PackageReference found - nothing to check", file=sys.stderr)
        return 0

    try:
        with ThreadPoolExecutor(max_workers=8) as pool:
            findings = list(pool.map(lambda kv: inspect(*kv), sorted(refs.items())))
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        print(f"error: could not reach nuget.org: {exc}", file=sys.stderr)
        return 2

    for f in findings:
        if f["package"].lower() in allowlist:
            f["allowlisted"] = allowlist[f["package"].lower()]

    order = {HIGH: 0, MEDIUM: 1, OK: 2}
    findings.sort(key=lambda f: (order[f["verdict"]], f["package"].lower()))

    blocking = [f for f in findings if f["verdict"] == HIGH and not f.get("allowlisted")]
    summary = {
        "stage": "dependencies/package-identity",
        "checked": len(findings),
        "high": sum(1 for f in findings if f["verdict"] == HIGH),
        "medium": sum(1 for f in findings if f["verdict"] == MEDIUM),
        "blocking": [f["package"] for f in blocking],
        "findings": findings,
    }

    if args.format == "json":
        print(json.dumps(summary, indent=2))
    elif args.format == "table":
        print(render_table(findings))
    else:
        print(render_text(findings))
        print(f"\n{len(findings)} checked - {summary['high']} high, {summary['medium']} medium")

    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(summary, indent=2), encoding="utf-8")

    if blocking and not args.no_fail:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
