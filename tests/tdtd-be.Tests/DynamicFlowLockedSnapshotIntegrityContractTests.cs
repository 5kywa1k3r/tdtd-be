using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;

internal static class DynamicFlowLockedSnapshotIntegrityContractTests
{
    private const string FamilyId = "64b64c10aafbc4a5ec001001";
    private const string VersionId = "64b64c10aafbc4a5ec001002";
    private const string FormId = "64b64c10aafbc4a5ec001003";
    private const string FormFamilyId = "64b64c10aafbc4a5ec001004";
    private const string HistoricalCatalogVersion = "1.1";
    private const string HistoricalCatalogSemanticHash =
        "e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf";
    private const string HistoricalV12CatalogVersion = "1.2";
    private const string HistoricalV12CatalogSemanticHash =
        "b26549d5de7a3d93bd6fc9bab7bfdfbdaffb66a01347039b2c3629692b60068f";
    private const string SealedV13CatalogVersion = "1.3";
    private const string SealedV13CatalogSemanticHash =
        "55cfa0a4420e01db6707011ffc7a0271088c5b01b63edb8f2d21978edd3e2497";

    public static void Run()
    {
        CanonicalLockedSnapshotPasses();
        HistoricalV11LockedSnapshotPasses();
        StoredV12AndV13LockedSnapshotsRemainReadable();
        V13RollbackBlocksPreflightButPreservesStoredRead();
        HistoricalCatalogPinRequiresExplicitReadMode();
        HistoricalV11ReadAndPreflightStayBlocked();
        StoredPayloadAndCatalogTamperingFailClosed();
        FamilyCurrentVersionPinsAreVerified();
        ReferencedFormPinsAndLiveSnapshotAreVerified();
        FailureEnvelopeDoesNotEchoReferencedIdentifiers();
    }

    private static void CanonicalLockedSnapshotPasses()
    {
        var fixture = Fixture();
        DynamicFlowLockedSnapshotIntegrity.Validate(
            fixture.Family,
            new[] { fixture.Version },
            fixture.Forms);

        var referenced = DynamicFlowLockedSnapshotIntegrity.CollectReferencedFormIds(
            new[] { fixture.Version });
        AssertTrue(referenced.SetEquals(new[] { FormId }), "exact referenced Form ids");
    }

    private static void HistoricalV11LockedSnapshotPasses()
    {
        var fixture = Fixture();
        ApplyHistoricalCatalogPin(fixture);

        DynamicFlowLockedSnapshotIntegrity.Validate(
            fixture.Family,
            new[] { fixture.Version },
            fixture.Forms);

        var referenced = DynamicFlowLockedSnapshotIntegrity.CollectReferencedFormIds(
            new[] { fixture.Version });
        AssertTrue(referenced.SetEquals(new[] { FormId }), "historical v1.1 referenced Form ids");
    }

    private static void StoredV12AndV13LockedSnapshotsRemainReadable()
    {
        foreach (var pin in new[]
                 {
                     (
                         Version: HistoricalV12CatalogVersion,
                         Hash: HistoricalV12CatalogSemanticHash,
                         Label: "v1.2"),
                     (
                         Version: SealedV13CatalogVersion,
                         Hash: SealedV13CatalogSemanticHash,
                         Label: "v1.3")
                 })
        {
            var fixture = Fixture();
            ApplyCatalogPin(fixture, pin.Version, pin.Hash);

            DynamicFlowLockedSnapshotIntegrity.Validate(
                fixture.Family,
                new[] { fixture.Version },
                fixture.Forms);
            var stored = DynamicFlowTemplatePayloadContract.ReadCanonical(
                fixture.Version.PayloadJson,
                fixture.Version.RootDynamicFormTemplateId);
            AssertTrue(
                stored.CatalogVersion == pin.Version &&
                stored.CatalogSemanticHash == pin.Hash,
                $"stored {pin.Label} read must preserve its exact catalog pin");
            var parsed =
                DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                    fixture.Version.PayloadJson,
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true,
                        AllowHistoricalCatalogPins: true));
            AssertTrue(
                parsed.Payload.CatalogVersion == pin.Version &&
                parsed.Payload.CatalogSemanticHash == pin.Hash,
                $"stored {pin.Label} parser must accept the exact historical pin");
            var isGeneratedCurrent =
                pin.Version ==
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion &&
                pin.Hash ==
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256;
            if (!isGeneratedCurrent)
            {
                AssertThrows<AppException>(() =>
                    DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                        fixture.Version.PayloadJson,
                        new DynamicFlowDefinitionValidationOptions(
                            AllowLegacy: false,
                            AllowServerManagedPins: true,
                            RequireServerManagedPins: true)));
            }
        }

        var crossPaired = Fixture();
        ApplyCatalogPin(
            crossPaired,
            HistoricalV12CatalogVersion,
            HistoricalV12CatalogSemanticHash);
        crossPaired.Version.PayloadJson =
            crossPaired.Version.PayloadJson.Replace(
                HistoricalV12CatalogSemanticHash,
                SealedV13CatalogSemanticHash,
                StringComparison.Ordinal);
        AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                crossPaired.Version.PayloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true)));

        var caseDrift = Fixture();
        ApplyCatalogPin(
            caseDrift,
            SealedV13CatalogVersion,
            SealedV13CatalogSemanticHash);
        caseDrift.Version.PayloadJson = caseDrift.Version.PayloadJson.Replace(
            SealedV13CatalogSemanticHash,
            SealedV13CatalogSemanticHash.ToUpperInvariant(),
            StringComparison.Ordinal);
        AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                caseDrift.Version.PayloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true)));

        var oneByteDrift = Fixture();
        ApplyCatalogPin(
            oneByteDrift,
            SealedV13CatalogVersion,
            SealedV13CatalogSemanticHash);
        oneByteDrift.Version.PayloadJson =
            oneByteDrift.Version.PayloadJson.Replace(
                SealedV13CatalogSemanticHash,
                $"{SealedV13CatalogSemanticHash[..^1]}0",
                StringComparison.Ordinal);
        AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                oneByteDrift.Version.PayloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true)));

        var halfPin = Fixture();
        ApplyCatalogPin(
            halfPin,
            SealedV13CatalogVersion,
            SealedV13CatalogSemanticHash);
        halfPin.Version.PayloadJson = halfPin.Version.PayloadJson.Replace(
            $"\"catalogSemanticHash\":\"{SealedV13CatalogSemanticHash}\"",
            "\"catalogSemanticHash\":null",
            StringComparison.Ordinal);
        AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                halfPin.Version.PayloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true)));

        var whitespacePin = Fixture();
        ApplyCatalogPin(
            whitespacePin,
            SealedV13CatalogVersion,
            SealedV13CatalogSemanticHash);
        whitespacePin.Version.PayloadJson =
            whitespacePin.Version.PayloadJson.Replace(
                $"\"catalogVersion\":\"{SealedV13CatalogVersion}\"",
                $"\"catalogVersion\":\" {SealedV13CatalogVersion}\"",
                StringComparison.Ordinal);
        AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                whitespacePin.Version.PayloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true)));
    }

    private static void V13RollbackBlocksPreflightButPreservesStoredRead()
    {
        var fixture = Fixture();
        ApplyCatalogPin(
            fixture,
            SealedV13CatalogVersion,
            SealedV13CatalogSemanticHash);
        var request = new DynamicFlowPreflightRequest
        {
            FlowTemplateVersionId = fixture.Version.Id,
            CommandId = "sealed-v13-rollback-preflight",
            TargetUnitIds = new List<string> { FormFamilyId },
            PeriodKey = "2026-07",
            ScheduleIdentityJson = """{"kind":"ONCE"}"""
        };
        var participants = new Dictionary<
            string,
            IReadOnlyList<DynamicFlowParticipantUserSnapshotDto>>(
            StringComparer.Ordinal)
        {
            [FormFamilyId] =
            [
                new DynamicFlowParticipantUserSnapshotDto
                {
                    UserId = FormId,
                    Username = "sealed-reader",
                    FullName = "Sealed Reader",
                    UnitId = FormFamilyId
                }
            ]
        };

        var rolledBack = DynamicFlowRuntimePreflightContract.Build(
            FamilyId,
            "TASK",
            fixture.Family,
            fixture.Version,
            request,
            FormId,
            FormFamilyId,
            participants,
            _ => false);
        AssertTrue(
            rolledBack.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase &&
            rolledBack.BlockedUntilPhase == "P6",
            "rollback must block exact v1.3 execution at preflight");
        AssertTrue(
            rolledBack.FlowPin.CatalogVersion == SealedV13CatalogVersion &&
            rolledBack.FlowPin.CatalogSemanticHash ==
                SealedV13CatalogSemanticHash,
            "rollback must not erase the stored v1.3 read pin");

        var sealedCurrent = DynamicFlowRuntimePreflightContract.Build(
            FamilyId,
            "TASK",
            fixture.Family,
            fixture.Version,
            request,
            FormId,
            FormFamilyId,
            participants,
            _ => true);
        AssertTrue(
            sealedCurrent.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate &&
            sealedCurrent.BlockedUntilPhase is null,
            "sealed v1.3 must admit its exact stored pin");
    }

    private static void HistoricalCatalogPinRequiresExplicitReadMode()
    {
        var fixture = Fixture();
        ApplyHistoricalCatalogPin(fixture);

        var authoringError = AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                fixture.Version.PayloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true)));
        AssertTrue(
            authoringError.Code == AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID,
            "historical catalog pin must be rejected without stored-read opt-in");

        var stored = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            fixture.Version.PayloadJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        AssertTrue(
            stored.Payload.CatalogVersion == HistoricalCatalogVersion &&
            stored.Payload.CatalogSemanticHash == HistoricalCatalogSemanticHash,
            "stored-read opt-in must preserve the exact historical catalog pin");

        var mismatchedPayload = fixture.Version.PayloadJson.Replace(
            HistoricalCatalogSemanticHash,
            new string('f', 64),
            StringComparison.Ordinal);
        AssertThrows<AppException>(() =>
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                mismatchedPayload,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true)));
    }

    private static void HistoricalV11ReadAndPreflightStayBlocked()
    {
        var fixture = Fixture();
        ApplyHistoricalCatalogPin(fixture);

        var read = DynamicFlowTemplatePayloadContract.ReadCanonical(
            fixture.Version.PayloadJson,
            fixture.Version.RootDynamicFormTemplateId);
        AssertTrue(
            read.CatalogVersion == HistoricalCatalogVersion &&
            read.CatalogSemanticHash == HistoricalCatalogSemanticHash,
            "authorized stored read must preserve the exact historical catalog pin");

        var request = new DynamicFlowPreflightRequest
        {
            FlowTemplateVersionId = fixture.Version.Id,
            CommandId = "historical-v11-preflight",
            TargetUnitIds = new List<string> { FormFamilyId },
            PeriodKey = "2026-07",
            ScheduleIdentityJson = """{"kind":"ONCE"}"""
        };
        var participants = new Dictionary<
            string,
            IReadOnlyList<DynamicFlowParticipantUserSnapshotDto>>(StringComparer.Ordinal)
        {
            [FormFamilyId] = new[]
            {
                new DynamicFlowParticipantUserSnapshotDto
                {
                    UserId = FormId,
                    Username = "historical-reader",
                    FullName = "Historical Reader",
                    UnitId = FormFamilyId
                }
            }
        };
        var preflight = DynamicFlowRuntimePreflightContract.Build(
            FamilyId,
            "TASK",
            fixture.Family,
            fixture.Version,
            request,
            FormId,
            FormFamilyId,
            participants);
        AssertTrue(
            preflight.Eligibility == DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog &&
            preflight.BlockedUntilPhase == "P5",
            "historical v1.1 preflight must remain readable but catalog-blocked");
        AssertTrue(
            preflight.FlowPin.CatalogVersion == HistoricalCatalogVersion &&
            preflight.FlowPin.CatalogSemanticHash == HistoricalCatalogSemanticHash,
            "historical v1.1 preflight must preserve the exact stored pin");
    }

    private static void StoredPayloadAndCatalogTamperingFailClosed()
    {
        var hashTampered = Fixture();
        hashTampered.Version.PayloadHash = new string('0', 64);
        AssertIntegrityFailure(
            () => DynamicFlowLockedSnapshotIntegrity.Validate(
                hashTampered.Family,
                new[] { hashTampered.Version },
                hashTampered.Forms),
            "stored payload hash");

        var catalogTampered = Fixture();
        catalogTampered.Version.CatalogSemanticHash = new string('f', 64);
        AssertIntegrityFailure(
            () => DynamicFlowLockedSnapshotIntegrity.Validate(
                catalogTampered.Family,
                new[] { catalogTampered.Version },
                catalogTampered.Forms),
            "stored catalog semantic hash");

        var knownCatalogMismatch = Fixture();
        ApplyHistoricalCatalogPin(knownCatalogMismatch);
        knownCatalogMismatch.Version.CatalogVersion =
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion;
        knownCatalogMismatch.Version.CatalogSemanticHash =
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256;
        AssertIntegrityFailure(
            () => DynamicFlowLockedSnapshotIntegrity.Validate(
                knownCatalogMismatch.Family,
                new[] { knownCatalogMismatch.Version },
                knownCatalogMismatch.Forms),
            "stored version-to-payload catalog pin mismatch");
    }

    private static void FamilyCurrentVersionPinsAreVerified()
    {
        var fixture = Fixture();
        fixture.Family.CurrentVersionHash = new string('a', 64);
        AssertIntegrityFailure(
            () => DynamicFlowLockedSnapshotIntegrity.Validate(
                fixture.Family,
                new[] { fixture.Version },
                fixture.Forms),
            "family current-version hash");
    }

    private static void ReferencedFormPinsAndLiveSnapshotAreVerified()
    {
        var missing = Fixture();
        AssertIntegrityFailure(
            () => DynamicFlowLockedSnapshotIntegrity.Validate(
                missing.Family,
                new[] { missing.Version },
                new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal)),
            "missing referenced Form");

        var liveTampered = Fixture();
        liveTampered.Forms[FormId].FieldsJson =
            """[{"id":"new-field","key":"new-field","type":"TEXT"}]""";
        AssertIntegrityFailure(
            () => DynamicFlowLockedSnapshotIntegrity.Validate(
                liveTampered.Family,
                new[] { liveTampered.Version },
                liveTampered.Forms),
            "published Form live schema");
    }

    private static void FailureEnvelopeDoesNotEchoReferencedIdentifiers()
    {
        var fixture = Fixture();
        fixture.Version.CatalogVersion = "tampered";
        var error = AssertThrows<AppException>(() =>
            DynamicFlowLockedSnapshotIntegrity.Validate(
                fixture.Family,
                new[] { fixture.Version },
                fixture.Forms));

        var details = JsonSerializer.Serialize(error.Details);
        AssertTrue(
            details.Contains(DynamicFlowLockedSnapshotIntegrity.FailureReason, StringComparison.Ordinal),
            "stable locked-snapshot integrity reason");
        AssertTrue(!details.Contains(FormId, StringComparison.Ordinal), "failure must not echo Form id");
        AssertTrue(!details.Contains(VersionId, StringComparison.Ordinal), "failure must not echo version id");
        AssertTrue(!details.Contains(FamilyId, StringComparison.Ordinal), "failure must not echo family id");
    }

    private static IntegrityFixture Fixture()
    {
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");
        var form = new DynamicFormTemplate
        {
            Id = FormId,
            FamilyId = FormFamilyId,
            VersionNo = 1,
            SchemaVersion = 1,
            IsActive = true,
            IsPublished = true,
            IsDeleted = false,
            SectionsJson = "[]",
            FieldsJson = "[]",
            BlocksJson = "[]",
            PublishedSchemaSnapshotJson = snapshot.Json,
            PublishedSchemaHash = snapshot.Sha256
        };
        var payload = new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = "FLOW-T01",
            EntryStepId = "step-a",
            RootDynamicFormTemplateId = FormId,
            CatalogVersion = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            CatalogSemanticHash = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            FormNodes = new List<DynamicFlowFormNodeDto>
            {
                new()
                {
                    FormNodeId = "form-a",
                    Role = "ROOT",
                    DynamicFormTemplateId = FormId,
                    DynamicFormFamilyId = FormFamilyId,
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = snapshot.Sha256,
                    DynamicFormSnapshotHash = snapshot.Sha256
                }
            },
            Nodes = new List<DynamicFlowTopologyNodeDto>
            {
                new()
                {
                    NodeId = "step-a",
                    NodeCode = "START",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "form-a",
                    DeclaredRoles = new List<string> { "OWNER" }
                }
            }
        };
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload,
            payloadJson: null,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true));
        var version = new DynamicFlowTemplateVersion
        {
            Id = VersionId,
            TemplateId = FamilyId,
            RootDynamicFormTemplateId = FormId,
            VersionNo = 1,
            Status = DynamicFlowTemplateVersionStatuses.Locked,
            DraftRevision = 1,
            SchemaVersion = DynamicFlowDefinitionSchema.CurrentVersion,
            AdapterVersion = DynamicFlowDefinitionSchema.CurrentAdapterVersion,
            CatalogVersion = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            CatalogSemanticHash = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            PayloadJson = canonical.CanonicalJson,
            PayloadHash = canonical.PayloadHash,
            DefinitionLockable = true,
            MigrationState = DynamicFlowDefinitionMigrationStates.Canonical,
            IsDeleted = false
        };
        var family = new DynamicFlowTemplate
        {
            Id = FamilyId,
            RootDynamicFormTemplateId = FormId,
            Status = DynamicFlowTemplateStatuses.Active,
            CurrentVersionId = VersionId,
            CurrentVersionNo = version.VersionNo,
            CurrentVersionHash = version.PayloadHash,
            HasLockedVersion = true,
            IsDeleted = false
        };
        return new IntegrityFixture(
            family,
            version,
            new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal)
            {
                [form.Id] = form
            });
    }

    private static void ApplyHistoricalCatalogPin(IntegrityFixture fixture)
        => ApplyCatalogPin(
            fixture,
            HistoricalCatalogVersion,
            HistoricalCatalogSemanticHash);

    private static void ApplyCatalogPin(
        IntegrityFixture fixture,
        string catalogVersion,
        string catalogSemanticHash)
    {
        var payloadJson = fixture.Version.PayloadJson
            .Replace(
                $"\"catalogVersion\":\"{DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion}\"",
                $"\"catalogVersion\":\"{catalogVersion}\"",
                StringComparison.Ordinal)
            .Replace(
                $"\"catalogSemanticHash\":\"{DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256}\"",
                $"\"catalogSemanticHash\":\"{catalogSemanticHash}\"",
                StringComparison.Ordinal);
        fixture.Version.CatalogVersion = catalogVersion;
        fixture.Version.CatalogSemanticHash = catalogSemanticHash;
        fixture.Version.PayloadJson = payloadJson;
        fixture.Version.PayloadHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)))
            .ToLowerInvariant();
        fixture.Family.CurrentVersionHash = fixture.Version.PayloadHash;
    }

    private static void AssertIntegrityFailure(Action action, string context)
    {
        var error = AssertThrows<AppException>(action);
        AssertTrue(
            error.Code == AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            $"{context}: stable conflict code");
        var details = JsonSerializer.Serialize(error.Details);
        AssertTrue(
            details.Contains(DynamicFlowLockedSnapshotIntegrity.FailureReason, StringComparison.Ordinal),
            $"{context}: stable failure reason");
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private sealed record IntegrityFixture(
        DynamicFlowTemplate Family,
        DynamicFlowTemplateVersion Version,
        Dictionary<string, DynamicFormTemplate> Forms);
}
