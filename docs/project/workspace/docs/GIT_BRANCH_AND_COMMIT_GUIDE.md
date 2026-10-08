# Git Branch And Commit Guide

> Navigation: `ARCHITECTURE_MAP.md` -> `DOCUMENTATION_MAP.md` -> this file.
> Type: Rule doc.
> Status: Applied.

## Purpose

This document defines the mandatory branch and commit rules for TDTD development. The goal is to
make every production change traceable to a feature, patch, debug, bug, or deploy work item, and to
make production rollback possible without guessing which edits belong together.

## Repository Boundary

The workspace currently has two application repositories:

- `tdtd-fe`: frontend repository.
- `tdtd-be`: backend repository.

The workspace root, `deploy/`, `docs/`, `scripts/`, and generated `artifacts/` are outside those two
application repositories unless a separate root repository is created later. If a change touches
files outside `tdtd-fe` or `tdtd-be`, record it in the docs and copy the changed deployment files
deliberately; do not assume an FE or BE commit captures it.

## Branch Policy

### Existing-work checkpoint exception — 2026-10-08

When Yud explicitly requests intake, commit and push of accumulated work, checkpoint the current non-production branch so unrelated WIP and existing history remain intact. This exception applies to `P20261008-lam-intake` on `refactor/canvas-unified-form-2026-09-17` in FE and BE. Push the same branch without force, and record both commits and verification limits in the shared handoff. This action does not merge into `dev` or `prod` or approve a deployment. The standard `dev`-to-`prod` flow below remains the rule for future integration and releases.

Mandatory branch roles:

- `dev`: integration branch for all feature, debug, bug, docs, deploy-script, and test work.
- `prod`: production branch. This branch must represent the last approved production baseline.

Rules:

- Start all work from `dev` in the affected repository.
- Do not commit directly on `prod`.
- Merge into `prod` only after the `dev` work is verified.
- Use `--no-ff` when merging `dev` into `prod` so the production release has one explicit merge
  commit that can be reverted.
- Revert production by reverting the production merge commit, not by editing files directly on
  `prod`.
- Do not rewrite `prod` history unless there is an explicit recovery decision.
- FE-only work is committed in `tdtd-fe`; BE-only work is committed in `tdtd-be`.
- Cross-app work uses the same work code in both FE and BE commits.

Current baseline as of 2026-05-13:

- `tdtd-fe`: `dev` was created from `prod` at `d7a6af0 production v1`.
- `tdtd-be`: `dev` was created from `prod` at `330a5ac production v1`.

## Work Code Requirement

Every commit must include one work code. The code identifies why the commit exists.

Allowed code families:

- Feature: `FNN`, matching `docs/features/FEATURE_NN_*.md`.
- Patch: `PYYYYMMDD-slug`, matching a patch document or a row in `docs/WORK_DONE.md`.
- Debug/investigation: `DYYYYMMDD-slug`, matching `docs/debug/DEBUG_YYYY_MM_DD_*.md`.
- Bug: `BUGYYYYMMDD-NN`, for production/user-visible defects that do not yet have a debug doc.
- Deploy/ops: `DEPYYYYMMDD-slug`, for deployment, infra, config, seed, or release-script work.
- Documentation/rule: `DOCYYYYMMDD-slug`, for documentation-only or process-rule changes.

If the work is not already documented, create or update the matching feature/debug/work-log document
before or in the same commit. Avoid anonymous commits such as `update`, `fix`, `debug`, `wip`, or
`production`.

## Commit Message Format

Required subject format:

```text
<type>(<scope>): <WORK_CODE> - <specific content>
```

Allowed `type` values:

- `feat`: new product behavior.
- `fix`: defect fix.
- `debug`: instrumentation, investigation, or diagnostic-only change.
- `docs`: documentation-only change.
- `test`: automated or manual-test support.
- `refactor`: structure change without intended behavior change.
- `deploy`: deployment, infra, seed, environment, or release-script change.
- `chore`: maintenance that does not affect product behavior.
- `revert`: revert of a previous commit or production merge.

Recommended scopes:

- `fe`
- `be`
- `docs`
- `deploy`
- `test`
- a smaller module name such as `dynamic-form`, `aggregation`, `auth`, `notifications`

Good examples:

```text
fix(fe): D20260513-vendor-chunk - keep React libraries in main bundle
deploy(docs): DEP20260513-windows-prod - document win-acme and seed flow
fix(be): BUG20260513-01 - read system bootstrap key from production config
docs(process): DOC20260513-git-flow - require dev-to-prod release commits
feat(be): F09 - add notification producer for report review
```

Bad examples:

```text
update
fix bug
prod
changes
test
```

## Commit Body

Use a body when the commit is not obvious from the subject. Keep it short and concrete.

Recommended body sections:

```text
Why:
- What problem or requirement triggered this change.

Changed:
- Main files, contracts, or behavior changed.

Verified:
- Commands, manual checks, or reason verification was not run.

Risk/Rollback:
- Any migration/config risk and how to revert.
```

## Production Merge Format

Production merge commits must also carry the work code:

```powershell
git -C tdtd-fe switch prod
git -C tdtd-fe merge --no-ff dev -m "merge(prod): F09 - release notification producer UI"

git -C tdtd-be switch prod
git -C tdtd-be merge --no-ff dev -m "merge(prod): F09 - release notification producer API"
```

If production fails after deployment, revert the merge commit:

```powershell
git -C tdtd-fe revert -m 1 <merge_commit>
git -C tdtd-be revert -m 1 <merge_commit>
```

## Standard Working Flow

Start work:

```powershell
git -C tdtd-fe switch dev
git -C tdtd-be switch dev
git -C tdtd-fe status --short --branch
git -C tdtd-be status --short --branch
```

Commit FE work:

```powershell
git -C tdtd-fe add <files>
git -C tdtd-fe commit -m "fix(fe): BUG20260513-01 - describe exact frontend fix"
```

Commit BE work:

```powershell
git -C tdtd-be add <files>
git -C tdtd-be commit -m "fix(be): BUG20260513-01 - describe exact backend fix"
```

Release to production after verification:

```powershell
git -C tdtd-fe switch prod
git -C tdtd-fe merge --no-ff dev

git -C tdtd-be switch prod
git -C tdtd-be merge --no-ff dev
```

Return to development after release:

```powershell
git -C tdtd-fe switch dev
git -C tdtd-be switch dev
```

## Pre-Commit Checklist

Before committing:

- Confirm the affected repository is on `dev`.
- Confirm the commit has exactly one primary work code.
- Confirm the subject says what changed, not just that something changed.
- Update the matching feature, patch, debug, work-log, or rule document when behavior, deployment,
  data, config, or workflow changes.
- Keep unrelated FE and BE changes in separate commits unless they are one cross-app work item.
- Run the focused build/test/check that matches the risk of the change, or state why it was not run.

## Pre-Prod Checklist

Before merging `dev` into `prod`:

- `dev` has all required commits and docs for the work code.
- FE build or focused FE verification passed for FE changes.
- BE build/tests or focused API verification passed for BE changes.
- Deployment/config/seed changes are reflected in deployment docs or work logs.
- The merge commit message includes the same work code and a release summary.
