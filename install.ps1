# SeaShell installer — iex (irm https://raw.githubusercontent.com/PLN/SeaShell/main/install.ps1)
$ErrorActionPreference = 'Stop'

Write-Host "`n{~} SeaShell Installer`n" -ForegroundColor Cyan

# 1. Check .NET 10
$dotnetVersion = $null
try { $dotnetVersion = (dotnet --version) } catch {}
if (-not $dotnetVersion -or -not $dotnetVersion.StartsWith('10.')) {
	Write-Host "ERROR: .NET 10 SDK is required (found: $dotnetVersion)" -ForegroundColor Red
	Write-Host "  Install from: https://dotnet.microsoft.com/download/dotnet/10.0"
	exit 1
}
Write-Host "  .NET SDK:  $dotnetVersion" -ForegroundColor Gray

# 2. Stop running SeaShell processes before install/update
#    Daemon and elevator lock DLLs in the tool store.
$taskFolder = '\SeaShell\'
$daemonTask = "SeaShell Daemon ($env:USERNAME)"
$elevatorTask = "SeaShell Elevator ($env:USERNAME)"

# Stop via IPC (works for any running daemon)
if (Get-Command sea -ErrorAction SilentlyContinue) {
	try { sea --daemon-stop 2>$null | Out-Null } catch {}
}

# Stop via Task Scheduler (in case IPC didn't reach it)
try { schtasks /End /TN "$taskFolder$daemonTask" 2>$null | Out-Null } catch {}
try { schtasks /End /TN "$taskFolder$elevatorTask" 2>$null | Out-Null } catch {}

# Kill any remaining seashell processes
Get-Process seashell-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process seashell-elevator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Shut down MSBuild server nodes (they can also lock tool store DLLs)
try { dotnet build-server shutdown 2>$null | Out-Null } catch {}

Start-Sleep -Milliseconds 500

Write-Host "  Stopped running instances." -ForegroundColor Gray

# 3. Install or update
$installed = dotnet tool list -g 2>$null | Select-String -Pattern '^seashell\s'
if ($installed) {
	Write-Host "  Updating SeaShell..." -ForegroundColor Gray
	dotnet tool update -g SeaShell
} else {
	Write-Host "  Installing SeaShell..." -ForegroundColor Gray
	dotnet tool install -g SeaShell
}

# Verify
$version = sea --version 2>$null
if (-not $version) {
	Write-Host "ERROR: sea not found on PATH after install." -ForegroundColor Red
	Write-Host "  Add %USERPROFILE%\.dotnet\tools to your PATH and restart your shell."
	exit 1
}
Write-Host "  $version" -ForegroundColor Gray

# 4. Register daemon task
Write-Host "  Registering daemon task..." -ForegroundColor Gray
sea --install-daemon

# 5. Register elevator task (optional, needs elevation)
if (Get-Command gsudo -ErrorAction SilentlyContinue) {
	Write-Host "  Registering elevator task (UAC prompt)..." -ForegroundColor Gray
	try { gsudo sea --install-elevator } catch {
		Write-Host "  Elevator registration skipped." -ForegroundColor Yellow
	}
} else {
	Write-Host "  Elevator registration skipped (gsudo not found)." -ForegroundColor Yellow
	Write-Host "  Install gsudo and run: sea --install-elevator" -ForegroundColor Yellow
}

# 6. Associate .cs
Write-Host "  Associating .cs extension..." -ForegroundColor Gray
sea --associate .cs

Write-Host "`n{~} Done! Run 'sea' to start the REPL.`n" -ForegroundColor Cyan
