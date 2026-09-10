#!/usr/bin/env bash
set +e

config="${1:-compat-radar.json}"
report="${2:-compat-radar-report.json}"
compat-radar check --config "$config" --format text --report "$report"
status=$?

summary_path="${GITHUB_STEP_SUMMARY:-${COMPATRADAR_ACTION_SUMMARY:-${RUNNER_TEMP:-}/compatradar-action-summary.md}}"
if [[ -n "$summary_path" && -f "$report" ]]; then
  mkdir -p "$(dirname "$summary_path")"
  {
    echo '## CompatRadar report'
    cat "$report"
  } >> "$summary_path"
fi

if [[ "$status" -eq 1 ]]; then
  echo "::error file=$report::CompatRadar confirmed a future compatibility regression."
fi

exit "$status"
