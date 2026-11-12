#!/usr/bin/env bash
# Deterministic quality gates. Run by the agent (/verify), by tools/run-prp.cs after a run, and by CI.
# Prints a log, then one JSON line with the result of each gate as the last line of output.
# Exit code: 0 if every gate that ran passed and none were skipped, 1 otherwise.
set -u
cd "$(dirname "$0")/.."

SLN=CompLedger.slnx
declare -A result=( [build]=skipped [format]=skipped [unit]=skipped [integration]=skipped )

run_gate() {   # name, command...
  local name=$1; shift
  echo "::: gate $name: $*"
  if "$@"; then result[$name]=pass; else result[$name]=fail; fi
}

if [ ! -f "$SLN" ]; then
  echo "::: $SLN not found: nothing to build yet, every gate skipped"
else
  run_gate build dotnet build "$SLN" -warnaserror --nologo -v quiet
  if [ "${result[build]}" = pass ]; then
    run_gate format dotnet format "$SLN" --verify-no-changes --no-restore
    [ -d tests/CompLedger.UnitTests ] && run_gate unit dotnet test tests/CompLedger.UnitTests --no-build --nologo
    if [ -d tests/CompLedger.IntegrationTests ]; then
      if docker info >/dev/null 2>&1; then
        run_gate integration dotnet test tests/CompLedger.IntegrationTests --no-build --nologo
      else
        echo "::: docker is not available: integration gate skipped (this does NOT count as a pass)"
      fi
    fi
  fi
fi

status=0
for g in build format unit integration; do [ "${result[$g]}" = pass ] || status=1; done
printf '{"build":"%s","format":"%s","unit":"%s","integration":"%s"}\n' \
  "${result[build]}" "${result[format]}" "${result[unit]}" "${result[integration]}"
exit $status
