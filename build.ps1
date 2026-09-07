param(
    [string]$Version = "1.0.0"
)

# ============================================================
# PhotoView リリースビルドスクリプト
# 使い方: powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.0
# ============================================================
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$installerDir = Join-Path $root "installer"
$publishDir = Join-Path $root "publish"
$msiOut = Join-Path $installerDir "PhotoView-$Version.msi"

Write-Host "== PhotoView リリースビルド v$Version ==" -ForegroundColor Cyan

# 1) アプリのビルド + publish
Write-Host "[1/3] アプリをビルド & publish..." -ForegroundColor Yellow
dotnet publish $root -c Release -r win-x64 -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "アプリのビルドに失敗しました。" }

# 2) MSI のビルド（WiX）
Write-Host "[2/3] MSI インストーラーをビルド..." -ForegroundColor Yellow
# WiX v4 の UI 拡張（WixToolset.UI.wixext）を DLL パスで解決する（名前指定は不安定なため）
$uiExt = Get-ChildItem "$env:USERPROFILE\.wix\extensions\WixToolset.UI.wixext" -Recurse -Filter "WixToolset.UI.wixext.dll" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $uiExt) { throw "WixToolset.UI.wixext 拡張が見つかりません。" }

wix build (Join-Path $installerDir "PhotoView.wxs") `
    -d "Version=$Version" `
    -d "ProjectDir=$root\" `
    -d "PublishDir=$publishDir\" `
    -ext $uiExt `
    -arch x64 `
    -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "MSI のビルドに失敗しました。" }

Write-Host "[3/3] 完了！" -ForegroundColor Green
Write-Host "  MSI: $msiOut" -ForegroundColor Green

# 配布フォルダにコピー
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Copy-Item $msiOut (Join-Path $distDir "PhotoView-$Version.msi") -Force
Write-Host "  dist: $(Join-Path $distDir "PhotoView-$Version.msi")" -ForegroundColor Green
