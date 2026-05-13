namespace tdtd_be.Services.Notifications;

public interface INotificationDueScanJobService
{
    Task ScanDueNotificationsAsync(CancellationToken ct = default);
}
