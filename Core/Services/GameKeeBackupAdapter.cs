using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlueArchiveLauncher.Core.Models;

namespace BlueArchiveLauncher.Core.Services;

public sealed class GameKeeBackupAdapter
{
    private const string PageUrl = "https://www.gamekee.com/ba/722792.html";
    private const string DetailApiUrl = "https://www.gamekee.com/v1/content/detail/722792";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ParenthesizedGroup = new(
        @"[（(](?<group>[^）)]{1,80})[）)]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ChineseName = new(
        @"[\u3400-\u9FFF]{2,8}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CodePattern = new(
        @"\b[A-Z][A-Z0-9-]{5,20}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FileLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlueArchiveLauncher",
        "cache");

    public GameKeeBackupAdapter(FileLogger logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BlueArchiveLauncher", "0.1"));
        }
    }

    public async Task<GameKeeRaidData?> GetAsync(
        ServerId serverId,
        string expectedBossName,
        DateTimeOffset expectedStart,
        DateTimeOffset expectedEnd,
        CancellationToken cancellationToken)
    {
        if (serverId != ServerId.JP)
        {
            return null;
        }

        try
        {
            var live = await FetchLiveAsync(expectedStart, expectedEnd, cancellationToken);
            if (IsApplicable(live, serverId, expectedBossName, expectedStart, expectedEnd))
            {
                await SaveCacheAsync(live!, cancellationToken);
                return live;
            }

            if (live is not null)
            {
                _logger.Info($"GameKee article did not match the current JP raid: {live.BossName}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"GameKee live data unavailable: {ex.Message}");
        }

        var cached = await ReadCacheAsync(serverId, cancellationToken);
        return IsApplicable(cached, serverId, expectedBossName, expectedStart, expectedEnd)
            ? cached
            : null;
    }

    public static bool IsApplicable(
        GameKeeRaidData? data,
        ServerId serverId,
        string expectedBossName,
        DateTimeOffset expectedStart,
        DateTimeOffset expectedEnd)
    {
        if (data is null
            || data.ServerId != serverId
            || data.RecommendationNames.Count == 0
            || !BossMatches(data.BossName, expectedBossName))
        {
            return false;
        }

        return data.WindowStart <= expectedEnd && data.WindowEnd >= expectedStart;
    }

    private async Task<GameKeeRaidData?> FetchLiveAsync(
        DateTimeOffset expectedStart,
        DateTimeOffset expectedEnd,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DetailApiUrl);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Lang", "zh-cn");
        request.Headers.TryAddWithoutValidation("game-alias", "ba");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var envelope = JsonSerializer.Deserialize<GameKeeEnvelope<GameKeeContent>>(content, JsonOptions);
        var data = envelope?.Data;
        if (data is null)
        {
            throw new InvalidOperationException("GameKee returned no article data.");
        }

        var body = data.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(data.ContentCdn))
        {
            try
            {
                body = await FetchContentAsync(ToAbsoluteUrl(data.ContentCdn), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"GameKee article body unavailable; using summary: {ex.Message}");
            }
        }

        var allText = string.Join(
            Environment.NewLine,
            data.Title,
            data.Summary,
            body);

        return new GameKeeRaidData(
            ServerId.JP,
            PageUrl,
            DateTimeOffset.UtcNow,
            data.Title ?? string.Empty,
            expectedStart,
            expectedEnd,
            ExtractRecommendationNames(allText),
            ExtractRedeemCode(allText));
    }

    private async Task<string> FetchContentAsync(
        string contentUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        request.Headers.Referrer = new Uri(PageUrl);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.gamekee.com");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(content);
        return FindContentString(document.RootElement) ?? string.Empty;
    }

    private static string? FindContentString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "content", "body", "html" })
        {
            if (element.TryGetProperty(propertyName, out var value))
            {
                var result = FindContentString(value);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var result = FindContentString(property.Value);
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    private static string ToAbsoluteUrl(string value) =>
        value.StartsWith("//", StringComparison.Ordinal) ? $"https:{value}" : value;

    private async Task SaveCacheAsync(
        GameKeeRaidData data,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(_cacheRoot, data.ServerId.ToString());
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "gamekee-722792.json");
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(
                stream,
                data,
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"GameKee cache write failed: {ex.Message}");
        }
    }

    private async Task<GameKeeRaidData?> ReadCacheAsync(
        ServerId serverId,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(_cacheRoot, serverId.ToString(), "gamekee-722792.json");
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<GameKeeRaidData>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"GameKee cache read failed: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractRecommendationNames(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var plainText = Regex.Replace(text, @"<[^>]+>", "\n");
        plainText = System.Net.WebUtility.HtmlDecode(plainText);
        var names = new List<string>();

        foreach (Match groupMatch in ParenthesizedGroup.Matches(plainText))
        {
            var group = groupMatch.Groups["group"].Value;
            foreach (var part in group.Split(
                         new[] { '/', '、', ',', '，', '|' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var nameMatch = ChineseName.Match(part);
                if (!nameMatch.Success)
                {
                    continue;
                }

                var name = nameMatch.Value.Trim();
                if (name.Contains("难度", StringComparison.Ordinal)
                    || name.Contains("攻略", StringComparison.Ordinal)
                    || name.Contains("作业", StringComparison.Ordinal)
                    || name.Contains("参考", StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(name);
            }
        }

        return names.Distinct(StringComparer.Ordinal).Take(12).ToArray();
    }

    private static RedeemCodeInfo ExtractRedeemCode(string text)
    {
        var hasCodeContext = Regex.IsMatch(
            text,
            @"兑换码|礼包码|serial|redeem|code",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!hasCodeContext)
        {
            return new RedeemCodeInfo(
                "GameKee 兑换码",
                "暂无",
                "--",
                "该攻略页未提供兑换码");
        }

        var code = CodePattern.Matches(text)
            .Select(match => match.Value)
            .FirstOrDefault(value =>
                !value.Equals("GAMEKEE", StringComparison.OrdinalIgnoreCase));

        return new RedeemCodeInfo(
            "GameKee 兑换码",
            code ?? "暂无",
            "--",
            code is null ? "未提取到可验证兑换码" : "已提取，待核验");
    }

    private static bool BossMatches(string pageTitle, string expectedBossName)
    {
        if (pageTitle.Contains("铁桶蟹", StringComparison.Ordinal)
            && expectedBossName.Contains("ドラム缶ガニ", StringComparison.Ordinal))
        {
            return true;
        }

        return pageTitle.Contains(expectedBossName, StringComparison.Ordinal)
            || expectedBossName.Contains(pageTitle, StringComparison.Ordinal);
    }

    private sealed record GameKeeEnvelope<T>(
        [property: JsonPropertyName("data")] T? Data);

    private sealed record GameKeeContent(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("content_cdn")] string? ContentCdn);
}
