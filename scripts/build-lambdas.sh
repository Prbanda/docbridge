#!/bin/bash
# Builds the Lambda deployment packages (ADR-011/017: framework-dependent, dotnet10 managed runtime).
# Output: dist/lambda/{fanout,delivery,ws,maintenance,db-bootstrap}.zip — read by Terraform on create and
# shipped by update-function-code afterwards (infra/modules/lambda).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)

declare -A PROJECTS=(
  [fanout]=workers/fanout/DocBridge.Worker.Fanout.csproj
  [delivery]=workers/delivery/DocBridge.Worker.Delivery.csproj
  [ws]=workers/ws/DocBridge.Worker.Ws.csproj
  [maintenance]=workers/maintenance/DocBridge.Worker.Maintenance.csproj
  [db-bootstrap]=tools/db-bootstrap/DocBridge.DbBootstrap.csproj
)

mkdir -p dist/lambda
for name in "${!PROJECTS[@]}"; do
  out="dist/lambda/$name"
  rm -rf "$out" "dist/lambda/$name.zip"
  dotnet publish "${PROJECTS[$name]}" -c Release -r linux-x64 --self-contained false -o "$out" -p:GenerateDocumentationFile=false >/dev/null
  if command -v zip >/dev/null 2>&1; then
    (cd "$out" && zip -qr "../$name.zip" .)
  else
    # Git Bash on Windows has no zip. ZipFile.CreateFromDirectory writes '/' separators (Compress-Archive on
    # Windows PowerShell can write '\', which Lambda's Linux runtime can't resolve).
    powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('$(cygpath -w "$ROOT/$out")', '$(cygpath -w "$ROOT/dist/lambda/$name.zip")')"
  fi
  echo "built dist/lambda/$name.zip ($(du -h "dist/lambda/$name.zip" | cut -f1))"
done
