$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\DiskPulse.App\DiskPulse.App.csproj"
$helperProject = Join-Path $repoRoot "src\DiskPulse.Helper\DiskPulse.Helper.csproj"
$outputPath = Join-Path $repoRoot "publish\win-x64"

& (Join-Path $PSScriptRoot "restore-contracts.ps1")

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

foreach ($project in @($appProject, $helperProject)) {
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Восстановление зависимостей $project завершилось с ошибкой: $LASTEXITCODE"
    }

    dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $outputPath `
        --no-restore

    if ($LASTEXITCODE -ne 0) {
        throw "Публикация $project завершилась с ошибкой: $LASTEXITCODE"
    }
}

Get-ChildItem -LiteralPath $outputPath -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Published: $outputPath\DiskPulse.exe and $outputPath\DiskPulse.Helper.exe"
