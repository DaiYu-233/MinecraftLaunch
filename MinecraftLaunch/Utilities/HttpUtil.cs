using Flurl;
using Flurl.Http;
using Flurl.Http.Configuration;
using MinecraftLaunch.Components.Parser;
using System.Text.Json;

namespace MinecraftLaunch.Utilities;

public static class HttpUtil {
    public static IFlurlClient FlurlClient { get; private set; }

    public static IFlurlRequest Request(string url) {
        if (FlurlClient is null)
            throw new InvalidOperationException("FlurlClient is not initialized.");

        return FlurlClient.Request(url);
    }

    public static IFlurlRequest Request(Url baseUrl, params string[] paths) {
        if (FlurlClient is null)
            throw new InvalidOperationException("FlurlClient is not initialized.");

        return FlurlClient.Request(baseUrl.AppendPathSegments(paths));
    }

    public static IFlurlClient Initialize(IFlurlClient client = default) {
        if (client is not null)
            return FlurlClient = client;

        return FlurlClient = new FlurlClient {
            Settings = {
                Timeout = TimeSpan.FromSeconds(100),
                JsonSerializer = new DefaultJsonSerializer(LauncherProfileParser.Get()),
            },
            Headers = {
                { "User-Agent", "MinecraftLaunch/4.0" },
            },
        };
    }
}