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

# 2. Install or update
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

# 3. Register daemon task
Write-Host "  Registering daemon task..." -ForegroundColor Gray
sea --install-daemon

# 4. Register elevator task (optional, needs elevation)
if (Get-Command gsudo -ErrorAction SilentlyContinue) {
	Write-Host "  Registering elevator task (UAC prompt)..." -ForegroundColor Gray
	try { gsudo sea --install-elevator } catch {
		Write-Host "  Elevator registration skipped." -ForegroundColor Yellow
	}
} else {
	Write-Host "  Elevator registration skipped (gsudo not found)." -ForegroundColor Yellow
	Write-Host "  Install gsudo and run: sea --install-elevator" -ForegroundColor Yellow
}

# 5. Associate .cs
Write-Host "  Associating .cs extension..." -ForegroundColor Gray
sea --associate .cs

Write-Host "`n{~} Done! Run 'sea' to start the REPL.`n" -ForegroundColor Cyan
