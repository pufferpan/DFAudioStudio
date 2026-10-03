# Add AutomationProperties.Name to icon buttons (content is StackPanel[FontIcon+TextBlock]).
# ASCII only (PS 5.1 + BOM-less UTF-8 .ps1 breaks on non-ASCII). Explicit UTF-8 file IO.
$ErrorActionPreference = 'Stop'
# derive paths from the script location so this file stays pure ASCII
# (PS 5.1 reads BOM-less UTF-8 .ps1 as ANSI: literal Chinese paths would turn into mojibake)
$repoRoot = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repoRoot 'src\DFAudioStudio.App'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$files = Get-ChildItem -Path (Join-Path $root '*') -Recurse -File | Where-Object { $_.Extension -eq '.xaml' -and $_.FullName -notmatch '\\obj\\|\\bin\\' }
Write-Host ("xaml files: {0}" -f $files.Count)
$totalButtons = 0
$totalNamed = 0

foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    $ms = [regex]::Matches($text, '(?s)<Button\b(?<attrs>[^>]*?)>(?<body>.*?)</Button>')
    if ($ms.Count -eq 0) { continue }

    $sb = New-Object System.Text.StringBuilder
    $pos = 0
    $namedHere = 0

    foreach ($m in $ms) {
        $totalButtons++
        [void]$sb.Append($text.Substring($pos, $m.Index - $pos))
        $pos = $m.Index + $m.Length

        $attrs = $m.Groups['attrs'].Value
        $body = $m.Groups['body'].Value
        $block = $m.Value

        if ($attrs -notmatch 'AutomationProperties\.Name') {
            $tm = [regex]::Match($body, 'Text="(?<t>[^"{][^"]*)"')
            if ($tm.Success) {
                $label = $tm.Groups['t'].Value.Trim()
                if ($label.Length -gt 0) {
                    $block = '<Button AutomationProperties.Name="' + $label + '"' + $attrs + '>' + $body + '</Button>'
                    $namedHere++
                } else { Write-Host ("    skip (empty label) in {0}" -f $f.Name) }
            } else { Write-Host ("    skip (no literal Text=) in {0}" -f $f.Name) }
        }
        [void]$sb.Append($block)
    }
    [void]$sb.Append($text.Substring($pos))

    $new = $sb.ToString()
    if ($new -ne $text) {
        [System.IO.File]::WriteAllText($f.FullName, $new, $utf8)
        Write-Host ("  {0,-40} named {1} button(s)" -f $f.Name, $namedHere)
        $totalNamed += $namedHere
    }
}
Write-Host ("buttons scanned: {0}; AutomationProperties.Name added: {1}" -f $totalButtons, $totalNamed)
