using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class CrossViewHardeningCases
{
    internal static void Run()
    {
        var direct = Fixtures.DirectLabel();
        Incomplete(Prove(direct with
        {
            Plan = direct.Plan with { ScopeType = "ROOT" }
        }), "API_ROOT_SCOPE");
        Incomplete(Prove(direct with
        {
            Plan = direct.Plan with
            {
                DirectPublicationGenerationSha256 = H("one-bit")
            }
        }), "DIRECT_PUBLICATION_SHA");
        Incomplete(Prove(direct with
        {
            Plan = direct.Plan with
            {
                DirectSourceRevision =
                    direct.Plan.DirectSourceRevision!.Value + 1
            }
        }), "DIRECT_SOURCE_REVISION");
        Incomplete(Prove(direct with
        {
            Plan = direct.Plan with
            {
                DirectPublicationRevision =
                    direct.Plan.DirectPublicationRevision!.Value + 1
            }
        }), "DIRECT_PUBLICATION_REVISION");
        Incomplete(Prove(direct with
        {
            Plan = direct.Plan with { ExportResultId = "mixed-owner" }
        }), "DIRECT_RESULT_ID");

        var baseApi = direct.Base.Api!;
        Incomplete(Prove(direct with
        {
            Base = direct.Base with
            {
                Api = baseApi with { WorkId = "other-work" }
            }
        }), "API_BASE_TARGET");
        Incomplete(Prove(direct with
        {
            Base = direct.Base with
            {
                Export = direct.Base.Export with { ScopeId = "other-scope" }
            }
        }), "EXPORT_BASE_TARGET");

        var labelBlockFilter = Canonical(new
        {
            blockId = "ignored-by-production-label-api",
            periodInstanceKey = Fixtures.Period
        });
        Incomplete(Prove(direct with
        {
            Plan = direct.Plan with
            {
                CanonicalApiFilterJson = labelBlockFilter,
                ApiFilterSha256 = RawSha(labelBlockFilter)
            }
        }), "LABEL_BLOCK_FILTER");

        var basic = Fixtures.Basic(false);
        var malformedBasicFilter = Canonical(new { q = 1 });
        Incomplete(Prove(basic with
        {
            Plan = basic.Plan with
            {
                CanonicalApiFilterJson = malformedBasicFilter,
                ApiFilterSha256 = RawSha(malformedBasicFilter)
            }
        }), "BASIC_FILTER_TYPE");
        Incomplete(Prove(basic with
        {
            Plan = basic.Plan with { ExportResultId = "mixed-snapshot" }
        }), "BASIC_RESULT_ID");
        Incomplete(Prove(basic with
        {
            Plan = basic.Plan with
            {
                BasicGeneration = basic.Plan.BasicGeneration! with
                {
                    ConfigRevision =
                        basic.Plan.BasicGeneration.ConfigRevision + 1
                }
            }
        }), "BASIC_GENERATION_PREIMAGE");

        var diff = Fixtures.DiffEmpty();
        Incomplete(Prove(diff with
        {
            Plan = diff.Plan with { ApiGenerationSha256 = H("mixed-diff") }
        }), "DIFF_RESULT_SHA");

        var primitive = direct.Base.Export.Rows[0] with
        {
            CanonicalSourceRowJson = "1"
        };
        Incomplete(Prove(direct with
        {
            Base = direct.Base with
            {
                Export = direct.Base.Export with { Rows = [primitive] }
            }
        }), "PRIMITIVE_EXPORT_SOURCE");
        Incomplete(Prove(direct with
        {
            Actual = direct.Actual with
            {
                SchemaVersion =
                    StatisticReconciliationActualCrossViewParityV2Schemas.Proof
            }
        }), "ACTUAL_SCHEMA_REUSE");
    }

    private static StatisticReconciliationActualCrossViewParityV2Proof Prove(
        Fixture fixture)
        => new StatisticReconciliationActualCrossViewParityV2().Prove(
            fixture.Plan,
            fixture.Base,
            fixture.Actual);

    private static void Incomplete(
        StatisticReconciliationActualCrossViewParityV2Proof proof,
        string reason)
    {
        Require(!proof.Complete, $"{reason}_FALSE_GREEN");
        Require(proof.FailureCode !=
                StatisticReconciliationActualCrossViewParityV2Failures.None,
            $"{reason}_FAILURE_REQUIRED");
    }
}
