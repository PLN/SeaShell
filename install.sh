#!/bin/sh
# SeaShell installer — curl -fsSL https://raw.githubusercontent.com/PLN/SeaShell/main/install.sh | sh
set -e

echo ""
echo "{~} SeaShell Installer"
echo ""

# 1. Check .NET 10
dotnet_version=$(dotnet --version 2>/dev/null || true)
case "$dotnet_version" in
	10.*) ;;
	*)
		echo "ERROR: .NET 10 SDK is required (found: $dotnet_version)"
		echo "  Install from: https://dotnet.microsoft.com/download/dotnet/10.0"
		exit 1
		;;
esac
echo "  .NET SDK:  $dotnet_version"

# 2. Stop running SeaShell processes before install/update
if command -v sea >/dev/null 2>&1; then
	sea --daemon-stop 2>/dev/null || true
fi
pkill -f seashell-daemon 2>/dev/null || true
pkill -f seashell-elevator 2>/dev/null || true
dotnet build-server shutdown 2>/dev/null || true
sleep 0.5
echo "  Stopped running instances."

# 3. Install or update
if dotnet tool list -g 2>/dev/null | grep -q '^seashell '; then
	echo "  Updating SeaShell..."
	dotnet tool update -g SeaShell
else
	echo "  Installing SeaShell..."
	dotnet tool install -g SeaShell
fi

# Verify (dotnet tools path may not be in PATH yet)
export PATH="$HOME/.dotnet/tools:$PATH"
version=$(sea --version 2>/dev/null || true)
if [ -z "$version" ]; then
	echo "ERROR: sea not found on PATH after install."
	echo "  Add ~/.dotnet/tools to your PATH."
	exit 1
fi
echo "  $version"

echo ""
echo "{~} Done! Run 'sea' to start the REPL."
echo ""
