# 配布用のフォルダと zip を dist\ に作る
#   ./publish.ps1              … .NET ランタイム同梱（そのまま動く）
#   ./publish.ps1 -Small       … ランタイムなし（.NET 10 Desktop Runtime が必要、サイズ小）
param([switch]$Small)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "dist\MyVideoPlayer"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$selfContained = if ($Small) { "false" } else { "true" }
dotnet publish src/MyVideoPlayer.App -c Release -r win-x64 --self-contained $selfContained -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "publish に失敗しました" }

# ffmpeg を exe の隣に置いておくと自動で使われる（あれば同梱）
$ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
$fp = Get-Command ffprobe -ErrorAction SilentlyContinue
if ($ff -and $fp) {
    New-Item -ItemType Directory -Force (Join-Path $out "ffmpeg") | Out-Null
    Copy-Item $ff.Source, $fp.Source (Join-Path $out "ffmpeg")
    Write-Host "ffmpeg を同梱しました: $($ff.Source)"
}

$zip = Join-Path $PSScriptRoot "dist\MyVideoPlayer-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "完成: $zip"
