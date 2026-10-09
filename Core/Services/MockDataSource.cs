using BlueArchiveLauncher.Core.Interfaces;
using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Services;

public sealed class MockDataSource : IDataSource
{
    private readonly FileLogger _logger;

    public MockDataSource(FileLogger logger)
    {
        _logger = logger;
    }

    public string Name => "本地演示数据（非实时）";

    public async Task<DashboardSnapshot> GetDashboardAsync(
        ServerId serverId,
        CancellationToken cancellationToken)
    {
        await Task.Delay(180, cancellationToken);
        var snapshot = BuildPending(serverId);
        _logger.Info($"本地演示数据加载完成：{serverId}");
        return snapshot with { RetrievedAt = DateTimeOffset.Now };
    }

    private static DashboardSnapshot BuildPending(ServerId serverId) =>
        new(
            serverId,
            DateTimeOffset.Now,
            "本地演示数据（非实时）",
            new BannerInfo(
                "实时卡池待接入",
                "--",
                "未接入实时源",
                "暂不可用",
                "当前不展示未经核验的卡池信息。"),
            new RaidInfo(
                "实时总力战待接入",
                "暂不可用",
                "待核验",
                "待核验",
                "请以游戏内和 GameKee 当前页面为准。"),
            new[]
            {
                new AnnouncementInfo("数据源", "实时活动与公告尚未接入", "等待接入")
            },
            new[]
            {
                new CharacterRecommendation("待接入", "--", "实时角色推荐", "待核验")
            },
            new RedeemCodeInfo(
                "实时兑换码待接入",
                "暂不可用",
                "暂不可用",
                "未接入实时来源"));
}
