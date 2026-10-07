$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$installDir = Join-Path $env:LOCALAPPDATA 'Programs/Clothesline'
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$sourceExe = Join-Path $PSScriptRoot 'bin/Clothesline.exe'
$targetExe = Join-Path $installDir 'Clothesline.exe'
$running = Get-Process Clothesline -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $targetExe }
if ($running) { throw 'Quit Clothesline from its tray menu before updating.' }
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $installDir 'README.md') -Force
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcutSpecs = @(
    @{ Folder = [Environment]::GetFolderPath('Desktop'); Arguments = '' },
    @{ Folder = [Environment]::GetFolderPath('Programs'); Arguments = '' },
    @{ Folder = [Environment]::GetFolderPath('Startup'); Arguments = '--background' }
)
foreach ($spec in $shortcutSpecs) {
    $shortcut = $shortcutShell.CreateShortcut((Join-Path $spec.Folder 'Clothesline.lnk'))
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Arguments = $spec.Arguments
    $shortcut.Description = 'Your last 10 copies, hung out to dry. Press Home.'
    $shortcut.Save()
}
Start-Process -FilePath $targetExe
Write-Output "Installed and started: $targetExe"
