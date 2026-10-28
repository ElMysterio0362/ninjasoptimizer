<#
.SYNOPSIS
  Compila, testa, publica e gera o instalador do Ninjas Optimizer.
.EXAMPLE
  ./build/build.ps1                      # self-contained (não exige .NET instalado)
  ./build/build.ps1 -FrameworkDependent  # instalador pequeno (~3 MB), exige .NET 8 Desktop Runtime
#>
param(
    [string]$Configuration = "Release",
    [switch]$FrameworkDependent,
    [switch]$SkipInstaller
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts/publish"

Write-Host "==> Testes" -ForegroundColor Magenta
dotnet test "$root/tests/NinjasOptimizer.Core.Tests" -c $Configuration
if ($LASTEXITCODE) { throw "Testes falharam" }

Write-Host "==> Publicando" -ForegroundColor Magenta
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
dotnet publish "$root/src/NinjasOptimizer.App" -c $Configuration -r win-x64 `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$selfContained `
    -p:DebugType=none `
    -o $publish
if ($LASTEXITCODE) { throw "Publish falhou" }

if ($SkipInstaller) { return }

Write-Host "==> Gerando instalador (Inno Setup)" -ForegroundColor Magenta
# Compatível com Windows PowerShell 5.1 e PowerShell 7
$cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
$iscc = if ($cmd) { $cmd.Source } else { "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup" }

$version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version
& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "$root/installer/NinjasOptimizer.iss"
if ($LASTEXITCODE) { throw "ISCC falhou" }
Write-Host "Instalador gerado em installer/Output" -ForegroundColor Green
