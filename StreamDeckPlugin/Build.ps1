$ErrorActionPreference = "Stop"

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
}
finally {
    Pop-Location
}
