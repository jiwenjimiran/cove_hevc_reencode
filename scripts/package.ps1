param([string]$Configuration = "Release", [string]$Version, [switch]$NoRestore)
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifest = Get-Content (Join-Path $root "src/HevcReencode/extension.json") -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $manifest.version }
if ($Version -ne $manifest.version) { throw "Version does not match manifest." }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Expected a semantic release version." }
$extensionSource = Get-Content (Join-Path $root "src/HevcReencode/HevcReencodeExtension.cs") -Raw
if ($extensionSource -notmatch ('public string Version => "' + [regex]::Escape($Version) + '"')) { throw "Extension class version does not match manifest." }
$project = Join-Path $root "src/HevcReencode/HevcReencode.csproj"
$publishDir = [IO.Path]::GetFullPath((Join-Path $root "artifacts/extension"))
$zipPath = Join-Path $root "artifacts/$($manifest.id)-$Version.zip"
if (-not $publishDir.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Publish directory outside workspace." }
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (-not $NoRestore) {
    dotnet restore $project -p:UseLocalCovePlugins=false
    if ($LASTEXITCODE -ne 0) { throw "Restore failed." }
}
dotnet build $project -c $Configuration --no-restore -p:UseLocalCovePlugins=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
dotnet publish $project -c $Configuration -o $publishDir --no-build --no-restore -p:UseLocalCovePlugins=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
foreach ($file in @($manifest.entryDll, 'extension.json', $manifest.jsBundle, $manifest.cssBundle)) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $file))) { throw "Missing package file: $file" }
}
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output "Package: $zipPath"
