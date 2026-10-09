using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Interfaces;

public interface ICacheStore
{
    Task SaveDashboardAsync(ServerId serverId, DashboardSnapshot snapshot, CancellationToken cancellationToken);

    Task<DashboardSnapshot?> ReadDashboardAsync(ServerId serverId, CancellationToken cancellationToken);
}
