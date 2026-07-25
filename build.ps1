$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $root "src\CloudflareDdnsTray.cs"
$manifest = Join-Path $root "src\app.manifest"
$out = Join-Path $root "dist\CloudflareDDNS.exe"
$ico = Join-Path $root "dist\CloudflareDDNS.ico"

if (-not (Test-Path $csc)) {
  throw "csc.exe not found: $csc"
}

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /win32icon:$ico /win32manifest:$manifest /out:$out `
  /reference:System.Windows.Forms.dll `
  /reference:System.Drawing.dll `
  /reference:System.Web.Extensions.dll `
  /reference:System.Security.dll `
  $src

if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

Write-Host "Built $out"
