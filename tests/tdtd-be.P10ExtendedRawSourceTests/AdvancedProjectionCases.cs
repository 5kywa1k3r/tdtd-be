using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class AdvancedProjectionCases
{
    internal static int Run()
    {
        var cases = 0;
        cases += OptionNormalization();
        cases += SingleSelectLabel();
        cases += MultiSelectFlatten();
        cases += UnorderedStringList();
        cases += StringListJoin("DAY", "2026-08-07", "stringList");
        cases += RichTextJoin("MONTH", "2026-08");
        cases += WhitespaceAndEmptySum();
        cases += JoinOrder("MONTH", "2026-08");
        cases += JoinOrder("YEAR", "2026");
        cases += FullDateYear();
        cases += ProjectionMutation();
        return cases;
    }

    private static int OptionNormalization()
    {
        using var document = JsonDocument.Parse(
            "{\"id\":\"choice\",\"options\":[" +
            "{\"code\":\" A \",\"label\":\" Alpha \"}," +
            "{\"code\":\"A\",\"label\":\"forged-second\"}," +
            "{\"code\":\"   \",\"label\":\"ignored\"}]}" );
        var result =
            (ImmutableArray<StatisticReconciliationActualExtendedAdvancedOptionProjection>)
            TestSupport.InvokePrivate(
                typeof(StatisticReconciliationActualMongoExtendedRawSourceCollectReader),
                "AdvancedOptions",
                document.RootElement);
        TestSupport.Equal(1, result.Length,
            "P10-EXT-ADV-01 option-blank-skip-duplicate-first");
        TestSupport.Equal("A", result[0].Code,
            "P10-EXT-ADV-02 option-code-trim");
        TestSupport.Equal("Alpha", result[0].Label,
            "P10-EXT-ADV-03 option-label-trim");
        return 3;
    }

    private static int SingleSelectLabel()
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            "DAY", "2026-08-01", "choice", "choice", "BUCKET_COUNT",
            "ENUM", ["VALUES"]);
        var projection = TestSupport.Projection(
            descriptor, "choice", "singleSelect", ("A", "Alpha"));
        var typed = Compile(
            descriptor,
            projection,
            "DAY",
            "2026-08-01",
            TestSupport.AdvancedEnvelope(
                0,
                "DAY",
                "2026-08-01",
                "report-choice",
                "{\"values\":{\"choice\":\"A\"}}"));
        TestSupport.Equal("Alpha", Atom(typed, "ENUM").CanonicalValue,
            "P10-EXT-ADV-04 single-select-code-to-label");
        TestSupport.Equal("1", Atom(typed, "COUNT").CanonicalValue,
            "P10-EXT-ADV-05 single-select-count");
        return 2;
    }

    private static int MultiSelectFlatten()
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            "DAY", "2026-08-02", "tags", "tags", "BUCKET_COUNT",
            "STRING_LIST", ["VALUES"]);
        var projection = TestSupport.Projection(
            descriptor,
            "tags",
            "multiSelect",
            ("A", "Alpha"),
            ("B", "Beta"));
        var typed = Compile(
            descriptor,
            projection,
            "DAY",
            "2026-08-02",
            TestSupport.AdvancedEnvelope(
                0,
                "DAY",
                "2026-08-02",
                "report-tags",
                "{\"values\":{\"tags\":[\"A\",\"B\"]}}"));
        TestSupport.Equal("[\"Alpha\",\"Beta\"]",
            Atom(typed, "STRING_LIST").CanonicalValue,
            "P10-EXT-ADV-06 multi-select-label-flatten");
        TestSupport.Equal("1", Atom(typed, "COUNT").CanonicalValue,
            "P10-EXT-ADV-07 string-list-raw-sample-count");
        var ownerCount = (long)TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "AdvancedOwnerValueCount",
            descriptor,
            typed.Atoms.ToArray());
        TestSupport.Equal(2L, ownerCount,
            "P10-EXT-ADV-08 string-list-owner-element-count");
        return 3;
    }

    private static int UnorderedStringList()
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            "DAY", "2026-08-06", "tags", "tags", "BUCKET_COUNT",
            "STRING_LIST", ["VALUES"], unordered: true);
        var projection = TestSupport.Projection(
            descriptor,
            "tags",
            "stringList",
            ("A", "Alpha"),
            ("B", "Beta"));
        var typed = Compile(
            descriptor,
            projection,
            "DAY",
            "2026-08-06",
            TestSupport.AdvancedEnvelope(
                0, "DAY", "2026-08-06", "unordered-1",
                "{\"values\":{\"tags\":[\"B\",\"A\"]}}"),
            TestSupport.AdvancedEnvelope(
                1, "DAY", "2026-08-06", "unordered-2",
                "{\"values\":{\"tags\":[\"A\",\"B\"]}}"));
        var atom = Atom(typed, "STRING_LIST");
        TestSupport.Equal("[\"Alpha\",\"Beta\"]", atom.CanonicalValue,
            "P10-EXT-ADV-09 unordered-string-list-canonical");
        TestSupport.Equal(2L, atom.OccurrenceCount,
            "P10-EXT-ADV-10 unordered-permutation-occurrence");
        var buckets = (string)TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "AdvancedBuckets",
            descriptor,
            typed.Atoms.ToArray(),
            4L);
        TestSupport.Equal("{\"Alpha\":2,\"Beta\":2}", buckets,
            "P10-EXT-ADV-11 unordered-bucket-count");
        return 3;
    }

    private static int WhitespaceAndEmptySum()
    {
        var text = TestSupport.AdvancedDescriptor(
            "DAY", "2026-08-03", "notes", "notes", "COUNT",
            "TEXT", ["COUNT"]);
        var textProjection = TestSupport.Projection(
            text, "notes", "longText");
        var whitespace = TestSupport.AdvancedEnvelope(
            0,
            "DAY",
            "2026-08-03",
            "report-space",
            "{\"values\":{\"notes\":\"   \"}}" );
        var textTyped = Compile(
            text, textProjection, "DAY", "2026-08-03", whitespace);
        TestSupport.Equal("0", Atom(textTyped, "COUNT").CanonicalValue,
            "P10-EXT-ADV-09 whitespace-count-zero");
        TestSupport.Equal(0,
            textTyped.DescriptorContributions.Single()
                .ValueSourceReportCount,
            "P10-EXT-ADV-10 whitespace-no-contribution");

        var sum = TestSupport.AdvancedDescriptor(
            "DAY", "2026-08-04", "amount", "amount", "SUM",
            "NUMBER", ["SUM"]);
        var sumProjection = TestSupport.Projection(
            sum, "amount", "number");
        var empty = TestSupport.AdvancedEnvelope(
            0,
            "DAY",
            "2026-08-04",
            "report-empty-sum",
            "{\"values\":{}}" );
        var sumTyped = Compile(
            sum, sumProjection, "DAY", "2026-08-04", empty);
        TestSupport.Equal("MISSING", Atom(sumTyped, "SUM").ValueState,
            "P10-EXT-ADV-11 raw-empty-sum-missing");
        var owner = new ActualAdvancedFieldObservation(
            0,
            "amount",
            "amount",
            "Amount",
            "NUMBER",
            "SUM",
            0,
            0,
            "0",
            [],
            TestSupport.Sha("empty-sum-field"));
        _ = TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "RequireAdvancedResult",
            sum,
            sumProjection,
            sumTyped.Atoms.ToArray(),
            owner,
            ImmutableArray.Create(empty));
        Console.WriteLine("PASS P10-EXT-ADV-12 empty-sum-owner-zero");
        return 4;
    }

    private static int StringListJoin(
        string grain,
        string key,
        string rawFieldType)
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            grain, key, "items", "items", "JOIN",
            "STRING_LIST", ["VALUES"]);
        var projection = rawFieldType == "stringList"
            ? TestSupport.Projection(
                descriptor,
                "items",
                rawFieldType,
                ("A", "Alpha"),
                ("B", "Beta"),
                ("C", "Gamma"))
            : TestSupport.Projection(
                descriptor, "items", rawFieldType);
        var firstJson = rawFieldType == "stringList"
            ? "{\"values\":{\"items\":[\" B \",\"A\"]}}"
            : "{\"values\":{\"items\":[\" Beta \",\"Alpha\"]}}";
        var first = TestSupport.AdvancedEnvelope(
            0,
            grain,
            key,
            $"{grain}-list-join-1",
            firstJson);
        var secondJson = rawFieldType == "stringList"
            ? "{\"values\":{\"items\":[\"A\",\"C\"]}}"
            : "{\"values\":{\"items\":[\"Alpha\",\"Gamma\"]}}";
        var second = TestSupport.AdvancedEnvelope(
            1,
            grain,
            key,
            $"{grain}-list-join-2",
            secondJson);
        var typed = Compile(
            descriptor, projection, grain, key, first, second);
        var owner = new ActualAdvancedFieldObservation(
            0,
            "items",
            "items",
            "Items",
            "STRING_LIST",
            "JOIN",
            4,
            2,
            "\"Beta, Alpha, Gamma\"",
            ["Beta", "Alpha", "Gamma"],
            TestSupport.Sha($"{grain}-{rawFieldType}-join-field"));
        _ = TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "RequireAdvancedResult",
            descriptor,
            projection,
            typed.Atoms.ToArray(),
            owner,
            ImmutableArray.Create(first, second));
        Console.WriteLine(
            $"PASS P10-EXT-ADV-{(grain == "DAY" ? "19" : "20")} " +
            $"{grain.ToLowerInvariant()}-{rawFieldType}-join-elements");
        return 1;
    }

    private static int RichTextJoin(string grain, string key)
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            grain, key, "rich", "rich", "JOIN",
            "STRING_LIST", ["VALUES"]);
        var projection = TestSupport.Projection(
            descriptor, "rich", "richText");
        var first = TestSupport.AdvancedEnvelope(
            0,
            grain,
            key,
            $"{grain}-rich-join-1",
            "{\"values\":{\"rich\":\" Beta \"}}");
        var second = TestSupport.AdvancedEnvelope(
            1,
            grain,
            key,
            $"{grain}-rich-join-2",
            "{\"values\":{\"rich\":\"Alpha\"}}");
        var typed = Compile(
            descriptor, projection, grain, key, first, second);
        var owner = new ActualAdvancedFieldObservation(
            0,
            "rich",
            "rich",
            "Rich",
            "STRING_LIST",
            "JOIN",
            2,
            2,
            "\"Beta, Alpha\"",
            ["Beta", "Alpha"],
            TestSupport.Sha($"{grain}-rich-join-field"));
        _ = TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "RequireAdvancedResult",
            descriptor,
            projection,
            typed.Atoms.ToArray(),
            owner,
            ImmutableArray.Create(first, second));
        Console.WriteLine(
            "PASS P10-EXT-ADV-20 month-richText-scalar-join");
        return 1;
    }

    private static int JoinOrder(string grain, string key)
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            grain, key, "notes-id", "notes-key", "JOIN",
            "TEXT", ["VALUES"]);
        var projection = TestSupport.Projection(
            descriptor, "notes-key", "longText");
        var first = TestSupport.AdvancedEnvelope(
            0,
            grain,
            key,
            $"{grain}-report-1",
            "{\"values\":{\"notes-key\":\" Beta \"}}" );
        var second = TestSupport.AdvancedEnvelope(
            1,
            grain,
            key,
            $"{grain}-report-2",
            "{\"values\":{\"notes-key\":\"Alpha\"}}" );
        var typed = Compile(
            descriptor, projection, grain, key, first, second);
        var owner = new ActualAdvancedFieldObservation(
            0,
            "notes-id",
            "notes-key",
            "Notes",
            "TEXT",
            "JOIN",
            2,
            2,
            "\"Beta, Alpha\"",
            ["Beta", "Alpha"],
            TestSupport.Sha($"{grain}-join-field"));
        _ = TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "RequireAdvancedResult",
            descriptor,
            projection,
            typed.Atoms.ToArray(),
            owner,
            ImmutableArray.Create(first, second));
        Console.WriteLine(
            $"PASS P10-EXT-ADV-{(grain == "MONTH" ? "13" : "14")} " +
            $"{grain.ToLowerInvariant()}-join-source-order");
        return 1;
    }

    private static int FullDateYear()
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            "YEAR", "2026", "due", "due", "MIN_DATE",
            "FULL_DATE", ["MIN"]);
        var projection = TestSupport.Projection(
            descriptor, "due", "fullDate");
        var typed = Compile(
            descriptor,
            projection,
            "YEAR",
            "2026",
            TestSupport.AdvancedEnvelope(
                0,
                "YEAR",
                "2026",
                "year-date-1",
                "{\"values\":{\"due\":\"02/08/2026\"}}"),
            TestSupport.AdvancedEnvelope(
                1,
                "YEAR",
                "2026",
                "year-date-2",
                "{\"values\":{\"due\":\"01/08/2026\"}}"));
        TestSupport.Equal("DAY:2026-08-01", Atom(typed, "MIN").CanonicalValue,
            "P10-EXT-ADV-15 full-date-year-min");
        return 1;
    }

    private static int ProjectionMutation()
    {
        var descriptor = TestSupport.AdvancedDescriptor(
            "DAY", "2026-08-05", "choice", "choice", "COUNT",
            "ENUM", ["COUNT"]);
        var projection = TestSupport.Projection(
            descriptor, "choice", "singleSelect", ("A", "Alpha"));
        var mutated = projection with
        {
            Options = projection.Options.SetItem(
                0, projection.Options[0] with { Label = "Alphb" })
        };
        var envelope = TestSupport.AdvancedEnvelope(
            0,
            "DAY",
            "2026-08-05",
            "mutation-report",
            "{\"values\":{\"choice\":\"A\"}}" );
        TestSupport.Throws(
            "P10-EXT-ADV-16 option-one-bit-mutation",
            () => Compile(
                descriptor, mutated, "DAY", "2026-08-05", envelope));
        return 1;
    }

    private static StatisticReconciliationActualExtendedAdvancedTypedPartition
        Compile(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            StatisticReconciliationActualExtendedAdvancedDescriptorProjection
                projection,
            string grain,
            string grainKey,
            params StatisticReconciliationActualExtendedRawSourceEnvelope[]
                envelopes)
        => TestSupport.Compiler().CompileAdvanced(
            TestSupport.Plan(descriptor),
            grain,
            grainKey,
            envelopes.ToImmutableArray(),
            [projection],
            TestSupport.SchemaBinding);

    private static StatisticReconciliationActualRawSummaryAtom Atom(
        StatisticReconciliationActualExtendedAdvancedTypedPartition value,
        string kind)
        => value.Atoms.Single(atom => atom.AtomKind == kind);
}
