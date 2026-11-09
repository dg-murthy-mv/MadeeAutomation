# Runs the browser (UI) tests in Firefox and WebKit (Safari's engine) as well as Chromium.
# The full suite (dotnet test) already runs everything in Chromium - run that first.
#
#   cd tests\MadeeAutomation.E2E
#   pwsh bin\Debug\net8.0\playwright.ps1 install          # once: downloads Chromium, Firefox and WebKit
#   .\run-browsers.ps1
#
# Firefox can't emulate phones/tablets, so DeviceTests run on WebKit only (Chromium covers them in the main run).

$ErrorActionPreference = "Stop"
$ui = "FullyQualifiedName~AdminTests|FullyQualifiedName~AuthTests|FullyQualifiedName~Round2AdminTests|FullyQualifiedName~Round3MarketTests"

Write-Host "=== Firefox: admin pages ===" -ForegroundColor Cyan
dotnet test --no-build --filter $ui -- Playwright.BrowserName=firefox
$firefox = $LASTEXITCODE

Write-Host "=== WebKit (Safari engine): admin pages + phone/tablet ===" -ForegroundColor Cyan
dotnet test --no-build --filter "$ui|FullyQualifiedName~DeviceTests" -- Playwright.BrowserName=webkit
$webkit = $LASTEXITCODE

if ($firefox -eq 0 -and $webkit -eq 0) { Write-Host "All browsers passed." -ForegroundColor Green; exit 0 }
Write-Host "Firefox exit code: $firefox   WebKit exit code: $webkit" -ForegroundColor Red
exit 1
