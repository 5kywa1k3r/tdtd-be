using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class DynamicFormNativeListContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string Hash = new('a', 64);

    public static void Run()
    {
        DefinitionV2KeepsListSeparateFromLegacyTable();
        SchemaAdapterPreservesListV2AtWriteAndReadBoundaries();
        LegacyRecordValuesStillValidateWithTheirOriginalDefinition();
        DraftAndSubmitHaveDifferentRequiredRules();
        StableIdsLimitsAndCatalogPinsFailClosed();
        NewListItemsRequireUuidWithoutRewritingExistingIds();
        ListValuesCannotUseContentReferenceOrExceedDraftMaximum();
        SectionProjectionReceivesBoundCatalogOptions();
        SystemChoicesRequireListAndActiveUnitMembership();
    }

    private static void SectionProjectionReceivesBoundCatalogOptions()
    {
        var report = new tdtd_be.Models.WorkAssignmentReport { Id = "list-report", DynamicFormSchemaHash = Hash };
        var section = new DynamicFormSectionSnapshot("form", "FORM", "Form", "main", "Danh sách", null,
            [], 0, 1, [], [], "[]", "[]", Hash) { NativeTableIds = ["staff-list"], NativeTablesJson = JsonSerializer.Serialize(Definitions(), WebJson) };
        var payload = "{\"blocks\":[],\"nativeTables\":" + Envelope("""
            [{"recordId":"item-1","cells":{"unit":{"type":"singleSelect","state":"value","value":"unit-a"}}}]
            """) + "}";
        bool Check(IReadOnlyDictionary<string, RuntimeEnumOptionSet>? options) =>
            WorkAssignmentReportSectionProjectionService.IsCompleteProjection(report, [section], [], "{}", payload, options);
        if (Check(Options("unit-a"))) throw new InvalidOperationException("Missing section projection must still require repair.");
        Contains(Throws(() => Check(null)).Details, "NATIVE_ENUM_CATALOG_UNAVAILABLE", "projection cannot invent catalog membership");
        Contains(Throws(() => Check(Options("unit-b"))).Details, "DYNAMIC_FORM_RUNTIME_FIELD_CHOICE_CODE_INVALID", "projection rejects invalid codes");
    }

    private static void SystemChoicesRequireListAndActiveUnitMembership()
    {
        const string unitId = "507f1f77bcf86cd799439011";
        var list = Definitions()[0];
        var system = list with { TypeConfig = list.TypeConfig! with { Rules = list.TypeConfig.Rules!
            .Select(rule => rule.Target!.FieldId == "unit"
                ? rule with { Spec = rule.Spec! with {
                    Type = "multiSelect", ValueSource = new() { SourceType = "SYSTEM_UNIT" },
                    MinSelected = null, MaxSelected = null } }
                : rule).ToList() } };
        DynamicFormNativeTableDefinition.Validate([system], "[{\"id\":\"main\"}]", "[]", "[]", 2);
        var sourceJson = JsonSerializer.Serialize(new DynamicFormNativeValueSourceDto { SourceType = "SYSTEM_UNIT" }, WebJson);
        if (sourceJson != "{\"sourceType\":\"SYSTEM_UNIT\"}")
            throw new InvalidOperationException("System source readback must not emit catalog-only null properties.");
        var ordinary = system with { Presentation = null, ItemConstraints = null };
        Contains(Throws(() => DynamicFormNativeTableDefinition.Validate([ordinary],
            "[{\"id\":\"main\"}]", "[]", "[]", 2)).Details,
            "NATIVE_OPTIONS_REQUIRED", "system choice source is scoped to List v2");
        var payload = Envelope("[{\"recordId\":\"item-1\",\"cells\":{\"unit\":{\"type\":\"multiSelect\",\"state\":\"value\",\"value\":[\"" + unitId + "\"]}}}]");
        using var document = JsonDocument.Parse(payload);
        DynamicFormNativeTableValues.ValidateEnvelope([system], Hash, document.RootElement,
            submitting: false, allowedSystemUnitIds: new HashSet<string>(StringComparer.Ordinal) { unitId });
        Contains(Throws(() => DynamicFormNativeTableValues.ValidateEnvelope([system], Hash, document.RootElement,
            submitting: false, allowedSystemUnitIds: new HashSet<string>(StringComparer.Ordinal))).Details,
            "DYNAMIC_FORM_RUNTIME_FIELD_CHOICE_CODE_INVALID", "an inactive unit cannot be written");

        var locality = system with { TypeConfig = system.TypeConfig! with { Rules = system.TypeConfig.Rules!
            .Select(rule => rule.Target!.FieldId == "unit" ? rule with { Spec = rule.Spec! with {
                Type = "singleSelect", ValueSource = new() { SourceType = "SYSTEM_LOCALITY" } } } : rule).ToList() } };
        DynamicFormNativeTableDefinition.Validate([locality], "[{\"id\":\"main\"}]", "[]", "[]", 2);
        using var localityPayload = JsonDocument.Parse(Envelope("[{\"recordId\":\"item-1\",\"cells\":{\"unit\":{\"type\":\"singleSelect\",\"state\":\"value\",\"value\":\"14797\"}}}]"));
        DynamicFormNativeTableValues.ValidateEnvelope([locality], Hash, localityPayload.RootElement, submitting: false);
    }

    private static void SchemaAdapterPreservesListV2AtWriteAndReadBoundaries()
    {
        var schema = new DynamicFormSchemaDto
        {
            Sections = [new() { Id = "main", Title = "Danh sách", Order = 0 }],
            Fields = [], Blocks = [], NativeTablesVersion = 2, Tables = Definitions()
        };
        var stored = DynamicFormSchemaAdapter.ResolveInput(schema, null, null, null, null);
        var read = DynamicFormSchemaAdapter.FromLegacy(stored.SectionsJson, stored.FieldsJson,
            stored.ExcelBlockJson, stored.BlocksJson, stored.NativeTablesVersion, stored.TablesJson);
        if (read.NativeTablesVersion != 2 || read.Tables![0].Presentation?.ItemLabel != "Cán bộ"
            || read.Tables[0].Fields![0].Id != "full-name")
            throw new InvalidOperationException("List v2 identity and presentation must survive the service schema adapter.");
        var legacy = DynamicFormSchemaAdapter.ToLegacy(schema with { NativeTablesVersion = 1, Tables = [] });
        if (legacy.NativeTablesVersion != 1 || legacy.TablesJson != "[]")
            throw new InvalidOperationException("Existing native v1 wire version must remain unchanged.");
        Contains(Throws(() => DynamicFormSchemaAdapter.ToLegacy(schema with { NativeTablesVersion = 3 })).Details,
            "NATIVE_TABLE_VERSION_OR_COLLECTION_INVALID", "unknown versions must stay rejected");
        Contains(Throws(() => DynamicFormSchemaAdapter.ToLegacy(schema with { Tables = null })).Details,
            "NATIVE_TABLE_VERSION_OR_COLLECTION_INVALID", "missing tables must stay rejected");
    }

    private static void LegacyRecordValuesStillValidateWithTheirOriginalDefinition()
    {
        var list = Definitions()[0];
        var oldTable = list with
        {
            Presentation = null, ItemConstraints = null,
            Fields = list.Fields!.Where(field => field.Id is "full-name" or "note").ToList(),
            TypeConfig = list.TypeConfig! with
            {
                Rules = list.TypeConfig.Rules!.Where(rule => rule.Target!.FieldId is "full-name" or "note")
                    .Select(rule => rule with { Spec = rule.Spec! with { MinLength = null } }).ToList()
            }
        };
        DynamicFormNativeTableDefinition.Validate([oldTable], "[{\"id\":\"main\"}]", "[]", "[]", 1);
        var envelope = """
            {"version":1,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "tables":[{"tableId":"staff-list","records":[{"recordId":"legacy-record","cells":{
               "full-name":{"type":"plainText","state":"value","value":"Bản ghi cũ"},
               "note":{"type":"plainText","state":"value","value":"Ghi chú cũ"}
             }}]}]}
            """;
        using var document = JsonDocument.Parse(envelope);
        DynamicFormNativeTableValues.ValidateEnvelope([oldTable], Hash, document.RootElement,
            submitting: true);
    }

    private static void DefinitionV2KeepsListSeparateFromLegacyTable()
    {
        var definitions = Definitions();
        DynamicFormNativeTableDefinition.Validate(definitions, "[{\"id\":\"main\"}]", "[]", "[]", 2);

        var error = Throws(() => DynamicFormNativeTableDefinition.Validate(
            definitions, "[{\"id\":\"main\"}]", "[]", "[]", 1));
        Contains(error.Details, "NATIVE_LIST_REQUIRES_V2", "v1 must reject List-only metadata");
    }

    private static void DraftAndSubmitHaveDifferentRequiredRules()
    {
        Validate(Envelope("[]"), submitting: false);
        Contains(Throws(() => Validate(Envelope("[]"), submitting: true)).Details,
            "NATIVE_LIST_ITEM_COUNT_INVALID", "submit must enforce minItems");

        var longNote = new string('x', 8_000);
        var record = """
            [{"recordId":"item-001","cells":{
              "full-name":{"type":"plainText","state":"value","value":"Nguyễn Văn A"},
              "cases":{"type":"number","state":"value","value":12},
              "received":{"type":"fullDate","state":"value","value":"15/09/2026"},
              "unit":{"type":"singleSelect","state":"value","value":"unit-a"},
              "note":{"type":"plainText","state":"value","value":
            """ + JsonSerializer.Serialize(longNote) + "}}}]";
        Validate(Envelope(record), submitting: true, Options("unit-a", "unit-b"));

        var unresolved = record.Replace(
            "{\"type\":\"number\",\"state\":\"value\",\"value\":12}",
            "{\"type\":\"number\",\"state\":\"error\",\"raw\":\"12x\",\"code\":\"NATIVE_INPUT_INVALID\"}",
            StringComparison.Ordinal);
        Validate(Envelope(unresolved), submitting: false, Options("unit-a"));
        Contains(Throws(() => Validate(Envelope(unresolved), submitting: true, Options("unit-a"))).Details,
            "NATIVE_VALUE_ERROR_UNRESOLVED", "submit must reject preserved error drafts");
    }

    private static void StableIdsLimitsAndCatalogPinsFailClosed()
    {
        var duplicate = "[{\"recordId\":\"item-001\",\"cells\":{}},{\"recordId\":\"item-001\",\"cells\":{}}]";
        Contains(Throws(() => Validate(Envelope(duplicate), submitting: false)).Details,
            "NATIVE_RECORD_ID_DUPLICATE", "recordId must remain stable and unique");

        var revoked = """
            [{"recordId":"item-001","cells":{
              "full-name":{"type":"plainText","state":"value","value":"Nguyễn Văn A"},
              "cases":{"type":"number","state":"value","value":1},
              "received":{"type":"fullDate","state":"value","value":"15/09/2026"},
              "unit":{"type":"singleSelect","state":"value","value":"unit-revoked"},
              "note":{"type":"plainText","state":"value","value":null}
            }}]
            """;
        Contains(Throws(() => Validate(Envelope(revoked), submitting: true, Options("unit-a"))).Details,
            "DYNAMIC_FORM_RUNTIME_FIELD_CHOICE_CODE_INVALID", "revoked catalog code must fail at submit");

        var outsideDate = revoked.Replace("unit-revoked", "unit-a", StringComparison.Ordinal)
            .Replace("15/09/2026", "31/12/2025", StringComparison.Ordinal);
        Contains(Throws(() => Validate(Envelope(outsideDate), submitting: true, Options("unit-a"))).Details,
            "NATIVE_DATE_CONSTRAINT", "canonical dd/MM/yyyy values must compare against ISO definition limits");

        var records = string.Join(',', Enumerable.Range(0, 201)
            .Select(index => $"{{\"recordId\":\"item-{index:D3}\",\"cells\":{{}}}}"));
        Contains(Throws(() => Validate(Envelope($"[{records}]"), submitting: false)).Details,
            "NATIVE_RECORD_LIMIT_200", "List record limit must be bounded before submit");
    }

    private static void NewListItemsRequireUuidWithoutRewritingExistingIds()
    {
        var form = new tdtd_be.Models.DynamicFormTemplate
        {
            NativeTablesVersion = 2,
            TablesJson = JsonSerializer.Serialize(Definitions(), WebJson),
            SectionsJson = "[{\"id\":\"main\"}]",
            FieldsJson = "[]", BlocksJson = "[]", PublishedSchemaHash = Hash
        };
        static string Root(string recordId) => "{\"nativeTables\":"
            + Envelope("[{\"recordId\":" + JsonSerializer.Serialize(recordId) + ",\"cells\":{}}]") + "}";
        var legacy = Root("item-a");
        var nonUuid = Root("new-item-a");
        var uuid = Root("11111111-1111-4111-8111-111111111111");

        // Historical readback and unchanged legacy records remain valid.
        DynamicFormNativeTableValues.Validate(form, Hash, legacy, submitting: false);
        DynamicFormNativeTableValues.Validate(form, Hash, legacy, submitting: false,
            enforceListUuidOnWrite: true, previousTableValuesJson: legacy);
        DynamicFormNativeTableValues.Validate(form, Hash, uuid, submitting: false,
            enforceListUuidOnWrite: true);
        Contains(Throws(() => DynamicFormNativeTableValues.Validate(form, Hash, nonUuid,
                submitting: false, enforceListUuidOnWrite: true, previousTableValuesJson: legacy)).Details,
            "NATIVE_LIST_ITEM_UUID_REQUIRED", "a new List item must use a UUID even when legacy rows exist");
    }

    private static void ListValuesCannotUseContentReferenceOrExceedDraftMaximum()
    {
        var definition = Definitions()[0];
        var bounded = definition with { ItemConstraints = definition.ItemConstraints! with { MaxItems = 1 } };
        var twoRecords = """
            {"version":1,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "tables":[{"tableId":"staff-list","records":[
               {"recordId":"item-1","cells":{}},{"recordId":"item-2","cells":{}}]}]}
            """;
        using (var document = JsonDocument.Parse(twoRecords))
            Contains(Throws(() => DynamicFormNativeTableValues.ValidateEnvelope([bounded], Hash,
                document.RootElement, submitting: false)).Details,
                "NATIVE_LIST_ITEM_COUNT_INVALID", "draft must reject more than configured maxItems");

        var twoText = definition with
        {
            Fields = definition.Fields!.Where(field => field.Id is "full-name" or "note").ToList(),
            TypeConfig = definition.TypeConfig! with
            {
                Rules = definition.TypeConfig.Rules!.Where(rule => rule.Target!.FieldId is "full-name" or "note").ToList()
            },
            Presentation = definition.Presentation! with { SummaryFieldIds = ["full-name"] }
        };
        var contentReference = """
            {"version":1,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "tables":[{"tableId":"staff-list","records":[],"contentRef":{}}]}
            """;
        using var referenceDocument = JsonDocument.Parse(contentReference);
        Contains(Throws(() => DynamicFormNativeTableValues.ValidateEnvelope([twoText], Hash,
            referenceDocument.RootElement, submitting: false)).Details,
            "NATIVE_CONTENT_TABLE_SHAPE", "List must not use the separate content table payload branch");
    }

    private static List<DynamicFormNativeTableDto> Definitions()
        => JsonSerializer.Deserialize<List<DynamicFormNativeTableDto>>("""
        [{
          "id":"staff-list","sectionId":"main","name":"Danh sách cán bộ","order":0,"layout":"vertical",
          "fields":[
            {"id":"full-name","name":"Họ tên","order":0},
            {"id":"cases","name":"Số vụ","order":1},
            {"id":"received","name":"Ngày tiếp nhận","order":2},
            {"id":"unit","name":"Đơn vị công tác","order":3},
            {"id":"note","name":"Ghi chú","order":4}
          ],
          "rows":[],
          "typeConfig":{"version":1,"sequence":5,"rules":[
            {"target":{"scope":"column","fieldId":"full-name"},"spec":{"type":"plainText","required":true,"maxLength":200},"order":1},
            {"target":{"scope":"column","fieldId":"cases"},"spec":{"type":"number","required":true,"minimum":0,"integerOnly":true},"order":2},
            {"target":{"scope":"column","fieldId":"received"},"spec":{"type":"fullDate","required":true,"minDate":"2026-01-01","maxDate":"2026-12-31"},"order":3},
            {"target":{"scope":"column","fieldId":"unit"},"spec":{"type":"singleSelect","required":true,"valueSource":{"sourceType":"ENUM_CATALOG","catalogId":"catalog-unit"}},"order":4},
            {"target":{"scope":"column","fieldId":"note"},"spec":{"type":"plainText","required":false,"maxLength":20000},"order":5}
          ]},
          "statisticTargets":[],
          "presentation":{"kind":"LIST","itemLabel":"Cán bộ","addLabel":"Thêm cán bộ","summaryFieldIds":["full-name","unit"]},
          "itemConstraints":{"minItems":1,"maxItems":200}
        }]
        """, WebJson)!;

    private static string Envelope(string records)
        => "{\"version\":1,\"schemaHash\":\"" + Hash
           + "\",\"tables\":[{\"tableId\":\"staff-list\",\"records\":" + records + "}]}";

    private static IReadOnlyDictionary<string, RuntimeEnumOptionSet> Options(params string[] codes)
        => new Dictionary<string, RuntimeEnumOptionSet>(StringComparer.Ordinal)
        {
            ["catalog-unit"] = new("catalog-unit", codes.ToHashSet(StringComparer.Ordinal))
        };

    private static void Validate(string envelope, bool submitting,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? options = null)
    {
        using var document = JsonDocument.Parse(envelope);
        DynamicFormNativeTableValues.ValidateEnvelope(Definitions(), Hash, document.RootElement, submitting, options);
    }

    private static AppException Throws(Action action)
    {
        try { action(); }
        catch (AppException error) { return error; }
        throw new InvalidOperationException("Expected AppException was not thrown.");
    }

    private static void Contains(object? value, string expected, string context)
    {
        var serialized = JsonSerializer.Serialize(value);
        if (!serialized.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected {expected}, got {serialized}.");
    }
}
