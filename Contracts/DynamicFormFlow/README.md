# Dynamic Form and Flow capability catalog

This directory is the canonical, Git-tracked source for the Dynamic Form and
Dynamic Flow capability contract. Documentation may reference the catalog, but
must not maintain an independent editable copy.

Published catalog versions are append-only:

1. Never edit or remove a catalog, schema, or lock entry that has been published.
2. Add a new versioned catalog and schema file.
3. Append its semantic hashes to
   `DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json`.
4. Copy the versioned catalog and schema byte-for-byte to `docs/features/`.
5. Move `DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json` to the exact
   published version and semantic hashes that should be generated.
6. Run the generator, then run it again with `--check`.

`DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json` is the only mutable
catalog pointer. It must resolve to one immutable lock entry by exact version,
catalog semantic hash, and schema semantic hash. It is intentionally not
mirrored under `docs/features/`; the versioned artifacts are the documentation
mirrors, while the canonical pointer has one owner.

The catalog SHA-256 is calculated from semantic canonical JSON: object keys are
sorted recursively, array order is preserved, and the compact JSON is encoded
as UTF-8. Formatting, indentation, and CRLF/LF differences therefore do not
change the hash. The generator also checks the lock file against its available
Git history so an already-published entry cannot be silently overwritten.

From `tdtd-be`:

```text
node tools/generate-dynamic-form-flow-capability-catalog.mjs
node tools/generate-dynamic-form-flow-capability-catalog.mjs --check
```

Generated outputs are deterministic and contain no timestamp:

- `Common/Capabilities/DynamicFormFlowCapabilityCatalog.g.cs`
- `../tdtd-fe/src/generated/dynamicFormFlowCapabilityCatalog.generated.ts`

To roll back P8 statistics-configuration mutation activation, point `CURRENT`
to the exact published v1.4 pin, regenerate both outputs, run `--check` and the
workspace validator, then rebuild and deploy backend and frontend together.
The v1.4 rollback keeps all P7 mapping capabilities active and makes every P8
configuration writer fail closed with `STAT_CONFIG_CAPABILITY_CONFLICT` before
a Mongo transaction starts. Never remove the v1.5 lock entry or its versioned
files during rollback; restore the exact v1.5 pointer to reactivate P8.

P9-12 completed at `ACHIEVED/P9-A2/P9-D2` and published exact catalog v1.6. The P9 rollback procedure is
CURRENT-only: point CURRENT to the exact v1.5 bytes, regenerate/check both outputs, and execute the frozen
`35/35` route matrix. Every P9 route must fail closed with `409` and zero writes while P8 remains green.
Never remove the v1.6 LOCK entry or versioned v1.6 catalog/schema during rollback. Restore the exact v1.6
CURRENT bytes and rerun generator, validator, build, browser, and regression checks. P9-CLOSE passed this
publish/rollback/restore sequence; P10 was not started.

P9-12 completed at `ACHIEVED/P9-A2/P9-D2` and published exact catalog v1.6. The P9 rollback procedure is
CURRENT-only: point CURRENT to the exact v1.5 bytes, regenerate/check both outputs, and execute the frozen
`35/35` route matrix. Every P9 route must fail closed with `409` and zero writes while P8 remains green.
Never remove the v1.6 LOCK entry or versioned v1.6 catalog/schema during rollback. Restore the exact v1.6
CURRENT bytes and rerun generator, validator, build, browser, and regression checks. P9-CLOSE passed this
publish/rollback/restore sequence; P10 was not started.

<!-- FULL-P10-RECONCILE P10-12 TERMINAL -->
## P10-12 catalog v1.7 publication

- Terminal state: `ACHIEVED/P10-A2/P10-D1`; chain `p10_chain_20260810002129_9f56`.
- Coverage: requirements `32/32`, groups `12/12`, cases `280/280`, P10-CLOSE `10/10`, two-clean `540/540`.
- Catalog v1.7 raw `072831d879352c76ca9e5af5f9cc2a20e13c632653fed466131f5f7a359204c4`; exact-v1.6 CURRENT-only rollback `19/19` with `57` stores and zero writes; restored CURRENT/LOCK v1.7.
- Production browser `3/3`, `networkMockCount=0`; P9 regression, security, accessibility and cleanup passed.
- Final evidence: `docs/features/FULL_P10_RECONCILE_EVIDENCE_2026_08_14.md`. Deferred items: none.
- Rollback is CURRENT-only to exact v1.6; LOCK remains append-only v1.7 and restore returns exact v1.7.
