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

& $dotnetPath run --project "$PSScriptRoot\..\src\YumeShelf\YumeShelf.csproj"
