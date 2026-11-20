# Runs the demo on Windows with what this machine has: Docker, then Podman, then the .NET SDK.
# Works on Windows PowerShell 5.1 (built into Windows) and on PowerShell 7.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\run.ps1 [arguments of the app]

# No $ErrorActionPreference = 'Stop': on Windows PowerShell 5.1 it turns any stderr output of docker, podman,
# dotnet or node into a fatal error, and the script would stop before trying the next option.
Set-Location (Split-Path -Parent $PSScriptRoot)

$image = 'skail-subscription-cycle-dotnet'
# ports the container publishes (the demos with a page use 8080)
$publish = @('-p', '127.0.0.1:8080:8080')

if (-not (Test-Path '.env')) {
    Write-Host 'Missing .env: copy .env.example to .env and fill in the values of your skail environment.' -ForegroundColor Red
    exit 1
}

# Builds the image and runs it. Returns when the engine is not usable or the build fails, so the next option is tried.
function Invoke-Container([string] $engine, [string[]] $appArgs) {
    if (-not (Get-Command $engine -ErrorAction SilentlyContinue)) { return }
    & $engine info *> $null
    if ($LASTEXITCODE -ne 0) { return }
    Write-Host "Building the image with $engine (the first time takes a few minutes)..."
    & $engine build -q -t $image . | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "The $engine build failed; trying the next option." -ForegroundColor Yellow
        return
    }
    Write-Host "Running with $engine."
    # the repository folder is the working directory of the app: it reads .env from there
    $folder = (Get-Location).Path
    & $engine run --rm -it --init @publish -v "${folder}:/data" $image @appArgs
    exit $LASTEXITCODE
}

function Test-DotNet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    foreach ($sdk in $sdks) {
        $major = 0
        if ([int]::TryParse(([string]$sdk).Split('.')[0], [ref]$major) -and $major -ge 8) { return $true }
    }
    return $false
}

Invoke-Container 'docker' $args
Invoke-Container 'podman' $args

if (Test-DotNet) {
    Write-Host 'Running with the .NET SDK.'
    & dotnet run -- @args
    exit $LASTEXITCODE
}

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host 'dotnet was found, but without the .NET 8 SDK (or newer): a runtime alone cannot build the demo.' -ForegroundColor Yellow
}
Write-Host 'Nothing to run the demo with. Install one of these and run the script again:' -ForegroundColor Red
Write-Host '  - Docker Desktop: https://docs.docker.com/desktop/setup/install/windows-install/  (it must be running)'
Write-Host '  - Podman:         https://podman.io/docs/installation'
Write-Host '  - .NET 8 SDK:     https://dotnet.microsoft.com/download/dotnet/8.0'
exit 1
