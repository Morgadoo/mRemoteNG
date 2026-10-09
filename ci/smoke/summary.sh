#!/usr/bin/env bash
# Prints a markdown summary of an `mRemoteNG.Avalonia --smoke-test` run (for $GITHUB_STEP_SUMMARY).
# Usage: ci/smoke/summary.sh <report-dir> <label>
set -u
dir="${1:-smoke-report}"
label="${2:-}"

echo "#### App smoke test: ${label}"
echo
if [ -f "$dir/smoke-report.md" ]; then
  cat "$dir/smoke-report.md"
  echo
  echo "<details><summary>smoke-report.json</summary>"
  echo
  echo '```json'
  cat "$dir/smoke-report.json"
  echo
  echo '```'
  echo
  echo "</details>"
else
  echo "❌ No report was written: the app did not start, or it was killed before finishing. See the step log."
fi
echo
