namespace BlueArchiveLauncher.Core.Models;

public sealed record DashboardSnapshot(
    ServerId ServerId,
    DateTimeOffset RetrievedAt,
    string SourceName,
    BannerInfo Banner,
    RaidInfo Raid,
    IReadOnlyList<AnnouncementInfo> Announcements,
    IReadOnlyList<CharacterRecommendation> RecommendedCharacters,
    RedeemCodeInfo RedeemCode)
{
    public IReadOnlyList<RedeemCodeInfo> RedeemCodes { get; init; } =
        Array.Empty<RedeemCodeInfo>();
}

public sealed record BannerInfo(
    string Name,
    string Initials,
    string Type,
    string Window,
    string Description);

public sealed record RaidInfo(
    string Name,
    string Window,
    string Armor,
    string Difficulty,
    string Hint)
{
    public IReadOnlyList<CharacterRecommendation> Recommendations { get; init; } =
        Array.Empty<CharacterRecommendation>();
}

public sealed record AnnouncementInfo(
    string Kind,
    string Title,
    string DateLabel);

public sealed record CharacterRecommendation(
    string Name,
    string Initials,
    string Role,
    string ScoreLabel);

public sealed record RedeemCodeInfo(
    string Name,
    string Value,
    string Expiry,
    string Status)
{
    public string Reward { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
}

public sealed record GameKeeRaidData(
    ServerId ServerId,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    string BossName,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<string> RecommendationNames,
    RedeemCodeInfo RedeemCode);
