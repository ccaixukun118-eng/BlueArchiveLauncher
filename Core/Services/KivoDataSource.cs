using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlueArchiveLauncher.Core.Interfaces;
using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Services;

public sealed class KivoDataSource : IDataSource
{
    private const string ApiBaseAddress = "https://api.kivo.wiki/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FileLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly GameKeeBackupAdapter _gameKeeBackupAdapter;
    private readonly PublicRedeemCodeProvider _publicRedeemCodeProvider;

    public KivoDataSource(
        FileLogger logger,
        HttpClient httpClient,
        GameKeeBackupAdapter gameKeeBackupAdapter,
        PublicRedeemCodeProvider publicRedeemCodeProvider)
    {
        _logger = logger;
        _httpClient = httpClient;
        _gameKeeBackupAdapter = gameKeeBackupAdapter;
        _publicRedeemCodeProvider = publicRedeemCodeProvider;

        _httpClient.BaseAddress ??= new Uri(ApiBaseAddress);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BlueArchiveLauncher", "0.1"));
        }
    }

    public string Name => "Kivo Wiki";

    public async Task<DashboardSnapshot> GetDashboardAsync(
        ServerId serverId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var gachaTask = GetTimelinePageAsync("Gacha", cancellationToken);
        var raidTask = GetTimelinePageAsync("Raid", cancellationToken);
        var eventTask = GetTimelinePageAsync("Event", cancellationToken);
        var pickUpTask = GetPickUpAsync(serverId, cancellationToken);
        var redeemCodesTask = _publicRedeemCodeProvider.GetAsync(serverId, cancellationToken);

        await Task.WhenAll(gachaTask, raidTask, eventTask, pickUpTask, redeemCodesTask);

        var gachas = SelectEntries(await gachaTask, serverId, now);
        var raid = SelectEntry(await raidTask, serverId, now);
        var eventEntry = SelectEntry(await eventTask, serverId, now);
        var pickUp = await pickUpTask;
        var publicRedeemCodes = await redeemCodesTask;

        if (gachas.Count == 0 && raid is null && eventEntry is null)
        {
            throw new InvalidOperationException($"Kivo 没有返回 {serverId} 的活动数据。");
        }

        var gachaDetails = await Task.WhenAll(
            gachas.Select(entry => GetOptionalDataAsync<KivoTimelineDetail>(
                $"api/v1/timeline/{entry.Id}", cancellationToken)));

        KivoTimelineDetail? raidDetail = null;
        if (raid is not null)
        {
            raidDetail = await GetOptionalDataAsync<KivoTimelineDetail>(
                $"api/v1/timeline/{raid.Id}", cancellationToken);
        }

        GameKeeRaidData? gameKeeRaid = null;
        if (serverId == ServerId.JP
            && raid is not null
            && FromUnixTime(raid.StartTime) is { } raidStart
            && FromUnixTime(raid.EndTime) is { } raidEnd)
        {
            gameKeeRaid = await _gameKeeBackupAdapter.GetAsync(
                serverId,
                raid.Title ?? string.Empty,
                raidStart,
                raidEnd,
                cancellationToken);
        }

        var recommendations = await BuildRecommendationsAsync(pickUp, gachaDetails, cancellationToken);
        var raidInfo = BuildRaid(raid, raidDetail, gameKeeRaid);
        var raidRecommendations = await BuildRaidRecommendationsAsync(
            serverId,
            raid?.Title ?? raidInfo.Name,
            gameKeeRaid,
            cancellationToken);
        raidInfo = raidInfo with { Recommendations = raidRecommendations };

        var announcements = BuildAnnouncements(gachas, raid, eventEntry, recommendations);
        var redeemCode = gameKeeRaid?.RedeemCode is { } gameKeeCode
            && IsUsableRedeemCode(gameKeeCode.Value)
            ? gameKeeCode
            : new RedeemCodeInfo(
                "兑换码数据",
                "暂无",
                "--",
                "公开来源未提供兑换码");
        if (publicRedeemCodes.Count > 0)
        {
            redeemCode = publicRedeemCodes[0];
        }

        var snapshot = new DashboardSnapshot(
            serverId,
            DateTimeOffset.Now,
            Name,
            BuildBanner(gachas, recommendations),
            raidInfo,
            announcements,
            recommendations,
            redeemCode)
        {
            RedeemCodes = publicRedeemCodes.Count > 0
                ? publicRedeemCodes
                : IsUsableRedeemCode(redeemCode.Value)
                    ? new[] { redeemCode }
                    : Array.Empty<RedeemCodeInfo>()
        };

        _logger.Info($"Kivo 数据加载完成：{serverId}，公告 {announcements.Count} 条，角色 {recommendations.Count} 条，推荐 {raidRecommendations.Count} 条，兑换码 {publicRedeemCodes.Count} 条");
        return snapshot;
    }

    private async Task<IReadOnlyList<CharacterRecommendation>> BuildRaidRecommendationsAsync(
        ServerId serverId,
        string bossName,
        GameKeeRaidData? gameKeeRaid,
        CancellationToken cancellationToken)
    {
        if (gameKeeRaid?.RecommendationNames.Count > 0)
        {
            var names = await Task.WhenAll(gameKeeRaid.RecommendationNames.Select(
                name => ResolveRaidRecommendationAsync(name, cancellationToken)));
            var resolved = names.Where(item => item is not null).Cast<CharacterRecommendation>().ToArray();
            if (resolved.Length > 0)
            {
                return resolved;
            }
        }

        return GetFallbackRaidRecommendations(serverId, bossName);
    }

    private static IReadOnlyList<CharacterRecommendation> GetFallbackRaidRecommendations(
        ServerId serverId,
        string bossName)
    {
        if (serverId == ServerId.Global
            && (bossName.Contains("Hovercraft", StringComparison.OrdinalIgnoreCase)
                || bossName.Contains("气垫船", StringComparison.Ordinal)))
        {
            return new[]
            {
                new CharacterRecommendation("小鸟游星野（泳装）", "星野", "公开攻略候选", "待核验"),
                new CharacterRecommendation("狐坂若藻（泳装）", "若藻", "公开攻略候选", "待核验"),
                new CharacterRecommendation("阿慈谷日步美", "日步", "公开攻略候选", "待核验")
            };
        }

        if (serverId == ServerId.CN
            && (bossName.Contains("Hod", StringComparison.OrdinalIgnoreCase)
                || bossName.Contains("霍德", StringComparison.Ordinal)))
        {
            return new[]
            {
                new CharacterRecommendation("鬼方佳代子", "佳代", "控制与辅助", "公开攻略候选"),
                new CharacterRecommendation("伊草遥香", "遥香", "控制与辅助", "公开攻略候选"),
                new CharacterRecommendation("和泉元艾米", "艾米", "前排与承伤", "公开攻略候选")
            };
        }

        return Array.Empty<CharacterRecommendation>();
    }

    private async Task<CharacterRecommendation?> ResolveRaidRecommendationAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var page = await GetOptionalDataAsync<KivoStudentsPage>(
            $"api/v1/data/students?name={Uri.EscapeDataString(name)}",
            cancellationToken);
        var student = SelectStudent(page?.Students, null);
        return student is null
            ? null
            : CreateCharacterRecommendation(student, "总力战推荐角色");
    }

    private async Task<IReadOnlyList<CharacterRecommendation>> BuildRecommendationsAsync(
        KivoPickUp? pickUp,
        IReadOnlyList<KivoTimelineDetail?> gachaDetails,
        CancellationToken cancellationToken)
    {
        var ids = pickUp?.Students?
            .Where(id => id > 0)
            .Distinct()
            .Take(6)
            .ToArray() ?? Array.Empty<int>();

        if (ids.Length > 0)
        {
            var students = await Task.WhenAll(ids.Select(id =>
                GetOptionalDataAsync<KivoStudent>($"api/v1/data/students/{id}", cancellationToken)));
            return students.Select((student, index) => student is null
                    ? new CharacterRecommendation("当前卡池角色待核验", "--", "角色数据待核验", "待核验")
                    : CreateCharacterRecommendation(student, "当前卡池角色"))
                .ToArray();
        }

        var names = gachaDetails
            .Where(detail => detail is not null)
            .SelectMany(detail => ExtractPickUpStudents(detail!.Body))
            .Distinct()
            .Take(6)
            .ToArray();
        if (names.Length == 0)
        {
            return new[] { new CharacterRecommendation("Kivo 未提供当前卡池角色名单", "--", "角色信息待核验", "待核验") };
        }

        return await Task.WhenAll(names.Select(name => ResolveCharacterByNameAsync(name, cancellationToken)));
    }

    private static BannerInfo BuildBanner(
        IReadOnlyList<KivoTimelineEntry> entries,
        IReadOnlyList<CharacterRecommendation> recommendations)
    {
        if (entries.Count == 0)
        {
            return new BannerInfo("当前没有卡池记录", "--", "Kivo", "时间待核验", "Kivo 当前没有返回卡池记录。");
        }

        var characterNames = recommendations
            .Where(item => item.Role == "当前卡池角色")
            .Select(item => item.Name)
            .Where(IsLocalizedCharacterName)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var displayName = characterNames.Length == 0
            ? "当前卡池（角色名单待核验）"
            : $"卡池：{string.Join("、", characterNames)}";

        return new BannerInfo(
            displayName,
            GetInitials(displayName),
            "Kivo 卡池",
            FormatWindow(entries),
            CleanText(string.Join(" ", entries.Select(entry => entry.Summary ?? entry.BodySummary)), "Kivo 未提供卡池说明。"));
    }

    private static RaidInfo BuildRaid(
        KivoTimelineEntry? entry,
        KivoTimelineDetail? detail,
        GameKeeRaidData? gameKeeRaid)
    {
        if (entry is null)
        {
            return new RaidInfo("当前没有总力战记录", "时间待核验", "待核验", "待核验", "Kivo 当前没有返回总力战记录。");
        }

        var description = detail?.Body ?? detail?.Summary ?? entry.Summary ?? entry.BodySummary;
        var name = gameKeeRaid is null ? BuildRaidName(entry.Title) : BuildRaidName(gameKeeRaid.BossName);
        var window = gameKeeRaid is null
            ? FormatWindow(entry.StartTime, entry.EndTime)
            : FormatWindow(gameKeeRaid.WindowStart, gameKeeRaid.WindowEnd);
        return new RaidInfo(
            name,
            window,
            ExtractField(description, "防御类型", "装甲类型", "Defense Type", "Armor"),
            ExtractField(description, "难度", "Difficulty"),
            CleanText(detail?.Summary ?? entry.Summary ?? entry.BodySummary, "Kivo 未提供总力战说明。"));
    }

    private static string BuildRaidName(string? title)
    {
        var value = NullIfBlank(title) ?? "当前总力战";
        return value
            .Replace("Hovercraft", "气垫船", StringComparison.OrdinalIgnoreCase)
            .Replace("Drumbarka", "铁桶蟹", StringComparison.OrdinalIgnoreCase)
            .Replace("Hod", "霍德", StringComparison.OrdinalIgnoreCase)
            .Replace("Field Warfare", "街区战", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AnnouncementInfo> BuildAnnouncements(
        IReadOnlyList<KivoTimelineEntry> gachas,
        KivoTimelineEntry? raid,
        KivoTimelineEntry? eventEntry,
        IReadOnlyList<CharacterRecommendation> recommendations)
    {
        var announcements = new List<AnnouncementInfo>();
        if (gachas.Count > 0)
        {
            var title = recommendations.FirstOrDefault(item => item.Role == "当前卡池角色")?.Name;
            AddAnnouncement(announcements, "卡池", title is null ? "当前卡池" : $"卡池：{title}", FormatWindow(gachas));
        }
        AddAnnouncement(announcements, "总力战", raid);
        AddAnnouncement(announcements, "活动", eventEntry);
        return announcements.Count == 0
            ? new[] { new AnnouncementInfo("Kivo", "当前没有可显示的活动记录", "时间待核验") }
            : announcements;
    }

    private static void AddAnnouncement(
        ICollection<AnnouncementInfo> announcements,
        string kind,
        KivoTimelineEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        announcements.Add(new AnnouncementInfo(
            kind,
            NullIfBlank(entry.Title) ?? $"当前{kind}记录",
            FormatWindow(entry.StartTime, entry.EndTime)));
    }

    private static void AddAnnouncement(
        ICollection<AnnouncementInfo> announcements,
        string kind,
        string title,
        string dateLabel) => announcements.Add(new AnnouncementInfo(kind, title, dateLabel));

    private async Task<CharacterRecommendation> ResolveCharacterByNameAsync(
        PickedUpStudent pickedUpStudent,
        CancellationToken cancellationToken)
    {
        var page = await GetOptionalDataAsync<KivoStudentsPage>(
            $"api/v1/data/students?name={Uri.EscapeDataString(pickedUpStudent.Name)}",
            cancellationToken);
        var student = SelectStudent(page?.Students, pickedUpStudent.Variant);
        return student is null
            ? new CharacterRecommendation("当前卡池角色待核验", "--", "角色数据待核验", "待核验")
            : CreateCharacterRecommendation(student, "当前卡池角色");
    }

    private static CharacterRecommendation CreateCharacterRecommendation(KivoStudent student, string role)
    {
        var name = GetStudentName(student);
        return new CharacterRecommendation(name, GetInitials(name), role, "Kivo 数据");
    }

    private static KivoStudent? SelectStudent(IReadOnlyList<KivoStudent>? students, string? variant)
    {
        if (students is null || students.Count == 0)
        {
            return null;
        }

        return students
            .OrderByDescending(student => VariantMatches(student, variant))
            .ThenBy(student => string.IsNullOrWhiteSpace(NullIfBlank(student.SkinCn) ?? NullIfBlank(student.Skin)))
            .First();
    }

    private static bool VariantMatches(KivoStudent student, string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant))
        {
            return string.IsNullOrWhiteSpace(student.SkinCn ?? student.Skin);
        }

        var skin = $"{student.SkinCn} {student.Skin}".ToUpperInvariant();
        var wanted = variant.ToUpperInvariant();
        return skin.Contains(wanted, StringComparison.Ordinal);
    }

    private static IReadOnlyList<PickedUpStudent> ExtractPickUpStudents(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<PickedUpStudent>();
        }

        var sections = Regex.Matches(body, @"Pick-Up Student[s]?\s*:\s*(?<students>[^\r\n]+)", RegexOptions.IgnoreCase);
        var students = new List<PickedUpStudent>();
        foreach (Match section in sections)
        {
            var matches = Regex.Matches(
                section.Groups["students"].Value,
                @"(?:\d+\s*\u00d7?\s*)?(?<name>[A-Za-z][A-Za-z' -]*?)(?:\s*\((?<variant>[^)]+)\))?(?=\s*(?:,\s*(?:\d+\s*\u00d7?\s*)?[A-Za-z]|$))",
                RegexOptions.IgnoreCase);
            students.AddRange(matches.Select(match => new PickedUpStudent(
                match.Groups["name"].Value.Trim(),
                NullIfBlank(match.Groups["variant"].Value))));
        }

        return students.Where(item => !string.IsNullOrWhiteSpace(item.Name)).Distinct().Take(6).ToArray();
    }

    private async Task<KivoTimelinePage> GetTimelinePageAsync(string type, CancellationToken cancellationToken)
    {
        return await GetDataAsync<KivoTimelinePage>(
            $"api/v1/timeline?page=1&page_size=50&type={type}&start_time_sort=desc",
            cancellationToken);
    }

    private async Task<KivoPickUp?> GetPickUpAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        if (serverId == ServerId.Global)
        {
            return null;
        }

        return await GetOptionalDataAsync<KivoPickUp>(
            $"api/v1/data/pick_up?server={GetServerCode(serverId)}",
            cancellationToken);
    }

    private async Task<T> GetDataAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Kivo 请求失败：{path}，HTTP {(int)response.StatusCode}");
        }

        var envelope = JsonSerializer.Deserialize<KivoEnvelope<T>>(content, JsonOptions);
        if (envelope?.Success != true || envelope.Data is null)
        {
            throw new InvalidOperationException($"Kivo 返回无效数据：{path}");
        }

        return envelope.Data;
    }

    private async Task<T?> GetOptionalDataAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await GetDataAsync<T>(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Kivo 可选接口不可用：{path}，{ex.Message}");
            return default;
        }
    }

    private static KivoTimelineEntry? SelectEntry(KivoTimelinePage page, ServerId serverId, DateTimeOffset now)
    {
        var entries = page.Timeline
            .Where(entry => MatchesServer(entry.LineType, serverId))
            .OrderByDescending(entry => entry.StartTime)
            .ToArray();
        return entries.FirstOrDefault(entry => IsCurrent(entry, now)) ?? entries.FirstOrDefault();
    }

    private static IReadOnlyList<KivoTimelineEntry> SelectEntries(KivoTimelinePage page, ServerId serverId, DateTimeOffset now)
    {
        var entries = page.Timeline
            .Where(entry => MatchesServer(entry.LineType, serverId))
            .OrderByDescending(entry => entry.StartTime)
            .ToArray();
        var current = entries.Where(entry => IsCurrent(entry, now)).ToArray();
        if (current.Length > 0)
        {
            return current;
        }

        var latest = entries.FirstOrDefault();
        return latest is null
            ? Array.Empty<KivoTimelineEntry>()
            : entries.Where(entry => entry.StartTime == latest.StartTime && entry.EndTime == latest.EndTime).ToArray();
    }

    private static bool MatchesServer(string? lineType, ServerId serverId)
    {
        var normalized = lineType?.Trim().ToUpperInvariant();
        return serverId switch
        {
            ServerId.Global => normalized is "GLOBLE" or "GLOBAL" or "EN",
            ServerId.CN => normalized == "CN",
            ServerId.JP => normalized == "JP",
            _ => false
        };
    }

    private static bool IsCurrent(KivoTimelineEntry entry, DateTimeOffset now)
    {
        var start = FromUnixTime(entry.StartTime);
        var end = FromUnixTime(entry.EndTime);
        return start is not null && end is not null && start <= now && now <= end;
    }

    private static string GetServerCode(ServerId serverId) => serverId switch
    {
        ServerId.CN => "cn",
        ServerId.JP => "jp",
        _ => throw new ArgumentOutOfRangeException(nameof(serverId), serverId, null)
    };

    private static string GetStudentName(KivoStudent student)
    {
        var familyName = NullIfBlank(student.FamilyNameCn) ?? NullIfBlank(student.FamilyName);
        var givenName = NullIfBlank(student.GivenNameCn) ?? NullIfBlank(student.GivenName);
        var name = $"{familyName}{givenName}".Trim();
        return string.IsNullOrWhiteSpace(name) ? NullIfBlank(student.GivenNameJp) ?? "当前角色" : name;
    }

    private static bool IsLocalizedCharacterName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(character => character >= '\u3400' && character <= '\u9FFF');

    private static string FormatWindow(IReadOnlyList<KivoTimelineEntry> entries) => entries.Count == 0
        ? "时间待核验"
        : FormatWindow(entries.Min(entry => entry.StartTime), entries.Max(entry => entry.EndTime));

    private static string FormatWindow(long startTime, long endTime)
    {
        var start = FromUnixTime(startTime);
        var end = FromUnixTime(endTime);
        if (start is null && end is null) return "时间待核验";
        if (start is null) return $"结束 {end!.Value:yyyy/MM/dd HH:mm}";
        if (end is null) return $"开始 {start.Value:yyyy/MM/dd HH:mm}";
        return $"{start.Value:yyyy/MM/dd HH:mm} - {end.Value:yyyy/MM/dd HH:mm}";
    }

    private static string FormatWindow(DateTimeOffset start, DateTimeOffset end) =>
        $"{start.LocalDateTime:yyyy/MM/dd HH:mm} - {end.LocalDateTime:yyyy/MM/dd HH:mm}";

    private static DateTimeOffset? FromUnixTime(long value) => value <= 0
        ? null
        : DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();

    private static string ExtractField(string? text, params string[] labels)
    {
        if (string.IsNullOrWhiteSpace(text)) return "待核验";
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidateLine = line.Trim().Trim('*', '`', ' ', '　');
            foreach (var label in labels)
            {
                var index = candidateLine.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                var candidate = candidateLine[(index + label.Length)..].Trim(' ', '　', ':', '：', '-', '—', '*', '`');
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }
        return "待核验";
    }

    private static string CleanText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return string.Join(" ", value.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetInitials(string? value)
    {
        var initials = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(2).ToArray());
        return string.IsNullOrWhiteSpace(initials) ? "BA" : initials.ToUpperInvariant();
    }

    private static bool IsUsableRedeemCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "--" and not "----------" and not "暂无";

    private sealed record KivoEnvelope<T>(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record KivoTimelinePage(
        [property: JsonPropertyName("timeline")] KivoTimelineEntry[] Timeline);

    private sealed record KivoTimelineEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("line_type")] string? LineType,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("body_summary")] string? BodySummary,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("start_time")] long StartTime,
        [property: JsonPropertyName("end_time")] long EndTime);

    private sealed record KivoTimelineDetail(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("start_time")] long StartTime,
        [property: JsonPropertyName("end_time")] long EndTime);

    private sealed record KivoStudentsPage(
        [property: JsonPropertyName("max_page")] int MaxPage,
        [property: JsonPropertyName("students")] KivoStudent[] Students);

    private sealed record KivoPickUp(
        [property: JsonPropertyName("start_date")] long StartDate,
        [property: JsonPropertyName("end_date")] long EndDate,
        [property: JsonPropertyName("students")] int[]? Students);

    private sealed record KivoStudent(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("family_name")] string? FamilyName,
        [property: JsonPropertyName("given_name")] string? GivenName,
        [property: JsonPropertyName("given_name_jp")] string? GivenNameJp,
        [property: JsonPropertyName("family_name_cn")] string? FamilyNameCn,
        [property: JsonPropertyName("given_name_cn")] string? GivenNameCn,
        [property: JsonPropertyName("skin")] string? Skin,
        [property: JsonPropertyName("skin_cn")] string? SkinCn);

    private sealed record PickedUpStudent(string Name, string? Variant);
}
