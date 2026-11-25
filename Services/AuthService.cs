// -----------------------------------------------------------------------
// <copyright file="AuthService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace MonzoManager.Services;

/// <summary>
/// Manages Monzo OAuth authentication and token lifecycle.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Starts OAuth sign-in and stores tokens if successful.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when login succeeds; otherwise <see langword="false"/>.</returns>
    Task<bool> LoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves Monzo API credentials to encrypted local storage.
    /// </summary>
    /// <param name="clientId">Monzo OAuth client identifier.</param>
    /// <param name="clientSecret">Monzo OAuth client secret.</param>
    /// <param name="redirectUri">OAuth redirect URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveCredentialsAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a pre-issued Monzo access token to encrypted local storage.
    /// </summary>
    /// <param name="accessToken">Bearer access token.</param>
    /// <param name="userId">Optional Monzo user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveAccessTokenAsync(
        string accessToken,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether encrypted Monzo API credentials are stored locally.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when credentials are available; otherwise <see langword="false"/>.</returns>
    Task<bool> HasStoredCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears local auth tokens and signs the user out.
    /// </summary>
    /// <returns><see langword="true"/> when logout completes.</returns>
    Task<bool> LogoutAsync();

    /// <summary>
    /// Ensures an access token is available and refreshed if needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a valid token is available.</returns>
    Task<bool> EnsureValidTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a currently available access token.
    /// </summary>
    /// <returns>The current access token when available; otherwise <see langword="null"/>.</returns>
    string? GetAccessToken();

    /// <summary>
    /// Gets a value indicating whether the session has an access token.
    /// </summary>
    bool IsAuthenticated { get; }
}

/// <summary>
/// Default implementation of Monzo OAuth authentication.
/// </summary>
public sealed class AuthService : IAuthService
{
    private const string CredentialsFileName = "credentials.dat";
    private const string LegacyCredentialsFileName = "credentials.json";
    private const string TokensFileName = "tokens.dat";
    private const string AuthorizeEndpoint = "https://auth.monzo.com";
    private const string TokenEndpoint = "https://api.monzo.com/oauth2/token";
    private static readonly TimeSpan DefaultAccessTokenLifetime = TimeSpan.FromHours(6);

    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private readonly string _appDataDirectory;
    private readonly string _tokenFilePath;
    private readonly string _credentialsFilePath;
    private readonly string _legacyCredentialsFilePath;

    private TokenInfo? _tokenInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthService"/> class.
    /// </summary>
    public AuthService()
    {
        _appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MonzoManager");
        _tokenFilePath = Path.Combine(_appDataDirectory, TokensFileName);
        _credentialsFilePath = Path.Combine(_appDataDirectory, CredentialsFileName);
        _legacyCredentialsFilePath = Path.Combine(_appDataDirectory, LegacyCredentialsFileName);
        _tokenInfo = LoadToken();
    }

    /// <inheritdoc />
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_tokenInfo?.AccessToken);

    /// <inheritdoc />
    public async Task SaveCredentialsAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new ArgumentException("Client secret is required.", nameof(clientSecret));
        }

        if (!TryGetLoopbackRedirect(redirectUri.Trim(), out _))
        {
            throw new ArgumentException("Use an HTTP loopback redirect on localhost or 127.0.0.1, without a query or fragment.", nameof(redirectUri));
        }

        var credentials = new MonzoCredentials
        {
            ClientId = clientId.Trim(),
            ClientSecret = clientSecret.Trim(),
            RedirectUri = redirectUri.Trim(),
        };

        await SaveCredentialsInternalAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAccessTokenAsync(
        string accessToken,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidBearer(accessToken.Trim()))
        {
            throw new ArgumentException("Enter a valid Monzo access token.", nameof(accessToken));
        }

        var token = new TokenInfo
        {
            AccessToken = accessToken.Trim(),
            RefreshToken = string.Empty,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(DefaultAccessTokenLifetime),
            TokenType = "Bearer",
            UserId = userId?.Trim(),
        };

        await StoreTokenAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasStoredCredentialsAsync(CancellationToken cancellationToken = default)
    {
        MonzoCredentials? credentials = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
        return credentials is not null;
    }

    /// <inheritdoc />
    public async Task<bool> LoginAsync(CancellationToken cancellationToken = default)
    {
        MonzoCredentials? credentials = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            Log.Error("Unable to log in because the stored credentials are missing or invalid.");
            return false;
        }

        if (!TryGetLoopbackRedirect(credentials.RedirectUri, out Uri? redirectUri))
        {
            Log.Warning("Configured OAuth redirect is invalid.");
            return false;
        }

        string listenerPrefix = redirectUri!.GetLeftPart(UriPartial.Authority) + "/";
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string authorizationUrl =
            $"{AuthorizeEndpoint}/?client_id={Uri.EscapeDataString(credentials.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(credentials.RedirectUri)}" +
            "&response_type=code" +
            $"&state={Uri.EscapeDataString(state)}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(listenerPrefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            Log.Error("Failed to start the local OAuth callback listener (code {ErrorCode}).", exception.ErrorCode);
            return false;
        }

        Log.Information("Opening browser for OAuth sign-in.");
        OpenBrowser(authorizationUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        HttpListenerContext context;
        string code;
        while (true)
        {
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Information("OAuth callback cancelled or timed out.");
                return false;
            }

            HttpListenerRequest request = context.Request;
            IPAddress? remoteAddress = request.RemoteEndPoint?.Address;
            string? returnedState = request.QueryString["state"];
            if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress)
                || request.HttpMethod != "GET" || request.Url is null
                || !string.Equals(request.Url.Host, redirectUri.Host, StringComparison.OrdinalIgnoreCase)
                || request.Url.Port != redirectUri.Port || request.Url.AbsolutePath != redirectUri.AbsolutePath
                || returnedState is null || returnedState.Length != state.Length
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(state)))
            {
                WriteHttpResponse(context.Response, HttpStatusCode.BadRequest, "Invalid callback. Return to Parc Spend.");
                continue;
            }

            string[]? codes = request.QueryString.GetValues("code");
            if (codes is not { Length: 1 } || string.IsNullOrWhiteSpace(codes[0]))
            {
                WriteHttpResponse(context.Response, HttpStatusCode.BadRequest, "Sign-in was not completed. You can close this tab.");
                return false;
            }

            code = codes[0];
            break;
        }

        TokenInfo? token = await ExchangeCodeForTokenAsync(credentials, code, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            WriteHttpResponse(context.Response, HttpStatusCode.BadRequest, "Authentication failed. You can close this tab.");
            return false;
        }

        await StoreTokenAsync(token, cancellationToken).ConfigureAwait(false);
        WriteHttpResponse(context.Response, HttpStatusCode.OK, "Authentication successful. Close this tab and return to Parc Spend.");
        Log.Information("OAuth login completed successfully.");
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> LogoutAsync()
    {
        await _tokenLock.WaitAsync().ConfigureAwait(false);
        try
        {
            File.Delete(_tokenFilePath);
            _tokenInfo = null;
            return true;
        }
        catch (IOException)
        {
            Log.Error("Failed to delete the stored Monzo token.");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Error("Access was denied while deleting the stored Monzo token.");
            return false;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureValidTokenAsync(CancellationToken cancellationToken = default)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenInfo is null)
            {
                _tokenInfo = LoadToken();
            }

            if (_tokenInfo is null)
            {
                return false;
            }

            if (_tokenInfo.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return true;
            }

            MonzoCredentials? credentials = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (credentials is null)
            {
                Log.Error("Token refresh failed: credentials not found.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_tokenInfo.RefreshToken))
            {
                Log.Error("Token refresh failed: refresh token is missing.");
                return false;
            }

            TokenInfo? refreshed = await RefreshTokenAsync(credentials, _tokenInfo.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
            {
                return false;
            }

            await SaveTokenAsync(refreshed, cancellationToken).ConfigureAwait(false);
            _tokenInfo = refreshed;
            return true;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task StoreTokenAsync(TokenInfo token, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveTokenAsync(token, cancellationToken).ConfigureAwait(false);
            _tokenInfo = token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <inheritdoc />
    public string? GetAccessToken() => _tokenInfo?.AccessToken;

    private static bool IsValidBearer(string token) =>
        token.Length is > 0 and <= 8192 && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || "-._~+/=".Contains(character));

    private static bool TryGetLoopbackRedirect(string input, out Uri? redirect) =>
        Uri.TryCreate(input, UriKind.Absolute, out redirect)
        && redirect.Scheme == Uri.UriSchemeHttp
        && (redirect.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || redirect.Host == "127.0.0.1")
        && redirect.Port > 0 && redirect.UserInfo.Length == 0 && redirect.Query.Length == 0
        && redirect.Fragment.Length == 0;

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private static void WriteHttpResponse(HttpListenerResponse response, HttpStatusCode statusCode, string body)
    {
        byte[] responseBuffer = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.ContentLength64 = responseBuffer.Length;
        try
        {
            using Stream output = response.OutputStream;
            output.Write(responseBuffer, 0, responseBuffer.Length);
        }
        catch (HttpListenerException)
        {
            Log.Debug("The OAuth browser tab closed before receiving a response.");
        }
        catch (IOException)
        {
            Log.Debug("Could not write the local OAuth response.");
        }
    }

    private async Task<MonzoCredentials?> LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_legacyCredentialsFilePath) && !File.Exists(_credentialsFilePath))
            {
                string legacyJson = await File.ReadAllTextAsync(_legacyCredentialsFilePath, cancellationToken).ConfigureAwait(false);
                MonzoCredentials? legacyCredentials = JsonConvert.DeserializeObject<MonzoCredentials>(legacyJson);
                if (IsValidCredentials(legacyCredentials))
                {
                    await SaveCredentialsInternalAsync(legacyCredentials!, cancellationToken).ConfigureAwait(false);
                    File.Delete(_legacyCredentialsFilePath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warning("Could not migrate legacy Monzo credentials ({ErrorType}).", exception.GetType().Name);
        }

        if (!File.Exists(_credentialsFilePath))
        {
            return null;
        }

        try
        {
            string encoded = await File.ReadAllTextAsync(_credentialsFilePath, cancellationToken).ConfigureAwait(false);
            byte[] protectedBytes = Convert.FromBase64String(encoded);
            byte[] unprotectedBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(unprotectedBytes);
            MonzoCredentials? credentials = JsonConvert.DeserializeObject<MonzoCredentials>(json);
            if (!IsValidCredentials(credentials))
            {
                Log.Error("Stored credentials are invalid.");
                return null;
            }

            return credentials;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("Could not load encrypted credentials ({ErrorType}).", exception.GetType().Name);
            return null;
        }
    }

    private static bool IsValidCredentials(MonzoCredentials? credentials)
    {
        return credentials is not null &&
            !string.IsNullOrWhiteSpace(credentials.ClientId) &&
            !string.IsNullOrWhiteSpace(credentials.ClientSecret) &&
            TryGetLoopbackRedirect(credentials.RedirectUri, out _);
    }

    private async Task SaveCredentialsInternalAsync(MonzoCredentials credentials, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_appDataDirectory);
        string json = JsonConvert.SerializeObject(credentials, Formatting.Indented);
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        string encoded = Convert.ToBase64String(protectedBytes);
        await LocalFiles.WriteAtomicAsync(_credentialsFilePath, encoded, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenInfo?> ExchangeCodeForTokenAsync(
        MonzoCredentials credentials,
        string code,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("client_id", credentials.ClientId),
            new KeyValuePair<string, string>("client_secret", credentials.ClientSecret),
            new KeyValuePair<string, string>("redirect_uri", credentials.RedirectUri),
            new KeyValuePair<string, string>("code", code),
        ]);

        return await RequestTokenAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenInfo?> RefreshTokenAsync(
        MonzoCredentials credentials,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", credentials.ClientId),
            new KeyValuePair<string, string>("client_secret", credentials.ClientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
        ]);

        return await RequestTokenAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TokenInfo?> RequestTokenAsync(FormUrlEncodedContent formContent, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = formContent,
        };

        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Monzo token request failed with status {StatusCode}.", (int)response.StatusCode);
            return null;
        }

        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        TokenResponse? tokenResponse;
        try
        {
            tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(payload);
        }
        catch (JsonException)
        {
            Log.Warning("Monzo returned a malformed token response.");
            return null;
        }
        if (tokenResponse is null || !IsValidBearer(tokenResponse.AccessToken))
        {
            Log.Error("Token response payload is invalid.");
            return null;
        }

        return new TokenInfo
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
            TokenType = tokenResponse.TokenType ?? "Bearer",
            UserId = tokenResponse.UserId,
        };
    }

    private async Task SaveTokenAsync(TokenInfo tokenInfo, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_appDataDirectory);
        string json = JsonConvert.SerializeObject(tokenInfo, Formatting.Indented);
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        string encoded = Convert.ToBase64String(protectedBytes);
        await LocalFiles.WriteAtomicAsync(_tokenFilePath, encoded, cancellationToken).ConfigureAwait(false);
    }

    private TokenInfo? LoadToken()
    {
        if (!File.Exists(_tokenFilePath))
        {
            return null;
        }

        try
        {
            string encoded = File.ReadAllText(_tokenFilePath);
            byte[] protectedBytes = Convert.FromBase64String(encoded);
            byte[] unprotectedBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(unprotectedBytes);
            TokenInfo? token = JsonConvert.DeserializeObject<TokenInfo>(json);
            return token is not null && IsValidBearer(token.AccessToken) ? token : null;
        }
        catch (Exception exception)
        {
            Log.Error("Failed to load the stored token ({ErrorType}).", exception.GetType().Name);
            return null;
        }
    }

    private sealed class MonzoCredentials
    {
        public string ClientId { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;

        public string RedirectUri { get; set; } = "http://localhost:8000/auth/callback";
    }

    private sealed class TokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonProperty("token_type")]
        public string? TokenType { get; set; }

        [JsonProperty("user_id")]
        public string? UserId { get; set; }
    }

    private sealed class TokenInfo
    {
        public string AccessToken { get; set; } = string.Empty;

        public string RefreshToken { get; set; } = string.Empty;

        public DateTimeOffset ExpiresAtUtc { get; set; }

        public string TokenType { get; set; } = "Bearer";

        public string? UserId { get; set; }
    }
}
