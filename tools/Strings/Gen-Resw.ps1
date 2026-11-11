<#
.SYNOPSIS
  Genera los textos de la app (Resources.resw en español e inglés) a partir de Strings.tsv.

.DESCRIPTION
  Strings.tsv tiene una fila por texto: clave<TAB>español<TAB>inglés.
  Para cambiar o añadir un texto, edita el .tsv y ejecuta:

    pwsh tools/Strings/Gen-Resw.ps1
#>
param(
    [string]$Tsv = (Join-Path $PSScriptRoot 'Strings.tsv'),
    [string]$Root = (Join-Path $PSScriptRoot '..\..\src\BatteryTray.App\Strings')
)
$ErrorActionPreference = 'Stop'

$rows = Get-Content -LiteralPath $Tsv -Encoding UTF8 | Where-Object { $_.Trim() -ne '' } | ForEach-Object {
    $p = $_ -split "`t"
    if ($p.Count -ne 3) { throw "Fila incorrecta (hacen falta 3 columnas): $_" }
    [pscustomobject]@{ Key = $p[0]; Es = $p[1]; En = $p[2] }
}
$dupes = $rows | Group-Object Key | Where-Object Count -gt 1
if ($dupes) { throw "Claves repetidas: $($dupes.Name -join ', ')" }

function Write-Resw($lang, $prop) {
    $dir = Join-Path $Root $lang
    New-Item -ItemType Directory -Force $dir | Out-Null
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<root>')
    [void]$sb.AppendLine('  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>')
    [void]$sb.AppendLine('  <resheader name="version"><value>2.0</value></resheader>')
    [void]$sb.AppendLine('  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>')
    [void]$sb.AppendLine('  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>')
    foreach ($r in $rows) {
        # Se escapa el texto para que el XML sea siempre válido.
        $v = [System.Security.SecurityElement]::Escape($r.$prop)
        [void]$sb.AppendLine("  <data name=""$($r.Key)"" xml:space=""preserve""><value>$v</value></data>")
    }
    [void]$sb.AppendLine('</root>')
    [System.IO.File]::WriteAllText((Join-Path $dir 'Resources.resw'), $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
}

Write-Resw 'es-ES' 'Es'
Write-Resw 'en-US' 'En'
"Textos: $($rows.Count)"
