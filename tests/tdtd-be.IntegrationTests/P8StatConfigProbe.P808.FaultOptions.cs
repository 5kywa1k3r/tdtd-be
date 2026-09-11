namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P808FaultCommandPrefix = "p808-fault-";

    private static readonly string[] P808OperationsFaultPoints =
    [
        "BEFORE_JOB_WRITE",
        "AFTER_JOB_WRITE",
        "BEFORE_RECEIPT_WRITE",
        "AFTER_RECEIPT_WRITE",
        "BEFORE_OUTBOX_WRITE",
        "AFTER_OUTBOX_WRITE",
        "VALIDATION_TRANSIENT",
        "VALIDATION_PERMANENT",
        "CLEANUP_TRANSIENT"
    ];

    private static BackendServerOptions BuildP808BackendOptions()
        => new()
        {
            StatConfigOperationsMaxActiveJobsPerActor = 1,
            StatConfigOperationsFaultCommandIdPrefix =
                P808FaultCommandPrefix,
            StatConfigOperationsFaultPoints = P808OperationsFaultPoints
        };
}

