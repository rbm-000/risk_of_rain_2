$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\ConfigurableStageCredit\ConfigurableStageCredit.csproj'
$buildOutput = Join-Path $root 'src\ConfigurableStageCredit\bin\Release\netstandard2.1\ConfigurableStageCredit.dll'
$artifactDir = Join-Path $root 'artifacts\thunderstore'
$packageDir = Join-Path $artifactDir 'ConfigurableStageCredit'
$zipPath = Join-Path $artifactDir 'ConfigurableStageCredit-0.3.4.zip'

if (-not (Test-Path (Join-Path $root 'Config.Build.user.props'))) {
    throw 'Missing Config.Build.user.props. Copy Config.Build.user.props.template to Config.Build.user.props and set your local game/profile paths first.'
}

if (Test-Path $artifactDir) {
    Remove-Item $artifactDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Write-Host 'Building ConfigurableStageCredit...'
dotnet build $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet build failed. Fix the build errors before packaging.'
}
if (-not (Test-Path $buildOutput)) {
    throw "Build completed but the plugin DLL was not found at $buildOutput"
}

Copy-Item (Join-Path $root 'manifest.json') $packageDir
Copy-Item (Join-Path $root 'README.md') $packageDir
Copy-Item (Join-Path $root 'CHANGELOG.md') $packageDir
Copy-Item (Join-Path $root 'icon.png') $packageDir

$pluginDir = Join-Path $packageDir 'plugins\ConfigurableStageCredit'
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
Copy-Item $buildOutput (Join-Path $pluginDir 'ConfigurableStageCredit.dll')

Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -Force
Write-Host "Thunderstore package created: $zipPath"
Write-Host 'Before uploading, test the zip by importing it into a separate r2modman profile.'
