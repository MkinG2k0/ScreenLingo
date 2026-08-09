$ErrorActionPreference = 'Stop'

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$windowsMetadata = 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd'
$windowsRuntime = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
$outputDirectory = Join-Path $PSScriptRoot 'dist'
$outputFile = Join-Path $outputDirectory 'ScreenLingo.exe'

if (-not (Test-Path $compiler)) { throw "C# compiler not found: $compiler" }
if (-not (Test-Path $windowsMetadata)) {
    $windowsMetadata = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata' -Filter Windows.winmd -Recurse |
        Where-Object { $_.FullName -notlike '*\Facade\*' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $windowsMetadata) { throw 'Windows.winmd not found.' }

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

& $compiler /nologo /target:winexe /platform:x64 /optimize+ /out:$outputFile `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Net.Http.dll `
    /reference:System.Security.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll `
    /reference:$windowsRuntime `
    /reference:$windowsMetadata `
    (Join-Path $PSScriptRoot 'ScreenLingo.cs')

if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $PSScriptRoot 'ScreenLingo.ini') $outputDirectory -Force
Write-Host "Built: $outputFile"
