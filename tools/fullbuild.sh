#!/usr/bin/env bash
# Baut die XVE-Solution im Debug-Modus mit begrenzter Prozessorzahl.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-6}"
export NUMBER_OF_PROCESSORS="$DOTNET_PROCESSOR_COUNT"
export MSBUILDDISABLENODEREUSE=1

exec dotnet build "$repo_root/XamlVisualEditor.slnx" \
    --configuration Debug --no-incremental -maxcpucount:1 \
    -p:UseSharedCompilation=false
