param([string]$RestoreSource = 'https://api.nuget.org/v3/index.json')
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $packageRoot 'src\YumeShelf\YumeShelf.csproj'
$projectXml = New-Object System.Xml.XmlDocument
$projectXml.Load($projectFile)
$version = [string]$projectXml.Project.PropertyGroup.Version
$packageName = "YumeShelf-$version-win-x64"
$releaseRoot = Join-Path $packageRoot ('.runtime\releases\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$packageDir = Join-Path $releaseRoot $packageName
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
& dotnet publish $projectFile -c Release -r win-x64 --self-contained true --source $RestoreSource --artifacts-path (Join-Path $packageRoot '.runtime\checks') --output $packageDir --nologo -warnaserror -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
Copy-Item -LiteralPath (Join-Path $packageRoot 'YumeTestV1.md') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $packageRoot 'docs\release-guide.md') -Destination (Join-Path $packageDir 'README-Release.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-test-build.ps1') -Destination (Join-Path $packageDir 'Install.ps1')
$exe = Join-Path $packageDir 'YumeShelf.exe'
foreach ($required in @('coreclr.dll','hostfxr.dll','PresentationFramework.dll','Assets\YumeShelf.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageDir $required))) { throw "Missing package dependency: $required" }
}
$report = Join-Path $releaseRoot 'package-smoke.json'
$process = Start-Process -FilePath $exe -ArgumentList @('--verify-package', ('"' + $report + '"')) -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(30000)) { throw 'Package check exceeded 30 seconds; inspect the diagnostic process.' }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Package check failed.' }
$files = Get-ChildItem -LiteralPath $packageDir -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($packageDir.Length + 1).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$commit = & git -C $packageRoot rev-parse HEAD
$dirty = [bool](& git -C $packageRoot status --porcelain)
[ordered]@{ version=$version; runtime='win-x64'; selfContained=$true; sourceCommit=$commit; sourceDirty=$dirty; builtAt=(Get-Date).ToUniversalTime().ToString('o'); files=@($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageDir 'package-manifest.json') -Encoding UTF8
$zip = Join-Path $releaseRoot ($packageName + '.zip')
Compress-Archive -LiteralPath $packageDir -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$hash  $packageName.zip" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
Write-Output "Package: $zip"
Write-Output "SHA256: $hash"
Write-Output "Smoke evidence: $report"
