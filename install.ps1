# SeaShell installer — iex (irm https://raw.githubusercontent.com/PLN/SeaShell/main/install.ps1)
$ErrorActionPreference = 'Stop'

Write-Host "`n{~} SeaShell Installer`n" -ForegroundColor Cyan

# ── Configuration ────────────────────────────────────────────────────
$installDir = Join-Path $env:LOCALAPPDATA 'seashell\bin'
$repo = 'PLN/SeaShell'
$rid = 'win-x64'

# ── 1. Check .NET 10 ─────────────────────────────────────────────────
$dotnetVersion = $null
try { $dotnetVersion = (dotnet --version) } catch {}
if (-not $dotnetVersion -or -not $dotnetVersion.StartsWith('10.')) {
	Write-Host "ERROR: .NET 10 SDK is required (found: $dotnetVersion)" -ForegroundColor Red
	Write-Host "  Install from: https://dotnet.microsoft.com/download/dotnet/10.0"
	exit 1
}
Write-Host "  .NET SDK:  $dotnetVersion" -ForegroundColor Gray

# ── 2. Stop running SeaShell processes before install ─────────────────
$seaExe = Join-Path $installDir 'sea.exe'
if (Test-Path $seaExe) {
	try { & $seaExe --daemon-stop 2>$null | Out-Null } catch {}
}

# Stop via Task Scheduler
$taskFolder = '\SeaShell\'
$daemonTask = "SeaShell Daemon ($env:USERNAME)"
$elevatorTask = "SeaShell Elevator ($env:USERNAME)"
try { schtasks /End /TN "$taskFolder$daemonTask" 2>$null | Out-Null } catch {}
try { schtasks /End /TN "$taskFolder$elevatorTask" 2>$null | Out-Null } catch {}

# Kill any remaining
Get-Process seashell-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process seashell-elevator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Write-Host "  Stopped running instances." -ForegroundColor Gray

# ── 3. Download or locate archive ────────────────────────────────────
$archivePath = $null

# If SEASHELL_ARCHIVE is set, use that (local/offline install)
if ($env:SEASHELL_ARCHIVE -and (Test-Path $env:SEASHELL_ARCHIVE)) {
	$archivePath = $env:SEASHELL_ARCHIVE
	Write-Host "  Using local archive: $archivePath" -ForegroundColor Gray
} else {
	# Download latest release from GitHub
	Write-Host "  Finding latest release..." -ForegroundColor Gray
	$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -ErrorAction Stop
	$asset = $release.assets | Where-Object { $_.name -like "*$rid*.zip" } | Select-Object -First 1
	if (-not $asset) {
		Write-Host "ERROR: No $rid archive found in release $($release.tag_name)" -ForegroundColor Red
		exit 1
	}
	$archivePath = Join-Path $env:TEMP "seashell-$rid.zip"
	Write-Host "  Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)..." -ForegroundColor Gray
	Invoke-WebRequest $asset.browser_download_url -OutFile $archivePath -ErrorAction Stop
}

# ── 4. Extract to install directory ──────────────────────────────────
Write-Host "  Installing to $installDir..." -ForegroundColor Gray
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Extract — overwrite existing files
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
	foreach ($entry in $zip.Entries) {
		if ($entry.FullName.EndsWith('/')) { continue }
		$dest = Join-Path $installDir $entry.FullName
		[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
	}
} finally {
	$zip.Dispose()
}

# Clean up downloaded archive (but not if user-provided)
if (-not $env:SEASHELL_ARCHIVE) {
	Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
}

# ── 5. Add to PATH ──────────────────────────────────────────────────
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($userPath -notlike "*$installDir*") {
	[Environment]::SetEnvironmentVariable('PATH', "$installDir;$userPath", 'User')
	$env:PATH = "$installDir;$env:PATH"
	Write-Host "  Added $installDir to user PATH" -ForegroundColor Gray
}

# ── 6. Verify ────────────────────────────────────────────────────────
$version = & $seaExe --version 2>$null
if (-not $version) {
	Write-Host "ERROR: sea.exe not working after install." -ForegroundColor Red
	exit 1
}
Write-Host "  $version" -ForegroundColor Gray

# ── 7. Register daemon task ──────────────────────────────────────────
Write-Host "  Registering daemon task..." -ForegroundColor Gray
& $seaExe --install-daemon

# ── 8. Register elevator task (optional, needs elevation) ────────────
if (Get-Command gsudo -ErrorAction SilentlyContinue) {
	Write-Host "  Registering elevator task (UAC prompt)..." -ForegroundColor Gray
	try { gsudo $seaExe --install-elevator } catch {
		Write-Host "  Elevator registration skipped." -ForegroundColor Yellow
	}
} else {
	Write-Host "  Elevator registration skipped (gsudo not found)." -ForegroundColor Yellow
}

# ── 9. Associate file extensions ─────────────────────────────────────
Write-Host "  Associating .cs and .csw extensions..." -ForegroundColor Gray
& $seaExe --associate .cs
$seawExe = Join-Path $installDir 'seaw.exe'
if (Test-Path $seawExe) {
	& $seawExe --associate .csw
}

Write-Host "`n{~} Done! Run 'sea' to start the REPL.`n" -ForegroundColor Cyan
if ($userPath -notlike "*$installDir*") {
	Write-Host "  NOTE: Restart your terminal for PATH changes to take effect." -ForegroundColor Yellow
}
