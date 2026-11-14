param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("win-x64", "linux-x64", "linux-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$moduleProject = Join-Path $repoRoot "src\DiskPulse.PulseModule\DiskPulse.PulseModule.csproj"
$helperProject = Join-Path $repoRoot "src\DiskPulse.Helper\DiskPulse.Helper.csproj"
$modulesOutDir = Join-Path $repoRoot "publish\modules"

& (Join-Path $PSScriptRoot "restore-contracts.ps1")

$version = ([string](dotnet msbuild $moduleProject -nologo -getProperty:Version)).Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Некорректная версия модуля: $version"
}

$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("diskpulse-module-pkg-" + [System.Guid]::NewGuid().ToString("N"))
$helperStagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("diskpulse-helper-pkg-" + [System.Guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Force -Path $stagingDir, $helperStagingDir, $modulesOutDir | Out-Null

    if (-not $NoRestore) {
        dotnet restore $moduleProject --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Restore DiskPulse.PulseModule завершился с ошибкой: $LASTEXITCODE" }

        dotnet restore $helperProject --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Restore DiskPulse.Helper ($Runtime) завершился с ошибкой: $LASTEXITCODE" }
    }

    dotnet publish $moduleProject `
        --configuration Release `
        --no-self-contained `
        --output $stagingDir `
        --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Publish DiskPulse.PulseModule завершился с ошибкой: $LASTEXITCODE" }

    dotnet publish $helperProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $helperStagingDir `
        --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Publish DiskPulse.Helper ($Runtime) завершился с ошибкой: $LASTEXITCODE" }

    $helperName = if ($Runtime.StartsWith("win-")) { "DiskPulse.Helper.exe" } else { "DiskPulse.Helper" }
    $helperSource = Join-Path $helperStagingDir $helperName
    if (!(Test-Path -LiteralPath $helperSource -PathType Leaf)) {
        throw "Не найден собранный helper: $helperSource"
    }
    Copy-Item -LiteralPath $helperSource -Destination (Join-Path $stagingDir $helperName) -Force

    # Удаляем дубликаты общих сборок хоста PCPulse и отладочные символы
    $excludedPatterns = @(
        "PCPulse.Contracts*",
        "Avalonia*",
        "MicroCom.*",
        "SkiaSharp*",
        "HarfBuzzSharp*",
        "Tmds.DBus*",
        "*.pdb"
    )

    foreach ($pattern in $excludedPatterns) {
        Get-ChildItem -LiteralPath $stagingDir -Filter $pattern -File | Remove-Item -Force
    }

    $runtimesDir = Join-Path $stagingDir "runtimes"
    if (Test-Path -LiteralPath $runtimesDir) {
        Remove-Item -LiteralPath $runtimesDir -Recurse -Force
    }

    # Проверка обязательных файлов модуля и манифеста
    $manifestPath = Join-Path $stagingDir "module.json"
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "В корне модуля отсутствует module.json"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $version) {
        throw "Версия в module.json ($($manifest.version)) не совпадает с MSBuild Version ($version)"
    }

    $entryAssembly = Join-Path $stagingDir $manifest.entryAssembly
    if (!(Test-Path -LiteralPath $entryAssembly -PathType Leaf)) {
        throw "Отсутствует entryAssembly: $entryAssembly"
    }

    $duplicateCheck = Get-ChildItem -LiteralPath $stagingDir -File | Where-Object {
        $_.Name -like "PCPulse.Contracts*" -or $_.Name -like "Avalonia*"
    }
    if ($duplicateCheck) {
        throw "Обнаружены конфликтующие сборки хоста в пакете модуля: $($duplicateCheck.Name -join ', ')"
    }

    $manifestOut = Join-Path $modulesOutDir "diskpulse-module-$version.manifest.json"
    Copy-Item -LiteralPath $manifestPath -Destination $manifestOut -Force

    $zipOut = Join-Path $modulesOutDir "diskpulse-module-$version-$Runtime.zip"
    if (Test-Path -LiteralPath $zipOut) {
        Remove-Item -LiteralPath $zipOut -Force
    }

    $zip = [System.IO.Compression.ZipFile]::Open($zipOut, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $basePrefix = $stagingDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $filesToPack = Get-ChildItem -LiteralPath $stagingDir -Recurse -File
        foreach ($file in $filesToPack) {
            $relPath = $file.FullName.Substring($basePrefix.Length).Replace('\', '/')
            $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                $file.FullName,
                $relPath,
                [System.IO.Compression.CompressionLevel]::Optimal)

            if (-not $Runtime.StartsWith("win-") -and $relPath -eq $helperName) {
                # 0100755 (-rwxr-xr-x) для Linux executable helper
                $entry.ExternalAttributes = (0x81ED -shl 16)
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $zipOut -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$zipOut.sha256", $hash, [System.Text.Encoding]::ASCII)

    Write-Host "Packaged PCPulse module:"
    Write-Host "  Manifest: $manifestOut"
    Write-Host "  Archive : $zipOut"
    Write-Host "  SHA-256 : $hash"
}
finally {
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $helperStagingDir) { Remove-Item -LiteralPath $helperStagingDir -Recurse -Force -ErrorAction SilentlyContinue }
}
