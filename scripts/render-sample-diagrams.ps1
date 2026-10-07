# Render the SVG sample (samples/diagrams/northbank.svg) to PNG and PDF, the way an export from Figma,
# Lucidchart or Miro arrives, using a headless Chromium browser. Also writes northbank.drawio.png: the PNG
# with the draw.io diagram embedded in a text chunk, exactly as draw.io's "editable PNG" export does.
#
#   .\scripts\render-sample-diagrams.ps1
#   .\scripts\render-sample-diagrams.ps1 -Browser "C:\Program Files\Google\Chrome\Application\chrome.exe"
param(
    [string]$Dir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'samples\diagrams'),
    [string]$Browser = $(if ($env:SECUREFLOW_BROWSER) { $env:SECUREFLOW_BROWSER } else { '' }),
    [int]$Width = 1180,
    [int]$Height = 620
)

$ErrorActionPreference = 'Stop'
if (-not $Browser) {
    $Browser = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Browser) { throw "No Edge or Chrome found. Pass -Browser <path to msedge.exe or chrome.exe>." }

$svg = Join-Path $Dir 'northbank.svg'
if (-not (Test-Path $svg)) { throw "Run scripts/make-sample-diagrams.py first ($svg not found)." }
$uri = ([Uri]$svg).AbsoluteUri
$png = Join-Path $Dir 'northbank.png'
$pdf = Join-Path $Dir 'northbank.pdf'
$profileDir = Join-Path $env:TEMP 'secureflow-render-profile'

# The browser logs harmless lines on stderr, which Windows PowerShell would treat as errors; success is judged
# by the files it writes instead.
$ErrorActionPreference = 'Continue'
& $Browser --headless=new --disable-gpu --hide-scrollbars "--user-data-dir=$profileDir" "--window-size=$Width,$Height" "--screenshot=$png" $uri 2>&1 | Out-Null
& $Browser --headless=new --disable-gpu "--user-data-dir=$profileDir" --no-pdf-header-footer "--print-to-pdf=$pdf" $uri 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
foreach ($f in $png, $pdf) { if (-not (Test-Path $f)) { throw "The browser did not produce $f." } }

# Editable PNG: same picture, plus the draw.io XML in a tEXt chunk named "mxfile" (URL-encoded), before IEND.
python -c @"
import struct, sys, urllib.parse, zlib
png, drawio, out = sys.argv[1], sys.argv[2], sys.argv[3]
data = open(png, 'rb').read()
xml = open(drawio, encoding='utf-8').read()
payload = b'mxfile\x00' + urllib.parse.quote(xml, safe='').encode('latin-1')
chunk = struct.pack('>I', len(payload)) + b'tEXt' + payload + struct.pack('>I', zlib.crc32(b'tEXt' + payload) & 0xffffffff)
iend = data.rfind(b'IEND') - 4
open(out, 'wb').write(data[:iend] + chunk + data[iend:])
"@ $png (Join-Path $Dir 'northbank.drawio') (Join-Path $Dir 'northbank.drawio.png')

Get-ChildItem $Dir -Filter 'northbank.*' | Where-Object { $_.Extension -in '.png', '.pdf' } | ForEach-Object { "{0,-24} {1,8:N0} bytes" -f $_.Name, $_.Length }
