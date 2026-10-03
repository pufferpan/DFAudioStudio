# Fetch Whisper ggml model for restricted network (ASCII only on purpose:
# Windows PowerShell 5.1 parses BOM-less UTF-8 .ps1 as ANSI, which breaks non-ASCII text).
# Sources: openaipublic.azureedge.net (official .pt), ghproxy.net (GitHub scripts), mirrors.aliyun.com (PyPI)
$ErrorActionPreference = 'Continue'

$work   = Join-Path $env:TEMP 'dfwhisper'
# 注意：Whisper 原生库用窄字符路径打开模型，**路径必须是纯 ASCII**（中文目录会抛 SEH 异常）。
# 因此模型统一放在 E:\DFAudioStudio\models（纯英文），并通过 settings.json 的 ModelsRoot 指过去。
$models = if (Test-Path 'E:\') { 'E:\DFAudioStudio\models' } else { Join-Path $env:LOCALAPPDATA 'DFAudioStudio\models' }
New-Item -ItemType Directory -Force -Path $work, (Join-Path $work 'whisper\assets'), $models | Out-Null
Write-Host "[dir ] work   = $work"
Write-Host "[dir ] models = $models"

function Fetch([string]$url, [string]$out, [long]$minBytes) {
    if ((Test-Path $out) -and (Get-Item $out).Length -ge $minBytes) {
        Write-Host ("[skip] {0} exists ({1:N1} MB)" -f (Split-Path $out -Leaf), ((Get-Item $out).Length / 1MB))
        return
    }
    Write-Host "[get ] $url"
    curl.exe -L -sS --retry 3 --retry-delay 2 --max-time 7200 -o $out $url
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) {
        Write-Host ("       -> {0:N2} MB" -f ((Get-Item $out).Length / 1MB))
    } else {
        Write-Host '       -> FAILED'
    }
}

# 1) conversion script + mel filters
$conv = Join-Path $work 'convert.py'
Fetch 'https://ghproxy.net/https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/models/convert-pt-to-ggml.py' $conv 5000
$mel = Join-Path $work 'whisper\assets\mel_filters.npz'
Fetch 'https://ghproxy.net/https://raw.githubusercontent.com/openai/whisper/main/whisper/assets/mel_filters.npz' $mel 3000

# 2) python deps (CPU torch + numpy). USTC mirror is fast here; aliyun is throttled.
Write-Host '[pip ] installing numpy + torch (CPU) from USTC mirror ...'
& python -m pip install --disable-pip-version-check numpy torch --index-url https://mirrors.ustc.edu.cn/pypi/web/simple --trusted-host mirrors.ustc.edu.cn
& python -c "import torch,numpy;print('[pip ] ok torch',torch.__version__,'numpy',numpy.__version__)"

# 3) official small.pt (~483 MB)
$pt = Join-Path $work 'small.pt'
Fetch 'https://openaipublic.azureedge.net/main/whisper/models/9ecf779972d90ba49c06d968637d720dd632c55bbf19d441fb42bf17a411e794/small.pt' $pt 400000000

# 4) convert -> ggml
Write-Host '[conv] converting small.pt to ggml ...'
Push-Location $work
& python $conv $pt (Join-Path $work 'whisper') $models
Pop-Location

Write-Host '=== result ==='
Get-ChildItem -LiteralPath $models -Filter '*.bin' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("[done] {0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB)) }
