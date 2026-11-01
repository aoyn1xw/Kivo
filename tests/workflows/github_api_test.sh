#!/usr/bin/env bash
# Focused regression tests for .github/scripts/github_api.sh, Pages build
# polling status selection, and workflow existence-check safety guards.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCRIPTS="$REPO_ROOT/.github/scripts"
WORKFLOWS="$REPO_ROOT/.github/workflows"

# shellcheck source=/dev/null
source "$SCRIPTS/github_api.sh"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export RUNNER_TEMP="$WORK"
export GH_TOKEN="fake-token"
export PATH="$WORK/bin:$PATH"
mkdir -p "$WORK/bin"

if ! command -v python3 >/dev/null 2>&1 && command -v python >/dev/null 2>&1; then
  cat > "$WORK/bin/python3" <<'PYWRAP'
#!/usr/bin/env bash
exec python "$@"
PYWRAP
  chmod +x "$WORK/bin/python3"
fi

PASS=0
FAIL=0

check() {
  if [ "$3" = "$2" ]; then
    PASS=$((PASS + 1))
    echo "ok - $1"
  else
    FAIL=$((FAIL + 1))
    echo "FAIL - $1 (expected $2, got $3)"
  fi
}

make_curl() {
  cat > "$WORK/bin/curl" <<STUB
#!/usr/bin/env bash
body_file=""
args=("\$@")
for ((i = 0; i < \${#args[@]}; i++)); do
  if [ "\${args[\$i]}" = "-o" ]; then
    body_file="\${args[\$((i + 1))]}"
  fi
done
if [ -n "\$body_file" ]; then
  printf '%s' '$1' > "\$body_file"
fi
printf '%s' '$2'
exit \${3:-0}
STUB
  chmod +x "$WORK/bin/curl"
}

# --- 1. api_http_status and classify_http_status --------------------------
make_curl '{"tag_name":"latest"}' '200'
status="$(api_http_status "repos/aoyn1xw/Kivo/releases/tags/latest")"
check "api_http_status returns 200 and writes body" 0 "$([ "$status" = "200" ] && grep -q 'tag_name' "$WORK/github-api-body.json"; echo $?)"

cat > "$WORK/bin/curl" <<'STUB'
#!/usr/bin/env bash
exit 28
STUB
chmod +x "$WORK/bin/curl"
status="$(api_http_status "repos/aoyn1xw/Kivo/releases/tags/latest")"
check "transport failure yields 000" 0 "$([ "$status" = "000" ]; echo $?)"

for code in 200 204 404; do
  classify_http_status "$code" "resource" >/dev/null 2>&1
  check "classify passes definite HTTP $code" 0 $?
done

for code in 401 403 429 500 503 000 '' 418; do
  out="$(classify_http_status "$code" "resource" 2>&1)" && rc=0 || rc=$?
  check "classify rejects ambiguous HTTP '${code:-empty}'" 1 "$rc"
  case "$out" in
    *'::error::'*) check "classify emits ::error:: for '${code:-empty}'" 0 0 ;;
    *) check "classify emits ::error:: for '${code:-empty}'" 0 1 ;;
  esac
done

# --- 2. pages_commit_build_status (documented fields: url, commit, status, created_at, error.message) ---
eval_pages() {
  local sha="$1"
  local payload="$2"
  local min_created="${3:-}"
  local build_url="${4:-}"
  printf '%s' "$payload" > "$WORK/builds.json"
  pages_commit_build_status "$WORK/builds.json" "$sha" "$min_created" "$build_url" | tr -d '\r'
}

SHA="abc123def456abc123def456abc123def456abc1"
OTHER="9999888877779999888877779999888877779999"

res="$(eval_pages "$SHA" "[
  {\"commit\":\"$OTHER\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:05:00Z\",\"error\":{\"message\":\"old fail\"}},
  {\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"retry\"}},
  {\"commit\":\"$SHA\",\"status\":\"built\",\"created_at\":\"2026-11-01T12:03:00Z\",\"error\":{\"message\":null}}
]")"
check "Pages selector picks newest matching commit and ignores unrelated failure" $'built\t' "$res"

res_err="$(eval_pages "$SHA" "[{\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:03:00Z\",\"error\":{\"message\":\"Liquid syntax error\"}}]")"
check "Pages selector surfaces errored status and error.message" $'errored\tLiquid syntax error' "$res_err"

res_none="$(eval_pages "$SHA" "[{\"commit\":\"$OTHER\",\"status\":\"built\",\"created_at\":\"2026-11-01T12:00:00Z\"}]")"
check "Pages selector returns none when commit has no build yet" "none" "$res_none"

res_bad="$(eval_pages "$SHA" "{not-json")"
check "Pages selector returns malformed on invalid JSON" "malformed" "$res_bad"

# Retry sequence for the same commit (previous failed build -> new build not yet visible -> queued -> built)
REQ_AT="2026-11-01T12:05:00Z"
OLD_URL="https://api.github.com/repos/aoyn1xw/Kivo/pages/builds/101"
NEW_URL="https://api.github.com/repos/aoyn1xw/Kivo/pages/builds/102"
LATEST_URL="https://api.github.com/repos/aoyn1xw/Kivo/pages/builds/latest"

# Step A: Previous failed build for the same commit exists, new build not yet visible (eventual consistency)
retry_not_visible_time="$(eval_pages "$SHA" "[
  {\"url\":\"$OLD_URL\",\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"prior failure\"}}
]" "$REQ_AT" "$LATEST_URL")"
check "Pages retry: ignores previous failed build when new build not yet visible (request-time boundary)" "none" "$retry_not_visible_time"

retry_not_visible_url="$(eval_pages "$SHA" "[
  {\"url\":\"$OLD_URL\",\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"prior failure\"}}
]" "$REQ_AT" "$NEW_URL")"
check "Pages retry: ignores previous failed build when new build not yet visible (build URL)" "none" "$retry_not_visible_url"

# Step B: New build becomes visible in queued state alongside previous failed build
retry_queued_time="$(eval_pages "$SHA" "[
  {\"url\":\"$NEW_URL\",\"commit\":\"$SHA\",\"status\":\"queued\",\"created_at\":\"2026-11-01T12:05:02Z\",\"error\":{\"message\":null}},
  {\"url\":\"$OLD_URL\",\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"prior failure\"}}
]" "$REQ_AT" "$LATEST_URL")"
check "Pages retry: reports queued once new build appears (request-time boundary)" $'queued\t' "$retry_queued_time"

retry_queued_url="$(eval_pages "$SHA" "[
  {\"url\":\"$NEW_URL\",\"commit\":\"$SHA\",\"status\":\"queued\",\"created_at\":\"2026-11-01T12:05:02Z\",\"error\":{\"message\":null}},
  {\"url\":\"$OLD_URL\",\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"prior failure\"}}
]" "$REQ_AT" "$NEW_URL")"
check "Pages retry: reports queued once new build appears (build URL)" $'queued\t' "$retry_queued_url"

# Step C: New build completes successfully while previous failed build remains in history
retry_built_time="$(eval_pages "$SHA" "[
  {\"url\":\"$NEW_URL\",\"commit\":\"$SHA\",\"status\":\"built\",\"created_at\":\"2026-11-01T12:05:02Z\",\"error\":{\"message\":null}},
  {\"url\":\"$OLD_URL\",\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"prior failure\"}}
]" "$REQ_AT" "$LATEST_URL")"
check "Pages retry: reports built when new build completes (request-time boundary)" $'built\t' "$retry_built_time"

retry_built_url="$(eval_pages "$SHA" "[
  {\"url\":\"$NEW_URL\",\"commit\":\"$SHA\",\"status\":\"built\",\"created_at\":\"2026-11-01T12:05:02Z\",\"error\":{\"message\":null}},
  {\"url\":\"$OLD_URL\",\"commit\":\"$SHA\",\"status\":\"errored\",\"created_at\":\"2026-11-01T12:01:00Z\",\"error\":{\"message\":\"prior failure\"}}
]" "$REQ_AT" "$NEW_URL")"
check "Pages retry: reports built when new build completes (build URL)" $'built\t' "$retry_built_url"

# --- 3. Workflow existence-check and pruning guards -----------------------
for workflow in ios-ota.yml release.yml; do
  path="$WORKFLOWS/$workflow"
  if grep -nE 'gh release view [^|>]*>/dev/null 2>&1|git/ref[^>]*>[[:space:]]*/dev/null 2>&1' "$path" >/dev/null; then
    check "$workflow: no bare >/dev/null 2>&1 existence checks" 0 1
  else
    check "$workflow: no bare >/dev/null 2>&1 existence checks" 0 0
  fi
  check "$workflow: sources github_api.sh" 0 "$(grep -qF '.github/scripts/github_api.sh' "$path"; echo $?)"
done

first_classify="$(grep -n 'classify_http_status' "$WORKFLOWS/release.yml" | head -n 1 | cut -d: -f1)"
first_mutate="$(grep -nE -- '--method (PATCH|POST)' "$WORKFLOWS/release.yml" | head -n 1 | cut -d: -f1)"
check "release.yml: ref mutations occur after classify_http_status" 0 "$([ "$first_mutate" -gt "$first_classify" ]; echo $?)"

verify_line="$(grep -n 'cmp -s "\$asset" "verify/\$name"' "$WORKFLOWS/release.yml" | head -n 1 | cut -d: -f1)"
prune_line="$(grep -n 'Removing superseded asset' "$WORKFLOWS/release.yml" | head -n 1 | cut -d: -f1)"
check "release.yml: pruning occurs after byte-for-byte verification" 0 "$([ "$prune_line" -gt "$verify_line" ]; echo $?)"

echo
echo "github_api_test: $PASS passed, $FAIL failed"
[ "$FAIL" = "0" ]
