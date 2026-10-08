param([string]$WorkspaceRoot = 'D:\Job\CA\tdtd')
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$guide = Join-Path (Split-Path -Parent $workspace) 'tdtd-guide'
$coordination = Join-Path $workspace 'docs/coordination'
$workspaceDocs = @(
    'ARCHITECTURE_MAP.md', 'DOCUMENTATION_MAP.md', 'FEATURE_MAP.md',
    'docs/GIT_BRANCH_AND_COMMIT_GUIDE.md', 'docs/WORK_DONE.md', 'docs/WORK_PLANNED.md',
    'docs/features/FEATURE_12_REQUEST_CENTER_AND_ACTION_NOTIFICATIONS.md',
    'docs/features/FEATURE_10_OPERATIONS_HISTORY_AND_JOB_RUNS.md',
    'docs/features/PATCH_2026_05_20_MONGO_PHASE4_JOB_RUN_OPS_OVERLAP_GUARD.md',
    'docs/training/tap-huan-3-gio/README.md',
    'docs/training/tap-huan-3-gio/CONTRACT_THEO_DOI_TIEN_DO_WORK_ASSIGNMENT_REPORT_2026_10_06.md',
    'docs/training/tap-huan-3-gio/HANDOFF_HOAN_THIEN_LUONG_VA_INP_2026_10_06.md',
    'docs/training/tap-huan-3-gio/HANDOFF_FIX_LIFECYCLE_CHUONG_NGAY_UAT_2026_10_06.md',
    'docs/training/tap-huan-3-gio/HANDOFF_UAT_MAU_2_FOUR_FIXES_2026_10_07.md',
    'docs/training/tap-huan-3-gio/HANDOFF_UAT_MAU_2_M02_2026_10_07.md',
    'docs/training/tap-huan-3-gio/HANDOFF_UAT_MAU_2_PA02_2026_10_07.md',
    'docs/training/tap-huan-3-gio/HANDOFF_RA_SOAT_TAI_LIEU_VA_GIAO_AN_2026_10_07.md',
    'docs/training/tap-huan-3-gio/GIAO_AN_180_PHUT_2026_10_07.md',
    'docs/training/tap-huan-3-gio/HANDOFF_THEME_REFACTOR_2026_10_06.md',
    'docs/training/tap-huan-3-gio/HANDOFF_DASHBOARD_DB_PAGING_RECIPIENTS_2026_10_07.md',
    'docs/training/tap-huan-3-gio/HANDOFF_MINDMAP_POPUP_NOTICE_2026_10_07.md',
    'docs/training/tap-huan-3-gio/HANDOFF_CONG_VIEC_GOM_NHOM_THONG_BAO_PHAN_TRANG_2026_10_06.md',
    'tdtd-fe/src/theme/README.md',
    'docs/training/aggregate-canvas-v1/handoffs/PERIODIC_P05_APPLICATION_UAT_HANDOFF_2026_10_07.md',
    'docs/training/aggregate-canvas-v1/handoffs/PERIODIC_P05_CLOSEOUT_2026_10_07.md',
    'docs/training/aggregate-canvas-v1/handoffs/PERIODIC_P05_QUEUE_MINIO_RETRY_2026_10_06.md',
    'docs/training/aggregate-canvas-v1/handoffs/KHCN_UI_ISSUES_FOR_OWNERS_2026_10_07.md',
    'docs/training/aggregate-canvas-v1/handoffs/KHCN_SAMPLE_UI_REPLAY_2026_10_07.md',
    'docs/training/aggregate-canvas-v1/handoffs/M01_PRACTICE_HANDOFF_FOR_COORDINATOR_2026_10_07.md',
    'docs/training/aggregate-canvas-v1/handoffs/AGGREGATE_OPERATORS_V3_IMPLEMENTATION_2026_10_05.md',
    'docs/training/aggregate-canvas-v1/handoffs/AGGREGATE_DATA_TYPE_TEST_MATRIX_2026_10_05.md',
    'docs/training/aggregate-canvas-v1/handoffs/AGGREGATE_FORMULA_SOURCE_STATUS_IMPLEMENTATION_2026_10_06.md'
)
$guideDocs = @(
    'README.md', 'AGENTS.md', 'REQUIREMENTS.md', 'PLAN.md',
    'qa/TRAINING-V087-HANDOFF.md', 'qa/QA-V087.md',
    'qa/QA-HOSTED-GUIDE-20261008.md', 'qa/training-v087/IMAGE-GAPS.md',
    'qa/SOURCE-MAP.md', 'qa/UAT-MATRIX.md'
)
$sources = @()
foreach ($kind in @('workspace', 'guide')) {
    $sourceRoot = if ($kind -eq 'workspace') { $workspace } else { $guide }
    $names = if ($kind -eq 'workspace') { $workspaceDocs } else { $guideDocs }
    foreach ($name in $names) {
        $source = Join-Path $sourceRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing current document: $source" }
        $sources += [ordered]@{
            source = $source
            snapshot = "docs/project/$kind/$name"
            bytes = (Get-Item -LiteralPath $source).Length
            sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    workCode = 'P20261008-lam-intake'
    date = '2026-10-08'
    timezone = 'Asia/Saigon'
    canonical = 'docs/coordination/LAM_CURRENT_2026_10_08.md'
    note = 'Selected source documents; historical links outside this snapshot require the original workspace. Generated coordination receipts are copied separately.'
    sources = $sources
}
$manifestPath = Join-Path $coordination 'LAM_DOCUMENT_MANIFEST_2026_10_08.json'
$manifestText = ($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($manifestPath, $manifestText, [System.Text.UTF8Encoding]::new($false))
foreach ($repo in @('tdtd-fe', 'tdtd-be')) {
    $repoRoot = Join-Path $workspace $repo
    foreach ($item in $sources) {
        $target = Join-Path $repoRoot $item.snapshot
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $item.source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256) { throw "Snapshot mismatch: $target" }
    }
    $targetCoordination = Join-Path $repoRoot 'docs/coordination'
    New-Item -ItemType Directory -Path $targetCoordination -Force | Out-Null
    Get-ChildItem -LiteralPath $coordination -File | Where-Object { $_.Extension -in @('.md', '.json', '.ps1') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetCoordination $_.Name) -Force
    }
    $projectReadme = "# Lam document snapshots`n`nRead ../coordination/LAM_CURRENT_2026_10_08.md first. workspace/ preserves selected TD-TD paths; guide/ preserves selected tdtd-guide paths. SHA-256 and original source paths are in ../coordination/LAM_DOCUMENT_MANIFEST_2026_10_08.json. Source maintenance stays in the shared workspace. Snapshot docs may reference history/images outside this selection; use the original workspace for those files.`n"
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'docs/project/README.md'), $projectReadme, [System.Text.UTF8Encoding]::new($false))
}
Write-Output "Synced $($sources.Count) document snapshots into FE and BE; all SHA-256 checks passed."
