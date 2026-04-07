#!/bin/bash
# Publish SeaShell binaries for all supported platforms.
# Usage: ./publish.sh
set -e

PROJ="$(cd "$(dirname "$0")" && pwd)"
OUT="$PROJ/publish"

rm -rf "$OUT"

for RID in win-x64 linux-x64; do
    echo "=== Publishing for $RID ==="
    dotnet publish "$PROJ/src/SeaShell.Cli/SeaShell.Cli.csproj" \
        -c Release -r $RID --self-contained false -o "$OUT/$RID" -p:PublishSingleFile=false
    dotnet publish "$PROJ/src/SeaShell.Daemon/SeaShell.Daemon.csproj" \
        -c Release -r $RID --self-contained false -o "$OUT/$RID" -p:PublishSingleFile=false
    dotnet publish "$PROJ/src/SeaShell.Elevator/SeaShell.Elevator.csproj" \
        -c Release -r $RID --self-contained false -o "$OUT/$RID" -p:PublishSingleFile=false
done

echo ""
echo "=== Published ==="
for RID in win-x64 linux-x64; do
    echo "$RID:"
    ls -1 "$OUT/$RID/sea"* "$OUT/$RID/seashell-"* 2>/dev/null | sed 's|.*/|  |'
done

echo ""
echo "Pack with: dotnet pack pkg/SeaShell.Binaries.csproj -c Release -o ./nupkg"
