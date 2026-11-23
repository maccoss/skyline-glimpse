<#
  Rebuild Glimpse's icon and logo images from the drawings in docs/logo/.

    docs/logo/glimpse-icon.svg        the full icon (64 px and up)
    docs/logo/glimpse-icon-small.svg  the eye, Space Needle and mountain only (16 to 48 px)
    docs/logo/glimpse-logo.svg        the mark and the wordmark, for the README

  Each icon size is rendered from its drawing (not scaled down from a large one) with VectorCraft's
  command-line tool, then the sizes are packed into src/Glimpse.App/glimpse.ico with Pillow.

  Usage:
    pwsh build/make-icon.ps1
    pwsh build/make-icon.ps1 -VectorCraft 'D:\Tools\VectorCraft\vectorcraft-cli.exe'
#>
param(
    [string]$VectorCraft = 'C:\Program Files\ArtCraft\VectorCraft\vectorcraft-cli.exe',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not (Test-Path $VectorCraft)) { throw "VectorCraft's command-line tool was not found at $VectorCraft (pass -VectorCraft)." }

$work = Join-Path ([IO.Path]::GetTempPath()) "glimpse-icon-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    function Render([string]$svg, [string]$png, [double]$scale) {
        $out = & $VectorCraft run --in (Join-Path $root $svg) --export $png --scale $scale 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $png)) { throw "VectorCraft could not render ${svg}: $out" }
    }
    $small = 16, 20, 24, 32, 40, 48
    $large = 64, 128, 256
    foreach ($s in $small) { Render 'docs/logo/glimpse-icon-small.svg' (Join-Path $work "ico-$s.png") ($s / 256) }
    foreach ($s in $large) { Render 'docs/logo/glimpse-icon.svg' (Join-Path $work "ico-$s.png") ($s / 256) }
    Render 'docs/logo/glimpse-icon.svg' (Join-Path $root 'docs/logo/glimpse-icon-256.png') 1
    Copy-Item (Join-Path $root 'docs/logo/glimpse-icon-256.png') (Join-Path $root 'src/Glimpse.App/tool-inf/Glimpse.png')   # the tool's logo in Skyline
    Render 'docs/logo/glimpse-logo.svg' (Join-Path $root 'docs/images/glimpse-logo.png') 2   # 2x for high-DPI screens

    $ico = Join-Path $root 'src/Glimpse.App/glimpse.ico'
    $pack = @"
import os, sys
from PIL import Image
work, out = sys.argv[1], sys.argv[2]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [Image.open(os.path.join(work, f'ico-{s}.png')).convert('RGBA') for s in sizes]
for s, im in zip(sizes, imgs):
    assert im.size == (s, s), (s, im.size)
imgs[-1].save(out, format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
print('wrote', out, 'with', ', '.join(map(str, sizes)), 'px')
"@
    $pack | & $Python - $work $ico
    if ($LASTEXITCODE -ne 0) { throw 'Packing the .ico failed (is Pillow installed? pip install pillow).' }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
