#!/usr/bin/env bash
set -euo pipefail

readonly expected_argument_count=1
readonly output_argument_position=1
readonly data_error_exit_code=65
readonly usage_error_exit_code=64
readonly output_creation_error_exit_code=73
readonly configuration_error_exit_code=78
readonly package_name="4sup"
readonly package_supplier="NFour"
readonly manifest_format="SPDX:2.2"
readonly namespace_base="https://github.com/JKamsker/AutoUpdater/sbom"
readonly release_configuration="Release"
readonly development_trust_root_acknowledgement="-p:NFourAllowDevelopmentTrustRoot=true"
readonly metadata_project="src/NFour.AutoUpdater.Cli/NFour.AutoUpdater.Cli.csproj"
readonly manifest_relative_path="_manifest/spdx_2.2/manifest.spdx.json"
readonly validation_report_name="validation.json"
readonly -a publish_names=("cli" "server" "gateway")
readonly -a publish_projects=(
  "$metadata_project"
  "src/NFour.AutoUpdater.Server/NFour.AutoUpdater.Server.csproj"
  "src/NFour.AutoUpdater.Gateway/NFour.AutoUpdater.Gateway.csproj"
)

if (( $# != expected_argument_count )); then
  echo "Usage: $0 <new-output-directory>" >&2
  exit "$usage_error_exit_code"
fi

if (( ${#publish_names[@]} != ${#publish_projects[@]} )); then
  echo "Each publish project must have exactly one output name." >&2
  exit "$configuration_error_exit_code"
fi

readonly requested_output_root="${!output_argument_position}"
if [[ -e "$requested_output_root" ]]; then
  echo "SBOM output path already exists: $requested_output_root" >&2
  exit "$output_creation_error_exit_code"
fi

readonly script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly repository_root="$(cd "$script_directory/.." && pwd -P)"
mkdir -p "$requested_output_root"
readonly output_root="$(cd "$requested_output_root" && pwd -P)"
readonly build_drop="$output_root/drop"
readonly validation_report="$output_root/$validation_report_name"
readonly source_revision="$(git -C "$repository_root" rev-parse HEAD)"

cd "$repository_root"
readonly package_version="$(dotnet msbuild "$metadata_project" -nologo -getProperty:Version)"
readonly namespace_unique_part="$package_version-$source_revision"

dotnet tool restore

for publish_index in "${!publish_projects[@]}"; do
  dotnet publish "${publish_projects[$publish_index]}" \
    --configuration "$release_configuration" \
    --output "$build_drop/${publish_names[$publish_index]}" \
    "$development_trust_root_acknowledgement"
done

dotnet tool run sbom-tool Generate \
  -b "$build_drop" \
  -bc "$repository_root" \
  -pn "$package_name" \
  -pv "$package_version" \
  -ps "$package_supplier" \
  -mi "$manifest_format" \
  -nsb "$namespace_base" \
  -nsu "$namespace_unique_part"

readonly manifest_path="$build_drop/$manifest_relative_path"
if [[ ! -s "$manifest_path" ]]; then
  echo "SBOM generator did not create a non-empty manifest: $manifest_path" >&2
  exit "$data_error_exit_code"
fi

dotnet tool run sbom-tool Validate \
  -b "$build_drop" \
  -mi "$manifest_format" \
  -n \
  -o "$validation_report"

if [[ ! -s "$validation_report" ]]; then
  echo "SBOM validator did not create a non-empty report: $validation_report" >&2
  exit "$data_error_exit_code"
fi

echo "Validated SBOM: $manifest_path"
