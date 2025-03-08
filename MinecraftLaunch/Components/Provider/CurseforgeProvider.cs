using Flurl.Http;
using MinecraftLaunch.Extensions;
using MinecraftLaunch.Utilities;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace MinecraftLaunch.Components.Provider;

public sealed class CurseforgeProvider {
    public static string CurseforgeApiKey = string.Empty;
    public static string CurseforgeApi = "https://api.curseforge.com/v1";

    public CurseforgeProvider(string apiKey) {
        CurseforgeApiKey = apiKey;
    }

    public static implicit operator CurseforgeProvider(string apiKey) {
        return new(apiKey);
    }

    public async IAsyncEnumerable<CurseforgeResource> GetFeaturedResourcesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) {
        IEnumerable<JsonNode> resources = null;
        var request = CreateRequest("featured");

        using var responseMessage = await request.PostJsonAsync(new CurseforgeFeaturedRequestPayload(432,
            [0]), cancellationToken: cancellationToken);

        var jsonNode = (await responseMessage.GetStringAsync()).AsNode()
            .Select("data");

        var popular = jsonNode.GetEnumerable("popular");
        var featured = jsonNode.GetEnumerable("featured");

        if (popular is not null && featured is not null)
            resources = popular.Union(featured);
        else
            yield break;

        foreach (var resource in resources) {
            yield return Parse(resource);
        }
    }

    #region Private and internals

    internal static async Task<JsonNode> GetModFileEntryAsync(long modId, long fileId, CancellationToken cancellationToken = default) {
        CheckApiKey();

        string json = string.Empty;
        try {
            using var responseMessage = await CreateRequest("mods", "files", $"{fileId}")
                .GetAsync(cancellationToken: cancellationToken); ;

            json = await responseMessage.GetStringAsync();
        } catch (Exception) { }

        return json?.AsNode()?.Select("data") ??
            throw new InvalidModpackFileException();
    }

    internal static async Task<string> GetModDownloadUrlAsync(long modId, long fileId, CancellationToken cancellationToken = default) {
        CheckApiKey();

        string json = string.Empty;
        try {
            using var responseMessage = await CreateRequest("mods", $"{modId}", "files", $"{fileId}", "download-url")
                .GetAsync(cancellationToken: cancellationToken);

            json = await responseMessage.GetStringAsync();
        } catch (FlurlHttpException ex) {
            if (ex.StatusCode is 403)
                return string.Empty;
        }

        return json?.AsNode()?.GetString("data")
            ?? throw new InvalidModpackFileException();
    }

    internal static async Task<string> TestDownloadUrlAsync(long fileId, string fileName, CancellationToken cancellationToken = default) {
        CheckApiKey();

        var fileIdStr = fileId.ToString();
        List<string> urls = [
            $"https://edge.forgecdn.net/files/{fileIdStr[..4]}/{fileIdStr[4..]}/{fileName}",
            $"https://mediafiles.forgecdn.net/files/{fileIdStr[..4]}/{fileIdStr[4..]}/{fileName}"
        ];

        try {
            foreach (var url in urls) {
                var response = await HttpUtil.Request(url)
                    .HeadAsync(cancellationToken: cancellationToken);

                if (!response.ResponseMessage.IsSuccessStatusCode)
                    continue;

                return url;
            }
        } catch (Exception) { }

        throw new InvalidOperationException();
    }

    private static CurseforgeResource Parse(JsonNode node) {
        return new CurseforgeResource {
            Id = node.GetInt32("id"),
            ClassId = node.GetInt32("classId"),
            DownloadCount = node.GetInt32("downloadCount"),
            Name = node.GetString("name"),
            IconUrl = node.GetString("iconUrl"),
            Summary = node.GetString("summary"),
            WebsiteUrl = node.GetString("websiteUrl"),
            DateModified = node.GetDateTime("dateModified"),
            Authors = node.GetEnumerable<string>("authors"),
            Categories = node.GetEnumerable<string>("categories"),
            Screenshots = node.GetEnumerable<string>("screenshots")
        };
    }

    private static IFlurlRequest CreateRequest(params string[] path) {
        CheckApiKey();

        return HttpUtil.Request(CurseforgeApi, path)
            .WithHeader("x-api-key", CurseforgeApiKey);
    }

    private static void CheckApiKey() {
        if (string.IsNullOrWhiteSpace(CurseforgeApiKey))
            throw new InvalidOperationException("Curseforge API key is not set.");
    }

    #endregion
}

[Serializable]
public class InvalidModpackFileException : Exception {
    public long ProjectId { get; set; }

    public InvalidModpackFileException() { }
    public InvalidModpackFileException(string message) : base(message) { }
    public InvalidModpackFileException(string message, Exception inner) : base(message, inner) { }
}

internal record CurseforgeFeaturedRequestPayload(int gameId, int[] excludedModIds, string gameVersionTypeId = null);

public record CurseforgeResource {
    public required int Id { get; init; }
    public required int ClassId { get; init; }
    public required int DownloadCount { get; init; }
    public required string Name { get; init; }
    public required string IconUrl { get; init; }
    public required string Summary { get; init; }
    public required string WebsiteUrl { get; init; }
    public required DateTime DateModified { get; init; }
    public required IEnumerable<string> Authors { get; init; }
    public required IEnumerable<string> Categories { get; init; }
    public required IEnumerable<string> Screenshots { get; init; }
}