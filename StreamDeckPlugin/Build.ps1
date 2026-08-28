param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$pauseOnExit = -not $NoPause -and
    [Environment]::UserInteractive -and
    (-not [Console]::IsInputRedirected)

$pluginRoot = [IO.Path]::GetFullPath($PSScriptRoot)

Push-Location $pluginRoot
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE."
    }

    & npm run test
    if ($LASTEXITCODE -ne 0) {
        throw "Stream Deck plugin tests failed with exit code $LASTEXITCODE."
    }

    & npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "Stream Deck plugin build failed with exit code $LASTEXITCODE."
    }

    & npm run validate
    if ($LASTEXITCODE -ne 0) {
        throw "Stream Deck plugin validation failed with exit code $LASTEXITCODE."
    }

    & npm exec -- streamdeck pack `
        "com.tomaae.appsupervisor.sdPlugin" `
        --force `
        --no-update-check `
        --output "..\artifacts\StreamDeck"
    if ($LASTEXITCODE -ne 0) {
        throw "Stream Deck plugin packaging failed with exit code $LASTEXITCODE."
    }

    Write-Host "Stream Deck package completed: ..\artifacts\StreamDeck\com.tomaae.appsupervisor.streamDeckPlugin"
}
finally {
    Pop-Location

    if ($pauseOnExit) {
        [void](Read-Host "Press Enter to close this window")
    }
}
