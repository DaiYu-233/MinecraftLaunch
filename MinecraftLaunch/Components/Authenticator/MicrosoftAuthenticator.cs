﻿using Flurl.Http;
using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Extensions;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MinecraftLaunch.Components.Authenticator;

public sealed class MicrosoftAuthenticator {
    private readonly string _clientId;
    private readonly IEnumerable<string> _scopes = ["XboxLive.signin", "offline_access", "openid", "profile", "email"];

    /// <summary>
    /// Authenticator for Microsoft accounts.
    /// </summary>
    public MicrosoftAuthenticator(string clientId) {
        _clientId = clientId;
    }

    public async Task<MicrosoftAccount> RefreshAsync(MicrosoftAccount account, CancellationToken cancellationToken = default) {
        var url = "https://login.live.com/oauth20_token.srf";

        var content = new {
            client_id = _clientId,
            refresh_token = account.RefreshToken,
            grant_type = "refresh_token",
        };

        var result = await url.PostUrlEncodedAsync(content, cancellationToken: cancellationToken);
        var oAuth2Token = await result.GetJsonAsync<OAuth2TokenResponse>();

        return await AuthenticateAsync(oAuth2Token, cancellationToken);
    }

    /// <summary>
    /// Asynchronously authenticates the Microsoft account.
    /// </summary>
    /// <returns>A ValueTask that represents the asynchronous operation. The task result contains the authenticated Microsoft account.</returns>
    public async Task<MicrosoftAccount> AuthenticateAsync(OAuth2TokenResponse oAuth2Token, CancellationToken cancellationToken = default) {
        try {
            if (oAuth2Token is null) {
                throw new KeyNotFoundException();
            }

            var xblToken = await GetXBLTokenAsync(oAuth2Token.AccessToken, cancellationToken);
            var xsts = await GetXSTSTokenAsync(xblToken, cancellationToken);
            var minecraftAccessToken = await GetMinecraftAccessTokenAsync((xblToken, xsts), cancellationToken);
            var profile = await GetMinecraftProfileAsync(minecraftAccessToken.GetString("access_token"), oAuth2Token.RefreshToken, cancellationToken);

            return profile;
        } catch (Exception) {
            throw;
        }
    }

    /// <summary>
    /// Asynchronously authenticates the Microsoft account using device flow authentication.
    /// </summary>
    /// <param name="deviceCode">The action to be performed with the device code response.</param>
    /// <param name="source">The cancellation token source to be used to cancel the operation.</param>
    /// <returns>A Task that represents the asynchronous operation. The task result contains the OAuth2 token response.</returns>
    public async Task<OAuth2TokenResponse> DeviceFlowAuthAsync(Action<DeviceCodeResponse> deviceCode, CancellationToken cancellationToken = default) {
        if (string.IsNullOrEmpty(_clientId)) {
            throw new ArgumentNullException("ClientId is empty!");
        }

        string tenant = "/consumers";
        var parameters = new Dictionary<string, string> {
            ["client_id"] = _clientId,
            ["tenant"] = tenant,
            ["scope"] = string.Join(" ", _scopes)
        };

        string json = await "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"
            .PostUrlEncodedAsync(parameters)
            .ReceiveString();

        var codeResponse = json.Deserialize(DeviceCodeResponseContext.Default.DeviceCodeResponse);
        deviceCode.Invoke(codeResponse);

        //Polling
        HttpClient client = new();
        int timeout = codeResponse.ExpiresIn;
        OAuth2TokenResponse tokenResponse = default!;

        var stopwatch = Stopwatch.StartNew();
        var requestParams =
            "grant_type=urn:ietf:params:oauth:grant-type:device_code" +
            $"&client_id={_clientId}" +
            $"&device_code={codeResponse.DeviceCode}";

        do {
            cancellationToken.ThrowIfCancellationRequested();

            //好他妈猎奇，这段用 Flurl 会直接返回 400
            using var responseMessage = await client.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new StringContent(requestParams, Encoding.UTF8, "application/x-www-form-urlencoded"), cancellationToken);

            var tokenJson = await responseMessage.Content.ReadAsStringAsync(cancellationToken);
            var tempTokenResponse = tokenJson.AsNode();

            if (tempTokenResponse["error"] == null) {
                tokenResponse = new() {
                    AccessToken = tempTokenResponse.GetString("access_token"),
                    RefreshToken = tempTokenResponse.GetString("refresh_token"),
                    ExpiresIn = tempTokenResponse.GetInt32("expires_in"),
                };
            }

            if (tempTokenResponse.GetString("token_type") is "Bearer")
                return tokenResponse;

            await Task.Delay(TimeSpan.FromSeconds(codeResponse.Interval), cancellationToken);
        } while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeout));

        throw new TimeoutException("登录操作已超时");
    }

    #region Privates

    /// <summary>
    /// Get Xbox live token & userhash
    /// </summary>
    private async Task<JsonNode> GetXBLTokenAsync(string token, CancellationToken cancellationToken = default) {
        var xblContent = new {
            Properties = new {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={token}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        using var xblJsonReq = await $"https://user.auth.xboxlive.com/user/authenticate"
            .PostJsonAsync(xblContent, cancellationToken: cancellationToken);

        return (await xblJsonReq.GetStringAsync()).AsNode();
    }

    /// <summary>
    /// Get Xbox security token service token & userhash
    /// </summary>
    /// <returns></returns>
    /// <exception cref="FailedAuthenticationException"></exception>
    private async Task<JsonNode> GetXSTSTokenAsync(JsonNode xblTokenNode, CancellationToken cancellationToken = default) {
        var xstsContent = new {
            Properties = new {
                SandboxId = "RETAIL",
                UserTokens = new[] {
                    xblTokenNode.GetString("Token")
                }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        using var xstsJsonReq = await $"https://xsts.auth.xboxlive.com/xsts/authorize"
            .PostJsonAsync(xstsContent, cancellationToken: cancellationToken);

        return (await xstsJsonReq.GetStringAsync()).AsNode();
    }

    /// <summary>
    /// Get Minecraft access token
    /// </summary>
    private async Task<JsonNode> GetMinecraftAccessTokenAsync((JsonNode xblTokenNode, JsonNode xstsTokenNode) nodes, CancellationToken cancellationToken = default) {
        var authenticateMinecraftContent = new {
            identityToken = $"XBL3.0 x={nodes.xblTokenNode["DisplayClaims"]["xui"].AsArray()
            .FirstOrDefault()
            .GetString("uhs")};{nodes.xstsTokenNode!
            .GetString("Token")}"
        };

        using var authenticateMinecraftPostRes = await $"https://api.minecraftservices.com/authentication/login_with_xbox"
            .PostJsonAsync(authenticateMinecraftContent, cancellationToken: cancellationToken);

        return (await authenticateMinecraftPostRes.GetStringAsync()).AsNode();
    }

    /// <summary>
    /// Get player's minecraft profile
    /// </summary>
    /// <param name="accessToken">Minecraft access token</param>
    /// <param name="refreshToken">Minecraft refresh token</param>
    /// <exception cref="InvalidOperationException">If authenticated user don't have minecraft, the exception will be thrown</exception>
    private async Task<MicrosoftAccount> GetMinecraftProfileAsync(string accessToken, string refreshToken, CancellationToken cancellationToken = default) {
        using var profileRes = await "https://api.minecraftservices.com/minecraft/profile"
            .WithHeader("Authorization", $"Bearer {accessToken}")
            .GetAsync(cancellationToken: cancellationToken);

        var profileNode = (await profileRes.GetStringAsync())
            .AsNode();

        return profileNode == null
            ? throw new InvalidOperationException("Failed to retrieve Minecraft profile")
            : new MicrosoftAccount(profileNode.GetString("name"), Guid.Parse(profileNode.GetString("id")), accessToken, refreshToken, DateTime.Now);
    }

    #endregion
}