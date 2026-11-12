param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$AudioPath = ''
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$refs = @('mscorlib', 'System', 'System.Core', 'UnityEngine', 'Assembly-CSharp') | ForEach-Object { '/reference:' + (Join-Path $gameRoot ('stbc_Data\Managed\' + $_ + '.dll')) }
$refs += @('BepInEx', '0Harmony') | ForEach-Object { '/reference:' + (Join-Path $gameRoot ('BepInEx\core\' + $_ + '.dll')) }
$sources = @('Plugin.cs', 'QuantumPolicy.cs', 'PcmWave.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /noconfig /nostdlib+ /target:library /optimize+ ('/out:' + (Join-Path $output 'SovereignQuantum.dll')) @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed' }
$tests = @('Tests.cs', 'QuantumPolicy.cs', 'PcmWave.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:exe ('/out:' + (Join-Path $output 'Tests.exe')) @tests
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& (Join-Path $output 'Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if ($AudioPath) {
    & (Join-Path $output 'Tests.exe') $AudioPath
    if ($LASTEXITCODE -ne 0) { throw 'Audio validation failed' }
    Copy-Item -LiteralPath $AudioPath -Destination (Join-Path $output 'quantum.wav')
}
Write-Output ('Built ' + (Join-Path $output 'SovereignQuantum.dll'))
