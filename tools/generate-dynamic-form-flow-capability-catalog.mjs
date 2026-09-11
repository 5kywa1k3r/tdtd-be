import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const generatorFilePath = fileURLToPath(import.meta.url);
const scriptDir = path.dirname(generatorFilePath);
const backendRoot = path.resolve(scriptDir, "..");
const workspaceRoot = path.resolve(backendRoot, "..");
const contractDir = path.join(backendRoot, "Contracts", "DynamicFormFlow");
const lockPath = path.join(contractDir, "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json");
const currentPath = path.join(
  contractDir,
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json",
);
const csharpOutputPath = path.join(
  backendRoot,
  "Common",
  "Capabilities",
  "DynamicFormFlowCapabilityCatalog.g.cs",
);
const typescriptOutputPath = path.join(
  workspaceRoot,
  "tdtd-fe",
  "src",
  "generated",
  "dynamicFormFlowCapabilityCatalog.generated.ts",
);
const lockGitPath = "Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json";
const approvedCurrentVersions = new Set(["1.2", "1.3", "1.4", "1.5", "1.6", "1.7"]);
const candidateCatalogFileName =
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.candidate.json";
const candidateSchemaFileName =
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.schema.candidate.json";
const candidateLockFileName = "candidate-lock.json";
const candidateCatalogVersion = "1.4";
const candidatePackId = "FULL-P7-MAPPING-POLICY";
const candidateSourceCatalogRawSha256 =
  "6d8b6e52b487f779fb577486779bed286197d9eb43083c2060e09442defad753";
const p8CandidateCatalogFileName =
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.candidate.json";
const p8CandidateSchemaFileName =
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.schema.candidate.json";
const p8CandidateCatalogVersion = "1.5";
const p8CandidatePackId = "FULL-P8-STAT-CONFIG";
const p8CandidateSourceCatalogRawSha256 =
  "cbb96a77117df6d55af7036e77f2efe36196bef5849c8af130cd7c5397b61092";
const p8CandidateSourceCatalogSemanticSha256 =
  "d2ca56a4745380688e24c6926752643d2b023b2d47bc1578d3b3e2f9379457ee";
const p8CandidateSourceSchemaRawSha256 =
  "6bf9d36d539f436adb92c54b0ca02fd319c68fd1ccac0a4041c27258a861a718";
const p8CandidateSourceSchemaSemanticSha256 =
  "c91abd87727e25bb62a7ecb9de939d85a747ecc131e7bd9dad695c45217014a1";
const p8InheritedStatisticsSemanticSha256 =
  "46e1c2b6429127b25963d948abaaf55ac7ad69abc9a97167a6b85ddefb4a8b8e";
const p8InheritedMappingSemanticSha256 =
  "5b3f14c0953e6c900973d151afc678bcb4b7f0dfc1097187ad6eb57ebe54a789";
const rollbackCatalogPointer = {
  pointerVersion: 1,
  catalogVersion: "1.4",
  catalogSha256: "d2ca56a4745380688e24c6926752643d2b023b2d47bc1578d3b3e2f9379457ee",
  schemaSha256: "c91abd87727e25bb62a7ecb9de939d85a747ecc131e7bd9dad695c45217014a1",
};
const p9RollbackCatalogPointer = {
  pointerVersion: 1,
  catalogVersion: "1.5",
  catalogSha256: "e3c335617721bbf8cd09c62c5e76377f23f3d024b20c848bf66c8c539f0c9d2f",
  schemaSha256: "9bab8219c4c097977de2417885206caacec9bca5a188a588d0d1a96063b84477",
};
const p9PublishedCatalogVersion = "1.6";
const p9PublishedCatalogSchemaFileName =
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json";
const p9PublishedApprovedAt = "2026-08-04";
const p9PublishedCatalogRawSha256 =
  "a790be94e4598208de08af39f8782a267ce434db2c20242a811991429932233c";
const p9PublishedSchemaRawSha256 =
  "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940";
const p9PromotedCapabilityIds = new Set([
  "DIRECT_FIELD_TABLE_LABEL",
  "BASIC_SUMMARY",
  "ADVANCED_SUMMARY",
  "DIFF",
  "FLOW_SCOPES",
]);
const p10RollbackCatalogPointer = {
  pointerVersion: 1,
  catalogVersion: "1.6",
  catalogSha256:
    "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b",
  schemaSha256:
    "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed",
};
const p10PublishedCatalogVersion = "1.7";
const p10PublishedCatalogSchemaFileName =
  "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json";
const p10PublishedApprovedAt = "2026-08-10";
const p10PublishedCatalogRawSha256 =
  "072831d879352c76ca9e5af5f9cc2a20e13c632653fed466131f5f7a359204c4";
const p10PublishedCatalogSemanticSha256 =
  "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1";
const p10PublishedSchemaRawSha256 =
  "16e796d421a8f3a6675afc32be96c6fea001ae7ed6b431dfe4655cabb98cc3b7";
const p10PublishedSchemaSemanticSha256 =
  "5842baf176bf1eec453b718d07e50da55a417aa9036f4f097ccfbb6fc58c1978";

const parseCliArguments = (rawArgs) => {
  let mode = "generate-current";
  let modeWasSet = false;
  let candidateDir = null;

  const setMode = (nextMode, argument) => {
    if (modeWasSet) {
      throw new Error(
        `Argument ${argument} cannot be combined with another mode argument.`,
      );
    }
    mode = nextMode;
    modeWasSet = true;
  };

  for (let index = 0; index < rawArgs.length; index += 1) {
    const argument = rawArgs[index];
    if (argument === "--check") {
      setMode("check-current", argument);
      continue;
    }
    if (argument === "--generate-candidate") {
      setMode("generate-candidate", argument);
      continue;
    }
    if (argument === "--verify-candidate") {
      setMode("verify-candidate", argument);
      continue;
    }
    if (argument === "--generate-p8-candidate") {
      setMode("generate-p8-candidate", argument);
      continue;
    }
    if (argument === "--verify-p8-candidate") {
      setMode("verify-p8-candidate", argument);
      continue;
    }
    if (argument === "--candidate-dir") {
      if (candidateDir !== null) {
        throw new Error("--candidate-dir may be specified only once.");
      }
      index += 1;
      if (index >= rawArgs.length || rawArgs[index].startsWith("--")) {
        throw new Error("--candidate-dir requires a path value.");
      }
      candidateDir = rawArgs[index];
      continue;
    }
    if (argument.startsWith("--candidate-dir=")) {
      if (candidateDir !== null) {
        throw new Error("--candidate-dir may be specified only once.");
      }
      candidateDir = argument.slice("--candidate-dir=".length);
      if (candidateDir.length === 0) {
        throw new Error("--candidate-dir requires a path value.");
      }
      continue;
    }
    throw new Error(
      `Unknown argument ${argument}. Supported modes: --check, ` +
        "--generate-candidate, --verify-candidate, --generate-p8-candidate, " +
        "--verify-p8-candidate; candidate modes require --candidate-dir.",
    );
  }

  const candidateMode =
    mode === "generate-candidate" ||
    mode === "verify-candidate" ||
    mode === "generate-p8-candidate" ||
    mode === "verify-p8-candidate";
  if (candidateMode && candidateDir === null) {
    throw new Error(`${mode} requires --candidate-dir.`);
  }
  if (!candidateMode && candidateDir !== null) {
    throw new Error("--candidate-dir is valid only in a candidate mode.");
  }

  return { mode, candidateDir };
};

const cli = parseCliArguments(process.argv.slice(2));
const checkOnly = cli.mode === "check-current";

const canonicalize = (value) => {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, canonicalize(value[key])]),
    );
  }
  return value;
};

const canonicalJson = (value) => JSON.stringify(canonicalize(value));
const semanticSha256 = (value) =>
  createHash("sha256").update(canonicalJson(value), "utf8").digest("hex");
const rawSha256 = (value) => createHash("sha256").update(value).digest("hex");
const cloneJson = (value) => JSON.parse(JSON.stringify(value));
const renderJsonFile = (value) => `${JSON.stringify(value, null, 2)}\n`;

const parseJsonText = (raw, displayPath) => {
  try {
    return JSON.parse(raw.replace(/^\uFEFF/, ""));
  } catch (error) {
    throw new Error(`${displayPath} is not valid JSON: ${error.message}`);
  }
};

const readJson = async (filePath) => {
  const displayPath = path.relative(workspaceRoot, filePath).replaceAll("\\", "/");
  return parseJsonText(await readFile(filePath, "utf8"), displayPath);
};

const expect = (errors, condition, message) => {
  if (!condition) errors.push(message);
};

const exactArray = (actual, expected) =>
  Array.isArray(actual) &&
  actual.length === expected.length &&
  actual.every((value, index) => value === expected[index]);

const expectExactKeys = (errors, value, expectedKeys, label) => {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${label} must be an object.`);
    return;
  }
  const actualKeys = Object.keys(value).sort();
  const sortedExpected = [...expectedKeys].sort();
  expect(
    errors,
    exactArray(actualKeys, sortedExpected),
    `${label} keys must be exactly: ${sortedExpected.join(", ")}.`,
  );
};

const currentExpectedDomainIds = {
  dynamicFormFieldTypes: [
    "shortText",
    "longText",
    "richText",
    "stringList",
    "number",
    "date",
    "fullDate",
    "singleSelect",
    "multiSelect",
    "boolean",
  ],
  dynamicFormValueSources: [
    "FIXED_ENUM",
    "ENUM_CATALOG",
    "SYSTEM_UNIT",
    "SYSTEM_USER",
    "SYSTEM_POSITION",
    "SYSTEM_UNIT_TYPE",
  ],
  dynamicFormTableModes: [
    "FIXED_GRID",
    "APPEND_ROWS",
    "APPEND_COLUMNS",
    "MATRIX",
    "SUMMARY_TEMPLATE",
  ],
  dynamicFlowArchetypes: Array.from(
    { length: 12 },
    (_, index) => `FLOW-T${String(index + 1).padStart(2, "0")}`,
  ),
  statisticsCapabilities: [
    "DIRECT_FIELD_TABLE_LABEL",
    "BASIC_SUMMARY",
    "ADVANCED_SUMMARY",
    "DIFF",
    "FLOW_SCOPES",
    "FLOW_STATISTIC_PROFILE",
  ],
};

const candidateMappingCapabilities = [
  {
    id: "FIELD_TYPED",
    name: "Typed field mapping",
    status: "SUPPORTED",
    targetPhase: "P7",
    testPrefix: "MAP-FIELD",
    uiSurface: "FLOW_MAPPING_DEFINITION_AND_REPORT_RUNTIME",
    notes:
      "Typed copy and multi-input calculation use the same versioned safe evaluator for validation, preview and apply.",
  },
  {
    id: "FIXED_GRID",
    name: "Fixed-grid target mapping",
    status: "SUPPORTED",
    targetPhase: "P7",
    testPrefix: "MAP-TABLE-FIXED-GRID",
    uiSurface: "FLOW_MAPPING_DEFINITION_AND_REPORT_RUNTIME",
    notes:
      "Target identity is exact blockId, columnKey, rowKey and value-slot/index-map identity; positional fallback is forbidden.",
  },
  {
    id: "APPEND_ROWS",
    name: "Append-rows target mapping",
    status: "SUPPORTED",
    targetPhase: "P7",
    testPrefix: "MAP-TABLE-APPEND-ROWS",
    uiSurface: "FLOW_MAPPING_DEFINITION_AND_REPORT_RUNTIME",
    notes:
      "The canonical source row key, scoped by mapping ID and version, is the idempotent target row business key.",
  },
  {
    id: "MATRIX_SPARSE",
    name: "Matrix and sparse target mapping",
    status: "SUPPORTED",
    targetPhase: "P7",
    testPrefix: "MAP-TABLE-MATRIX-SPARSE",
    uiSurface: "FLOW_MAPPING_DEFINITION_AND_REPORT_RUNTIME",
    notes:
      "Exact sparse coordinates keep values1D, value slots, index maps and matrix cells synchronized without positional fallback.",
  },
  {
    id: "SOURCE_REPORT_GRAIN",
    name: "Source-report evaluation grain",
    status: "SUPPORTED",
    targetPhase: "P7",
    testPrefix: "MAP-SOURCE-REPORT-GRAIN",
    uiSurface: "FLOW_MAPPING_DEFINITION_AND_REPORT_RUNTIME",
    notes:
      "Each exact authorized source report is evaluated independently and bound to its runtime and source-signature identity.",
  },
  {
    id: "GROUP_GRAIN",
    name: "Group evaluation grain",
    status: "INTENTIONAL_BLOCK",
    targetPhase: null,
    testPrefix: "MAP-BLOCK-GROUP-GRAIN",
    uiSurface: "FLOW_MAPPING_DEFINITION_DISABLED",
    notes: "Blocked with DYNAMIC_FLOW_MAPPING_GROUP_INTENTIONAL_BLOCK.",
  },
  {
    id: "CUSTOM_JOIN_KEY",
    name: "Custom join key",
    status: "INTENTIONAL_BLOCK",
    targetPhase: null,
    testPrefix: "MAP-BLOCK-CUSTOM-JOIN-KEY",
    uiSurface: "FLOW_MAPPING_DEFINITION_DISABLED",
    notes:
      "Blocked with DYNAMIC_FLOW_MAPPING_CUSTOM_JOIN_KEY_INTENTIONAL_BLOCK; null or blank selects the canonical row-key join.",
  },
  {
    id: "APPEND_COLUMNS_TARGET",
    name: "Append-columns target mapping",
    status: "INTENTIONAL_BLOCK",
    targetPhase: null,
    testPrefix: "MAP-BLOCK-APPEND-COLUMNS-TARGET",
    uiSurface: "FLOW_MAPPING_DEFINITION_DISABLED",
    notes:
      "Blocked with DYNAMIC_FLOW_MAPPING_APPEND_COLUMNS_TARGET_INTENTIONAL_BLOCK; exact APPEND_COLUMNS source reads remain read-only.",
  },
  {
    id: "SCALAR_TO_ROW",
    name: "Scalar-to-row mapping",
    status: "INTENTIONAL_BLOCK",
    targetPhase: null,
    testPrefix: "MAP-BLOCK-SCALAR-TO-ROW",
    uiSurface: "FLOW_MAPPING_DEFINITION_DISABLED",
    notes: "Blocked with DYNAMIC_FLOW_MAPPING_SCALAR_TO_ROW_INTENTIONAL_BLOCK.",
  },
  {
    id: "ROW_TO_REPORT",
    name: "Row-to-report mapping",
    status: "INTENTIONAL_BLOCK",
    targetPhase: null,
    testPrefix: "MAP-BLOCK-ROW-TO-REPORT",
    uiSurface: "FLOW_MAPPING_DEFINITION_DISABLED",
    notes: "Blocked with DYNAMIC_FLOW_MAPPING_ROW_TO_REPORT_INTENTIONAL_BLOCK.",
  },
];

const p8CandidateStatisticsConfigurationCapabilities = [
  {
    id: "LABEL_TAXONOMY_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-LABEL",
    uiSurface: "STAT_LABEL_CONFIG",
    notes: "Four layers stay distinct; classification never contributes.",
  },
  {
    id: "FIELD_METADATA_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-FIELD",
    uiSurface: "FORM_STATISTIC_FIELD_CONFIG",
    notes: "Config/version/readback only; Form structure immutable; no result.",
  },
  {
    id: "TABLE_METADATA_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-TABLE",
    uiSurface: "FORM_STATISTIC_TABLE_CONFIG",
    notes: "Stable metricKey; block-local disable; no positional fallback.",
  },
  {
    id: "BASIC_SUMMARY_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-BASIC",
    uiSurface: "STAT_BASIC_CONFIG",
    notes: "Typed operation/scope/target/period/grouping; no run.",
  },
  {
    id: "FLOW_SCOPE_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-FLOW-SCOPE",
    uiSurface: "STAT_FLOW_SCOPE_CONFIG",
    notes: "Four FLOW selectors readable/versioned; runtime P9-blocked.",
  },
  {
    id: "ADVANCED_SUMMARY_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-ADVANCED",
    uiSurface: "STAT_ADVANCED_CONFIG",
    notes:
      "Draft/lock/version/gates/hierarchy/empty/budget; WorkSummaryToken stays a quota ledger.",
  },
  {
    id: "DIFF_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-DIFF",
    uiSurface: "STAT_DIFF_CONFIG",
    notes: "Compatible concept/period/direction/scope/missing; no delta.",
  },
  {
    id: "FLOW_CONTRIBUTION_CONFIG",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-CONTRIBUTION",
    uiSurface: "FLOW_STATISTIC_CONTRIBUTION_CONFIG",
    notes: "V_EXCLUDE/V_INCLUDE; EXCLUDE default; preserve P7; no result.",
  },
  {
    id: "FLOW_STATISTIC_PROFILE_BARRIER",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-PROFILE-BARRIER",
    uiSurface: "FLOW_STATISTIC_PROFILE_DISABLED",
    notes: "Non-empty fail-closed validation barrier only; no executor.",
  },
  {
    id: "CONFIG_OPERATIONS_READINESS",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-OPS",
    uiSurface: "STAT_CONFIG_OPERATIONS_READINESS",
    notes: "Indexes/no-dataset worker/diagnostics/quota/cleanup; no official rows.",
  },
  {
    id: "CONFIG_BUNDLE_READBACK",
    status: "SUPPORTED",
    targetPhase: "P8",
    testPrefix: "STAT-CONFIG-BUNDLE",
    uiSurface: "STAT_CONFIG_BUNDLE_READBACK",
    notes: "Exact pins/empty eligibility/P9-P10 fail closed.",
  },
];
const p10PublishedStatisticsReconciliationCapabilities = [
  {
    id: "SOURCE_TO_RESULT_RECONCILIATION",
    name: "Source-to-result statistics reconciliation",
    status: "SUPPORTED",
    targetPhase: "P10",
    testPrefix: "P10-RECONCILE",
    uiSurface: "STAT_RECONCILIATION",
  },
  {
    id: "EXPECTED_ACTUAL_DELTA",
    name: "Expected-versus-actual typed delta",
    status: "SUPPORTED",
    targetPhase: "P10",
    testPrefix: "P10-DELTA",
    uiSurface: "STAT_RECONCILIATION",
  },
  {
    id: "INDEPENDENT_REVIEW_SIGNOFF",
    name: "Independent reconciliation review sign-off",
    status: "SUPPORTED",
    targetPhase: "P10",
    testPrefix: "P10-REVIEW",
    uiSurface: "STAT_RECONCILIATION_REVIEW",
  },
  {
    id: "RECONCILIATION_EVIDENCE_EXPORT",
    name: "Deterministic reconciliation evidence export",
    status: "SUPPORTED",
    targetPhase: "P10",
    testPrefix: "P10-EVIDENCE",
    uiSurface: "STAT_RECONCILIATION_EVIDENCE",
  },
];
const candidateExpectedDomainIds = {
  ...currentExpectedDomainIds,
  dynamicFlowMappingCapabilities: candidateMappingCapabilities.map(
    (capability) => capability.id,
  ),
};

const p8CandidateExpectedDomainIds = {
  ...candidateExpectedDomainIds,
  statisticsConfigurationCapabilities:
    p8CandidateStatisticsConfigurationCapabilities.map((capability) => capability.id),
};
const p10PublishedExpectedDomainIds = {
  ...p8CandidateExpectedDomainIds,
  statisticsReconciliationCapabilities:
    p10PublishedStatisticsReconciliationCapabilities.map((capability) => capability.id),
};
const expectedDomainIdsForVersion = (catalogVersion) =>
  catalogVersion === p10PublishedCatalogVersion
    ? p10PublishedExpectedDomainIds
    : catalogVersion === p8CandidateCatalogVersion ||
        catalogVersion === p9PublishedCatalogVersion
      ? p8CandidateExpectedDomainIds
      : catalogVersion === candidateCatalogVersion
        ? candidateExpectedDomainIds
        : currentExpectedDomainIds;

const requiredUiStates = [
  "LOADING",
  "EMPTY",
  "ERROR",
  "FORBIDDEN",
  "READONLY",
  "LOCKED",
  "STALE_CONFLICT",
  "SUCCESS",
  "RETRYING",
  "UNSUPPORTED",
];
const requiredActors = [
  "OWNER",
  "ISSUER",
  "REPORTER",
  "REVIEWER",
  "COORDINATOR",
  "SYSTEM_ADMIN",
  "OUTSIDER",
];
const requiredEvidence = [
  "COMPONENT_TEST",
  "BROWSER_TEST",
  "ACCESSIBILITY_SMOKE",
  "RESPONSIVE_REVIEW",
  "SCREENSHOT",
  "TRACE",
];
const allowedStatuses = ["SUPPORTED", "PATCH_REQUIRED", "TARGET", "INTENTIONAL_BLOCK"];

const p8StatisticsConfigurationCapabilitySchema = {
  type: "object",
  additionalProperties: false,
  required: [
    "id",
    "status",
    "targetPhase",
    "testPrefix",
    "uiSurface",
    "notes",
  ],
  properties: {
    id: {
      type: "string",
      minLength: 1,
    },
    status: {
      type: "string",
      enum: ["SUPPORTED", "PATCH_REQUIRED", "TARGET", "INTENTIONAL_BLOCK"],
    },
    targetPhase: {
      type: ["string", "null"],
      pattern: "^P([0-9]|1[0-2])$",
    },
    testPrefix: {
      type: "string",
      minLength: 1,
    },
    uiSurface: {
      type: "string",
      minLength: 1,
    },
    notes: {
      type: "string",
    },
  },
};

const p8StatisticsConfigurationCapabilityArraySchema = {
  type: "array",
  items: {
    $ref: "#/$defs/statisticsConfigurationCapability",
  },
  minItems: 1,
};

const expectedDefaults = {
  publishedFormSchemaMutable: false,
  assignmentBindsFormVersionId: true,
  emptyFlowPolicy: "DENY",
  runtimeRoleSource: "SERVER_DERIVED",
  rawSourceReportAccess: "EXPLICIT_PERMISSION_ONLY",
  mappedTargetStatisticContribution: "EXCLUDE",
  flowStatisticProfile: "INTENTIONAL_BLOCK",
};

const validateSchema = (
  schema,
  catalogVersion,
  schemaFile,
  expectedDomainIds = expectedDomainIdsForVersion(catalogVersion),
) => {
  const errors = [];
  const majorVersion = catalogVersion.split(".")[0];
  expect(
    errors,
    schema?.$schema === "https://json-schema.org/draft/2020-12/schema",
    `${schemaFile}: schema draft must be 2020-12.`,
  );
  expect(
    errors,
    schema?.$id === `urn:tdtd:dynamic-form-flow:capability-catalog:v${majorVersion}`,
    `${schemaFile}: unexpected $id.`,
  );
  expect(errors, schema?.type === "object", `${schemaFile}: root type must be object.`);
  expect(
    errors,
    schema?.additionalProperties === false,
    `${schemaFile}: root additionalProperties must be false.`,
  );
  expect(
    errors,
    schema?.properties?.catalogVersion?.const === catalogVersion,
    `${schemaFile}: catalogVersion const must be ${catalogVersion}.`,
  );
  expect(
    errors,
    exactArray(schema?.$defs?.capability?.properties?.status?.enum, allowedStatuses),
    `${schemaFile}: capability statuses are out of sync.`,
  );
  for (const [key, value] of Object.entries(expectedDefaults)) {
    expect(
      errors,
      schema?.properties?.defaults?.properties?.[key]?.const === value,
      `${schemaFile}: defaults.${key} const is out of sync.`,
    );
  }
  const expectedDomainNames = Object.keys(expectedDomainIds);
  expect(
    errors,
    exactArray(schema?.properties?.domains?.required, expectedDomainNames),
    `${schemaFile}: domains.required must match the locked domain order and values.`,
  );
  expectExactKeys(
    errors,
    schema?.properties?.domains?.properties,
    expectedDomainNames,
    `${schemaFile} domain properties`,
  );
  for (const domainName of expectedDomainNames) {
    const expectedReference =
      domainName === "statisticsConfigurationCapabilities"
        ? "#/$defs/statisticsConfigurationCapabilityArray"
        : "#/$defs/capabilityArray";
    expect(
      errors,
      schema?.properties?.domains?.properties?.[domainName]?.$ref ===
        expectedReference,
      `${schemaFile}: domains.${domainName} must reference ${expectedReference}.`,
    );
  }
  if (
    catalogVersion === p8CandidateCatalogVersion ||
    catalogVersion === p9PublishedCatalogVersion ||
    catalogVersion === p10PublishedCatalogVersion
  ) {
    expect(
      errors,
      canonicalJson(schema?.$defs?.statisticsConfigurationCapability) ===
        canonicalJson(p8StatisticsConfigurationCapabilitySchema),
      `${schemaFile}: statisticsConfigurationCapability schema must match P8-D1 exactly.`,
    );
    expect(
      errors,
      canonicalJson(schema?.$defs?.statisticsConfigurationCapabilityArray) ===
        canonicalJson(p8StatisticsConfigurationCapabilityArraySchema),
      `${schemaFile}: statisticsConfigurationCapabilityArray schema must match P8-D1 exactly.`,
    );
  }
  if (errors.length > 0) throw new Error(errors.join("\n"));
};

const validateCatalog = (
  catalog,
  catalogVersion,
  catalogFile,
  schemaFile,
  expectedDomainIds = expectedDomainIdsForVersion(catalogVersion),
) => {
  const errors = [];
  expectExactKeys(
    errors,
    catalog,
    ["$schema", "catalogVersion", "approvedAt", "terminology", "defaults", "uiGate", "domains"],
    `${catalogFile} root`,
  );
  const expectedSchemaFile = catalogVersion === p8CandidateCatalogVersion
    ? p8CandidateSchemaFileName
    : catalogVersion === candidateCatalogVersion
      ? candidateSchemaFileName
      : schemaFile;
  expect(
    errors,
    catalog?.$schema === `./${expectedSchemaFile}`,
    `${catalogFile}: $schema must reference ${expectedSchemaFile}.`,
  );
  expect(errors, catalog?.catalogVersion === catalogVersion, `${catalogFile}: catalogVersion must be ${catalogVersion}.`);
  expect(errors, /^\d{4}-\d{2}-\d{2}$/.test(catalog?.approvedAt), `${catalogFile}: approvedAt must be YYYY-MM-DD.`);

  expectExactKeys(errors, catalog?.terminology, ["flowScopeName", "fullBpmnClaimAllowed"], `${catalogFile} terminology`);
  expect(
    errors,
    catalog?.terminology?.flowScopeName === "BPMN_LITE_12_ARCHETYPES",
    `${catalogFile}: flowScopeName must be BPMN_LITE_12_ARCHETYPES.`,
  );
  expect(
    errors,
    catalog?.terminology?.fullBpmnClaimAllowed === false,
    `${catalogFile}: full BPMN must not be claimed.`,
  );

  expectExactKeys(errors, catalog?.defaults, Object.keys(expectedDefaults), `${catalogFile} defaults`);
  for (const [key, value] of Object.entries(expectedDefaults)) {
    expect(errors, catalog?.defaults?.[key] === value, `${catalogFile}: defaults.${key} must be ${String(value)}.`);
  }

  expectExactKeys(
    errors,
    catalog?.uiGate,
    ["continuous", "requiredStates", "requiredActors", "requiredEvidence"],
    `${catalogFile} uiGate`,
  );
  expect(errors, catalog?.uiGate?.continuous === true, `${catalogFile}: uiGate.continuous must be true.`);
  expect(
    errors,
    exactArray(catalog?.uiGate?.requiredStates, requiredUiStates),
    `${catalogFile}: requiredStates must match the locked order and values.`,
  );
  expect(
    errors,
    exactArray(catalog?.uiGate?.requiredActors, requiredActors),
    `${catalogFile}: requiredActors must match the locked order and values.`,
  );
  expect(
    errors,
    exactArray(catalog?.uiGate?.requiredEvidence, requiredEvidence),
    `${catalogFile}: requiredEvidence must match the locked order and values.`,
  );

  expectExactKeys(errors, catalog?.domains, Object.keys(expectedDomainIds), `${catalogFile} domains`);
  const seenTestPrefixes = new Set();
  for (const [domainName, expectedIds] of Object.entries(expectedDomainIds)) {
    const items = catalog?.domains?.[domainName];
    expect(errors, Array.isArray(items), `${catalogFile}: ${domainName} must be an array.`);
    if (!Array.isArray(items)) continue;
    expect(
      errors,
      exactArray(items.map((item) => item?.id), expectedIds),
      `${catalogFile}: ${domainName} ids must be exactly ${expectedIds.join(", ")}.`,
    );
    for (const item of items) {
      const label = `${catalogFile}: ${domainName}/${String(item?.id)}`;
      const isP8ConfigurationCapability =
        domainName === "statisticsConfigurationCapabilities";
      const allowedKeys = isP8ConfigurationCapability
        ? ["id", "status", "targetPhase", "testPrefix", "uiSurface", "notes"]
        : ["id", "name", "status", "targetPhase", "testPrefix", "uiSurface"];
      if (!isP8ConfigurationCapability && item && Object.hasOwn(item, "notes")) {
        allowedKeys.push("notes");
      }
      expectExactKeys(errors, item, allowedKeys, label);
      expect(errors, typeof item?.id === "string" && item.id.length > 0, `${label} has a blank id.`);
      if (isP8ConfigurationCapability) {
        expect(errors, typeof item?.notes === "string", `${label} must have notes.`);
      } else {
        expect(errors, typeof item?.name === "string" && item.name.length > 0, `${label} has a blank name.`);
      }
      expect(errors, allowedStatuses.includes(item?.status), `${label} has invalid status ${String(item?.status)}.`);
      expect(
        errors,
        typeof item?.testPrefix === "string" && item.testPrefix.length > 0,
        `${label} has a blank testPrefix.`,
      );
      expect(errors, !seenTestPrefixes.has(item?.testPrefix), `${label} reuses testPrefix ${String(item?.testPrefix)}.`);
      seenTestPrefixes.add(item?.testPrefix);
      expect(
        errors,
        typeof item?.uiSurface === "string" && item.uiSurface.length > 0,
        `${label} has a blank uiSurface.`,
      );
      if (item?.status === "INTENTIONAL_BLOCK") {
        expect(errors, item.targetPhase === null, `${label} must have targetPhase null while intentionally blocked.`);
      } else {
        expect(errors, /^P(?:[0-9]|1[0-2])$/.test(item?.targetPhase), `${label} has invalid targetPhase.`);
      }
    }
  }

  const profile = catalog?.domains?.statisticsCapabilities?.find(
    (item) => item.id === "FLOW_STATISTIC_PROFILE",
  );
  expect(
    errors,
    profile?.status === "INTENTIONAL_BLOCK" && profile?.targetPhase === null,
    `${catalogFile}: FLOW_STATISTIC_PROFILE must remain intentionally blocked.`,
  );

  if (catalogVersion === candidateCatalogVersion) {
    expect(
      errors,
      canonicalJson(catalog?.domains?.dynamicFlowMappingCapabilities) ===
        canonicalJson(candidateMappingCapabilities),
      `${catalogFile}: dynamicFlowMappingCapabilities must match the frozen P7 capability entries exactly.`,
    );
  }
  if (
    catalogVersion === p8CandidateCatalogVersion ||
    catalogVersion === p9PublishedCatalogVersion ||
    catalogVersion === p10PublishedCatalogVersion
  ) {
    expect(
      errors,
      canonicalJson(catalog?.domains?.statisticsConfigurationCapabilities) ===
        canonicalJson(p8CandidateStatisticsConfigurationCapabilities),
      `${catalogFile}: statisticsConfigurationCapabilities must match the frozen P8 capability entries exactly.`,
    );
  }
  if (catalogVersion === p10PublishedCatalogVersion) {
    expect(
      errors,
      canonicalJson(catalog?.domains?.statisticsReconciliationCapabilities) ===
        canonicalJson(p10PublishedStatisticsReconciliationCapabilities),
      `${catalogFile}: statisticsReconciliationCapabilities must match the sealed P10 capability entries exactly.`,
    );
  }

  if (errors.length > 0) throw new Error(errors.join("\n"));
};

const validateLockShape = (lock) => {
  const errors = [];
  expectExactKeys(errors, lock, ["lockVersion", "publishedCatalogs"], "catalog lock");
  expect(errors, lock?.lockVersion === 1, "catalog lockVersion must be 1.");
  expect(errors, Array.isArray(lock?.publishedCatalogs) && lock.publishedCatalogs.length > 0, "publishedCatalogs must not be empty.");
  if (!Array.isArray(lock?.publishedCatalogs)) throw new Error(errors.join("\n"));

  const seenVersions = new Set();
  const seenCatalogFiles = new Set();
  const seenSchemaFiles = new Set();
  let previousVersion = null;
  for (const entry of lock.publishedCatalogs) {
    expectExactKeys(
      errors,
      entry,
      ["catalogVersion", "catalogFile", "schemaFile", "catalogSha256", "schemaSha256"],
      `catalog lock entry ${String(entry?.catalogVersion)}`,
    );
    expect(errors, /^\d+\.\d+$/.test(entry?.catalogVersion), `Invalid catalogVersion ${String(entry?.catalogVersion)}.`);
    expect(errors, !seenVersions.has(entry?.catalogVersion), `Duplicate catalogVersion ${String(entry?.catalogVersion)}.`);
    expect(errors, !seenCatalogFiles.has(entry?.catalogFile), `Catalog file ${String(entry?.catalogFile)} is reused.`);
    expect(errors, !seenSchemaFiles.has(entry?.schemaFile), `Schema file ${String(entry?.schemaFile)} is reused.`);
    expect(
      errors,
      /^DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V\d+(?:_\d+)*\.json$/.test(entry?.catalogFile),
      `Invalid versioned catalog file ${String(entry?.catalogFile)}.`,
    );
    expect(
      errors,
      /^DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V\d+(?:_\d+)*\.schema\.json$/.test(entry?.schemaFile),
      `Invalid versioned schema file ${String(entry?.schemaFile)}.`,
    );
    expect(errors, /^[a-f0-9]{64}$/.test(entry?.catalogSha256), `Invalid catalogSha256 for ${String(entry?.catalogVersion)}.`);
    expect(errors, /^[a-f0-9]{64}$/.test(entry?.schemaSha256), `Invalid schemaSha256 for ${String(entry?.catalogVersion)}.`);

    if (previousVersion !== null) {
      const previousParts = previousVersion.split(".").map(Number);
      const currentParts = entry.catalogVersion.split(".").map(Number);
      expect(
        errors,
        currentParts[0] > previousParts[0] ||
          (currentParts[0] === previousParts[0] && currentParts[1] > previousParts[1]),
        `Catalog versions must be strictly ascending; ${entry.catalogVersion} follows ${previousVersion}.`,
      );
    }
    previousVersion = entry.catalogVersion;
    seenVersions.add(entry.catalogVersion);
    seenCatalogFiles.add(entry.catalogFile);
    seenSchemaFiles.add(entry.schemaFile);
  }
  if (errors.length > 0) throw new Error(errors.join("\n"));
};

const resolveCurrentPointer = (pointer, lock, label) => {
  const errors = [];
  expectExactKeys(
    errors,
    pointer,
    ["pointerVersion", "catalogVersion", "catalogSha256", "schemaSha256"],
    label,
  );
  expect(errors, pointer?.pointerVersion === 1, `${label}: pointerVersion must be 1.`);
  expect(
    errors,
    approvedCurrentVersions.has(pointer?.catalogVersion),
    `${label}: catalogVersion must be one of ${[...approvedCurrentVersions].join(", ")}.`,
  );
  expect(
    errors,
    /^[a-f0-9]{64}$/.test(pointer?.catalogSha256),
    `${label}: catalogSha256 must be a lowercase SHA-256.`,
  );
  expect(
    errors,
    /^[a-f0-9]{64}$/.test(pointer?.schemaSha256),
    `${label}: schemaSha256 must be a lowercase SHA-256.`,
  );

  const entry = Array.isArray(lock?.publishedCatalogs)
    ? lock.publishedCatalogs.find(
        (candidate) => candidate.catalogVersion === pointer?.catalogVersion,
      )
    : undefined;
  expect(
    errors,
    entry !== undefined,
    `${label}: catalogVersion ${String(pointer?.catalogVersion)} is not published.`,
  );
  if (entry !== undefined) {
    expect(
      errors,
      entry.catalogSha256 === pointer.catalogSha256,
      `${label}: catalogSha256 does not match the immutable lock entry.`,
    );
    expect(
      errors,
      entry.schemaSha256 === pointer.schemaSha256,
      `${label}: schemaSha256 does not match the immutable lock entry.`,
    );
  }

  if (errors.length > 0) throw new Error(errors.join("\n"));
  return entry;
};

const assertAppendOnlyAgainstGitHistory = (currentLock) => {
  let revisions;
  try {
    revisions = execFileSync("git", ["log", "--format=%H", "--", lockGitPath], {
      cwd: backendRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    })
      .trim()
      .split(/\r?\n/)
      .filter(Boolean);
  } catch (error) {
    throw new Error(`Unable to verify append-only catalog history with Git: ${error.message}`);
  }

  const currentByVersion = new Map(
    currentLock.publishedCatalogs.map((entry) => [entry.catalogVersion, canonicalJson(entry)]),
  );
  const errors = [];
  for (const revision of revisions) {
    let historicalLock;
    try {
      const raw = execFileSync("git", ["show", `${revision}:${lockGitPath}`], {
        cwd: backendRoot,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "ignore"],
      });
      historicalLock = parseJsonText(raw, `${revision}:${lockGitPath}`);
    } catch (error) {
      errors.push(`Cannot read historical catalog lock at ${revision}: ${error.message}`);
      continue;
    }
    for (const historicalEntry of historicalLock?.publishedCatalogs ?? []) {
      const currentEntry = currentByVersion.get(historicalEntry.catalogVersion);
      if (currentEntry === undefined) {
        errors.push(`Published catalog ${historicalEntry.catalogVersion} from ${revision} was removed.`);
      } else if (currentEntry !== canonicalJson(historicalEntry)) {
        errors.push(`Published catalog ${historicalEntry.catalogVersion} from ${revision} was overwritten.`);
      }
    }
  }
  if (errors.length > 0) {
    throw new Error(
      `Published catalogs are append-only. Add a new versioned catalog and lock entry instead.\n${errors.join("\n")}`,
    );
  }
};

const loadAndValidateCatalogs = async () => {
  const lock = await readJson(lockPath);
  validateLockShape(lock);
  assertAppendOnlyAgainstGitHistory(lock);
  const currentPointer = await readJson(currentPath);
  const currentEntry = resolveCurrentPointer(
    currentPointer,
    lock,
    "catalog current pointer",
  );
  const rollbackEntry = resolveCurrentPointer(
    rollbackCatalogPointer,
    lock,
    "catalog v1.3 rollback oracle",
  );
  const p9RollbackEntry = resolveCurrentPointer(
    p9RollbackCatalogPointer,
    lock,
    "catalog P9 v1.5 rollback oracle",
  );
  const p10RollbackEntry = resolveCurrentPointer(
    p10RollbackCatalogPointer,
    lock,
    "catalog P10 v1.6 rollback oracle",
  );

  const files = await readdir(contractDir);
  const listedCatalogFiles = new Set(lock.publishedCatalogs.map((entry) => entry.catalogFile));
  const listedSchemaFiles = new Set(lock.publishedCatalogs.map((entry) => entry.schemaFile));
  const unlistedFiles = files.filter(
    (file) =>
      (/^DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V\d+(?:_\d+)*\.json$/.test(file) &&
        !listedCatalogFiles.has(file)) ||
      (/^DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V\d+(?:_\d+)*\.schema\.json$/.test(file) &&
        !listedSchemaFiles.has(file)),
  );
  if (unlistedFiles.length > 0) {
    throw new Error(`Versioned contract files are missing lock entries: ${unlistedFiles.join(", ")}.`);
  }

  const loaded = [];
  for (const entry of lock.publishedCatalogs) {
    const catalog = await readJson(path.join(contractDir, entry.catalogFile));
    const schema = await readJson(path.join(contractDir, entry.schemaFile));
    validateSchema(schema, entry.catalogVersion, entry.schemaFile);
    validateCatalog(catalog, entry.catalogVersion, entry.catalogFile, entry.schemaFile);

    const catalogSha256 = semanticSha256(catalog);
    const schemaSha256 = semanticSha256(schema);
    if (catalogSha256 !== entry.catalogSha256) {
      throw new Error(
        `${entry.catalogFile} semantic SHA-256 is ${catalogSha256}, expected locked ${entry.catalogSha256}. ` +
          "Published catalogs are immutable; add a new version instead.",
      );
    }
    if (schemaSha256 !== entry.schemaSha256) {
      throw new Error(
        `${entry.schemaFile} semantic SHA-256 is ${schemaSha256}, expected locked ${entry.schemaSha256}. ` +
          "Published schemas are immutable; add a new version instead.",
      );
    }
    loaded.push({ entry, catalog, schema });
  }
  const current = loaded.find(({ entry }) => entry === currentEntry);
  const rollback = loaded.find(({ entry }) => entry === rollbackEntry);
  const p9Rollback = loaded.find(({ entry }) => entry === p9RollbackEntry);
  const p10Rollback = loaded.find(({ entry }) => entry === p10RollbackEntry);
  if (
    current === undefined || rollback === undefined ||
    p9Rollback === undefined || p10Rollback === undefined
  ) {
    throw new Error(
      "Current/rollback catalog resolution drifted from the validated immutable lock.",
    );
  }

  const publishedP9 = loaded.find(
    ({ entry }) => entry.catalogVersion === p9PublishedCatalogVersion,
  );
  if (publishedP9 !== undefined) {
    const [publishedCatalogRaw, publishedSchemaRaw] = await Promise.all([
      readFile(path.join(contractDir, publishedP9.entry.catalogFile)),
      readFile(path.join(contractDir, publishedP9.entry.schemaFile)),
    ]);
    if (
      rawSha256(publishedCatalogRaw) !== p9PublishedCatalogRawSha256 ||
      rawSha256(publishedSchemaRaw) !== p9PublishedSchemaRawSha256
    ) {
      throw new Error(
        "Published v1.6 files are not byte-identical to the sealed P9-11 candidate.",
      );
    }

    const expectedCatalog = cloneJson(p9Rollback.catalog);
    expectedCatalog.$schema = `./${p9PublishedCatalogSchemaFileName}`;
    expectedCatalog.catalogVersion = p9PublishedCatalogVersion;
    expectedCatalog.approvedAt = p9PublishedApprovedAt;
    for (const capability of expectedCatalog.domains.statisticsCapabilities) {
      if (p9PromotedCapabilityIds.has(capability.id)) capability.status = "SUPPORTED";
    }
    if (canonicalJson(publishedP9.catalog) !== canonicalJson(expectedCatalog)) {
      throw new Error(
        "Published v1.6 catalog is not the exact five-capability successor of immutable v1.5.",
      );
    }

    const expectedSchema = cloneJson(p9Rollback.schema);
    expectedSchema.title = "Dynamic Form Flow Capability Catalog v1.6";
    expectedSchema.properties.catalogVersion.const = p9PublishedCatalogVersion;
    if (canonicalJson(publishedP9.schema) !== canonicalJson(expectedSchema)) {
      throw new Error(
        "Published v1.6 schema changed outside title and catalogVersion const.",
      );
    }
  }
  const publishedP10 = loaded.find(
    ({ entry }) => entry.catalogVersion === p10PublishedCatalogVersion,
  );
  if (publishedP10 !== undefined) {
    const [publishedCatalogRaw, publishedSchemaRaw] = await Promise.all([
      readFile(path.join(contractDir, publishedP10.entry.catalogFile)),
      readFile(path.join(contractDir, publishedP10.entry.schemaFile)),
    ]);
    if (
      rawSha256(publishedCatalogRaw) !== p10PublishedCatalogRawSha256 ||
      rawSha256(publishedSchemaRaw) !== p10PublishedSchemaRawSha256
    ) {
      throw new Error(
        "Published v1.7 files are not byte-identical to the sealed P10-11 candidate.",
      );
    }
    if (
      publishedP10.entry.catalogSha256 !== p10PublishedCatalogSemanticSha256 ||
      publishedP10.entry.schemaSha256 !== p10PublishedSchemaSemanticSha256
    ) {
      throw new Error(
        "Published v1.7 LOCK entry does not match the sealed P10-11 semantic pins.",
      );
    }

    const expectedCatalog = cloneJson(p10Rollback.catalog);
    expectedCatalog.$schema = `./${p10PublishedCatalogSchemaFileName}`;
    expectedCatalog.catalogVersion = p10PublishedCatalogVersion;
    expectedCatalog.approvedAt = p10PublishedApprovedAt;
    expectedCatalog.domains.statisticsReconciliationCapabilities = cloneJson(
      p10PublishedStatisticsReconciliationCapabilities,
    );
    if (canonicalJson(publishedP10.catalog) !== canonicalJson(expectedCatalog)) {
      throw new Error(
        "Published v1.7 catalog is not the exact four-capability successor of immutable v1.6.",
      );
    }

    const expectedSchema = cloneJson(p10Rollback.schema);
    expectedSchema.title = "Dynamic Form Flow Capability Catalog v1.7";
    expectedSchema.properties.catalogVersion.const = p10PublishedCatalogVersion;
    expectedSchema.properties.domains.required = [
      ...expectedSchema.properties.domains.required,
      "statisticsReconciliationCapabilities",
    ];
    expectedSchema.properties.domains.properties.statisticsReconciliationCapabilities = {
      $ref: "#/$defs/capabilityArray",
    };
    if (canonicalJson(publishedP10.schema) !== canonicalJson(expectedSchema)) {
      throw new Error(
        "Published v1.7 schema changed outside title, catalogVersion const, and the reconciliation capability domain.",
      );
    }
  }
  return { catalogs: loaded, current, rollback, p9Rollback, p10Rollback };
};

const csString = (value) => JSON.stringify(String(value));
const csNullableString = (value) => (value === null ? "null" : csString(value));
const csBool = (value) => (value ? "true" : "false");

const renderCsharpStringList = (propertyName, values) => `
    public static IReadOnlyList<string> ${propertyName} { get; } = Array.AsReadOnly(new[]
    {
${values.map((value) => `        ${csString(value)},`).join("\n")}
    });`;

const renderCsharpCapabilityList = (propertyName, items) =>
  items.length === 0
    ? `
    public static IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> ${propertyName} { get; } = Array.Empty<DynamicFormFlowCapabilityItemMetadata>();`
    : `
    public static IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> ${propertyName} { get; } = Array.AsReadOnly(new[]
    {
${items
  .map(
    (item) =>
      "        new DynamicFormFlowCapabilityItemMetadata(" +
      [
        csString(item.id),
        csString(item.name),
        csString(item.status),
        csNullableString(item.targetPhase),
        csString(item.testPrefix),
        csString(item.uiSurface),
        csNullableString(item.notes ?? null),
      ].join(", ") +
      "),",
  )
  .join("\n")}
    });`;

const renderCsharpStatisticsConfigurationCapabilityList = (items) =>
  items.length === 0
    ? `
    public static IReadOnlyList<StatisticsConfigurationCapabilityMetadata> StatisticsConfigurationCapabilities { get; } = Array.Empty<StatisticsConfigurationCapabilityMetadata>();`
    : `
    public static IReadOnlyList<StatisticsConfigurationCapabilityMetadata> StatisticsConfigurationCapabilities { get; } = Array.AsReadOnly(new[]
    {
${items
  .map(
    (item) =>
      `        new StatisticsConfigurationCapabilityMetadata(${[
        csString(item.id),
        csString(item.status),
        csNullableString(item.targetPhase),
        csString(item.testPrefix),
        csString(item.uiSurface),
        csString(item.notes),
      ].join(", ")}),`,
  )
  .join("\n")}
    });`;

const renderCsharp = ({ entry, catalog }) => `// <auto-generated />
// Source: Contracts/DynamicFormFlow/${entry.catalogFile}
// Catalog SHA-256: semantic canonical JSON (recursively sorted object keys, preserved array order, UTF-8, no whitespace).
#nullable enable

using System;
using System.Collections.Generic;

namespace tdtd_be.Common.Capabilities;

public sealed record DynamicFormFlowCapabilityItemMetadata(
    string Id,
    string Name,
    string Status,
    string? TargetPhase,
    string TestPrefix,
    string UiSurface,
    string? Notes);

public sealed record DynamicFormFlowCapabilityDefaultsMetadata(
    bool PublishedFormSchemaMutable,
    bool AssignmentBindsFormVersionId,
    string EmptyFlowPolicy,
    string RuntimeRoleSource,
    string RawSourceReportAccess,
    string MappedTargetStatisticContribution,
    string FlowStatisticProfile);

public sealed record StatisticsConfigurationCapabilityMetadata(
    string Id,
    string Status,
    string? TargetPhase,
    string TestPrefix,
    string UiSurface,
    string Notes);

public static class DynamicFormFlowCapabilityCatalogMetadata
{
    public const string CatalogVersion = ${csString(catalog.catalogVersion)};
    public const string CatalogSha256 = ${csString(entry.catalogSha256)};
    public const string SchemaSha256 = ${csString(entry.schemaSha256)};
    public const string ApprovedAt = ${csString(catalog.approvedAt)};
    public const string FlowScopeName = ${csString(catalog.terminology.flowScopeName)};
    public const bool FullBpmnClaimAllowed = ${csBool(catalog.terminology.fullBpmnClaimAllowed)};
    public const bool UiGateContinuous = ${csBool(catalog.uiGate.continuous)};

    public static DynamicFormFlowCapabilityDefaultsMetadata Defaults { get; } = new(
        ${csBool(catalog.defaults.publishedFormSchemaMutable)},
        ${csBool(catalog.defaults.assignmentBindsFormVersionId)},
        ${csString(catalog.defaults.emptyFlowPolicy)},
        ${csString(catalog.defaults.runtimeRoleSource)},
        ${csString(catalog.defaults.rawSourceReportAccess)},
        ${csString(catalog.defaults.mappedTargetStatisticContribution)},
        ${csString(catalog.defaults.flowStatisticProfile)});
${renderCsharpStringList("RequiredUiStates", catalog.uiGate.requiredStates)}
${renderCsharpStringList("RequiredActors", catalog.uiGate.requiredActors)}
${renderCsharpStringList("RequiredEvidence", catalog.uiGate.requiredEvidence)}
${renderCsharpCapabilityList("DynamicFormFieldTypes", catalog.domains.dynamicFormFieldTypes)}
${renderCsharpCapabilityList("DynamicFormValueSources", catalog.domains.dynamicFormValueSources)}
${renderCsharpCapabilityList("DynamicFormTableModes", catalog.domains.dynamicFormTableModes)}
${renderCsharpCapabilityList("DynamicFlowArchetypes", catalog.domains.dynamicFlowArchetypes)}
${renderCsharpCapabilityList("StatisticsCapabilities", catalog.domains.statisticsCapabilities)}${renderCsharpCapabilityList(
  "DynamicFlowMappingCapabilities",
  catalog.domains.dynamicFlowMappingCapabilities ?? [],
)}${renderCsharpStatisticsConfigurationCapabilityList(
  catalog.domains.statisticsConfigurationCapabilities ?? [],
)}${renderCsharpCapabilityList(
  "StatisticsReconciliationCapabilities",
  catalog.domains.statisticsReconciliationCapabilities ?? [],
)}
}
`;

const renderTypescript = ({ entry, catalog }) => {
  const metadata = {
    catalogVersion: catalog.catalogVersion,
    catalogSha256: entry.catalogSha256,
    schemaSha256: entry.schemaSha256,
    approvedAt: catalog.approvedAt,
    terminology: catalog.terminology,
    defaults: catalog.defaults,
    uiGate: catalog.uiGate,
    domains: catalog.domains,
  };
  return `// <auto-generated />
// Source: tdtd-be/Contracts/DynamicFormFlow/${entry.catalogFile}
// Catalog SHA-256: semantic canonical JSON (recursively sorted object keys, preserved array order, UTF-8, no whitespace).

export const DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_VERSION = ${JSON.stringify(catalog.catalogVersion)} as const;
export const DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_SHA256 = ${JSON.stringify(entry.catalogSha256)} as const;
export const DYNAMIC_FORM_FLOW_CAPABILITY_SCHEMA_SHA256 = ${JSON.stringify(entry.schemaSha256)} as const;

export const dynamicFormFlowCapabilityCatalogMetadata = ${JSON.stringify(metadata, null, 2)} as const;

export type DynamicFormFlowCapabilityCatalogMetadata =
  typeof dynamicFormFlowCapabilityCatalogMetadata;
export type DynamicFormFlowCapabilityDomainName =
  keyof typeof dynamicFormFlowCapabilityCatalogMetadata.domains;
export type DynamicFormFlowCapabilityId = {
  [Domain in DynamicFormFlowCapabilityDomainName]:
    (typeof dynamicFormFlowCapabilityCatalogMetadata.domains)[Domain][number]["id"];
}[DynamicFormFlowCapabilityDomainName];
export type DynamicFormFlowCapabilityStatus =
  (typeof dynamicFormFlowCapabilityCatalogMetadata.domains)[DynamicFormFlowCapabilityDomainName][number]["status"];
`;
};

const normalizeGeneratedText = (value) => value.replace(/^\uFEFF/, "").replace(/\r\n/g, "\n");

const checkGeneratedFile = async (filePath, expected) => {
  let actual;
  try {
    actual = await readFile(filePath, "utf8");
  } catch (error) {
    if (error.code === "ENOENT") return "missing";
    throw error;
  }
  return normalizeGeneratedText(actual) === expected ? null : "stale";
};

const writeGeneratedFile = async (filePath, content) => {
  await mkdir(path.dirname(filePath), { recursive: true });
  await writeFile(filePath, content, "utf8");
};

const displayPath = (filePath) => path.relative(workspaceRoot, filePath).replaceAll("\\", "/");

const resolveCandidateContext = (candidateDirArgument) => {
  const candidateDir = path.resolve(workspaceRoot, candidateDirArgument);
  const relativePath = path.relative(workspaceRoot, candidateDir);
  const normalizedPath = relativePath.replaceAll("\\", "/");
  if (
    normalizedPath.length === 0 ||
    path.isAbsolute(relativePath) ||
    normalizedPath === ".." ||
    normalizedPath.startsWith("../")
  ) {
    throw new Error(
      `Candidate directory must stay inside ${displayPath(workspaceRoot) || "the workspace root"}.`,
    );
  }

  const parts = normalizedPath.split("/");
  if (
    parts.length !== 4 ||
    parts[0] !== ".p7-artifacts" ||
    parts[1] !== "catalog-candidates"
  ) {
    throw new Error(
      "Candidate directory must be exactly " +
        ".p7-artifacts/catalog-candidates/<chainId>/<promptId>.",
    );
  }

  const chainId = parts[2];
  const promptId = parts[3];
  const chainMatch = /^p7_chain_(\d{14})_([a-f0-9]{4})$/.exec(chainId);
  if (chainMatch === null) {
    throw new Error(`Invalid P7 chain ID in candidate directory: ${chainId}.`);
  }
  const promptMatch = /^P7-(\d{2})$/.exec(promptId);
  const promptNumber = promptMatch === null ? 0 : Number(promptMatch[1]);
  if (promptNumber < 1 || promptNumber > 11) {
    throw new Error(
      `Candidate prompt ID must be P7-01 through P7-11; received ${promptId}.`,
    );
  }

  const compactTimestamp = chainMatch[1];
  const createdAtUtc =
    `${compactTimestamp.slice(0, 4)}-${compactTimestamp.slice(4, 6)}-` +
    `${compactTimestamp.slice(6, 8)}T${compactTimestamp.slice(8, 10)}:` +
    `${compactTimestamp.slice(10, 12)}:${compactTimestamp.slice(12, 14)}Z`;
  const parsedTimestamp = new Date(createdAtUtc);
  if (
    Number.isNaN(parsedTimestamp.getTime()) ||
    parsedTimestamp.toISOString().replace(".000Z", "Z") !== createdAtUtc
  ) {
    throw new Error(`P7 chain ID contains an invalid UTC timestamp: ${chainId}.`);
  }

  const catalogPath = path.join(candidateDir, candidateCatalogFileName);
  const schemaPath = path.join(candidateDir, candidateSchemaFileName);
  const candidateLockPath = path.join(candidateDir, candidateLockFileName);
  return {
    candidateDir,
    normalizedPath,
    chainId,
    promptId,
    createdAtUtc,
    approvedAt: createdAtUtc.slice(0, 10),
    catalogPath,
    schemaPath,
    candidateLockPath,
    catalogRelativePath: `${normalizedPath}/${candidateCatalogFileName}`,
    schemaRelativePath: `${normalizedPath}/${candidateSchemaFileName}`,
    lockRelativePath: `${normalizedPath}/${candidateLockFileName}`,
  };
};

const buildCandidateCatalog = (sourceCatalog, context) => {
  const candidate = cloneJson(sourceCatalog);
  candidate.$schema = `./${candidateSchemaFileName}`;
  candidate.catalogVersion = candidateCatalogVersion;
  candidate.approvedAt = context.approvedAt;
  candidate.domains.dynamicFlowMappingCapabilities = cloneJson(
    candidateMappingCapabilities,
  );
  return candidate;
};

const buildCandidateSchema = (sourceSchema) => {
  const candidate = cloneJson(sourceSchema);
  candidate.title = "TDTD Dynamic Form and Flow capability catalog v1.4";
  candidate.properties.catalogVersion.const = candidateCatalogVersion;
  candidate.properties.domains.required = [
    ...candidate.properties.domains.required,
    "dynamicFlowMappingCapabilities",
  ];
  candidate.properties.domains.properties.dynamicFlowMappingCapabilities = {
    $ref: "#/$defs/capabilityArray",
  };
  return candidate;
};

const buildCandidateBundle = async (active, context) => {
  if (
    active.entry.catalogVersion !== "1.3" ||
    active.entry.catalogFile !==
      "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_3.json" ||
    active.entry.schemaFile !==
      "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_3.schema.json"
  ) {
    throw new Error(
      "P7 v1.4 candidate generation requires exact CURRENT v1.3 source files.",
    );
  }

  const sourceCatalogPath = path.join(contractDir, active.entry.catalogFile);
  const sourceCatalogRaw = await readFile(sourceCatalogPath);
  const sourceCatalogActualRawSha256 = rawSha256(sourceCatalogRaw);
  if (sourceCatalogActualRawSha256 !== candidateSourceCatalogRawSha256) {
    throw new Error(
      `${displayPath(sourceCatalogPath)} raw SHA-256 is ` +
        `${sourceCatalogActualRawSha256}, expected exact P7 source ` +
        `${candidateSourceCatalogRawSha256}.`,
    );
  }

  const candidateCatalog = buildCandidateCatalog(active.catalog, context);
  const candidateSchema = buildCandidateSchema(active.schema);
  validateSchema(
    candidateSchema,
    candidateCatalogVersion,
    candidateSchemaFileName,
    candidateExpectedDomainIds,
  );
  validateCatalog(
    candidateCatalog,
    candidateCatalogVersion,
    candidateCatalogFileName,
    candidateSchemaFileName,
    candidateExpectedDomainIds,
  );

  const catalogContent = Buffer.from(renderJsonFile(candidateCatalog), "utf8");
  const schemaContent = Buffer.from(renderJsonFile(candidateSchema), "utf8");
  const catalogRawSha256 = rawSha256(catalogContent);
  const catalogSemanticSha256 = semanticSha256(candidateCatalog);
  const schemaRawSha256 = rawSha256(schemaContent);
  const schemaSemanticSha256 = semanticSha256(candidateSchema);
  const generatorSha256 = rawSha256(await readFile(generatorFilePath));
  const candidateLock = {
    schemaVersion: 1,
    packId: candidatePackId,
    chainId: context.chainId,
    promptId: context.promptId,
    catalogVersion: candidateCatalogVersion,
    catalogPath: context.catalogRelativePath,
    catalogRawSha256,
    catalogSemanticSha256,
    schemaPath: context.schemaRelativePath,
    schemaRawSha256,
    schemaSemanticSha256,
    generatorPath:
      "tdtd-be/tools/generate-dynamic-form-flow-capability-catalog.mjs",
    generatorSha256,
    sourceCatalogVersion: "1.3",
    sourceCatalogRawSha256: candidateSourceCatalogRawSha256,
    createdAtUtc: context.createdAtUtc,
  };
  const lockContent = Buffer.from(renderJsonFile(candidateLock), "utf8");
  const lockRawSha256 = rawSha256(lockContent);

  return {
    context,
    candidateCatalog,
    candidateSchema,
    candidateLock,
    catalogRawSha256,
    catalogSemanticSha256,
    schemaRawSha256,
    schemaSemanticSha256,
    lockRawSha256,
    generatorSha256,
    files: [
      {
        filePath: context.catalogPath,
        relativePath: context.catalogRelativePath,
        content: catalogContent,
      },
      {
        filePath: context.schemaPath,
        relativePath: context.schemaRelativePath,
        content: schemaContent,
      },
      {
        filePath: context.candidateLockPath,
        relativePath: context.lockRelativePath,
        content: lockContent,
      },
    ],
  };
};

const resolveP8CandidateContext = (candidateDirArgument) => {
  const candidateDir = path.resolve(workspaceRoot, candidateDirArgument);
  const relativePath = path.relative(workspaceRoot, candidateDir);
  const normalizedPath = relativePath.replaceAll("\\", "/");
  if (
    normalizedPath.length === 0 ||
    path.isAbsolute(relativePath) ||
    normalizedPath === ".." ||
    normalizedPath.startsWith("../")
  ) {
    throw new Error(
      `Candidate directory must stay inside ${displayPath(workspaceRoot) || "the workspace root"}.`,
    );
  }

  const parts = normalizedPath.split("/");
  if (
    parts.length !== 4 ||
    parts[0] !== ".p8-artifacts" ||
    parts[1] !== "catalog-candidates"
  ) {
    throw new Error(
      "P8 candidate directory must be exactly " +
        ".p8-artifacts/catalog-candidates/<chainId>/<promptId>.",
    );
  }

  const chainId = parts[2];
  const promptId = parts[3];
  const chainMatch = /^p8_chain_(\d{14})_([a-f0-9]{4})$/.exec(chainId);
  if (chainMatch === null) {
    throw new Error(`Invalid P8 chain ID in candidate directory: ${chainId}.`);
  }
  const promptMatch = /^P8-(\d{2})$/.exec(promptId);
  const promptNumber = promptMatch === null ? 0 : Number(promptMatch[1]);
  if (promptNumber < 1 || promptNumber > 11) {
    throw new Error(
      `Candidate prompt ID must be P8-01 through P8-11; received ${promptId}.`,
    );
  }

  const compactTimestamp = chainMatch[1];
  const createdAtUtc =
    `${compactTimestamp.slice(0, 4)}-${compactTimestamp.slice(4, 6)}-` +
    `${compactTimestamp.slice(6, 8)}T${compactTimestamp.slice(8, 10)}:` +
    `${compactTimestamp.slice(10, 12)}:${compactTimestamp.slice(12, 14)}Z`;
  const parsedTimestamp = new Date(createdAtUtc);
  if (
    Number.isNaN(parsedTimestamp.getTime()) ||
    parsedTimestamp.toISOString().replace(".000Z", "Z") !== createdAtUtc
  ) {
    throw new Error(`P8 chain ID contains an invalid UTC timestamp: ${chainId}.`);
  }

  const catalogPath = path.join(candidateDir, p8CandidateCatalogFileName);
  const schemaPath = path.join(candidateDir, p8CandidateSchemaFileName);
  const candidateLockPath = path.join(candidateDir, candidateLockFileName);
  return {
    candidateDir,
    normalizedPath,
    chainId,
    promptId,
    createdAtUtc,
    approvedAt: createdAtUtc.slice(0, 10),
    catalogPath,
    schemaPath,
    candidateLockPath,
    catalogRelativePath: `${normalizedPath}/${p8CandidateCatalogFileName}`,
    schemaRelativePath: `${normalizedPath}/${p8CandidateSchemaFileName}`,
    lockRelativePath: `${normalizedPath}/${candidateLockFileName}`,
  };
};

const buildP8CandidateCatalog = (sourceCatalog, context) => {
  const candidate = cloneJson(sourceCatalog);
  candidate.$schema = `./${p8CandidateSchemaFileName}`;
  candidate.catalogVersion = p8CandidateCatalogVersion;
  candidate.approvedAt = context.approvedAt;
  candidate.domains.statisticsConfigurationCapabilities = cloneJson(
    p8CandidateStatisticsConfigurationCapabilities,
  );
  return candidate;
};

const buildP8CandidateSchema = (sourceSchema) => {
  const candidate = cloneJson(sourceSchema);
  candidate.title = "TDTD Dynamic Form and Flow capability catalog v1.5";
  candidate.properties.catalogVersion.const = p8CandidateCatalogVersion;
  candidate.properties.domains.required = [
    ...candidate.properties.domains.required,
    "statisticsConfigurationCapabilities",
  ];
  candidate.properties.domains.properties.statisticsConfigurationCapabilities = {
    $ref: "#/$defs/statisticsConfigurationCapabilityArray",
  };
  candidate.$defs.statisticsConfigurationCapability = cloneJson(
    p8StatisticsConfigurationCapabilitySchema,
  );
  candidate.$defs.statisticsConfigurationCapabilityArray = cloneJson(
    p8StatisticsConfigurationCapabilityArraySchema,
  );
  return candidate;
};

const assertP8InheritedDomains = (sourceCatalog, candidateCatalog) => {
  const sourceDomainNames = Object.keys(sourceCatalog.domains);
  for (const domainName of sourceDomainNames) {
    if (
      canonicalJson(candidateCatalog.domains[domainName]) !==
      canonicalJson(sourceCatalog.domains[domainName])
    ) {
      throw new Error(`P8 candidate inherited domain ${domainName} changed.`);
    }
  }
  if (
    semanticSha256(candidateCatalog.domains.statisticsCapabilities) !==
    p8InheritedStatisticsSemanticSha256
  ) {
    throw new Error(
      "P8 candidate inherited statisticsCapabilities semantic pin changed.",
    );
  }
  if (
    semanticSha256(candidateCatalog.domains.dynamicFlowMappingCapabilities) !==
    p8InheritedMappingSemanticSha256
  ) {
    throw new Error(
      "P8 candidate inherited dynamicFlowMappingCapabilities semantic pin changed.",
    );
  }
};

const buildP8CandidateBundle = async (active, context) => {
  if (
    active.entry.catalogVersion !== "1.4" ||
    active.entry.catalogFile !==
      "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.json" ||
    active.entry.schemaFile !==
      "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.schema.json"
  ) {
    throw new Error(
      "P8 v1.5 candidate generation requires exact CURRENT v1.4 source files.",
    );
  }

  const sourceCatalogPath = path.join(contractDir, active.entry.catalogFile);
  const sourceSchemaPath = path.join(contractDir, active.entry.schemaFile);
  const sourceCatalogRaw = await readFile(sourceCatalogPath);
  const sourceSchemaRaw = await readFile(sourceSchemaPath);
  const sourceCatalogActualRawSha256 = rawSha256(sourceCatalogRaw);
  const sourceSchemaActualRawSha256 = rawSha256(sourceSchemaRaw);
  if (sourceCatalogActualRawSha256 !== p8CandidateSourceCatalogRawSha256) {
    throw new Error(
      `${displayPath(sourceCatalogPath)} raw SHA-256 is ` +
        `${sourceCatalogActualRawSha256}, expected exact P8 source ` +
        `${p8CandidateSourceCatalogRawSha256}.`,
    );
  }
  if (sourceSchemaActualRawSha256 !== p8CandidateSourceSchemaRawSha256) {
    throw new Error(
      `${displayPath(sourceSchemaPath)} raw SHA-256 is ` +
        `${sourceSchemaActualRawSha256}, expected exact P8 source ` +
        `${p8CandidateSourceSchemaRawSha256}.`,
    );
  }
  if (
    semanticSha256(active.catalog) !== p8CandidateSourceCatalogSemanticSha256 ||
    semanticSha256(active.schema) !== p8CandidateSourceSchemaSemanticSha256
  ) {
    throw new Error(
      "P8 source v1.4 semantic catalog/schema pins do not match P8-D1.",
    );
  }

  const candidateCatalog = buildP8CandidateCatalog(active.catalog, context);
  const candidateSchema = buildP8CandidateSchema(active.schema);
  assertP8InheritedDomains(active.catalog, candidateCatalog);
  validateSchema(
    candidateSchema,
    p8CandidateCatalogVersion,
    p8CandidateSchemaFileName,
    p8CandidateExpectedDomainIds,
  );
  validateCatalog(
    candidateCatalog,
    p8CandidateCatalogVersion,
    p8CandidateCatalogFileName,
    p8CandidateSchemaFileName,
    p8CandidateExpectedDomainIds,
  );

  const catalogContent = Buffer.from(renderJsonFile(candidateCatalog), "utf8");
  const schemaContent = Buffer.from(renderJsonFile(candidateSchema), "utf8");
  const catalogRawSha256 = rawSha256(catalogContent);
  const catalogSemanticSha256 = semanticSha256(candidateCatalog);
  const schemaRawSha256 = rawSha256(schemaContent);
  const schemaSemanticSha256 = semanticSha256(candidateSchema);
  const generatorSha256 = rawSha256(await readFile(generatorFilePath));
  const candidateLock = {
    schemaVersion: 1,
    packId: p8CandidatePackId,
    chainId: context.chainId,
    promptId: context.promptId,
    catalogVersion: p8CandidateCatalogVersion,
    catalogPath: context.catalogRelativePath,
    catalogRawSha256,
    catalogSemanticSha256,
    schemaPath: context.schemaRelativePath,
    schemaRawSha256,
    schemaSemanticSha256,
    generatorPath:
      "tdtd-be/tools/generate-dynamic-form-flow-capability-catalog.mjs",
    generatorSha256,
    sourceCatalogVersion: "1.4",
    sourceCatalogRawSha256: p8CandidateSourceCatalogRawSha256,
    sourceCatalogSemanticSha256: p8CandidateSourceCatalogSemanticSha256,
    sourceSchemaRawSha256: p8CandidateSourceSchemaRawSha256,
    sourceSchemaSemanticSha256: p8CandidateSourceSchemaSemanticSha256,
    inheritedStatisticsSemanticSha256: p8InheritedStatisticsSemanticSha256,
    inheritedMappingSemanticSha256: p8InheritedMappingSemanticSha256,
    createdAtUtc: context.createdAtUtc,
  };
  const lockContent = Buffer.from(renderJsonFile(candidateLock), "utf8");
  const lockRawSha256 = rawSha256(lockContent);

  return {
    context,
    candidateCatalog,
    candidateSchema,
    candidateLock,
    catalogRawSha256,
    catalogSemanticSha256,
    schemaRawSha256,
    schemaSemanticSha256,
    lockRawSha256,
    generatorSha256,
    sourceCatalogRawSha256: p8CandidateSourceCatalogRawSha256,
    files: [
      { filePath: context.catalogPath, relativePath: context.catalogRelativePath, content: catalogContent },
      { filePath: context.schemaPath, relativePath: context.schemaRelativePath, content: schemaContent },
      { filePath: context.candidateLockPath, relativePath: context.lockRelativePath, content: lockContent },
    ],
  };
};

const readExistingCandidateFile = async (file) => {
  try {
    return await readFile(file.filePath);
  } catch (error) {
    if (error.code === "ENOENT") return null;
    throw error;
  }
};

const assertCandidateBytes = (file, actual) => {
  if (actual.equals(file.content)) return;
  throw new Error(
    `Append-only candidate file differs and will not be overwritten: ` +
      `${file.relativePath} (actual ${rawSha256(actual)}, expected ` +
      `${rawSha256(file.content)}).`,
  );
};

const ensureCandidateDirectoryContents = async (bundle, allowMissing) => {
  let names;
  try {
    names = await readdir(bundle.context.candidateDir);
  } catch (error) {
    if (error.code === "ENOENT" && allowMissing) return;
    if (error.code === "ENOENT") {
      throw new Error(
        `Candidate directory is missing: ${bundle.context.normalizedPath}.`,
      );
    }
    throw error;
  }

  const isP8Candidate = bundle.context.promptId.startsWith("P8-");
  const expectedNames = new Set([
    isP8Candidate ? p8CandidateCatalogFileName : candidateCatalogFileName,
    isP8Candidate ? p8CandidateSchemaFileName : candidateSchemaFileName,
    candidateLockFileName,
  ]);
  const unexpected = names.filter((name) => !expectedNames.has(name));
  if (unexpected.length > 0) {
    throw new Error(
      `Candidate directory contains unexpected files: ${unexpected.join(", ")}.`,
    );
  }
};

const generateCandidateBundle = async (bundle) => {
  await ensureCandidateDirectoryContents(bundle, true);
  const existingStates = [];
  for (const file of bundle.files) {
    const actual = await readExistingCandidateFile(file);
    if (actual !== null) assertCandidateBytes(file, actual);
    existingStates.push(actual !== null);
  }

  await mkdir(bundle.context.candidateDir, { recursive: true });
  for (let index = 0; index < bundle.files.length; index += 1) {
    if (existingStates[index]) continue;
    const file = bundle.files[index];
    try {
      await writeFile(file.filePath, file.content, { flag: "wx" });
    } catch (error) {
      if (error.code !== "EEXIST") throw error;
      const actual = await readFile(file.filePath);
      assertCandidateBytes(file, actual);
    }
  }
};

const verifyCandidateBundle = async (bundle) => {
  await ensureCandidateDirectoryContents(bundle, false);
  for (const file of bundle.files) {
    const actual = await readExistingCandidateFile(file);
    if (actual === null) {
      throw new Error(`Candidate file is missing: ${file.relativePath}.`);
    }
    assertCandidateBytes(file, actual);
  }
};

const candidateResultLines = (bundle) => [
  `catalogPath=${bundle.context.catalogRelativePath}`,
  `catalogRawSha256=${bundle.catalogRawSha256}`,
  `catalogSemanticSha256=${bundle.catalogSemanticSha256}`,
  `schemaPath=${bundle.context.schemaRelativePath}`,
  `schemaRawSha256=${bundle.schemaRawSha256}`,
  `schemaSemanticSha256=${bundle.schemaSemanticSha256}`,
  `lockPath=${bundle.context.lockRelativePath}`,
  `lockRawSha256=${bundle.lockRawSha256}`,
  `generatorSha256=${bundle.generatorSha256}`,
  `sourceCatalogRawSha256=${bundle.sourceCatalogRawSha256 ?? candidateSourceCatalogRawSha256}`,
];

const main = async () => {
  const { current: active, rollback, p9Rollback, p10Rollback } =
    await loadAndValidateCatalogs();
  if (
    cli.mode === "generate-p8-candidate" ||
    cli.mode === "verify-p8-candidate"
  ) {
    const context = resolveP8CandidateContext(cli.candidateDir);
    const bundle = await buildP8CandidateBundle(active, context);
    if (cli.mode === "generate-p8-candidate") {
      await generateCandidateBundle(bundle);
      console.log(
        `Capability catalog candidate ${p8CandidateCatalogVersion} generated append-only:\n` +
          candidateResultLines(bundle).join("\n"),
      );
    } else {
      await verifyCandidateBundle(bundle);
      console.log(
        `Capability catalog candidate ${p8CandidateCatalogVersion} verified byte-for-byte:\n` +
          candidateResultLines(bundle).join("\n"),
      );
    }
    return;
  }
  if (
    cli.mode === "generate-candidate" ||
    cli.mode === "verify-candidate"
  ) {
    const context = resolveCandidateContext(cli.candidateDir);
    const bundle = await buildCandidateBundle(active, context);
    if (cli.mode === "generate-candidate") {
      await generateCandidateBundle(bundle);
      console.log(
        `Capability catalog candidate ${candidateCatalogVersion} generated append-only:\n` +
          candidateResultLines(bundle).join("\n"),
      );
    } else {
      await verifyCandidateBundle(bundle);
      console.log(
        `Capability catalog candidate ${candidateCatalogVersion} verified byte-for-byte:\n` +
          candidateResultLines(bundle).join("\n"),
      );
    }
    return;
  }

  const csharp = renderCsharp(active);
  const typescript = renderTypescript(active);
  // Exercise the same deterministic renderer against the immutable rollback pin
  // without mutating CURRENT or generated outputs.
  const rollbackCsharp = renderCsharp(rollback);
  const rollbackTypescript = renderTypescript(rollback);
  const p9RollbackCsharp = renderCsharp(p9Rollback);
  const p9RollbackTypescript = renderTypescript(p9Rollback);
  const p10RollbackCsharp = renderCsharp(p10Rollback);
  const p10RollbackTypescript = renderTypescript(p10Rollback);
  if (
    !rollbackCsharp.includes('public const string CatalogVersion = "1.4";') ||
    !rollbackCsharp.includes(
      `public const string CatalogSha256 = "${rollbackCatalogPointer.catalogSha256}";`,
    ) ||
    !rollbackCsharp.includes(
      'new DynamicFormFlowCapabilityItemMetadata("FIELD_TYPED"',
    ) ||
    !rollbackCsharp.includes(
      "StatisticsConfigurationCapabilities { get; } = " +
        "Array.Empty<StatisticsConfigurationCapabilityMetadata>();",
    ) ||
    !rollbackTypescript.includes(
      'DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_VERSION = "1.4"',
    ) ||
    !rollbackTypescript.includes(rollbackCatalogPointer.catalogSha256)
  ) {
    throw new Error(
      "Catalog v1.4 rollback oracle did not render the exact BE/FE metadata pin.",
    );
  }
  if (
    !p9RollbackCsharp.includes('public const string CatalogVersion = "1.5";') ||
    !p9RollbackCsharp.includes(
      `public const string CatalogSha256 = "${p9RollbackCatalogPointer.catalogSha256}";`,
    ) ||
    !p9RollbackCsharp.includes(
      'new StatisticsConfigurationCapabilityMetadata("LABEL_TAXONOMY_CONFIG"',
    ) ||
    !p9RollbackCsharp.includes(
      'new DynamicFormFlowCapabilityItemMetadata("DIRECT_FIELD_TABLE_LABEL",',
    ) ||
    !p9RollbackCsharp.includes('"PATCH_REQUIRED", "P9", "STAT-DIRECT"') ||
    !p9RollbackTypescript.includes(
      'DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_VERSION = "1.5"',
    ) ||
    !p9RollbackTypescript.includes(p9RollbackCatalogPointer.catalogSha256)
  ) {
    throw new Error(
      "Catalog v1.5 P9 rollback oracle did not render the exact inherited P8/P9 metadata pin.",
    );
  }
  if (
    !p10RollbackCsharp.includes('public const string CatalogVersion = "1.6";') ||
    !p10RollbackCsharp.includes(
      `public const string CatalogSha256 = "${p10RollbackCatalogPointer.catalogSha256}";`,
    ) ||
    !p10RollbackCsharp.includes(
      'new StatisticsConfigurationCapabilityMetadata("LABEL_TAXONOMY_CONFIG"',
    ) ||
    !p10RollbackCsharp.includes(
      'new DynamicFormFlowCapabilityItemMetadata("DIRECT_FIELD_TABLE_LABEL", "Projection và thống kê trực tiếp field, table, label", "SUPPORTED", "P9"',
    ) ||
    !p10RollbackCsharp.includes(
      "StatisticsReconciliationCapabilities { get; } = " +
        "Array.Empty<DynamicFormFlowCapabilityItemMetadata>();",
    ) ||
    !p10RollbackTypescript.includes(
      'DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_VERSION = "1.6"',
    ) ||
    !p10RollbackTypescript.includes(p10RollbackCatalogPointer.catalogSha256) ||
    p10RollbackTypescript.includes('"statisticsReconciliationCapabilities"')
  ) {
    throw new Error(
      "Catalog v1.6 P10 rollback oracle did not render the exact published P9 metadata pin with P10 absent.",
    );
  }
  const generated = [
    [csharpOutputPath, csharp],
    [typescriptOutputPath, typescript],
  ];

  if (checkOnly) {
    const stale = [];
    for (const [filePath, expected] of generated) {
      const state = await checkGeneratedFile(filePath, expected);
      if (state !== null) stale.push(`${displayPath(filePath)} (${state})`);
    }
    if (stale.length > 0) {
      throw new Error(
        `Generated capability metadata is stale:\n${stale.map((file) => `- ${file}`).join("\n")}\n` +
          "Run from tdtd-be: node tools/generate-dynamic-form-flow-capability-catalog.mjs",
      );
    }
    console.log(
      `Capability catalog CURRENT ${active.entry.catalogVersion} verified (${active.entry.catalogSha256}); ` +
        `generated metadata is current; rollback oracle ${rollback.entry.catalogVersion} verified (${rollback.entry.catalogSha256}); ` +
        `P9 rollback oracle ${p9Rollback.entry.catalogVersion} verified (${p9Rollback.entry.catalogSha256}).`,
    );
    return;
  }

  for (const [filePath, content] of generated) await writeGeneratedFile(filePath, content);
  console.log(
    `Generated capability catalog CURRENT ${active.entry.catalogVersion} (${active.entry.catalogSha256}); ` +
      `rollback oracle ${rollback.entry.catalogVersion} verified (${rollback.entry.catalogSha256}); ` +
      `P9 rollback oracle ${p9Rollback.entry.catalogVersion} verified (${p9Rollback.entry.catalogSha256}):\n` +
      generated.map(([filePath]) => `- ${displayPath(filePath)}`).join("\n"),
  );
};

main().catch((error) => {
  console.error(`Capability catalog generation failed:\n${error.message}`);
  process.exitCode = 1;
});
