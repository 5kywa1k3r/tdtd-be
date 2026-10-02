# Native statistic calculator, publication and readback checks

## Current checkpoint — 2026-09-17 source publication

The latest native publication run recorded **371/371** cases, including the 37
HTTP cases and 17 native Form publication cases. Native Form publication now uses
the real service/P8 transaction; candidate activation and report lifecycle inputs
remain explicit isolated fixtures. Historical paragraphs below describe earlier
checkpoints and do not override this statement. This source-publication turn does
not claim a fresh test run, full application acceptance or production activation.

The executable's project references the product backend and the sibling
`tdtd-be.CanvasRoundtripTests/CanvasMongo.cs`; include both test directories when
checking out the source. Run only with a fresh artifact directory and the test's
own loopback Mongo process. Its output supplies the synthetic wire fixtures used
by the FE host/consumer suites. No raw local databases or generated binaries are
part of the source commit.

## Current checkpoint — 2026-09-17 source publication

The latest native publication run recorded **371/371** cases, including the 37
HTTP cases and 17 native Form publication cases. Native Form publication now uses
the real service/P8 transaction; candidate activation and report lifecycle inputs
remain explicit isolated fixtures. Historical paragraphs below describe earlier
checkpoints and do not override this statement. This source-publication turn does
not claim a fresh test run, full application acceptance or production activation.

The executable's project references the product backend and the sibling
`tdtd-be.CanvasRoundtripTests/CanvasMongo.cs`; include both test directories when
checking out the source. Run only with a fresh artifact directory and the test's
own loopback Mongo process. Its output supplies the synthetic wire fixtures used
by the FE host/consumer suites. No raw local databases or generated binaries are
part of the source commit.

Current continuation: `NativeHttpCases` uses real loopback Kestrel, JwtBearer,
Me middleware with the existing in-memory session cache, product controllers,
service ACLs and a hosted Hangfire Mongo server. Synthetic published owners and
candidate activation remain explicit fixtures. No production Program/Redis/login,
recurring jobs, catalog activation or real Form publication is implied.
37 isolated HTTP cases pass; the previous 317 cases were rerun successfully before
the initial HTTP setup failure, which is retained in evidence and fixed separately.
See `L5C3_PREACCEPTANCE_2026_09_17.md` for precise runs and remaining release gates.

Current runner (use a fresh label):

```powershell
& './canvas-l5c3-preacceptance-20260917-artifacts/run-final.ps1' -Label <unique-label>
# Add -HttpOnly to isolate HTTP/hosted worker verification.
```

The scoped FE consumer suite additionally requires `CANVAS_NATIVE_HTTP_WIRE`
pointing at the verified HTTP output directory; the current `run-fe.ps1` sets both
fixture roots. HTTP and Mongo shutdown evidence is retained. Older counts below
describe historical checkpoints.

Latest continuation: `NativeMetadataCases` exports service DTO JSON for the FE
consumer suite (Direct/Basic/Advanced current/history/stale) plus eight typed
calculator fixtures. The latter assemble metadata explicitly and are not published
P8 reader/history evidence. BE 317 cases, FE 94 cases; see
`L5C3_FE_CONSUMER_2026_09_17.md`. No BE product changes in this slice.

Current work: snapshot-derived native result metadata across Direct/Basic/Advanced
current and history readers. Tests exercise selected operations, axis order/specs,
literal separators, pinned labels, restart/history, hidden targets, malformed
metadata, schema/plan/structure/operation mismatch and metadata quota. Stored
snapshot/artifact bytes and hashes stay unchanged by reads. See
`L5C3_RESULT_METADATA_2026_09_17.md`; isolated runner in
`canvas-l5c3-result-metadata-20260917-artifacts`. The previous checkpoints below
are historical. No FE/HTTP/JWT/hosted scheduler/native publish acceptance is implied.

Final metadata checkpoint: 316/316 (298 existing + 18 new). Full source build:
91 backend warnings, 0 errors. Two isolated runs and 38 Mongo starts/restarts
stopped, raw evidence retained. A test-only nullable assertion was added after
the final run and verified by a fresh full build; no executable behavior changed.

Current work: nonempty STATISTIC labels via real Label/Form P8 writers,
pinned-version integrity/visibility, explicit v1 rejection/upgrade, and durable
Basic/Advanced refresh command recovery. Includes ambiguous dispatch, lost response,
restart, concurrent replay, conflicting request, stale source, revoked actor,
corrupt raw and expired lease. See `L5C3_LABELS_RECOVERY_2026_09_17.md` for final
counts/evidence and remaining boundaries. Run the isolated `run-final.ps1` in
`canvas-l5c3-labels-recovery-20260917-artifacts`; never point these fixtures at live DB.
Published/LOCKED Form and lifecycle/activation remain synthetic; label/config
writers, storage, calculation, transactions and worker methods are product code.
The dispatcher is a spy. Label metadata is asserted in the persisted snapshot,
not an as-yet-unimplemented metadata projection of the result DTO.

Final verification: 298/298 (258 existing + 40 new), full backend build
91 warnings / 0 errors. Two isolated DB runs, 28 Mongo starts/restarts stopped;
all raw evidence retained. The initial test run had 24 fixture/assertion failures;
they were corrected without removing guards or cases. See the handoff for details.

Previous checkpoint:

Current checkpoint: Advanced native DAY/MONTH/YEAR result and refresh worker.
Final: 258/258 (210 existing + 48 new); full backend build: 91 warnings, 0 errors.
Product source-day resolution, raw-source calculator, immutable current/history
snapshots, actual broad-build quota and Hangfire dispatch expression are exercised.
Workers run directly without HTTP context; the dispatcher is a spy, not a hosted
scheduler. Coverage includes unequal daily AVG counts, CRLF, section scoping,
source scan quota before date filtering, restart, drift, transaction fences and
lease/retry/acknowledgement. Final evidence and limits:
`L5C3_ADVANCED_RESULT_WORKER_2026_09_17.md`; runner:
`canvas-l5c3-advanced-result-20260917-artifacts/run-final.ps1` with a fresh label.
Native Form publication, nonempty selected label snapshots, active v1,
HTTP/JWT/FE/hosted scheduler and scalar hierarchy/query consumers remain open.

Earlier configuration checkpoint:

Latest checkpoint: Advanced native configuration through the actual P805 services.
Final result: 210/210 (180 existing + 30 new); full backend build: 91 warnings, 0 errors.
Adds section ownership, exact operation selection, legacy canonical compatibility,
put/lock/replay/history/next-draft, concurrent CAS, restart, raw corruption and
dependency drift checks. The actual WorkSummaryTokenService is used with a seeded
isolated unit; lock replay, quota denial and transaction rollback are covered.
Basic and Advanced share selection/pinning. Scalar Advanced consumers stay guarded.
Advanced native hierarchy/result/worker, nonempty selected label snapshots, active
v1 and HTTP/JWT/FE/hosted scheduler/native Form publication remain unverified.
Evidence and runner: canvas-l5c3-advanced-config-20260917-artifacts/run-final.ps1
with a fresh label. See L5C3_ADVANCED_CONFIG_2026_09_17.md for final results.

Earlier worker checkpoint:

Latest checkpoint: Basic native worker adds 28 cases (180 total). Product queue,
worker without HTTP context, durable intent/lease/receipt, actor/source/config
fences and snapshot readback in the acknowledgement transaction are exercised.
Covers ambiguous dispatch/retry, duplicate claims, expiry, cancellation, restart
between storage and acknowledgement, corruption and pre/post-ack drift.
Hangfire client is a spy; no hosted scheduler, HTTP/JWT/FE or production job.
Native Form publication, nonempty selected labels, virtual units, Advanced and
active v1 remain open. Evidence: canvas-l5c3-worker-20260917-artifacts.
Run-worker-verified is 180/180; final build adds retry delays (hosted timing untested).

Earlier checkpoints follow.

This executable checks L5b, L5c stage storage, the Direct/Foundation refresh caller,
publication and native current/history reader. It does not run a hosted scheduler,
HTTP server, native Form publish endpoint, production job or P11 activation.

The next Basic result slice adds 31 cases (152 total): product source selection,
actual payload reader, raw cross-period calculation, full typed immutable storage,
transaction source/config/ACL fences and rollback, concurrent replay, restart,
current revalidation and pinned history under current rights. EXCLUDE/recall,
pre/post-write drift, corrupt captures and storage quota are checked. The new
`native-summary` route is compiled but has not been exercised through HTTP/JWT/FE.
The source Form and approval lifecycle are still synthetic isolated fixtures;
Basic background worker/refresh, Advanced and active v1 compatibility remain open.
Use `canvas-l5c3-result-20260917-artifacts/run-tests.ps1` with a fresh label for this checkpoint.

L5c.3 adds 18 Basic config cases (121 total): actual P8 put/lock/CAS/receipt,
history/next-draft/readback across a Mongo restart, strict native references,
legacy canonical hash compatibility, selected-plan fidelity, raw fixture AVG,
authorization and the legacy result guard. Basic result persistence/membership,
Advanced, nonempty native label ownership and active v1 compatibility are not
covered by these additions. Published Form/activation inputs remain synthetic.
The retained runner for this checkpoint is
`canvas-l5c3-20260917-artifacts/run-tests.ps1` with a fresh label.

- Uses the backend project and existing `CanvasMongo` helper; no new package.
- Calculator cases use hand-authored expected values for all 13 methods and all
  eight supported input types. Rich text remains rejected.
- Storage checks use a dedicated Mongo process/database, full content hashes,
  restart, exact/concurrent replay, partial staging, immutable conflict, corruption,
  cancellation, quotas, and historical artifact reads.
- The `p8-draft-owner.fixture.json` file is synthetic test data captured from the
  earlier isolated Canvas roundtrip. `LockedUnitInput` constructs a locked model
  in RAM to exercise intake validation. PublicationCases explicitly seeds that
  synthetic locked/published configuration, lifecycle documents and candidate
  activation into an isolated test database. This does not prove real Form
  publication, lifecycle HTTP commands or production catalog activation.
- The original 82 cases include a synthetic authorization callback. The 21 added
  cases use product membership/tenant, payload writer/reader, all six legacy stages,
  Direct/Foundation refresh, transaction runner, lease CAS and current/history reader.
  They cover EXCLUDE, concurrent/restarted replay, corrupt artifact, precommit
  lease/source/config/catalog/ACL/period drift, rollback, and recall down to no sources.
  Basic/Advanced, active native v1, hosted worker/HTTP/JWT/FE remain unverified.

From the workspace, use the retained evidence script with a fresh label:

```powershell
& './canvas-l5c2-20260917-artifacts/run-tests.ps1' -Label <unique-label>
```

Requires installed .NET 8, cached backend NuGet packages, and the existing
`C:/Program Files/MongoDB/Server/7.0/bin/mongod.exe`. Build uses the private artifact
directory and a NuGet config with no package sources. No dependency installation.
The script runs no FE build/browser/server. Test Mongo binds loopback and is shut
down by `await DisposeAsync`; its raw data is retained under a new
`canvas-save-readback-20260916-artifacts/run-l5c-*` directory, as required by the
reused helper. Never point the helper or the store tests at an existing database.
