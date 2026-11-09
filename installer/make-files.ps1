# Scrive installer\obj\Files.wxs: un componente per ogni file di dist\TiaTracker, nelle stesse
# sottocartelle. Lo chiama publish.ps1 prima di compilare l'installer.
#
#   powershell -File installer\make-files.ps1 -Dist dist\TiaTracker

param([Parameter(Mandatory)] [string]$Dist)

$ErrorActionPreference = 'Stop'
$Dist = (Resolve-Path $Dist).Path.TrimEnd('\')
$out = Join-Path $PSScriptRoot 'obj\Files.wxs'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">')
Get-ChildItem $Dist -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($Dist.Length + 1)
    $sub = Split-Path $rel -Parent
    $subAttr = if ($sub) { ' Subdirectory="' + (Esc $sub) + '"' } else { '' }
    [void]$sb.AppendLine('      <Component' + $subAttr + '><File Source="' + (Esc $_.FullName) + '" /></Component>')
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
Write-Host "== Files.wxs: $((Get-ChildItem $Dist -Recurse -File).Count) file"
