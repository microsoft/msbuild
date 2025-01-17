#
# Copyright (c) .NET Foundation and contributors. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#

[CmdletBinding(PositionalBinding=$false)]
Param(
  [string][Alias('c')]$configuration = "Debug",
  [string] $projects,
  [string][Alias('v')]$verbosity = "minimal",
  [string] $msbuildEngine = $null,
  [bool] $warnAsError = $true,
  [bool] $nodeReuse = $true,
  [switch][Alias('r')]$restore,
  [switch] $deployDeps,
  [switch][Alias('b')]$build,
  [switch] $rebuild,
  [switch] $deploy,
  [switch] $test,
  [switch] $integrationTest,
  [switch] $performanceTest,
  [switch] $sign,
  [switch] $pack,
  [switch] $publish,
  [switch][Alias('bl')]$binaryLog,
  [switch] $ci,
  [switch] $prepareMachine,
  [switch] $help,

  # official build settings
  [string]$officialBuildId = "",
  [string]$officialSkipApplyOptimizationData = "",

  [Parameter(ValueFromRemainingArguments=$true)][String[]]$properties
)

function Print-Usage() {
  Write-Host "Common settings:"
  Write-Host "  -configuration <value>  Build configuration: 'Debug' or 'Release' (short: -c)"
  Write-Host "  -verbosity <value>      Msbuild verbosity: q[uiet], m[inimal], n[ormal], d[etailed], and diag[nostic] (short: -v)"
  Write-Host "  -binaryLog              Output binary log (short: -bl)"
  Write-Host ""

  Write-Host "Actions:"
  Write-Host "  -restore                Restore dependencies (short: -r)"
  Write-Host "  -build                  Build solution (short: -b)"
  Write-Host "  -rebuild                Rebuild solution"
  Write-Host "  -deploy                 Deploy built VSIXes"
  Write-Host "  -deployDeps             Deploy dependencies (e.g. VSIXes for integration tests)"
  Write-Host "  -test                   Run all unit tests in the solution"
  Write-Host "  -pack                   Package build outputs into NuGet packages and Willow components"
  Write-Host "  -help                   Print help and exit"
  Write-Host "  -integrationTest        Run all integration tests in the solution"
  Write-Host "  -performanceTest        Run all performance tests in the solution"
  Write-Host "  -sign                   Sign build outputs"
  Write-Host "  -publish                Publish artifacts (e.g. symbols)"
  Write-Host ""

  Write-Host "Advanced settings:"
  Write-Host "  -projects <value>       Semi-colon delimited list of sln/proj's to build. Globbing is supported (*.sln)"
  Write-Host "  -ci                     Set when running on CI server"
  Write-Host "  -prepareMachine         Prepare machine for CI run"
  Write-Host "  -msbuildEngine <value>  Msbuild engine to use to run build ('dotnet', 'vs', or unspecified)."
  Write-Host ""

  Write-Host "Official build settings:"
  Write-Host "  -officialBuildId                            An official build id, e.g. 20190102.3"
  Write-Host "  -officialSkipApplyOptimizationData <bool>   Pass 'true' to not apply optimization data"
  Write-Host ""
  Write-Host "Command line arguments not listed above are passed thru to msbuild."
  Write-Host "The above arguments can be shortened as much as to be unambiguous (e.g. -co for configuration, -t for test, etc.)."
}

function Process-Arguments() {
  function OfficialBuildOnly([string]$argName) {
    if ((Get-Variable $argName -Scope Script).Value) {
      if (!$officialBuildId) {
        Write-Host "$argName can only be specified for official builds"
        exit 1
      }
    } else {
      if ($officialBuildId) {
        Write-Host "$argName must be specified in official builds"
        exit 1
      }
    }
  }

  if ($help -or (($properties -ne $null) -and ($properties.Contains("/help") -or $properties.Contains("/?")))) {
    Print-Usage
    exit 0
  }

  OfficialBuildOnly "officialSkipApplyOptimizationData"

  if ($officialBuildId) {
    $script:applyOptimizationData = ![System.Boolean]::Parse($officialSkipApplyOptimizationData)
  } else {
    $script:applyOptimizationData = $false
  }

  if ($ci) {
    $script:binaryLog = $true
    $script:nodeReuse = $false
  }
}

function Build-Repo() {
  $bl = if ($binaryLog) { "/bl:" + (Join-Path $LogDir "Build.binlog") } else { "" }
  $toolsetBuildProj = InitializeToolset

  if ($projects) {
    # Re-assign properties to a new variable because PowerShell doesn't let us append properties directly for unclear reasons.
    # Explicitly set the type as string[] because otherwise PowerShell would make this char[] if $properties is empty.
    [string[]] $msbuildArgs = $properties
    $msbuildArgs += "/p:Projects=$projects"
    $properties = $msbuildArgs
  }

  # Do not set this property to true explicitly, since that would override values set in projects.
  $suppressPartialNgenOptimization = if (!$applyOptimizationData) { "/p:EnableNgenOptimization=false" } else { "" }

  MSBuild $toolsetBuildProj `
    $bl `
    /p:Configuration=$configuration `
    /p:RepoRoot=$RepoRoot `
    /p:Restore=$restore `
    /p:DeployDeps=$deployDeps `
    /p:Build=$build `
    /p:Rebuild=$rebuild `
    /p:Deploy=$deploy `
    /p:Test=$test `
    /p:Pack=$pack `
    /p:IntegrationTest=$integrationTest `
    /p:PerformanceTest=$performanceTest `
    /p:Sign=$sign `
    /p:Publish=$publish `
    /p:ContinuousIntegrationBuild=$ci `
    /p:OfficialBuildId=$officialBuildId `
    $suppressPartialNgenOptimization `
    @properties
}

# Set VSO variables used by MicroBuildBuildVSBootstrapper pipeline task
function Set-OptProfVariables() {
  $insertionDir = Join-Path $VSSetupDir "Insertion"
  $manifestList = [string]::Join(',', (Get-ChildItem "$insertionDir\*.vsman"))
  Write-Host "##vso[task.setvariable variable=VisualStudio.SetupManifestList;]$manifestList"
}

function Check-EditedFiles() {
  # Log VSTS errors for changed lines
  git --no-pager diff HEAD --unified=0 --no-color --exit-code -- src/ | ForEach-Object { "##vso[task.logissue type=error] $_" }
  if ($LASTEXITCODE -ne 0) {
    throw "##vso[task.logissue type=error] After building, there are changed files.  Please build locally and include these changes in your pull request."
  }
}

function Check-RequiredVersionBumps() {
  # Log VSTS errors for missing required version bumps
  $targetBranch = $env:SYSTEM_PULLREQUEST_TARGETBRANCH
  if ($targetBranch) {
    # Some PRs specify the bare target branch (most commonly "main"), some prefix it with "refs/heads/".
    # The following statement normalizes both to a revision spec that git understands.
    $targetBranch = "refs/remotes/origin/" + ($targetBranch -replace "^refs/heads/", "")
    $versionLineChanged = $false
    git --no-pager diff --unified --no-color --exit-code -w $targetBranch HEAD src\Framework\EngineServices.cs `
      | Select-String -Pattern "int Version =" | ForEach-Object -process { $versionLineChanged = $true }
    if (($LASTEXITCODE -ne 0) -and (-not $versionLineChanged)) {
      throw "##vso[task.logissue type=error] Detected changes in Framework\EngineServices.cs without a version bump.  " +
            "If you are making API changes, please bump the version.  " +
            "If the changes in the file are cosmetic, please change an inline comment on the `"int Version =`" line in EngineServices.cs to silence the error."
    }
  }
}

try {
  Process-Arguments

  # Import Arcade functions
  . (Join-Path $PSScriptRoot "common\tools.ps1")
  . (Join-Path $PSScriptRoot "configure-toolset.ps1")

  $VSSetupDir = Join-Path $ArtifactsDir "VSSetup\$configuration"

  if ($ci -and $build) {
    Check-RequiredVersionBumps
  }

  Build-Repo

  if ($ci -and $build) {
    Set-OptProfVariables

    Check-EditedFiles
  }
}
catch {
  Write-Host $_
  Write-Host $_.Exception
  Write-Host $_.ScriptStackTrace
  ExitWithExitCode 1
}

ExitWithExitCode 0
