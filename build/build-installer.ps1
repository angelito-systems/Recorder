<#
.SYNOPSIS
  Builds the release: tests → self-contained single-file exe → (optional signing) → per-user MSI → (optional signing).
.PARAMETER FfmpegDir
  Folder with ffmpeg.exe (and its DLLs, for shared builds) to bundle into the installer. Omit to rely on an FFmpeg already
  installed on the target PC (PATH). Bundling distributes FFmpeg: use an LGPL build, or meet the GPL of the build you ship.
.PARAMETER CertThumbprint
  SHA-1 thumbprint of a code-signing certificate in the user's store. When given, the exe and the MSI are signed and
  timestamped, so Windows and future update checks can verify they came from Angelito Systems.
#>
param([string]$FfmpegDir, [string]$CertThumbprint, [string]$TimestampUrl = "http://timestamp.digicert.com", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$proj = "src\Angelito.ScreenStudio.Desktop\Angelito.ScreenStudio.Desktop.csproj"
$version = (dotnet msbuild $proj -getProperty:Version).Trim()
$publish = Join-Path $root "artifacts\publish"
$msi = Join-Path $root "artifacts\AngelitoScreenStudio-$version-x64.msi"
Write-Host "Angelito Screen Studio Pro $version"

if (-not $SkipTests) { dotnet test -c Release; if ($LASTEXITCODE) { throw "Las pruebas fallaron; no se genera el instalador." } }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained -o $publish `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:SatelliteResourceLanguages=es -p:DebugType=none
if ($LASTEXITCODE) { throw "dotnet publish falló." }

$includeFfmpeg = 0
if ($FfmpegDir) {
  if (-not (Test-Path (Join-Path $FfmpegDir "ffmpeg.exe"))) { throw "No hay ffmpeg.exe en $FfmpegDir" }
  New-Item -ItemType Directory -Force "$publish\ffmpeg" | Out-Null
  Copy-Item "$FfmpegDir\ffmpeg.exe", "$FfmpegDir\*.dll" "$publish\ffmpeg" -ErrorAction SilentlyContinue
  $includeFfmpeg = 1
}

function Sign($file) {
  if (-not $CertThumbprint) { return }
  $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
  & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d "Angelito Screen Studio Pro" $file
  if ($LASTEXITCODE) { throw "No se pudo firmar $file" }
}
Sign "$publish\AngelitoScreenStudio.exe"

dotnet tool restore | Out-Null
dotnet wix build build\Package.wxs -arch x64 -o $msi `
  -d Version=$version -d PublishDir=$publish -d AppIcon="$root\src\Angelito.ScreenStudio.Desktop\Assets\app.ico" -d IncludeFfmpeg=$includeFfmpeg
if ($LASTEXITCODE) { throw "WiX falló." }
Sign $msi

$hash = (Get-FileHash $msi -Algorithm SHA256).Hash
"$hash  $(Split-Path $msi -Leaf)" | Set-Content "$msi.sha256"
Write-Host ("Instalador: {0} ({1:N0} MB)`nSHA-256: {2}" -f $msi, ((Get-Item $msi).Length / 1MB), $hash)
