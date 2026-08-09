#!/usr/bin/env bash
set -euo pipefail

readonly warning_limit=300
readonly hard_limit=500
readonly legacy_baselines_file='scripts/file-size-legacy.tsv'
readonly source_path_pattern='^(src|tests)/.*[.]cs$'

declare -A legacy_baselines
while IFS=$'\t' read -r path baseline reason; do
  if [[ -z "${path}" || "${path}" == \#* ]]; then
    continue
  fi
  legacy_baselines["${path}"]="${baseline}"
done < "${legacy_baselines_file}"

violation_count=0
warning_count=0

while IFS= read -r source_file; do
  case "${source_file}" in
    */Migrations/*|*.g.cs|*.generated.cs|*.Designer.cs)
      continue
      ;;
  esac

  line_count="$(awk 'END { print NR }' "${source_file}")"
  if (( line_count > hard_limit )); then
    allowed_baseline="${legacy_baselines[${source_file}]:-}"
    if [[ -z "${allowed_baseline}" ]]; then
      printf 'ERROR: %s has %d lines; non-generated C# files may not exceed %d.\n' \
        "${source_file}" "${line_count}" "${hard_limit}"
      ((violation_count += 1))
    elif (( line_count > allowed_baseline )); then
      printf 'ERROR: %s grew to %d lines; its justified legacy ceiling is %d.\n' \
        "${source_file}" "${line_count}" "${allowed_baseline}"
      ((violation_count += 1))
    else
      printf 'WARNING: %s remains a justified legacy exception at %d lines (ceiling %d).\n' \
        "${source_file}" "${line_count}" "${allowed_baseline}"
      ((warning_count += 1))
    fi
  elif (( line_count > warning_limit )); then
    printf 'WARNING: %s has %d lines; prefer composition before it reaches the %d-line hard limit.\n' \
      "${source_file}" "${line_count}" "${hard_limit}"
    ((warning_count += 1))
  fi
done < <(git ls-files --cached --others --exclude-standard \
  | grep -E "${source_path_pattern}" \
  | sort)

printf 'C# file-size check: %d warnings, %d violations.\n' "${warning_count}" "${violation_count}"
exit "${violation_count}"
