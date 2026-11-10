<#
.SYNOPSIS
    Genera el paquete para la Microsoft Store (x64 + ARM64 en un .msixbundle).

.DESCRIPTION
    1. Publica la app empaquetada (MSIX) para x64 y ARM64.
    2. Las une en artifacts\store\BatteryTray_<versión>.msixbundle, listo para subir a Partner Center.
    El paquete va sin firmar: la Store lo firma al publicarlo.

.PARAMETER Version
    Versión en formato X.Y.Z (la Store exige que el cuarto número sea 0).

.EXAMPLE
    pwsh tools/Pack-Store.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\BatteryTray.App\BatteryTray.App.csproj'
$manifest = Join-Path $root 'src\BatteryTray.App\Package.appxmanifest'
$out = Join-Path $root 'artifacts\store'
$packageVersion = "$Version.0"

# La versión del paquete sale del manifiesto: se cambia solo ese atributo (sin tocar el formato).
$text = [IO.File]::ReadAllText($manifest)
$text = [regex]::Replace($text, '(<Identity[^>]*?Version=")[^"]*(")', "`${1}$packageVersion`${2}")
[IO.File]::WriteAllText($manifest, $text, (New-Object Text.UTF8Encoding($false)))

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
foreach ($arch in @(@{ Rid = 'win-x64'; Platform = 'x64' }, @{ Rid = 'win-arm64'; Platform = 'ARM64' })) {
    Write-Host "Publicando $($arch.Rid)..."
    dotnet publish $project -c Release -r $arch.Rid -p:Platform=$($arch.Platform) -p:Packaged=true `
        -p:Version=$Version -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de $($arch.Rid)" }
}

# Solo los paquetes de la app (no las dependencias de prueba que deja la compilación).
$packages = @(Get-ChildItem $out -Recurse -Filter 'BatteryTray.App_*.msix' |
    Where-Object { $_.DirectoryName -notmatch 'Dependencies' })
$bundleInput = Join-Path $out 'bundle-input'
New-Item -ItemType Directory -Force $bundleInput | Out-Null
$packages | Copy-Item -Destination $bundleInput

$count = (Get-ChildItem $bundleInput -Filter *.msix).Count
if ($count -ne 2) { throw "Se esperaban 2 paquetes (x64 y ARM64) y hay $count" }

$makeappx = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter makeappx.exe |
    Where-Object { $_.DirectoryName -match '\\x64$' } | Select-Object -First 1
if (-not $makeappx) { throw 'No se encontró makeappx.exe (paquete Microsoft.Windows.SDK.BuildTools).' }

$bundle = Join-Path $out "BatteryTray_$packageVersion.msixbundle"
& $makeappx.FullName bundle /d $bundleInput /p $bundle /bv $packageVersion /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx no pudo crear el bundle' }

$hash = (Get-FileHash $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "Listo: $bundle"
Write-Host "SHA-256: $hash"
