param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\YumeShelf'),
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'
$sourceDir = [IO.Path]::GetFullPath($PSScriptRoot)
$manifestPath = Join-Path $sourceDir 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Run Install.ps1 from the extracted YumeShelf test package.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Invalid package version.' }
$installBase = [IO.Path]::GetFullPath($InstallRoot)
$destination = Join-Path $installBase ([string]$manifest.version)
if ($destination.StartsWith($sourceDir + '\', [StringComparison]::OrdinalIgnoreCase) -or $sourceDir.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installation destination overlaps the package source.' }
if (Test-Path -LiteralPath $destination) { throw 'This version already has an install directory. Keep it or choose another InstallRoot; no files were overwritten.' }
# Validate the complete manifest before copying. No executing downloaded helpers or changing user data.
foreach ($entry in $manifest.files) {
    if ([IO.Path]::IsPathRooted($entry.path)) { throw 'Package manifest paths must be relative.' }
    $file = [IO.Path]::GetFullPath((Join-Path $sourceDir $entry.path))
    if (-not $file.StartsWith($sourceDir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes the source directory.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package hash mismatch: $($entry.path)" }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($entry in $manifest.files) {
    $target = Join-Path $destination $entry.path
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceDir $entry.path) -Destination $target
}
Copy-Item -LiteralPath $manifestPath -Destination $destination
New-Item -ItemType Directory -Path $ShortcutDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path $ShortcutDirectory 'YumeShelf.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $destination 'YumeShelf.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.IconLocation = (Join-Path $destination 'YumeShelf.exe') + ',0'
$shortcut.Save()
Write-Output "Installed: $destination"
Write-Output "Shortcut: $shortcutPath"
Write-Output 'User library/settings are preserved. Close the old app before launching this version.'
