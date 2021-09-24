[CmdletBinding(PositionalBinding=$false)]
Param(
  [Parameter(Mandatory = $true)]
  [string] $destination,
  [ValidateSet('Debug','Release')]
  [string] $configuration = "Debug",
  [ValidateSet('Core','Desktop')]
  [string] $runtime = "Desktop"
)

Set-StrictMode -Version "Latest"
$ErrorActionPreference = "Stop"

function Copy-WithBackup ($origin, $destinationSubFolder = "") {
    $directoryPart = [IO.Path]::Combine($destination, $destinationSubFolder, $origin.IntermediaryDirectories)
    $destinationPath = Join-Path -Path $directoryPart (Split-Path $origin.SourceFile -leaf)

    $backupInto = [IO.Path]::Combine($BackupFolder, $destinationSubFolder)

    if (Test-Path $destinationPath -PathType Leaf) {
        # Back up previous copy of the file
        if (!(Test-Path $backupInto)) {
            [system.io.directory]::CreateDirectory($backupInto)
        }
        Copy-Item $destinationPath $backupInto -ErrorAction Stop
    }

    if (!(Test-Path $directoryPart)) {
        [system.io.directory]::CreateDirectory($directoryPart)
    }

    Copy-Item $origin.SourceFile $destinationPath -ErrorAction Stop

    echo "Copied $($origin.SourceFile) to $destinationPath"
}

function FileToCopy([string] $sourceFileRelativeToRepoRoot, [string] $intermediaryDirectories)
{
    return [PSCustomObject]@{"SourceFile"=$([IO.Path]::Combine($PSScriptRoot, "..", $sourceFileRelativeToRepoRoot)); "IntermediaryDirectories"=$intermediaryDirectories}
}

# TODO: find destination in PATH if not specified

# TODO: identify processes likely to be using target MSBuild and warn/offer to kill

# TODO: find most-recently-built MSBuild and make it default $configuration

$BackupFolder = New-Item (Join-Path $destination -ChildPath "Backup-$(Get-Date -Format FileDateTime)") -itemType directory -ErrorAction Stop

Write-Verbose "Copying $configuration MSBuild to $destination"
Write-Host "Existing MSBuild assemblies backed up to $BackupFolder"

if ($runtime -eq "Desktop") {
    $targetFramework = "net472"
} else {
    $targetFramework = "net6.0"
}

$bootstrapBinDirectory = "artifacts\bin\MSBuild.Bootstrap\$configuration\$targetFramework"

$filesToCopyToBin = @(
    FileToCopy "$bootstrapBinDirectory\Microsoft.Build.dll"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Build.Framework.dll"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Build.Tasks.Core.dll"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Build.Utilities.Core.dll"
    FileToCopy "$bootstrapBinDirectory\Microsoft.NET.StringTools.dll"

    FileToCopy "$bootstrapBinDirectory\Microsoft.Common.CrossTargeting.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Common.CurrentVersion.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Common.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.CSharp.CrossTargeting.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.CSharp.CurrentVersion.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.CSharp.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Managed.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Managed.Before.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.Managed.After.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.NET.props"
    FileToCopy "$bootstrapBinDirectory\Microsoft.NETFramework.CurrentVersion.props"
    FileToCopy "$bootstrapBinDirectory\Microsoft.NETFramework.CurrentVersion.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.NETFramework.props"
    FileToCopy "$bootstrapBinDirectory\Microsoft.NETFramework.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.VisualBasic.CrossTargeting.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.VisualBasic.CurrentVersion.targets"
    FileToCopy "$bootstrapBinDirectory\Microsoft.VisualBasic.targets"

    FileToCopy "$bootstrapBinDirectory\Microsoft.Common.tasks"
)

if ($runtime -eq "Desktop") {
    $runtimeSpecificFiles = @(
        FileToCopy "artifacts\bin\Microsoft.Build.Conversion\$configuration\$targetFramework\Microsoft.Build.Conversion.Core.dll"
        FileToCopy "artifacts\bin\Microsoft.Build.Engine\$configuration\$targetFramework\Microsoft.Build.Engine.dll"

        FileToCopy "$bootstrapBinDirectory\Microsoft.Bcl.AsyncInterfaces.dll"
        FileToCopy "$bootstrapBinDirectory\Microsoft.Data.Entity.targets"
        FileToCopy "$bootstrapBinDirectory\Microsoft.ServiceModel.targets"
        FileToCopy "$bootstrapBinDirectory\Microsoft.WinFx.targets"
        FileToCopy "$bootstrapBinDirectory\Microsoft.WorkflowBuildExtensions.targets"
        FileToCopy "$bootstrapBinDirectory\Microsoft.Xaml.targets"
        FileToCopy "$bootstrapBinDirectory\Workflow.targets"
        FileToCopy "$bootstrapBinDirectory\Workflow.VisualBasic.targets"

        FileToCopy "$bootstrapBinDirectory\System.Buffers.dll"
        FileToCopy "$bootstrapBinDirectory\System.Collections.Immutable.dll"
        FileToCopy "$bootstrapBinDirectory\System.Memory.dll"
        FileToCopy "$bootstrapBinDirectory\System.Numerics.Vectors.dll"
        FileToCopy "$bootstrapBinDirectory\System.Resources.Extensions.dll"
        FileToCopy "$bootstrapBinDirectory\System.Runtime.CompilerServices.Unsafe.dll"
        FileToCopy "$bootstrapBinDirectory\System.Text.Encodings.Web.dll"
        FileToCopy "$bootstrapBinDirectory\System.Text.Json.dll"
        FileToCopy "$bootstrapBinDirectory\System.Threading.Tasks.Dataflow.dll"
        FileToCopy "$bootstrapBinDirectory\System.Threading.Tasks.Extensions.dll"
        FileToCopy "$bootstrapBinDirectory\System.ValueTuple.dll"
    )
} else {
    $runtimeSpecificFiles = @(
        FileToCopy "$bootstrapBinDirectory\MSBuild.dll"
    )
}

if ($runtime -eq "Desktop") {
    $x86files = @(
        FileToCopy "$bootstrapBinDirectory\MSBuild.exe"
        FileToCopy "$bootstrapBinDirectory\MSBuild.exe.config"
        FileToCopy "artifacts\bin\MSBuildTaskHost\$configuration\net35\MSBuildTaskHost.exe"
        FileToCopy "artifacts\bin\MSBuildTaskHost\$configuration\net35\MSBuildTaskHost.pdb"
    )
    $amd64files = @(
        FileToCopy "artifacts\bin\MSBuild\x64\$configuration\$targetFramework\MSBuild.exe"
        FileToCopy "artifacts\bin\MSBuild\x64\$configuration\$targetFramework\MSBuild.exe.config"
        FileToCopy "artifacts\bin\MSBuildTaskHost\x64\$configuration\net35\MSBuildTaskHost.exe"
        FileToCopy "artifacts\bin\MSBuildTaskHost\x64\$configuration\net35\MSBuildTaskHost.pdb"
    )
}

$filesToCopyToBin += $runtimeSpecificFiles

foreach ($file in $filesToCopyToBin) {
    Copy-WithBackup $file
}

if ($runtime -eq "Desktop") {
    foreach ($file in $x86files) {
        Copy-WithBackup $file
    }

    foreach ($file in $filesToCopyToBin) {
        Copy-WithBackup $file "amd64"
    }

    foreach ($file in $amd64files) {
        Copy-WithBackup $file "amd64"
    }
}

Write-Host -ForegroundColor Green "Copy succeeded"
Write-Verbose "Run $destination\MSBuild.exe"
