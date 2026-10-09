using BlueArchiveLauncher.Core.Interfaces;
using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Services;

public sealed class SourceManager
{
    private readonly IReadOnlyDictionary<string, IDataSource> _sources;
    private readonly FileLogger _logger;

    public SourceManager(IEnumerable<IDataSource> sources, FileLogger logger)
    {
        _logger = logger;
        _sources = sources.ToDictionary(
            source => source.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public Task<DashboardSnapshot> GetDashboardAsync(
        ServerContext context,
        CancellationToken cancellationToken)
    {
        if (!_sources.TryGetValue(context.Definition.DefaultDataSource, out var source))
        {
            throw new InvalidOperationException(
                $"未找到数据源 {context.Definition.DefaultDataSource}，服务器 {context.Definition.Id} 无法加载。");
        }

        _logger.Info($"开始读取 {context.Definition.Id} 数据，数据源：{source.Name}");
        return source.GetDashboardAsync(context.Definition.Id, cancellationToken);
    }
}
