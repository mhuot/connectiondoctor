#!/usr/bin/env bash
# Hardware verification kit.
#
# The things this project gets wrong on real hardware are format-valid and
# semantically false: a location token that parses as a serial, one unitKey
# shared by three unrelated devices, a panel that is "external" because its
# name says "Generic PnP Monitor". CI cannot see any of that, and neither can
# a checklist that only asks whether the tool ran.
#
# So this does two things a person cannot reliably do by hand: it records
# exactly what was observed with provenance attached, and it compares that
# observation against ground truth you declared in advance (the rig file)
# rather than against your memory of what looks right.
#
# Every past hardware bug is a standing invariant below. That is the whole
# design: curiosity found them once, and it does not have to find them again.
#
#   ./scripts/hwkit.sh capture <label>        snapshot the endpoint, with provenance
#   ./scripts/hwkit.sh compare <a> <b>        what changed between two captures
#   ./scripts/hwkit.sh verify [rig.json]      invariants, plus the rig if given
#   ./scripts/hwkit.sh report                 write report.md for the run
#
# Env: HWKIT_ENDPOINT (default http://127.0.0.1:8787), HWKIT_RUN (run directory)

set -euo pipefail

ENDPOINT="${HWKIT_ENDPOINT:-http://127.0.0.1:8787}"
RUNS_ROOT="${HWKIT_RUNS_ROOT:-hwkit-runs}"

die() { printf 'hwkit: %s\n' "$*" >&2; exit 1; }

require_tools() {
  command -v curl >/dev/null 2>&1 || die "curl not found"
  command -v jq >/dev/null 2>&1 || die "jq not found — 'brew install jq' / 'winget install jqlang.jq'"
}

# One run directory per sitting, so captures taken minutes apart are compared
# against each other rather than against last week's hardware.
run_dir() {
  if [ -n "${HWKIT_RUN:-}" ]; then printf '%s' "$HWKIT_RUN"; return; fi
  if [ -f "$RUNS_ROOT/.current" ]; then cat "$RUNS_ROOT/.current"; return; fi
  local dir="$RUNS_ROOT/$(date -u +%Y%m%dT%H%M%SZ)"
  mkdir -p "$dir/captures"
  printf '%s' "$dir" > "$RUNS_ROOT/.current"
  printf '%s' "$dir"
}

sha256_of() {
  if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
  elif command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else printf 'unavailable'; fi
}

# ---------------------------------------------------------------- capture ---
cmd_capture() {
  local label="${1:-}"
  [ -n "$label" ] || die "usage: hwkit.sh capture <label>"
  local dir; dir="$(run_dir)"
  local base="$dir/captures/$label"

  local headers="$base.headers"
  curl -sS -D "$headers" -o "$base.contract.json" "$ENDPOINT/contract" \
    || die "could not reach $ENDPOINT/contract — is the collector serving?"
  curl -sS -o "$base.events.jsonl" "$ENDPOINT/events" || : > "$base.events.jsonl"

  jq -e . "$base.contract.json" >/dev/null 2>&1 \
    || die "$ENDPOINT/contract did not return JSON — capture kept at $base.contract.json"

  # The producer names itself in the Server header on every response, which is
  # the one provenance value that cannot be mistyped by whoever runs this.
  local server; server="$(grep -i '^server:' "$headers" | tr -d '\r' | cut -d' ' -f2- || printf 'unknown')"
  jq -n --arg label "$label" --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        --arg server "${server:-unknown}" --arg endpoint "$ENDPOINT" \
        --arg contractSha "$(sha256_of "$base.contract.json")" \
     '{label:$label, capturedAt:$at, producer:$server, endpoint:$endpoint, contractSha256:$contractSha}' \
     > "$base.provenance.json"

  printf '%s  %s\n' "$label" "$(summarize "$base.contract.json")"
  printf '  producer %s  →  %s\n' "${server:-unknown}" "$base.contract.json"
}

# Always print the statistics that made past bugs visible, pass or fail. The
# retimer collision was found because someone printed keyed *and* distinct.
summarize() {
  jq -r '
    def keyed: [.nodes[]? | select(.unitKey)];
    "\(.nodes|length) nodes, \(keyed|length) keyed / \([keyed[].unitKey]|unique|length) distinct keys, " +
    "\([.nodes[]? | select(.kind=="display")]|length) displays, " +
    "\([.nodes[]? | select(.builtIn==true)]|length) built-in"
  ' "$1"
}

# ---------------------------------------------------------------- compare ---
cmd_compare() {
  local a="${1:-}" b="${2:-}"
  [ -n "$b" ] || die "usage: hwkit.sh compare <labelA> <labelB>"
  local dir; dir="$(run_dir)"
  local fa="$dir/captures/$a.contract.json" fb="$dir/captures/$b.contract.json"
  [ -f "$fa" ] && [ -f "$fb" ] || die "missing capture: need $a and $b"

  printf '%s → %s\n' "$a" "$b"
  printf '  %s\n  %s\n' "$(summarize "$fa")" "$(summarize "$fb")"
  printf '  lost:\n'
  jq -r --slurpfile other "$fb" '
    [$other[0].nodes[].id] as $keep
    | [.nodes[] | select(.id as $i | ($keep | index($i)) | not)]
    | if length == 0 then "    (none)" else .[] | "    \(.name)  [\(.id)]" end' "$fa"
  printf '  gained:\n'
  jq -r --slurpfile other "$fa" '
    [$other[0].nodes[].id] as $had
    | [.nodes[] | select(.id as $i | ($had | index($i)) | not)]
    | if length == 0 then "    (none)" else .[] | "    \(.name)  [\(.id)]" end' "$fb"
}

# ----------------------------------------------------------------- verify ---
PASS=0; FAIL=0; UNKNOWN=0
ok()      { printf '  PASS     %s\n' "$*"; PASS=$((PASS+1)); }
bad()     { printf '  FAIL     %s\n' "$*"; FAIL=$((FAIL+1)); }
unknown() { printf '  UNKNOWN  %s\n' "$*"; UNKNOWN=$((UNKNOWN+1)); }

cmd_verify() {
  local rig="${1:-}"
  local dir; dir="$(run_dir)"
  local captures; captures="$(ls "$dir/captures"/*.contract.json 2>/dev/null || true)"
  [ -n "$captures" ] || die "no captures in $dir — run 'hwkit.sh capture <label>' first"

  printf 'run %s\n' "$dir"
  for f in $captures; do
    printf '%s\n' "$(basename "$f" .contract.json)"
    verify_invariants "$f"
    [ -n "$rig" ] && verify_rig "$f" "$rig"
  done
  verify_across "$dir"

  printf '\n%s passed, %s failed, %s unknown\n' "$PASS" "$FAIL" "$UNKNOWN"
  # Unknown is not success. A kit that reports PASS for hardware that was not
  # attached is worse than no kit, because it retires a question without
  # answering it.
  [ "$FAIL" -eq 0 ] || return 1
}

verify_invariants() {
  local f="$1"

  local hostId; hostId="$(jq -r '.host.id // ""' "$f")"
  if [ -z "$hostId" ]; then
    unknown "host.id absent — producer predates identity, or has none it can stand behind"
  elif printf '%s' "$hostId" | grep -Eq '^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'; then
    ok "host.id is a random UUIDv4"
  else
    bad "host.id is not a UUIDv4: $hostId"
  fi

  # The invariant the retimer collision violated: unit keys identify units, so
  # two of them being equal means two devices were merged into one.
  local keyed distinct
  keyed="$(jq '[.nodes[]? | select(.unitKey)] | length' "$f")"
  distinct="$(jq '[.nodes[]?.unitKey | select(.)] | unique | length' "$f")"
  if [ "$keyed" -eq 0 ]; then
    unknown "no keyed nodes — nothing attached reports a serial, so unit identity is untested here"
  elif [ "$keyed" -eq "$distinct" ]; then
    ok "$keyed keyed nodes, all distinct"
  else
    bad "$keyed keyed nodes but only $distinct distinct keys — unrelated devices share an identity"
    jq -r '[.nodes[]? | select(.unitKey)] | group_by(.unitKey)[] | select(length > 1)
           | "           shared \(.[0].unitKey): " + ([.[].name] | join(", "))' "$f"
  fi

  local badFormat; badFormat="$(jq '[.nodes[]?.unitKey | select(. and (test("^[0-9a-f]{16}$") | not))] | length' "$f")"
  [ "$badFormat" -eq 0 ] || bad "$badFormat unitKey values are not 16 hex characters"

  # A key without a model cannot mean "this unit", because the same serial
  # string is reported by unrelated products.
  local unmodelled; unmodelled="$(jq '[.nodes[]? | select(.unitKey and (.vidPid | not))] | length' "$f")"
  if [ "$unmodelled" -eq 0 ]; then
    [ "$keyed" -eq 0 ] || ok "every keyed node carries a VID:PID"
  else
    bad "$unmodelled keyed nodes have no vidPid — the key cannot be model-scoped"
  fi
}

# Ground truth you declared, compared mechanically. This is what replaces a
# person squinting at the output and deciding it looks about right.
verify_rig() {
  local f="$1" rig="$2"
  [ -f "$rig" ] || die "rig file not found: $rig"

  local count; count="$(jq '.devices | length' "$rig")"
  local i=0
  while [ "$i" -lt "$count" ]; do
    local label match expectBuiltIn expectKeyed found
    label="$(jq -r ".devices[$i].label" "$rig")"
    match="$(jq -r ".devices[$i].match" "$rig")"
    expectBuiltIn="$(jq -r ".devices[$i].expect.builtIn // \"\"" "$rig")"
    expectKeyed="$(jq -r ".devices[$i].expect.keyed // \"\"" "$rig")"

    found="$(jq --arg m "$match" '[.nodes[]? | select(.name | test($m; "i"))] | length' "$f")"
    if [ "$found" -eq 0 ]; then
      # Declared but absent is a question this run did not answer.
      unknown "$label — not present in this capture"
    else
      if [ -n "$expectBuiltIn" ]; then
        local actual; actual="$(jq -r --arg m "$match" 'first(.nodes[]? | select(.name | test($m; "i"))) | .builtIn // false' "$f")"
        [ "$actual" = "$expectBuiltIn" ] \
          && ok "$label builtIn=$actual" \
          || bad "$label builtIn=$actual, declared $expectBuiltIn"
      fi
      if [ -n "$expectKeyed" ]; then
        local hasKey; hasKey="$(jq -r --arg m "$match" 'first(.nodes[]? | select(.name | test($m; "i"))) | if .unitKey then "true" else "false" end' "$f")"
        [ "$hasKey" = "$expectKeyed" ] \
          && ok "$label keyed=$hasKey" \
          || bad "$label keyed=$hasKey, declared $expectKeyed"
      fi
    fi
    i=$((i+1))
  done

  # The serial must never leave the machine. Declared here so the check is
  # against the actual strings on your desk, not against a guess.
  local serials; serials="$(jq -r '.neverExport[]? // empty' "$rig")"
  if [ -z "$serials" ]; then
    unknown "no neverExport strings declared — serial leakage is unchecked"
  else
    local leaked=0
    while IFS= read -r secret; do
      [ -z "$secret" ] && continue
      if grep -qiF "$secret" "$f"; then bad "declared secret appears in the document: $secret"; leaked=1; fi
    done <<< "$serials"
    [ "$leaked" -eq 0 ] && ok "no declared serial appears anywhere in the document"
  fi
}

# Invariants that only exist between captures — the ones a single snapshot
# cannot test, which is why the procedure asks for a restart.
verify_across() {
  local dir="$1"
  local ids; ids="$(jq -r '.host.id // empty' "$dir"/captures/*.contract.json 2>/dev/null | sort -u | wc -l | tr -d ' ')"
  local n; n="$(ls "$dir"/captures/*.contract.json | wc -l | tr -d ' ')"
  if [ "$n" -lt 2 ]; then
    unknown "only one capture — host.id stability across a restart is untested"
  elif [ "$ids" -eq 1 ]; then
    ok "host.id identical across all $n captures"
  else
    bad "host.id changed between captures — one endpoint is being reported as several"
  fi
}

# ----------------------------------------------------------------- report ---
cmd_report() {
  local dir; dir="$(run_dir)"
  local out="$dir/report.md"
  {
    printf '# Hardware run %s\n\n' "$(basename "$dir")"
    printf '| capture | producer | summary |\n|---|---|---|\n'
    for f in "$dir"/captures/*.contract.json; do
      local label; label="$(basename "$f" .contract.json)"
      printf '| %s | %s | %s |\n' "$label" \
        "$(jq -r '.producer' "$dir/captures/$label.provenance.json" 2>/dev/null || printf unknown)" \
        "$(summarize "$f")"
    done
    printf '\n## Verify\n\n```\n'
    cmd_verify "${1:-}" 2>&1 || true
    printf '```\n'
  } > "$out"
  printf 'wrote %s\n' "$out"
}

require_tools
case "${1:-}" in
  capture) shift; cmd_capture "$@" ;;
  compare) shift; cmd_compare "$@" ;;
  verify)  shift; cmd_verify "$@" ;;
  report)  shift; cmd_report "$@" ;;
  *) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
