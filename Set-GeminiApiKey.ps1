<#
.SYNOPSIS
    Configures the Gemini API Key for IntelliMon AI in %APPDATA%\IntelliMon\settings.json.

.DESCRIPTION
    Reads or securely prompts for a Gemini API key and saves it into the IntelliMon
    settings configuration file. Safely merges with existing configuration without
    overwriting custom threshold settings.

.PARAMETER ApiKey
    Optional. The Gemini API key string. If omitted, the script prompts securely for it.

.EXAMPLE
    .\Set-GeminiApiKey.ps1

.EXAMPLE
    .\Set-GeminiApiKey.ps1 -ApiKey "AIzaSy..."
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false, Position = 0)]
    [string]$ApiKey
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $secureKey = Read-Host "Enter your Gemini API key" -AsSecureString
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try {
        $ApiKey = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Warning "No API key was provided. Settings were not updated."
    exit 1
}

$settingsDir  = Join-Path $env:APPDATA "IntelliMon"
$settingsFile = Join-Path $settingsDir "settings.json"

if (-not (Test-Path $settingsDir)) {
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
}

if (Test-Path $settingsFile) {
    try {
        $settings = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Warning "Existing settings.json was malformed or unreadable. Initializing with defaults."
        $settings = $null
    }
}

if ($null -eq $settings) {
    $settings = [PSCustomObject]@{
        cpuThresholdPercent     = 20.0
        memoryThresholdPercent  = 10.0
        diskThresholdPercent    = 15.0
        rotationIntervalSeconds = 3.0
        startWithWindows        = $false
    }
}

$settings | Add-Member -MemberType NoteProperty -Name "geminiApiKey" -Value $ApiKey.Trim() -Force

$jsonContent = $settings | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($settingsFile, $jsonContent, [System.Text.Encoding]::UTF8)

Write-Host ""
Write-Host "Success: Gemini API key has been configured for IntelliMon AI." -ForegroundColor Green
Write-Host "Target:  $settingsFile" -ForegroundColor Cyan
Write-Host ""
