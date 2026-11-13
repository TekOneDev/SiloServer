#!/usr/bin/env bash
# Builds the Windows package: Silo-Server-<version>-win-x64.zip (installer + self-contained app).
# Runs on Linux or Windows (Git Bash) with the .NET 10 SDK.
set -euo pipefail
cd "$(dirname "$0")"
VER=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
OUT=${1:-dist}
PKG="$OUT/Silo-Server-$VER-win-x64"
rm -rf "$PKG" && mkdir -p "$PKG/App"
dotnet test tests/Silo.Tests --nologo -v q
dotnet publish src/Silo.Server     -c Release -r win-x64 --self-contained -o "$PKG/App" --nologo -v q
dotnet publish src/Silo.PluginHost -c Release -r win-x64 --self-contained -o "$PKG/App" --nologo -v q
# Built-in plugin manifests (bin/plugins/<id>/plugin.json), written by the plugin host itself.
dotnet run --project src/Silo.PluginHost -c Release -- --manifests "$PKG/App/plugins"
rm -f "$PKG"/App/*.pdb
cp installer/* "$PKG/"
cp README.md "$PKG/README.md"
(cd "$OUT" && rm -f "Silo-Server-$VER-win-x64.zip" && zip -qr9 "Silo-Server-$VER-win-x64.zip" "Silo-Server-$VER-win-x64")
git archive --format=zip -o "$OUT/Silo-Server-$VER-source.zip" HEAD 2>/dev/null || true
echo "Built $OUT/Silo-Server-$VER-win-x64.zip"
