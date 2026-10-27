[CmdletBinding()]
param([switch]$CoreOnly, [switch]$NativePolicyTests, [switch]$VirtualDisplayTests)
if ($CoreOnly -and $VirtualDisplayTests) { throw 'Virtual display acceptance requires the desktop harness.' }
. "$PSScriptRoot\tools.ps1"
$taskDotnet = Get-YomadoDotnet
& $taskDotnet build "$PSScriptRoot\Yomado.Tests\Yomado.Tests.csproj" -c Release --nologo
Assert-YomadoExit 'Core test build'
& $taskDotnet "$PSScriptRoot\Yomado.Tests\bin\Release\net10.0\Yomado.Tests.dll"
Assert-YomadoExit 'Core tests'
if (!$CoreOnly) {
    & $taskDotnet build "$PSScriptRoot\Yomado.App\Yomado.App.csproj" -c Release --nologo
    Assert-YomadoExit 'Native harness build'
    $taskAssembly = Join-Path $PSScriptRoot 'Yomado.App\bin\Release\net10.0-windows\Yomado.dll'
    $taskReport = Join-Path $PSScriptRoot 'artifacts\native-window-test.json'
    New-Item -ItemType Directory -Path (Split-Path $taskReport -Parent) -Force | Out-Null
    & $taskDotnet $taskAssembly --native-window-test $taskReport
    Assert-YomadoExit 'Native fixture window test'
    $taskResult = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
    if (!$taskResult.passed) { throw $taskResult.error }
    & $taskDotnet $taskAssembly --smoke-panel "$PSScriptRoot\artifacts\panel-smoke.json"
    Assert-YomadoExit 'Control panel smoke test'
    $taskLeaseReport = Join-Path $PSScriptRoot 'artifacts\input-lease-smoke.json'
    & $taskDotnet $taskAssembly --test-input-lease $taskLeaseReport
    Assert-YomadoExit 'Read-only input lease IPC smoke test'
    if (!(Get-Content -LiteralPath $taskLeaseReport -Raw | ConvertFrom-Json).passed) { throw 'Input lease IPC smoke test failed.' }
    $taskPointerReport = Join-Path $PSScriptRoot 'artifacts\pointer-boundary-smoke.json'
    & $taskDotnet $taskAssembly --test-pointer-boundary $taskPointerReport
    Assert-YomadoExit 'Read-only pointer hook lifecycle smoke test'
    if (!(Get-Content -LiteralPath $taskPointerReport -Raw | ConvertFrom-Json).passed) { throw 'Pointer hook lifecycle smoke test failed.' }
    foreach ($taskTheme in @('light','dark')) {
        & $taskDotnet $taskAssembly --preview-ui "$PSScriptRoot\artifacts\preview-$taskTheme.png" $taskTheme
        Assert-YomadoExit "$taskTheme preview"
    }
    if ($VirtualDisplayTests) {
        & $taskDotnet $taskAssembly --virtual-display-test "$PSScriptRoot\artifacts\virtual-display-test.json"
        Assert-YomadoExit 'Explicit virtual monitor acceptance'
        $taskAcceptance = Get-Content -LiteralPath "$PSScriptRoot\artifacts\virtual-display-test.json" -Raw | ConvertFrom-Json
        if (!$taskAcceptance.passed) { throw ($taskAcceptance.failures -join [Environment]::NewLine) }
    }
}
if ($NativePolicyTests) {
    & (Get-YomadoMSBuild) "$PSScriptRoot\driver\PolicyTests.vcxproj" /p:Configuration=Release /p:Platform=x64 /nologo /verbosity:minimal
    Assert-YomadoExit 'Native policy test build'
    & "$PSScriptRoot\artifacts\tests\YomadoDriverTests.exe"
    Assert-YomadoExit 'Native policy tests'
}
