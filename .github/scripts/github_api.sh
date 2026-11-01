# Shared GitHub API helpers for Kivo release and OTA workflows.
# Distinguishes a confirmed HTTP 404 (absent) or 2xx (present) from auth,
# rate-limit, server, or transport failures before any recovery mutation.

api_http_status() {
  local path="$1"
  local body_file="${GITHUB_API_BODY_FILE:-${RUNNER_TEMP:-/tmp}/github-api-body.json}"
  local status

  status="$(
    curl -sS \
      --max-time 60 \
      -o "$body_file" \
      -w '%{http_code}' \
      -H "Authorization: Bearer $GH_TOKEN" \
      -H "Accept: application/vnd.github+json" \
      "https://api.github.com/$path" \
    2>>"${RUNNER_TEMP:-/tmp}/github-api-stderr.log" \
    || true
  )"

  printf '%s' "${status:-000}"
}

classify_http_status() {
  local status="$1"
  local what="$2"

  case "$status" in
    2??|404)
      return 0
      ;;
    401|403)
      echo "::error::GitHub API authentication/permission failure (HTTP $status) while checking $what."
      return 1
      ;;
    429)
      echo "::error::GitHub API rate limit hit (HTTP 429) while checking $what."
      return 1
      ;;
    5??)
      echo "::error::GitHub API server error (HTTP $status) while checking $what."
      return 1
      ;;
    ""|000)
      echo "::error::No response from the GitHub API while checking $what (transport failure or timeout)."
      return 1
      ;;
    *)
      echo "::error::Unexpected HTTP status $status while checking $what."
      return 1
      ;;
  esac
}

pages_commit_build_status() {
  # pages_commit_build_status <builds-json-path> <manifest-sha> [min-created-at] [build-url]
  # Uses documented GET /repos/{owner}/{repo}/pages/builds fields:
  # url, commit, status in {queued, building, built, errored}, created_at, error.message.
  local py_bin="${PYTHON_BIN:-python3}"
  if ! "$py_bin" -c "import sys" >/dev/null 2>&1; then
    py_bin="python"
  fi
  "$py_bin" - "$1" "$2" "${3:-}" "${4:-}" <<'PY'
import json, sys
from datetime import datetime, timezone

def parse_iso(val):
    if not isinstance(val, str) or not val:
        return None
    try:
        dt = datetime.fromisoformat(val.replace("Z", "+00:00"))
        return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)
    except Exception:
        return None

try:
    with open(sys.argv[1], encoding="utf-8") as f:
        data = json.load(f)
except Exception:
    print("malformed")
    sys.exit(0)

if isinstance(data, dict):
    data = [data]
elif not isinstance(data, list):
    print("malformed")
    sys.exit(0)

sha = sys.argv[2]
min_created_raw = sys.argv[3] if len(sys.argv) > 3 else ""
build_url_raw = sys.argv[4] if len(sys.argv) > 4 else ""

min_dt = parse_iso(min_created_raw)
if min_created_raw and min_dt is None:
    print("malformed")
    sys.exit(0)

concrete_url = (
    build_url_raw.rstrip("/")
    if build_url_raw and not build_url_raw.rstrip("/").endswith("/latest")
    else ""
)

matches = [
    b for b in data
    if isinstance(b, dict)
    and isinstance(b.get("commit"), str)
    and b["commit"].startswith(sha)
]

if concrete_url:
    url_matches = [
        b for b in matches
        if isinstance(b.get("url"), str) and b["url"].rstrip("/") == concrete_url
    ]
    if url_matches:
        matches = url_matches
    elif min_dt is not None:
        matches = [
            b for b in matches
            if not isinstance(b.get("url"), str)
            and (dt := parse_iso(b.get("created_at"))) is not None
            and dt >= min_dt
        ]
    else:
        matches = []
elif min_dt is not None:
    matches = [
        b for b in matches
        if (dt := parse_iso(b.get("created_at"))) is not None and dt >= min_dt
    ]

if not matches:
    print("none")
    sys.exit(0)

epoch = datetime.min.replace(tzinfo=timezone.utc)
newest = max(
    matches,
    key=lambda b: (parse_iso(b.get("created_at")) or epoch, str(b.get("url") or "")),
)
status = newest.get("status")
err = (
    (newest.get("error") or {}).get("message") or ""
    if isinstance(newest.get("error"), dict)
    else ""
)
if status in {"queued", "building", "built", "errored"}:
    print(f"{status}\t{err}")
else:
    print("malformed")
PY
}
