# ISSUE-030 isolated service checks

Runs the real catalog service, DynamicForm create/read/publish, child assignment form resolver,
report-scoped option reader, and the private validator shared by report save/submit.
The report service has only the two dependencies consumed by these tested paths injected.
This is not a test of the complete HTTP authentication pipeline or report payload commit lifecycle.

The harness starts its own mongod on a random loopback port in a fresh `run-*` directory under
`D:/Job/CA/tdtd/issue030-artifacts`. It never loads production settings or runs the application
startup, jobs, seeders, or UAT MongoDB. Data and logs are retained; the child process is stopped.

Example from workspace root (PowerShell; requires the existing MongoDB executable and package cache):

```powershell
$env:APPDATA = 'D:/Job/CA/tdtd/issue030-artifacts/appdata'
$env:NUGET_PACKAGES = 'C:/Users/LENOVO/.nuget/packages'
dotnet build tdtd-be/tests/EnumCatalogChecks/EnumCatalogChecks.csproj --configfile issue030-artifacts/NuGet.Config --artifacts-path D:/Job/CA/tdtd/issue030-artifacts/isolated-build -p:UseSharedCompilation=false
dotnet issue030-artifacts/isolated-build/bin/EnumCatalogChecks/debug/EnumCatalogChecks.dll D:/Job/CA/tdtd/issue030-artifacts/run-new 'C:/Program Files/MongoDB/Server/7.0/bin/mongod.exe'
```

Use a new run directory each time. `results.json` lists every passing check; `stopped.json`
records the owned child process stop. The Mongo helper is adapted from the existing Canvas
test helper, with a separate directory/replica-set/database namespace for this task.
