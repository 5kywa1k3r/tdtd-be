using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.FileProviders;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Net;
using System.Text;
using tdtd_be.Common.Cache;
using tdtd_be.Common.Middleware;
using tdtd_be.Common.Time;
using tdtd_be.DashboardModel.Services;
using tdtd_be.Data;
using tdtd_be.Data.Indexes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Hubs;
using tdtd_be.Jobs;
using tdtd_be.Models;
using tdtd_be.Options;
using tdtd_be.OpenApi;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.EvaluationTemplates;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Aggregate;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Handover;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Lookups;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Review;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkAssignments.SummaryTokens;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.Nq57;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.Production;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.SummaryReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Services.Works;
using tdtd_be.Uploads;
using tusdotnet.Interfaces;
using tusdotnet.Stores;
using static tdtd_be.Services.Works.WorkServices;

var builder = WebApplication.CreateBuilder(args);

if (OperatingSystem.IsWindows()
    && !builder.Configuration.GetValue<bool>("Logging:UseWindowsEventLog"))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

// ================== config ==================
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
builder.Services.Configure<WorkAssignmentScopeOptions>(builder.Configuration.GetSection("WorkAssignmentScope"));
builder.Services.Configure<ForwardedHeadersOptions>(opt =>
{
    opt.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    // Production runs behind nginx/Docker. The proxy address is not stable enough to whitelist here.
    opt.KnownNetworks.Clear();
    opt.KnownProxies.Clear();
});

// ================== mongo ==================
builder.Services.AddSingleton<MongoDbContext>();
var fixedUtcRaw = builder.Configuration[FixedAppTimeService.ConfigurationKey];
if (fixedUtcRaw is null)
{
    builder.Services.AddSingleton<IAppTimeService, AppTimeService>();
}
else
{
    if (!builder.Environment.IsEnvironment("Testing") ||
        !DateTime.TryParseExact(fixedUtcRaw, "O",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var fixedUtc) || fixedUtc.Kind != DateTimeKind.Utc)
        throw new InvalidOperationException("APP_TIME_TESTING_FIXED_UTC_INVALID");
    builder.Services.AddSingleton<IAppTimeService>(
        new FixedAppTimeService(fixedUtc));
}

// ================== Hangfire ==================
var mongoConnectionString = builder.Configuration["Mongo:ConnectionString"]
    ?? throw new Exception("Mongo:ConnectionString is missing");
var mongoDatabaseName = builder.Configuration["Mongo:Database"]
    ?? throw new Exception("Mongo:Database is missing");

var hangfirePrefix = builder.Configuration["Hangfire:Prefix"] ?? "hangfire";
var hangfireServerEnabled = builder.Configuration.GetValue<bool?>("Hangfire:ServerEnabled") ?? true;
var hangfireDashboardEnabled = builder.Configuration.GetValue<bool?>("Hangfire:DashboardEnabled") ?? true;
var hangfireRecurringRegistrationEnabled =
    builder.Configuration.GetValue<bool?>("Hangfire:RecurringRegistrationEnabled") ?? true;
var schedulePollingSeconds = Math.Clamp(
    builder.Configuration.GetValue<int?>("Hangfire:SchedulePollingSeconds") ?? 15,
    5,
    300);
var invisibilityMinutes = Math.Clamp(
    builder.Configuration.GetValue<int?>("Hangfire:InvisibilityTimeoutMinutes") ?? 5,
    1,
    120);
var queuedJobsStrategyRaw = builder.Configuration["Hangfire:CheckQueuedJobsStrategy"] ?? "Poll";

if (!Enum.TryParse<CheckQueuedJobsStrategy>(queuedJobsStrategyRaw, true, out var queuedJobsStrategy))
    queuedJobsStrategy = CheckQueuedJobsStrategy.Poll;

var mongoUrlBuilder = new MongoUrlBuilder(mongoConnectionString) { DatabaseName = mongoDatabaseName };
var hangfireMongoClient = new MongoClient(mongoUrlBuilder.ToMongoUrl());

var migrationOptions = new MongoMigrationOptions
{
    MigrationStrategy = new MigrateMongoMigrationStrategy(),
    BackupStrategy = new CollectionMongoBackupStrategy()
};

var storageOptions = new MongoStorageOptions
{
    Prefix = hangfirePrefix,
    CheckQueuedJobsStrategy = queuedJobsStrategy,
    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(invisibilityMinutes),
    MigrationOptions = migrationOptions
};

var hangfireSucceededExpirationDays = Math.Clamp(
    builder.Configuration.GetValue<int?>("HangfireHistoryArchive:SucceededExpirationDays") ?? 15,
    2,
    60);
GlobalJobFilters.Filters.Add(
    new HangfireSucceededJobExpirationFilter(TimeSpan.FromDays(hangfireSucceededExpirationDays)));

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseMongoStorage(hangfireMongoClient, mongoDatabaseName, storageOptions));

if (hangfireServerEnabled)
{
    builder.Services.AddHangfireServer(options =>
    {
        options.ServerName = $"tdtd-be:{Environment.MachineName}";
        options.WorkerCount = Math.Max(1, Environment.ProcessorCount);
        options.Queues = new[] { "default" };
        options.SchedulePollingInterval = TimeSpan.FromSeconds(schedulePollingSeconds);
    });
}

// ================== MinIO client + TUS ==================
builder.Services.AddScoped<UploadFinalizeService>();
builder.Services.AddSingleton<UploadTokenService>();
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    var rawEndpoint = cfg.GetValue<string>("Minio:Endpoint") ?? "localhost:9000";
    var accessKey = cfg.GetValue<string>("Minio:AccessKey") ?? throw new Exception("Minio:AccessKey missing");
    var secretKey = cfg.GetValue<string>("Minio:SecretKey") ?? throw new Exception("Minio:SecretKey missing");
    var secure = cfg.GetValue<bool>("Minio:Secure");

    var ep = rawEndpoint.Trim()
        .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
        .Replace("https://", "", StringComparison.OrdinalIgnoreCase);
    var slash = ep.IndexOf('/');
    if (slash >= 0) ep = ep[..slash];

    var parts = ep.Split(':', 2);
    var host = parts[0];
    var port = (parts.Length == 2 && int.TryParse(parts[1], out var p)) ? p : (secure ? 443 : 80);

    Console.WriteLine($"[MinIO] endpoint={host}:{port} secure={secure} accessKey={accessKey} secretLen={secretKey.Length}");

    return new MinioClient()
        .WithEndpoint(host, port)
        .WithCredentials(accessKey, secretKey)
        .WithSSL(secure)
        .WithRegion("us-east-1")
        .Build();
});
builder.Services.AddSingleton<IMinioObjectDeleter, MinioObjectDeleter>();
builder.Services.AddScoped<IMinioFileDocCleanupJob, MinioFileDocCleanupJob>();
builder.Services.AddScoped<ITusTempCleanupJob, TusTempCleanupJob>();
builder.Services.AddScoped<IHangfireHistoryArchiveJob, HangfireHistoryArchiveJob>();
builder.Services.AddScoped<NonOverlappingRecurringJobRunner>();

// ================== core services ==================
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<tdtd_be.Common.Auth.MeAccessor>();
builder.Services.AddScoped<tdtd_be.Common.Auth.UnitTreeHelper>();
builder.Services.AddSingleton<ManagementAccountConvention>();
builder.Services.AddScoped<IManagementAccountProvisioner, ManagementAccountProvisioner>();
builder.Services.AddScoped<IUnitTypeAdminService, UnitTypeAdminService>();
builder.Services.AddScoped<IPositionAdminService, PositionAdminService>();
builder.Services.AddScoped<IUnitSelectionService, UnitSelectionService>();
builder.Services.AddScoped<IUnitService, UnitService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IAdminImportService, AdminImportService>();
builder.Services.AddScoped<IDynamicExcelService, DynamicExcelService>();
builder.Services.AddScoped<
    IDynamicFormStatisticConfigCommandService,
    DynamicFormStatisticConfigCommandService>();
builder.Services.AddScoped<IDynamicFormService, DynamicFormService>();
builder.Services.AddScoped<IDynamicFormCloneRequestService, DynamicFormCloneRequestService>();
builder.Services.AddScoped<IDynamicFlowTemplateService, DynamicFlowTemplateService>();
builder.Services.AddScoped<IDynamicFlowDefinitionTransactionRunner, DynamicFlowDefinitionTransactionRunner>();
builder.Services.AddSingleton<IDynamicFlowDefinitionFaultInjector, DynamicFlowDefinitionFaultInjector>();
builder.Services.AddHealthChecks().AddCheck<DynamicFlowDefinitionTransactionHealthCheck>(
    "dynamic-flow-definition-transactions",
    tags: new[] { "ready", "dynamic-flow" });
builder.Services.AddScoped<IDynamicFlowRuntimeService, DynamicFlowRuntimeService>();
builder.Services.AddScoped<IDynamicFlowRuntimeReadService, DynamicFlowRuntimeReadService>();
builder.Services.AddScoped<IDynamicFlowPeriodicService, DynamicFlowPeriodicService>();
builder.Services.AddScoped<IDynamicFlowRuntimePersistence, DynamicFlowRuntimePersistence>();
builder.Services.AddSingleton<IDynamicFlowRuntimeActivationPolicy, DynamicFlowRuntimeActivationPolicy>();
builder.Services.AddSingleton<IDynamicFlowRuntimeFaultInjector, DynamicFlowRuntimeFaultInjector>();
builder.Services.AddScoped<IDynamicFlowRuntimeMaterializer, DynamicFlowRuntimeMaterializer>();
builder.Services.AddSingleton<
    IDynamicFlowRuntimeStateProjectionFaultInjector,
    DynamicFlowRuntimeStateProjectionFaultInjector>();
builder.Services.AddScoped<IDynamicFlowRuntimeStateProjector, DynamicFlowRuntimeStateProjector>();
builder.Services.AddSingleton<IDynamicFlowPolicyEvaluator, DynamicFlowPolicyEvaluator>();
builder.Services.AddScoped<ILabelService, LabelService>();
builder.Services.AddScoped<
    ILabelConfigCommandService,
    LabelConfigCommandService>();
builder.Services.AddScoped<IStatConfigTransactionRunner, StatConfigTransactionRunner>();
builder.Services.AddScoped<IStatConfigOperationsService, StatConfigOperationsService>();
builder.Services.AddScoped<IStatConfigBundleService, StatConfigBundleService>();
builder.Services.AddSingleton<
    IStatConfigOperationsFaultInjector,
    StatConfigOperationsFaultInjector>();
builder.Services.AddSingleton<IStatRunCandidateActivation, StatRunCapabilityActivation>();
builder.Services.AddSingleton<
    IStatisticReconciliationCandidateActivation,
    StatisticReconciliationCapabilityActivation>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<P9DirectResultService>();
builder.Services.AddScoped<IP9DirectResultService>(services =>
    services.GetRequiredService<P9DirectResultService>());
builder.Services.AddScoped<IStatisticReconciliationActualPinnedDirectResultService>(services =>
    services.GetRequiredService<P9DirectResultService>());
builder.Services.AddScoped<StatRunService>();
builder.Services.AddScoped<IStatRunService>(services =>
    services.GetRequiredService<StatRunService>());
builder.Services.AddScoped<IStatRunFoundationWorkerStateOwner>(services =>
    services.GetRequiredService<StatRunService>());
builder.Services.AddScoped<IStatRunFoundationDirectProjectionOwner,
    StatRunFoundationDirectProjectionOwner>();
builder.Services.AddScoped<IStatRunFoundationWorker, StatRunFoundationWorker>();
builder.Services.AddScoped<StatRunExportService>();
builder.Services.AddScoped<IStatRunExportService>(sp =>
    sp.GetRequiredService<StatRunExportService>());
builder.Services.AddScoped<
    IStatisticReconciliationRunService,
    StatisticReconciliationRunService>();
builder.Services.AddSingleton<
    IStatisticReconciliationExpectedLedgerCompiler,
    StatisticReconciliationExpectedLedgerCompiler>();
builder.Services.AddSingleton<
    IStatisticReconciliationExpectedMetricIdentityCompiler,
    StatisticReconciliationExpectedMetricIdentityCompiler>();
builder.Services.AddSingleton<
    IStatisticReconciliationExpectedSourcePlanner,
    StatisticReconciliationExpectedSourcePlanner>();
builder.Services.AddSingleton<
    IStatisticReconciliationExpectedTypedCompiler,
    StatisticReconciliationExpectedTypedCompiler>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedObservationBackend,
    StatisticReconciliationExpectedObservationMongoBackend>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedObservationStore,
    StatisticReconciliationExpectedObservationStore>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedAuthoritativeSnapshotReader,
    StatisticReconciliationExpectedMongoSnapshotReader>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedProjectionInputReader,
    StatisticReconciliationExpectedMongoProjectionInputReader>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedGenerationBindingReader,
    StatisticReconciliationExpectedMongoGenerationBindingReader>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedAuthoritativeLifecycleProjectionReader,
    StatisticReconciliationExpectedMongoAuthoritativeLifecycleProjectionReader>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedGenerationOwner,
    StatisticReconciliationExpectedGenerationOwner>();
builder.Services.AddScoped<
    IStatisticReconciliationExpectedAuthoritativeCurrentValidator,
    StatisticReconciliationExpectedAuthoritativeCurrentValidator>();
builder.Services.AddSingleton<StatisticReconciliationFinalVerdictEvaluator>();
builder.Services.AddScoped<
    IStatisticReconciliationReviewBackend,
    StatisticReconciliationReviewMongoBackend>();
builder.Services.AddScoped<StatisticReconciliationFinalVerdictPublisher>();
builder.Services.AddScoped<IStatisticReconciliationTrustedFinalizer,
    StatisticReconciliationTrustedFinalizer>();
builder.Services.AddScoped<
    IStatisticReconciliationRecheckReviewSupersessionAppender,
    StatisticReconciliationRecheckReviewSupersessionAppender>();
builder.Services.AddScoped<
    IStatisticReconciliationTrustedRecheckFinalizer,
    StatisticReconciliationTrustedRecheckFinalizer>();
builder.Services.AddSingleton<StatisticReconciliationSummaryReconciler>();
builder.Services.AddSingleton<StatisticReconciliationLifecycleEvaluator>();
builder.Services.AddScoped<
    IStatisticReconciliationLifecycleObservationStore,
    StatisticReconciliationLifecycleMongoObservationStore>();
builder.Services.AddScoped<
    IStatisticReconciliationIndependentReviewBackend,
    StatisticReconciliationIndependentReviewMongoBackend>();
builder.Services.AddScoped<StatisticReconciliationIndependentReviewService>();
builder.Services.AddScoped<
    IStatisticReconciliationIndependentReviewCandidateGate,
    StatisticReconciliationIndependentReviewCandidateGate>();
builder.Services.AddScoped<
    IStatisticReconciliationCurrentReviewValidator,
    StatisticReconciliationCurrentReviewValidator>();
builder.Services.AddScoped<
    IStatisticReconciliationIndependentReviewOwner,
    StatisticReconciliationIndependentReviewOwner>();
builder.Services.AddScoped<IStatisticReconciliationEvidenceStore,
    StatisticReconciliationEvidenceMongoStore>();
builder.Services.AddScoped<IStatisticReconciliationEvidenceCandidateGate,
    StatisticReconciliationEvidenceCandidateGate>();
builder.Services.AddScoped<IStatisticReconciliationEvidenceOwner,
    StatisticReconciliationEvidenceOwner>();
builder.Services.AddScoped<ILabelEnumCatalogService, LabelEnumCatalogService>();
builder.Services.AddSingleton<WorkAssignmentTargetScopePolicy>();

builder.Services.AddScoped<DocRoleReadModelProjectionService>();
builder.Services.AddScoped<IDocRoleReadModelProjectionRetryJobService, DocRoleReadModelProjectionRetryJobService>();
builder.Services.AddScoped<IDocRoleReadModelProjectionService, DocRoleReadModelProjectionResilientService>();
builder.Services.AddScoped<IDocRoleReadModelFreshnessService, DocRoleReadModelFreshnessService>();
builder.Services.AddScoped<IDocRoleReadModelRepairService, DocRoleReadModelRepairService>();
builder.Services.AddScoped<IDocRoleReadModelDriftService, DocRoleReadModelDriftService>();
builder.Services.AddScoped<IWorkStatusOperationLogService, WorkStatusOperationLogService>();
builder.Services.AddScoped<IUserActionLogService, UserActionLogService>();
builder.Services.AddScoped<IJobRunManagementService, JobRunManagementService>();
builder.Services.AddScoped<IDocRoleService, DocRoleService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<tdtd_be.Services.WorkInbox.WorkInboxService>();
builder.Services.AddScoped<INotificationDueScanJobService, tdtd_be.Services.WorkInbox.WorkInboxNotificationJob>();

builder.Services.AddScoped<IWorkCodeGenerator, WorkCodeGenerator>();
builder.Services.AddScoped<IWorkService, WorkService>();
builder.Services.AddScoped<IWorkHistoryService, WorkHistoryService>();
builder.Services.AddScoped<IWorkPermissionService, WorkPermissionService>();
builder.Services.AddScoped<IWorkDocumentPermissionService, WorkDocumentPermissionService>();

builder.Services.AddScoped<IWorkAssignmentLookupService, WorkAssignmentLookupService>();
builder.Services.AddScoped<IDynamicExcelLookupService, DynamicExcelLookupService>();
builder.Services.AddScoped<IWorkAssignmentTemplateResolver, WorkAssignmentTemplateResolver>();
builder.Services.AddScoped<IWorkAssignmentDataGuardService, WorkAssignmentDataGuardService>();
builder.Services.AddScoped<IWorkAssignmentTreeService, WorkAssignmentTreeService>();
builder.Services.AddScoped<IWorkTemplateAssigneeBindingService, WorkTemplateAssigneeBindingService>();
builder.Services.AddScoped<IWorkAssignmentService, WorkAssignmentService>();
builder.Services.AddScoped<IWorkAssignmentHandoverService, WorkAssignmentHandoverService>();

builder.Services.AddScoped<IWorkAssignmentReportService, WorkAssignmentReportService>();
builder.Services.AddScoped<WorkReportPayloadService>();
builder.Services.AddScoped<IWorkReportPayloadReader>(sp => sp.GetRequiredService<WorkReportPayloadService>());
builder.Services.AddScoped<IWorkReportPayloadWriter>(sp => sp.GetRequiredService<WorkReportPayloadService>());
builder.Services.AddScoped<IWorkReportPayloadDiagnosticsService, WorkReportPayloadDiagnosticsService>();
builder.Services.AddScoped<IWorkReportLabelStatisticsService, WorkReportLabelStatisticsService>();
builder.Services.AddScoped<IWorkReportTableStatisticsService, WorkReportTableStatisticsService>();
builder.Services.AddScoped<IWorkReportFieldStatisticsService, WorkReportFieldStatisticsService>();
builder.Services.AddScoped<IWorkReportStatisticRebuildJobService, WorkReportStatisticRebuildJobService>();
builder.Services.AddScoped<IWorkReportStatisticDiffService, WorkReportStatisticDiffService>();
builder.Services.AddScoped<StatRunDirectProjectionService>();
builder.Services.AddScoped<IStatRunDirectProjectionService>(sp =>
    sp.GetRequiredService<StatRunDirectProjectionService>());
builder.Services.AddStatisticReconciliationActualCapture(builder.Configuration);
builder.Services.AddStatisticReconciliationProductionWorker(builder.Configuration);
builder.Services.AddScoped<IWorkReportAggregateDependentRecoveryService, WorkReportAggregateDependentRecoveryService>();
builder.Services.AddScoped<IWorkAssignmentReportSectionProjectionService, WorkAssignmentReportSectionProjectionService>();
builder.Services.AddScoped<IWorkReportLifecycleProjectionReconciler, WorkReportLifecycleProjectionReconciler>();
builder.Services.AddScoped<
    IDynamicFlowMappingOutboxReconciler,
    DynamicFlowMappingOutboxReconciler>();
builder.Services.AddScoped<IWorkReportLifecycleBusinessLogProjector, WorkReportLifecycleBusinessLogProjector>();
builder.Services.AddSingleton<IWorkReportLifecycleProjectionFaultInjector, WorkReportLifecycleProjectionFaultInjector>();
builder.Services.AddSingleton<
    IDynamicFlowMappingReconcileFaultInjector,
    DynamicFlowMappingReconcileFaultInjector>();
builder.Services.AddSingleton<
    IDynamicFlowMappingApplyFaultInjector,
    DynamicFlowMappingApplyFaultInjector>();
builder.Services.AddScoped<IWorkReportLifecycleSeriesLockService, WorkReportLifecycleSeriesLockService>();

builder.Services.AddScoped<IWorkAssignmentProgressService, WorkAssignmentProgressService>();
builder.Services.AddScoped<WorkCompletionWorkflowService>();
builder.Services.AddScoped<WorkExecutionReconcileJob>();
builder.Services.AddScoped<IWorkAssignmentReviewService, WorkAssignmentReviewService>();
builder.Services.AddScoped<IAggregateTableService, AggregateTableService>();
builder.Services.AddScoped<IWorkAssignmentBasicSummaryService, WorkAssignmentBasicSummaryService>();
builder.Services.AddScoped<IWorkAssignmentAdvancedSummaryConfigService, WorkAssignmentAdvancedSummaryConfigService>();
builder.Services.AddScoped<IWorkAssignmentAdvancedSummaryHierarchyService, WorkAssignmentAdvancedSummaryHierarchyService>();
builder.Services.AddScoped<IWorkAssignmentAdvancedSummaryDirtyService, WorkAssignmentAdvancedSummaryDirtyService>();
builder.Services.AddScoped<IWorkSummaryTokenService, WorkSummaryTokenService>();

builder.Services.AddScoped<IWorkAssignmentRuntimeMaterializeService, WorkAssignmentRuntimeMaterializeService>();
builder.Services.AddScoped<IWorkAssignmentQueueService, WorkAssignmentQueueService>();
builder.Services.AddScoped<IWorkAssignmentQueueJobService, WorkAssignmentQueueJobService>();
builder.Services.AddScoped<IWorkAssignmentStatusSyncService, WorkAssignmentStatusSyncService>();
builder.Services.AddScoped<IWorkAssignmentStatusRepairService, WorkAssignmentStatusRepairService>();

builder.Services.AddScoped<IWorkAssignmentMaterializeJobService, WorkAssignmentMaterializeJobService>();
builder.Services.AddScoped<IEvaluationTemplateService, EvaluationTemplateService>();
builder.Services.AddScoped<IDashboardQueryService, DashboardQueryService>();
builder.Services.AddScoped<IDashboardOverviewService, DashboardOverviewService>();
builder.Services.AddScoped<IDashboardMindMapQueryService, DashboardMindMapQueryService>();
builder.Services.AddHttpClient<INq57IntegrationService, Nq57IntegrationService>();

// ================== redis (cache) ==================
var redisEnabled = builder.Configuration.GetValue<bool>("Redis:Enabled");

if (redisEnabled)
{
    var cs = builder.Configuration["Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(cs))
        throw new Exception("Redis:ConnectionString is missing");

    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect($"{cs},abortConnect=false")
    );

    builder.Services.AddSingleton<IUserSessionCache, RedisUserCache>();
}
else
{
    builder.Services.AddSingleton<IUserSessionCache, InMemoryUserSessionCache>();
}
builder.Services.AddSingleton<RedisDashboardCache>(sp =>
    new RedisDashboardCache(
        sp.GetService<IConnectionMultiplexer>(),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddTransient<MeContextRedisMiddleware>();

// ================== ApiExceptionMiddleware ==================
builder.Services.AddScoped<ApiExceptionMiddleware>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

// ================== Tus temp path ==================
var tusTempPath = builder.Configuration["Tus:TempPath"] ?? "App_Data/tus";
Directory.CreateDirectory(tusTempPath);

builder.Services.AddSingleton<TusDiskStore>(_ => new TusDiskStore(tusTempPath));
builder.Services.AddSingleton<ITusStore>(sp => sp.GetRequiredService<TusDiskStore>());
builder.Services.AddSingleton<ITusTerminationStore>(sp => sp.GetRequiredService<TusDiskStore>());

// ================== CORS ==================
var corsAllowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? Array.Empty<string>();

if (corsAllowedOrigins.Length == 0)
{
    corsAllowedOrigins =
    [
        "http://localhost:5173",
        "https://localhost:5173",
        "http://127.0.0.1:5173",
        "https://127.0.0.1:5173"
    ];
}

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("fe", p =>
    {
        p.WithOrigins(corsAllowedOrigins)
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()
         .WithExposedHeaders(
            "Location",
            "Upload-Offset",
            "Tus-Resumable",
            "Upload-Length"
         );
    });
});

// ================== auth ==================
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(10)
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs/notifications"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ================== swagger ==================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "TDTD API", Version = "v1" });
    c.SchemaFilter<DeprecatedSchemaPropertyFilter>();
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseCors("fe");

var frontendStaticFilesEnabled = false;
var frontendStaticIndexPath = "";
if (app.Configuration.GetValue<bool>("Frontend:Enabled"))
{
    var frontendStaticRootConfig = app.Configuration["Frontend:StaticRoot"] ?? "../wwwroot";
    var frontendStaticRoot = Path.GetFullPath(
        Path.IsPathRooted(frontendStaticRootConfig)
            ? frontendStaticRootConfig
            : Path.Combine(app.Environment.ContentRootPath, frontendStaticRootConfig));
    frontendStaticIndexPath = Path.Combine(frontendStaticRoot, "index.html");

    if (File.Exists(frontendStaticIndexPath))
    {
        var frontendFileProvider = new PhysicalFileProvider(frontendStaticRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFileProvider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFileProvider });
        frontendStaticFilesEnabled = true;
        app.Logger.LogInformation("Serving frontend static files from {FrontendStaticRoot}", frontendStaticRoot);
    }
    else
    {
        app.Logger.LogWarning(
            "Frontend static files are enabled but index.html was not found at {FrontendStaticIndexPath}",
            frontendStaticIndexPath);
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    using var scope = app.Services.CreateScope();
    var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var bucket = cfg.GetValue<string>("Minio:Bucket") ?? "tdtd-attachments";

    try
    {
        var ok = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket));
        app.Logger.LogInformation("[MinIO] BucketExists({Bucket}) => {Ok}", bucket, ok);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "[MinIO] sanity failed");
    }
}

app.UseAuthentication();
app.UseMiddleware<MeContextRedisMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseAuthorization();

if (hangfireDashboardEnabled)
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new LocalRequestsOnlyAuthorizationFilter() },
        DisplayStorageConnectionString = false,
        DashboardTitle = "TDTD Hangfire"
    });
}

app.MapControllers();
app.MapHealthChecks("/health/ready");
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapTusUploads();
if (frontendStaticFilesEnabled)
{
    app.MapFallback(async context =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") ||
            path.StartsWithSegments("/hubs") ||
            path.StartsWithSegments("/hangfire") ||
            path.StartsWithSegments("/swagger"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(frontendStaticIndexPath);
    });
}

using (var scope = app.Services.CreateScope())
{
    var skipMongoIndexInitializationForTesting =
        string.Equals(
            app.Environment.EnvironmentName,
            "Testing",
            StringComparison.Ordinal) &&
        app.Configuration.GetValue<bool>(
            "Mongo:TestingSkipIndexInitialization");
    if (skipMongoIndexInitializationForTesting)
    {
        app.Logger.LogWarning(
            "Skipping Mongo index initialization for the Testing environment.");
    }
    else
    {
        var ctx = scope.ServiceProvider
            .GetRequiredService<MongoDbContext>();
        var mongoOpt = scope.ServiceProvider
            .GetRequiredService<
                Microsoft.Extensions.Options.IOptions<MongoOptions>>()
            .Value;
        await MongoIndexInitializer.EnsureAsync(
            ctx.Db,
            mongoOpt);
        await WorkCompletionIndexes.EnsureAsync(ctx.Db);
    }

    if (hangfireRecurringRegistrationEnabled)
    {
        JobStorage.Current =
            scope.ServiceProvider.GetRequiredService<JobStorage>();
        HangfireRecurringJobRegistrar.Register(
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            scope.ServiceProvider.GetRequiredService<IAppTimeService>());
    }
}

app.Run();

public sealed class LocalRequestsOnlyAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var remoteIp = httpContext.Connection.RemoteIpAddress;
        var localIp = httpContext.Connection.LocalIpAddress;

        if (remoteIp is null)
            return false;

        if (IPAddress.IsLoopback(remoteIp))
            return true;

        if (localIp is not null && remoteIp.Equals(localIp))
            return true;

        return remoteIp.ToString() is "::1" or "127.0.0.1";
    }
}
