param(
    [string]$CompilerPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$artifacts = Join-Path $projectRoot "artifacts"

if (-not $CompilerPath) {
    $CompilerPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}
if (-not $CompilerPath) {
    $installRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA "Programs")) |
        Where-Object { $_ } | Select-Object -Unique
    foreach ($version in @("Inno Setup 7", "Inno Setup 6")) {
        foreach ($installRoot in $installRoots) {
            $candidate = Join-Path $installRoot "$version\ISCC.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $CompilerPath = $candidate
                break
            }
        }
        if ($CompilerPath) { break }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw "Install Inno Setup 6.6 or newer, or pass -CompilerPath pointing to ISCC.exe."
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).ProviderPath

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $artifacts "installer"
}
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$stagingRoot = (New-Item -ItemType Directory -Force -Path (Join-Path $artifacts "installer-build")).FullName
$staging = (New-Item -ItemType Directory -Path (Join-Path $stagingRoot ([Guid]::NewGuid().ToString("N")))).FullName

try {
    & (Join-Path $PSScriptRoot "publish.ps1") -Configuration Release -Runtime win-x64 -OutputDirectory $staging

    foreach ($file in @("MonzoManager.exe", "MonzoManager.dll", "coreclr.dll", "MainWindow.xbf", "MonzoManager.pri", "Assets\Parc.ico")) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $file) -PathType Leaf)) {
            throw "The installer payload is missing $file."
        }
    }

    $installerScript = Join-Path $projectRoot "installer\MonzoManager.iss"
    & $CompilerPath /Qp "/DPublishDirectory=$staging" "/O$output" $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }
    Get-Item -LiteralPath (Join-Path $output "ParcSpend-Setup.exe")
}
finally {
    $resolvedStaging = (Resolve-Path -LiteralPath $staging).ProviderPath
    $resolvedRoot = (Resolve-Path -LiteralPath $stagingRoot).ProviderPath
    $stagingItem = Get-Item -LiteralPath $resolvedStaging
    $rootItem = Get-Item -LiteralPath $resolvedRoot
    if ([IO.Path]::GetDirectoryName($resolvedStaging) -ne $resolvedRoot -or
        (($stagingItem.Attributes -bor $rootItem.Attributes) -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove an installer staging directory outside the intended staging root."
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}

