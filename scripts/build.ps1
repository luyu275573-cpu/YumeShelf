$ErrorActionPreference = "Stop"

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($null -ne $dotnetCommand) {
    $dotnetCommand.Path
} else {
    Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe'
}

if (-not (Test-Path $dotnetPath)) {
    throw ".NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again."
}

& $dotnetPath restore "$PSScriptRoot\..\YumeShelf.sln"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnetPath build "$PSScriptRoot\..\YumeShelf.sln" --configuration Release
exit $LASTEXITCODE
