param(
    [Parameter(Mandatory)]
    [ValidateSet('Enable', 'Disable', 'Uninstall')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
$name = 'Trace17'
$exe = Join-Path $PSScriptRoot "$name.exe"
$startup = [Environment]::GetFolderPath('Startup')
if (-not $startup -or [IO.Path]::GetPathRoot($startup) -ne 'C:\' -or
    [IO.Path]::GetPathRoot($PSScriptRoot) -ne 'C:\') {
    throw 'The application and the Windows Startup folder must both be on C:.'
}
$linkPath = Join-Path $startup "$name.lnk"
$legacyLink = Join-Path $startup 'ATHEREAL Health Monitor.lnk'
$shell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $linkPath) {
    $existing = $shell.CreateShortcut($linkPath)
    if ($existing.TargetPath -ne $exe -or $existing.Arguments) {
        throw "The existing shortcut points elsewhere. Review $linkPath."
    }
}

switch ($Action) {
    'Enable' {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing $exe." }
        if (Test-Path -LiteralPath $legacyLink) { Remove-Item -LiteralPath $legacyLink }
        $shortcut = $shell.CreateShortcut($linkPath)
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $PSScriptRoot
        $shortcut.Description = 'Trace17 hardware telemetry, FPS overlay, and WHEA Event 17 monitor'
        $shortcut.Save()
        Write-Output $linkPath
    }
    'Disable' {
        if (Test-Path -LiteralPath $linkPath) { Remove-Item -LiteralPath $linkPath }
        if (Test-Path -LiteralPath $legacyLink) { Remove-Item -LiteralPath $legacyLink }
        Write-Output 'Trace17 startup disabled. Use Exit in the tray to stop the current session.'
    }
    'Uninstall' {
        $running = Get-Process -Name $name -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exe }
        if ($running) { throw 'Use Exit on each Trace17 tray icon, then rerun Uninstall.' }
        if (Test-Path -LiteralPath $linkPath) { Remove-Item -LiteralPath $linkPath }
        if (Test-Path -LiteralPath $legacyLink) { Remove-Item -LiteralPath $legacyLink }
        foreach ($path in @($exe, (Join-Path $PSScriptRoot 'sound-muted.flag'))) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
        Write-Output "Trace17 uninstalled. Your logs and source are in $PSScriptRoot."
    }
}
