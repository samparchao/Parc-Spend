param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$publishDir = Join-Path $projectRoot "artifacts\publish\$Runtime"
$msbuild = (Get-Command MSBuild.exe -ErrorAction SilentlyContinue).Source
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        foreach ($installation in (& $vswhere -products * -requires Microsoft.Component.MSBuild -property installationPath)) {
            $candidate = Join-Path $installation "MSBuild\Current\Bin\amd64\MSBuild.exe"
            $targets = Join-Path $installation "MSBuild\Microsoft\VisualStudio"
            $priTasks = Get-ChildItem -Path "$targets\v*\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll" -ErrorAction SilentlyContinue
            if ((Test-Path -LiteralPath $candidate) -and $priTasks) {
                $msbuild = $candidate
                break
            }
        }
    }
}

if (-not $msbuild) {
    throw "Install Visual Studio with the .NET desktop and Windows SDK build tools, then retry."
}

Write-Host "Publishing Parc Spend to $publishDir"
& $msbuild (Join-Path $projectRoot "MonzoManager.csproj") -restore -t:Rebuild `
    "-p:Configuration=$Configuration" `
    "-p:RuntimeIdentifier=$Runtime" `
    "-p:OutputPath=$publishDir" `
    -p:PublishSingleFile=false `
    -p:AppendTargetFrameworkToOutputPath=false `
    -p:AppendRuntimeIdentifierToOutputPath=false -nologo -v:minimal

if ($LASTEXITCODE -ne 0) {
    throw "Publishing failed with MSBuild exit code $LASTEXITCODE."
}

Write-Host "Publish complete."



