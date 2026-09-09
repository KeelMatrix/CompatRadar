#!/usr/bin/env bash
set +e

config="${1:-compat-radar.json}"
report="${2:-compat-radar-report.json}"
compat-radar check --config "$config" --format text --report "$report"
status=$?

if [[ -n "${GITHUB_STEP_SUMMARY:-}" && -f "$report" ]]; then
  {
    echo '## CompatRadar report'
    cat "$report"
  } >> "$GITHUB_STEP_SUMMARY"
fi

if [[ "$status" -eq 1 ]]; then
  echo "::error file=$report::CompatRadar confirmed a future compatibility regression."
fi

exit "$status"
