#!/bin/sh
# SeaShell installer — curl -fsSL https://raw.githubusercontent.com/PLN/SeaShell/main/install.sh | sh
set -e

echo ""
echo "{~} SeaShell Installer"
echo ""

# ── Configuration ────────────────────────────────────────────────────
INSTALL_DIR="${SEASHELL_INSTALL_DIR:-$HOME/.local/share/seashell/bin}"
BIN_DIR="$HOME/.local/bin"
REPO="PLN/SeaShell"

# Detect RID
detect_rid() {
	case "$(uname -s)" in
		Linux)
			# Check for musl (Alpine, Void, etc.)
			if ldd --version 2>&1 | grep -qi musl; then
				echo "linux-musl-x64"
			else
				echo "linux-x64"
			fi
			;;
		*)
			echo "Unsupported OS: $(uname -s)" >&2
			exit 1
			;;
	esac
}
RID=$(detect_rid)

# ── 1. Check .NET 10 ─────────────────────────────────────────────────
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
echo "  Platform:  $RID"

# ── 2. Stop running SeaShell processes ────────────────────────────────
if [ -x "$INSTALL_DIR/sea" ]; then
	"$INSTALL_DIR/sea" --daemon-stop 2>/dev/null || true
fi
pkill -f seashell-daemon 2>/dev/null || true
pkill -f seashell-elevator 2>/dev/null || true
sleep 0.5
echo "  Stopped running instances."

# ── 3. Download or locate archive ────────────────────────────────────
ARCHIVE=""
if [ -n "$SEASHELL_ARCHIVE" ] && [ -f "$SEASHELL_ARCHIVE" ]; then
	ARCHIVE="$SEASHELL_ARCHIVE"
	echo "  Using local archive: $ARCHIVE"
else
	echo "  Finding latest release..."
	RELEASE_URL="https://api.github.com/repos/$REPO/releases/latest"
	DOWNLOAD_URL=$(curl -fsSL "$RELEASE_URL" | grep "browser_download_url.*$RID.*\.zip" | head -1 | cut -d'"' -f4)
	if [ -z "$DOWNLOAD_URL" ]; then
		echo "ERROR: No $RID archive found in latest release"
		exit 1
	fi
	ARCHIVE="$(mktemp -d)/seashell-$RID.zip"
	echo "  Downloading $(basename "$DOWNLOAD_URL")..."
	curl -fsSL "$DOWNLOAD_URL" -o "$ARCHIVE"
fi

# ── 4. Extract to install directory ──────────────────────────────────
echo "  Installing to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
unzip -o -q "$ARCHIVE" -d "$INSTALL_DIR"

# Set execute bit on apphosts
chmod +x "$INSTALL_DIR/sea" 2>/dev/null || true
chmod +x "$INSTALL_DIR/seashell-daemon" 2>/dev/null || true
chmod +x "$INSTALL_DIR/seashell-elevator" 2>/dev/null || true

# Clean up downloaded archive (but not if user-provided)
if [ -z "$SEASHELL_ARCHIVE" ]; then
	rm -f "$ARCHIVE"
fi

# ── 5. Symlink to PATH ──────────────────────────────────────────────
mkdir -p "$BIN_DIR"
ln -sf "$INSTALL_DIR/sea" "$BIN_DIR/sea"
echo "  Symlinked sea → $BIN_DIR/sea"

# Ensure ~/.local/bin is on PATH
export PATH="$BIN_DIR:$PATH"

# ── 6. Verify ────────────────────────────────────────────────────────
version=$(sea --version 2>/dev/null || true)
if [ -z "$version" ]; then
	echo "ERROR: sea not working after install."
	echo "  Ensure $BIN_DIR is on your PATH."
	exit 1
fi
echo "  $version"

echo ""
echo "{~} Done! Run 'sea' to start the REPL."
echo ""
if ! echo "$PATH" | grep -q "$BIN_DIR"; then
	echo "  NOTE: Add $BIN_DIR to your PATH:"
	echo "    export PATH=\"$BIN_DIR:\$PATH\""
	echo ""
fi
