# Canvas save/readback integration tests

## Current checkpoint — 2026-09-17

The R2 continuation now exercises actual native Form publish, create-next-version,
history and lifecycle retry through this isolated host. The latest full FE-driven
roundtrip suite records 84 passing cases. The older coverage paragraph at the end
is historical: native publish/next-version are no longer absent from this suite.
Report lifecycle/worker integration, browser/login and production activation are
still not covered. Source publication does not rerun or extend that evidence.

## Current checkpoint — 2026-09-17

The R2 continuation now exercises actual native Form publish, create-next-version,
history and lifecycle retry through this isolated host. The latest full FE-driven
roundtrip suite records 84 passing cases. The older coverage paragraph at the end
is historical: native publish/next-version are no longer absent from this suite.
Report lifecycle/worker integration, browser/login and production activation are
still not covered. Source publication does not rerun or extend that evidence.

This executable hosts the real `DynamicFormController`, `DynamicFormService`, P8 command service and transaction runner on a fresh loopback port. It launches its own MongoDB replica set in a new `run-*` directory, with no production connection strings or application startup. It never starts jobs or calls P11. Synthetic identities exercise service ownership; production JWT/login middleware is **not** covered.

The FE Vitest suites invoke the real `planCanvasSave` / `advanceCanvasSave` over HTTP. Test-only diagnostics read fresh Mongo state; independent fixtures and Node SHA-256 verify the persisted content. Failures can be injected before dispatch, after a real commit, during paired GET, or after the transactional owner and receipt writes before commit.

Run [verify.ps1](./verify.ps1) from PowerShell. It restores only from an existing package cache, builds to a dedicated artifact directory and runs only the Canvas suites plus the two existing P8 adapter/method regression suites. `MongoExecutable` and `PackageCache` can be provided explicitly. It does not install dependencies. Node and .NET must already be available; FE dependencies must already be installed.

Artifacts are kept under `canvas-save-readback-20260916-artifacts` at the workspace root. Every invocation gets its own log/build directory and every host gets its own data directory. Host/Mongo restart intentionally reuses that run's data. Stopping closes the exact children; raw test DB files are retained and no existing data directories are deleted.

Coverage limits: this is module HTTP/DB integration, not a browser workflow or the complete application startup. Form/Phần snapshot builders are checked; native publish and create-next-version remain gated. Report value codec checks are not report DB tests. L5c result generation/storage/readers are absent and no job persistence success is claimed.
