using System.IO;
using System.Text.Json;
using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Services;

public sealed class JsonStateStore
{
    private readonly FileLogger _logger;
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlueArchiveLauncher",
        "state.json");

    public JsonStateStore(FileLogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    public async Task<ServerId> ReadSelectedServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return ServerId.Global;
            }

            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<LauncherState>(stream, cancellationToken: cancellationToken);
            return state?.SelectedServer ?? ServerId.Global;
        }
        catch (Exception ex)
        {
            _logger.Error("读取启动器状态失败，使用 Global", ex);
            return ServerId.Global;
        }
    }

    public async Task SaveSelectedServerAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Create(_statePath);
            await JsonSerializer.SerializeAsync(
                stream,
                new LauncherState(serverId),
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("保存启动器状态失败", ex);
        }
    }

    private sealed record LauncherState(ServerId SelectedServer);
}
