using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal interface IStatisticReconciliationActualCrossViewLabelCatalogOwner
{
    Task<IReadOnlyDictionary<string, LabelCatalogItem>> ReadAsync(
        IReadOnlyCollection<string> labelCodes,
        CancellationToken cancellationToken = default);
}

internal sealed class
    StatisticReconciliationActualMongoCrossViewLabelCatalogOwner(
        MongoDbContext context)
    : IStatisticReconciliationActualCrossViewLabelCatalogOwner
{
    public async Task<IReadOnlyDictionary<string, LabelCatalogItem>> ReadAsync(
        IReadOnlyCollection<string> labelCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labelCodes);
        var codes = labelCodes
            .Select(value => StatisticReconciliationActualCanonical.Required(
                value,
                "CROSS_VIEW_LABEL_CODE"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 0)
            return new Dictionary<string, LabelCatalogItem>(
                StringComparer.Ordinal);
        if (codes.Length > 50_000)
            throw new StatisticReconciliationActualObservationException(
                "CROSS_VIEW_LABEL_CODE_LIMIT");
        var rows = await context.Labels.Find(value =>
                codes.Contains(value.Code) && !value.IsDeleted &&
                value.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.GroupBy(value => value.Code, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new StatisticReconciliationActualObservationException(
                "CROSS_VIEW_LABEL_CATALOG_AMBIGUOUS");
        return rows.ToDictionary(value => value.Code, StringComparer.Ordinal);
    }
}
