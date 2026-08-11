$ErrorActionPreference = "Stop"

$workspaceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$solutionPath = Join-Path $workspaceRoot "AppSupervisor.slnx"
$mainProjectPath = Join-Path $workspaceRoot "AppSupervisor\AppSupervisor.csproj"
$hostProjectPath = Join-Path $workspaceRoot "AppSupervisor.NotificationHost\AppSupervisor.NotificationHost.csproj"
$runtimeIdentifier = "win-x64"
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot "artifacts"))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot "AppSupervisor"))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot "AppSupervisor.staging"))
$archivePath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "AppSupervisor-win-x64.zip"))
$temporaryArchivePath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "AppSupervisor-win-x64.pending.zip"))
$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar

$validatedArtifactNames = @(
    "AppSupervisor",
    "AppSupervisor.staging",
    "AppSupervisor-win-x64.zip",
    "AppSupervisor-win-x64.pending.zip"
)

foreach ($artifactPath in @($publishRoot, $stagingRoot, $archivePath, $temporaryArchivePath)) {
    if (-not $artifactPath.StartsWith(
        $artifactPrefix,
        [StringComparison]::OrdinalIgnoreCase
    ) -or (Split-Path -Leaf $artifactPath) -notin $validatedArtifactNames) {
        throw "Artifact path validation failed: $artifactPath"
    }
}

Write-Host "Restoring projects..."
& dotnet restore $solutionPath --runtime $runtimeIdentifier
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Host "Running Release tests..."
& dotnet test $solutionPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse
}

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

Write-Host "Publishing AppSupervisor single-file package..."
& dotnet publish $mainProjectPath `
    --configuration Release `
    --no-restore `
    --runtime $runtimeIdentifier `
    --self-contained false `
    --output $stagingRoot
if ($LASTEXITCODE -ne 0) {
    throw "AppSupervisor publish failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing notification host single-file package..."
& dotnet publish $hostProjectPath `
    --configuration Release `
    --no-restore `
    --runtime $runtimeIdentifier `
    --self-contained false `
    --output $stagingRoot
if ($LASTEXITCODE -ne 0) {
    throw "Notification host publish failed with exit code $LASTEXITCODE."
}

$packagedConfigPath = Join-Path $stagingRoot "config.json"
$emptyPackagedConfiguration = "[]$([Environment]::NewLine)"
[IO.File]::WriteAllText(
    $packagedConfigPath,
    $emptyPackagedConfiguration,
    [Text.UTF8Encoding]::new($false)
)

$expectedFiles = @(
    "AppSupervisor.exe",
    "AppSupervisor.NotificationHost.exe",
    "config.json"
)

$publishedFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
$unexpectedFiles = @($publishedFiles |
    Where-Object { $_.DirectoryName -ne $stagingRoot -or $_.Name -notin $expectedFiles })
$missingFiles = @($expectedFiles |
    Where-Object { -not (Test-Path -LiteralPath (Join-Path $stagingRoot $_) -PathType Leaf) })

if ($unexpectedFiles.Count -gt 0) {
    throw "Unexpected publish files: $($unexpectedFiles.FullName -join ', ')"
}

if ($missingFiles.Count -gt 0) {
    throw "Missing publish files: $($missingFiles -join ', ')"
}

$packagedConfiguration = [IO.File]::ReadAllText($packagedConfigPath)
if (-not [string]::Equals(
    $packagedConfiguration,
    $emptyPackagedConfiguration,
    [StringComparison]::Ordinal
)) {
    throw "The packaged config.json is not the required empty configuration."
}

if (Test-Path -LiteralPath $temporaryArchivePath) {
    Remove-Item -LiteralPath $temporaryArchivePath
}

Compress-Archive `
    -Path (Join-Path $stagingRoot "*") `
    -DestinationPath $temporaryArchivePath `
    -CompressionLevel Optimal

if (-not (Test-Path -LiteralPath $temporaryArchivePath -PathType Leaf)) {
    throw "Temporary package archive was not created: $temporaryArchivePath"
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath
}

Move-Item -LiteralPath $temporaryArchivePath -Destination $archivePath

$publishRootLocked = $false
if (Test-Path -LiteralPath $publishRoot) {
    foreach ($fileName in $expectedFiles) {
        $existingFilePath = Join-Path $publishRoot $fileName
        if (-not (Test-Path -LiteralPath $existingFilePath -PathType Leaf)) {
            continue
        }

        try {
            $stream = [IO.File]::Open(
                $existingFilePath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None
            )
            $stream.Dispose()
        }
        catch [UnauthorizedAccessException] {
            $publishRootLocked = $true
            break
        }
        catch [IO.IOException] {
            $publishRootLocked = $true
            break
        }
    }
}

if ($publishRootLocked) {
    $currentExtractedPackage = $stagingRoot
    Write-Warning "The existing extracted package is in use. Its folder was left untouched; the current package is available in AppSupervisor.staging and the ZIP was updated."
}
else {
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse
    }

    Move-Item -LiteralPath $stagingRoot -Destination $publishRoot
    $currentExtractedPackage = $publishRoot
}

Write-Host "Publish completed and audited: $currentExtractedPackage"
Get-ChildItem -LiteralPath $currentExtractedPackage -File |
    Sort-Object Name |
    Select-Object Name, Length
Write-Host "Package archive: $archivePath"
Get-Item -LiteralPath $archivePath |
    Select-Object Name, Length
