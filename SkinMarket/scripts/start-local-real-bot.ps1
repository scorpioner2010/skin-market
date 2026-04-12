$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.local"

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Create it from .env.local.example and fill the required values."
}

Get-Content $envFile | ForEach-Object {
    $line = $_.Trim()
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
        return
    }

    $separatorIndex = $line.IndexOf("=")
    if ($separatorIndex -lt 1) {
        return
    }

    $name = $line.Substring(0, $separatorIndex).Trim()
    $value = $line.Substring($separatorIndex + 1).Trim()
    [Environment]::SetEnvironmentVariable($name, $value, "Process")
}

$botDir = Join-Path $root "bot-service"
if (-not (Test-Path (Join-Path $botDir "node_modules"))) {
    Push-Location $botDir
    try {
        npm ci --no-fund --no-audit
    }
    finally {
        Pop-Location
    }
}

$botProcess = Start-Process -FilePath "node" -ArgumentList "src/server.js" -WorkingDirectory $botDir -PassThru

try {
    Push-Location $root
    dotnet run --launch-profile local-real-bot
}
finally {
    Pop-Location
    if ($botProcess -and -not $botProcess.HasExited) {
        Stop-Process -Id $botProcess.Id -Force
    }
}
