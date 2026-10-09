using System.IO;
using System.Text.Json;
using BlueArchiveLauncher.Core.Interfaces;
using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Services;

public sealed class JsonCacheStore : ICacheStore
{
    private readonly FileLogger _logger;
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlueArchiveLauncher",
        "cache");

    public JsonCacheStore(FileLogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task SaveDashboardAsync(
        ServerId serverId,
        DashboardSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var serverDirectory = GetServerDirectory(serverId);
            Directory.CreateDirectory(serverDirectory);
            await using var stream = File.Create(Path.Combine(serverDirectory, "dashboard.json"));
            await JsonSerializer.SerializeAsync(
                stream,
                snapshot,
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error($"保存 {serverId} 缓存失败", ex);
        }
    }

    public async Task<DashboardSnapshot?> ReadDashboardAsync(
        ServerId serverId,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(GetServerDirectory(serverId), "dashboard.json");
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<DashboardSnapshot>(
                stream,
                cancellationToken: cancellationToken);
            return SanitizeRedeemCodes(serverId, snapshot);
        }
        catch (Exception ex)
        {
            _logger.Error($"读取 {serverId} 缓存失败", ex);
            return null;
        }
    }

    private string GetServerDirectory(ServerId serverId) =>
        Path.Combine(_cacheRoot, serverId.ToString());

    private static DashboardSnapshot? SanitizeRedeemCodes(
        ServerId serverId,
        DashboardSnapshot? snapshot)
    {
        if (snapshot is null || serverId == ServerId.Global)
        {
            return snapshot;
        }

        var source = snapshot.RedeemCode.SourceUrl;
        var hasRegionalSource = source.Contains("gamekee.com", StringComparison.OrdinalIgnoreCase);
        var hasCachedCodes = snapshot.RedeemCodes.Count > 0 || IsUsableCode(snapshot.RedeemCode.Value);
        if (hasRegionalSource || !hasCachedCodes)
        {
            return snapshot;
        }

        return snapshot with
        {
            RedeemCode = new RedeemCodeInfo(
                "\u5151\u6362\u7801\u6570\u636e",
                "\u6682\u65e0",
                "--",
                "\u5f53\u524d\u670d\u65e0\u53ef\u9a8c\u8bc1\u5151\u6362\u7801"),
            RedeemCodes = Array.Empty<RedeemCodeInfo>()
        };
    }

    private static bool IsUsableCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "--", StringComparison.Ordinal)
        && !string.Equals(value, "----------", StringComparison.Ordinal)
        && !string.Equals(value, "\u6682\u65e0", StringComparison.Ordinal);
}
