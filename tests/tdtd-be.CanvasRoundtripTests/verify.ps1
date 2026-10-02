param(
    [string]$MongoExecutable = 'C:/Program Files/MongoDB/Server/7.0/bin/mongod.exe',
    [string]$PackageCache
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$evidence = Join-Path $workspace 'canvas-save-readback-20260916-artifacts'
$invocation = Join-Path $evidence ('invocation-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $invocation | Out-Null
if (-not (Test-Path -LiteralPath $MongoExecutable)) { throw 'Mongo executable is required; no installation is performed.' }
if (-not $PackageCache) {
    $assets = Get-Content -Raw -Encoding UTF8 (Join-Path $workspace 'tdtd-be/obj/project.assets.json') | ConvertFrom-Json
    $PackageCache = $assets.packageFolders.PSObject.Properties.Name | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $PackageCache) { throw 'Pass an existing NuGet PackageCache.' }
$config = Join-Path $invocation 'NuGet.Config'
'<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -Encoding utf8 $config
$previousAppData = $env:APPDATA
$previousDll = $env:CANVAS_TEST_HOST_DLL
$previousMongo = $env:CANVAS_TEST_MONGOD
Push-Location $workspace
try {
    $env:APPDATA = Join-Path $invocation 'appdata'
    New-Item -ItemType Directory -Path $env:APPDATA | Out-Null
    $build = Join-Path $invocation 'dotnet'
    dotnet build (Join-Path $PSScriptRoot 'tdtd-be.CanvasRoundtripTests.csproj') --artifacts-path $build "-p:RestoreConfigFile=$config" "-p:RestorePackagesPath=$PackageCache" -p:NuGetAudit=false -v:minimal *> (Join-Path $invocation 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Test host build failed; inspect $invocation/build.log" }
    $env:CANVAS_TEST_HOST_DLL = Join-Path $build 'bin/tdtd-be.CanvasRoundtripTests/debug/tdtd-be.IntegrationTests.dll'
    $env:CANVAS_TEST_MONGOD = [IO.Path]::GetFullPath($MongoExecutable)
    Set-Location (Join-Path $workspace 'tdtd-fe')
    node node_modules/vitest/vitest.mjs run --config tests/canvasWorkspace/vitest.roundtrip.config.ts --reporter=json "--outputFile=$invocation/canvas.json" *> (Join-Path $invocation 'canvas.log')
    $canvasExit = $LASTEXITCODE
    node node_modules/vitest/vitest.mjs run tests/statConfig/P8DynamicFormStatisticAdapter.test.ts tests/statConfig/DynamicFormStatisticMethodContract.test.tsx --reporter=json "--outputFile=$invocation/regression.json" *> (Join-Path $invocation 'regression.log')
    if ($canvasExit -ne 0 -or $LASTEXITCODE -ne 0) { throw "Tests did not all pass; inspect $invocation" }
    Write-Output "Canvas save/readback tests completed. Evidence: $invocation"
}
finally {
    Pop-Location
    $env:APPDATA = $previousAppData
    $env:CANVAS_TEST_HOST_DLL = $previousDll
    $env:CANVAS_TEST_MONGOD = $previousMongo
}
