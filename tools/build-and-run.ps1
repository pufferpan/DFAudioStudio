# Rebuild DFAudioStudio.App, refresh the release folder, and (re)start it.
# ASCII only on purpose: Windows PowerShell 5.1 parses BOM-less UTF-8 .ps1 as ANSI,
# and this project lives under a path containing Chinese characters.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\DFAudioStudio.App\DFAudioStudio.App.csproj'
$bin  = Join-Path $root 'src\DFAudioStudio.App\bin\x64\Release\net10.0-windows10.0.19041.0'
# Release output goes to an ASCII-only path (same drive as models/data):
#   E:\DFAudioStudio\app      <- runnable program
#   E:\DFAudioStudio\models   <- whisper ggml models
#   E:\DFAudioStudio\data     <- index.db + logs
$pub  = 'E:\DFAudioStudio\app'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

Write-Host '[1/4] stop running instances'
Get-Process | Where-Object { $_.ProcessName -like 'DFAudioStudio*' } | ForEach-Object {
    Write-Host ("      stop PID {0}" -f $_.Id); Stop-Process -Id $_.Id -Force
}
Start-Sleep -Seconds 2

Write-Host '[2/4] build (Release)'
& $dotnet build $proj -c Release | Select-String -Pattern 'error|Build succeeded|0 Error|errors' | Select-Object -First 12

Write-Host '[3/4] refresh publish folder'
if (-not (Test-Path $pub)) { New-Item -ItemType Directory -Force -Path $pub | Out-Null }
Remove-Item -LiteralPath (Join-Path $bin 'models') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $bin '*') -Destination $pub -Recurse -Force
Remove-Item -LiteralPath (Join-Path $pub 'models') -Recurse -Force -ErrorAction SilentlyContinue
$readme = Join-Path $root 'README.md'
if (Test-Path $readme) { Copy-Item $readme (Join-Path $pub 'README.md') -Force }

Write-Host '[4/4] start app'
$exe = Join-Path $pub 'DFAudioStudio.App.exe'
$p = Start-Process -FilePath $exe -PassThru
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 200
    $q = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($q -and $q.MainWindowTitle) { break }
}
$q = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($q) {
    Write-Host ("      running PID {0}  mem {1:N0} MB  title '{2}'" -f $q.Id, ($q.WorkingSet64 / 1MB), $q.MainWindowTitle)
} else {
    Write-Host '      FAILED to start'
}
