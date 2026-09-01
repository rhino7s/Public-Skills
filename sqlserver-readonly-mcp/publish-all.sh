#!/usr/bin/env bash
set -euo pipefail

project_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
project_path="$project_directory/src/SqlServerReadonlyMcp/SqlServerReadonlyMcp.csproj"
configuration="${CONFIGURATION:-Release}"
runtime_identifiers=(win-x64 linux-x64 osx-x64 osx-arm64)

if ! command -v dotnet >/dev/null 2>&1; then
    printf '%s\n' '找不到 dotnet。请先安装 .NET 10 SDK。' >&2
    exit 1
fi

for runtime_identifier in "${runtime_identifiers[@]}"; do
    dotnet publish "$project_path" \
        --configuration "$configuration" \
        --runtime "$runtime_identifier" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        --output "$project_directory/publish/$runtime_identifier"
done
