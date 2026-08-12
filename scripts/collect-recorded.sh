#!/usr/bin/env bash
# Lane A: recorded provenance evidence for a pull request.
#
#   collect-recorded.sh <base-ref> <head-ref> [pr-number] [repo] > recorded-evidence.json
#
# Emits facts only - commit trailers (E1), git AI notes (E2), bot authors (E3) and
# declarations in the PR body or branch name (E4). No judgement, no model. An empty
# result means "no provenance recorded", never "human-written"; the consumer must
# report it that way.
#
# Runs standalone against any local clone. Requires a full history: fetch-depth 0.

set -uo pipefail

BASE="${1:?usage: collect-recorded.sh <base-ref> <head-ref> [pr-number] [repo]}"
HEAD="${2:?usage: collect-recorded.sh <base-ref> <head-ref> [pr-number] [repo]}"
PR_NUMBER="${3:-}"
REPO="${4:-${GITHUB_REPOSITORY:-}}"

if ! git rev-parse --git-dir >/dev/null 2>&1; then
  echo "not a git repository" >&2
  exit 1
fi

RANGE="$(git merge-base "$BASE" "$HEAD" 2>/dev/null || echo "$BASE")..$HEAD"

# Bot authors (E3). Anything ending in [bot], plus the named agent accounts.
BOT_RE='\[bot\]|copilot-swe-agent|Copilot Autofix|\bCopilot\b|dependabot|renovate|github-actions'
# Agent trailers (E1). Deliberately narrow - a "Co-authored-by" alone is not evidence.
TRAILER_RE='Co-?[Aa]uthored-[Bb]y:.*(Claude|Copilot|Cursor|Codex|Devin|Aider|Windsurf)'
# Declarations (E4) in a PR body or branch name.
DECL_RE='[Gg]enerated with|[Cc]reated by (Claude|Copilot|Cursor)|via Claude Code|🤖|^(copilot|cursor|claude|codex)/'

# Do these commits carry an AI note? Fetch once; absent on almost every repo.
git fetch origin 'refs/notes/ai:refs/notes/ai' >/dev/null 2>&1 || true

emit_commits() {
  local first=1
  while IFS=$'\x1f' read -r sha author_name author_email committer_name subject; do
    [ -z "$sha" ] && continue

    local body e1=false e2=false e3=false
    body="$(git log -1 --format='%B' "$sha" 2>/dev/null)"

    grep -Eq "$TRAILER_RE" <<<"$body" && e1=true
    git notes --ref=ai show "$sha" >/dev/null 2>&1 && e2=true
    grep -Eq "$BOT_RE" <<<"$author_name $author_email $committer_name" && e3=true

    # Dependabot/renovate are machine-written but not LLM-written - E3's own caveat.
    local kind="agent"
    grep -Eq 'dependabot|renovate' <<<"$author_name $author_email" && kind="dependency-bot"

    if $e1 || $e2 || $e3; then
      [ $first -eq 0 ] && printf ','
      first=0
      printf '\n    {"sha":%s,"author":%s,"subject":%s,"E1":%s,"E2":%s,"E3":%s,"kind":%s,"files":%s}' \
        "$(jq -Rn --arg v "$sha" '$v')" \
        "$(jq -Rn --arg v "$author_name <$author_email>" '$v')" \
        "$(jq -Rn --arg v "$subject" '$v')" \
        "$e1" "$e2" "$e3" \
        "$(jq -Rn --arg v "$kind" '$v')" \
        "$(git show --name-only --format= "$sha" 2>/dev/null | jq -Rsc 'split("\n") | map(select(length>0))')"
    fi
  done < <(git log --no-merges --format="%H%x1f%an%x1f%ae%x1f%cn%x1f%s" "$RANGE" 2>/dev/null)
  [ $first -eq 0 ] && printf '\n  '
}

# E4 - declared in the PR body, title or branch name.
e4=false
e4_source=""
if [ -n "$PR_NUMBER" ] && [ -n "$REPO" ] && command -v gh >/dev/null 2>&1; then
  meta="$(gh pr view "$PR_NUMBER" --repo "$REPO" --json body,title,headRefName 2>/dev/null || echo '{}')"
  for field in headRefName title body; do
    value="$(jq -r --arg f "$field" '.[$f] // ""' <<<"$meta")"
    if grep -Eq "$DECL_RE" <<<"$value"; then
      e4=true
      e4_source="$field"
      break
    fi
  done
fi

total_commits="$(git rev-list --count "$RANGE" 2>/dev/null || echo 0)"
added_lines="$(git diff --numstat "$RANGE" 2>/dev/null | awk '{s+=$1} END {print s+0}')"

printf '{\n'
printf '  "range": %s,\n' "$(jq -Rn --arg v "$RANGE" '$v')"
printf '  "commits_total": %s,\n' "$total_commits"
printf '  "added_lines": %s,\n' "$added_lines"
printf '  "E4": %s,\n' "$e4"
printf '  "E4_source": %s,\n' "$(jq -Rn --arg v "$e4_source" '$v')"
printf '  "recorded_commits": ['
emit_commits
printf ']\n}\n'
