param([string[]]$ScanRoots = @())
$ErrorActionPreference = "Stop"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Path } else { Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw ".NET 8 SDK is required." }
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifacts = Join-Path $projectRoot ".runtime\checks"
$publish = Join-Path $projectRoot ".runtime\publish"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnetPath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Check command failed (exit $LASTEXITCODE): dotnet $($Arguments -join ' ')" }
}
Push-Location $projectRoot
Start-Transcript -Path (Join-Path $artifacts ("check-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")) | Out-Null
try {
    Invoke-Dotnet -Arguments @("build", "YumeShelf.sln", "--configuration", "Release", "--artifacts-path", $artifacts, "--nologo", "-warnaserror")
    Invoke-Dotnet -Arguments @((Join-Path $artifacts "bin\YumeShelf.SettingsChecks\release\YumeShelf.SettingsChecks.dll"))
    $testGame = Join-Path $artifacts "bin\YumeShelf.TestGame\release\YumeShelf.TestGame.exe"
    Invoke-Dotnet -Arguments (@((Join-Path $artifacts "bin\YumeShelf.ReliabilityChecks\release\YumeShelf.ReliabilityChecks.dll"), $testGame) + $ScanRoots)
    Invoke-Dotnet -Arguments @("publish", "src\YumeShelf\YumeShelf.csproj", "--configuration", "Release", "--artifacts-path", $artifacts, "--self-contained", "false", "--output", $publish, "--nologo", "-warnaserror")
    foreach ($asset in @("YumeShelf.exe", "YumeShelf.dll", "YumeShelf.runtimeconfig.json", "Assets\DefaultCover.png", "Assets\YumeShelfIcon.png", "Assets\YumeShelf.ico", "Assets\Backgrounds\pink.png", "Assets\Backgrounds\blue.png", "Assets\Backgrounds\yellow.png")) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $asset))) { throw "Missing publish asset: $asset" }
    }
    $exe = Join-Path $publish "YumeShelf.exe"
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $projectXml = New-Object System.Xml.XmlDocument
    $projectXml.Load((Join-Path $projectRoot "src\YumeShelf\YumeShelf.csproj"))
    $expectedVersion = ([string]$projectXml.Project.PropertyGroup.Version).Split('-')[0] + '.0'
    if ($version.FileVersion -ne $expectedVersion) { throw "Missing native version resource." }
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
    try {
        if ($null -eq $icon) { throw "Missing native icon resource." }
        $bitmap = $icon.ToBitmap()
        try { $bitmap.Save((Join-Path $artifacts "published-icon.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $bitmap.Dispose() }
    } finally { if ($icon) { $icon.Dispose() } }
    Write-Output "All build, regression and framework-dependent publish checks passed."
    Write-Output "Publish output: $publish (requires .NET 8 Desktop Runtime)"
} finally { Stop-Transcript | Out-Null; Pop-Location }
