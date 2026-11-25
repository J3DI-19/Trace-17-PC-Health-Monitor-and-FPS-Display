param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw "C# compiler not found at $csc"
}

$out = Join-Path $PSScriptRoot 'Trace17.exe'
$sources = @(
    (Join-Path $PSScriptRoot 'src\Monitor.cs'),
    (Join-Path $PSScriptRoot 'src\Telemetry.cs'),
    (Join-Path $PSScriptRoot 'src\Tray.cs'),
    (Join-Path $PSScriptRoot 'src\Verification.cs')
)

& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$out" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Management.dll `
    /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Xml.Linq.dll `
    @sources

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Output "Built $out"

if ($SelfTest) {
    & $out --self-test
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test failed with exit code $LASTEXITCODE"
    }
    Write-Output 'Self-tests passed.'
}
