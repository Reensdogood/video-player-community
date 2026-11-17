using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoPlayer.Wpf;

public sealed class CloudMediaClient
{
    // ResponseHeadersRead keeps long media bodies outside HttpClient's header timeout.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly ConcurrentDictionary<string, TokenCache> Tokens = new();
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".asf", ".webm", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp"];
    private readonly CloudPlaybackRelay _relay = CloudPlaybackRelay.Instance;

    public async Task<CloudAccount> ConnectAsync(CloudProviderKind provider, string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("먼저 서비스 개발자 콘솔에서 발급한 Client ID를 입력하세요.");
        var port = provider switch { CloudProviderKind.GoogleDrive => 53680, CloudProviderKind.OneDrive => 53681, _ => 53682 };
        var redirectUri = provider switch
        {
            CloudProviderKind.GoogleDrive => $"http://127.0.0.1:{port}/",
            CloudProviderKind.OneDrive => $"http://localhost:{port}/",
            _ => $"http://localhost:{port}/oauth/callback/"
        };
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new InvalidOperationException($"OAuth 수신 포트 {port}를 사용할 수 없습니다. 앱을 닫고 다시 시도하세요.", ex); }

        var authUri = BuildAuthorizationUri(provider, clientId, redirectUri, state, challenge);
        Process.Start(new ProcessStartInfo(authUri.AbsoluteUri) { UseShellExecute = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var code = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];
        var error = context.Request.QueryString["error_description"] ?? context.Request.QueryString["error"];
        var html = string.IsNullOrWhiteSpace(error)
            ? "<html><meta charset='utf-8'><body style='font-family:sans-serif'>인증이 완료되었습니다. 이 창을 닫고 ohmyplayer로 돌아가세요.</body></html>"
            : "<html><meta charset='utf-8'><body style='font-family:sans-serif'>로그인이 취소되었거나 거부되었습니다. 이 창을 닫고 앱으로 돌아가세요.</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
        listener.Stop();
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException($"인증이 완료되지 않았습니다: {error}");
        if (string.IsNullOrWhiteSpace(code) || !string.Equals(state, returnedState, StringComparison.Ordinal)) throw new InvalidOperationException("OAuth 응답을 확인할 수 없습니다. 다시 연결하세요.");

        using var response = await Http.PostAsync(GetTokenEndpoint(provider), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        }), cancellationToken);
        await EnsureSuccessAsync(response, "OAuth 토큰 교환");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var refresh = GetString(json.RootElement, "refresh_token");
        if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("서비스에서 갱신 토큰을 반환하지 않았습니다. 권한과 오프라인 액세스 설정을 확인하세요.");

        var account = new CloudAccount { Provider = provider, ClientId = clientId, ProtectedRefreshToken = UserSecret.Protect(refresh) };
        var settings = AppSettingsStore.Load();
        settings.CloudAccounts.RemoveAll(existing => existing.Provider == provider);
        settings.CloudAccounts.Add(account);
        AppSettingsStore.Save(settings);
        Tokens.TryRemove(TokenKey(account), out _);
        await GetAccessTokenAsync(account, cancellationToken);
        return account;
    }

    public async Task<IReadOnlyList<StorageEntry>> BrowseAsync(CloudAccount account, string? folderId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(account, cancellationToken);
        return account.Provider switch
        {
            CloudProviderKind.GoogleDrive => await BrowseGoogleAsync(account, token, folderId, cancellationToken),
            CloudProviderKind.OneDrive => await BrowseOneDriveAsync(account, token, folderId, cancellationToken),
            CloudProviderKind.Dropbox => await BrowseDropboxAsync(account, token, folderId, cancellationToken),
            CloudProviderKind.NaverMyBox => await BrowseMyBoxAsync(account, token, folderId, cancellationToken),
            _ => throw new NotSupportedException("이 클라우드 공급자는 아직 지원되지 않습니다.")
        };
    }

    public async Task<CloudAccount> ConnectMyBoxAsync(string personalAccessToken, CancellationToken cancellationToken = default)
    {
        var token = personalAccessToken.Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("MYBOX 설정에서 발급한 개인 액세스 토큰을 입력하세요.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://open-api.mybox.naver.com/v1/drive/resources?count=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "MYBOX 토큰 확인");
        var account = new CloudAccount { Provider = CloudProviderKind.NaverMyBox, ProtectedRefreshToken = UserSecret.Protect(token) };
        var settings = AppSettingsStore.Load();
        settings.CloudAccounts.RemoveAll(existing => existing.Provider == CloudProviderKind.NaverMyBox);
        settings.CloudAccounts.Add(account);
        AppSettingsStore.Save(settings);
        return account;
    }

    public async Task DisconnectAsync(CloudProviderKind provider)
    {
        var settings = AppSettingsStore.Load();
        var account = settings.CloudAccounts.FirstOrDefault(item => item.Provider == provider);
        settings.CloudAccounts.RemoveAll(item => item.Provider == provider);
        AppSettingsStore.Save(settings);
        if (account is not null) Tokens.TryRemove(TokenKey(account), out _);
        await Task.CompletedTask;
    }

    private async Task<IReadOnlyList<StorageEntry>> BrowseGoogleAsync(CloudAccount account, string token, string? folderId, CancellationToken cancellationToken)
    {
        var parent = string.IsNullOrWhiteSpace(folderId) ? "root" : folderId;
        var query = $"'{parent.Replace("'", "\\'", StringComparison.Ordinal)}' in parents and trashed = false";
        var url = "https://www.googleapis.com/drive/v3/files?q=" + Uri.EscapeDataString(query) + "&pageSize=1000&orderBy=name&fields=nextPageToken,files(id,name,mimeType,size)";
        var items = new List<StorageEntry>();
        while (!string.IsNullOrEmpty(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "Google Drive 목록 조회");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                var id = GetString(file, "id");
                var name = GetString(file, "name");
                var isFolder = GetString(file, "mimeType") == "application/vnd.google-apps.folder";
                if (isFolder || IsVideo(name))
                {
                    long? size = long.TryParse(GetString(file, "size"), out var parsed) ? parsed : null;
                    var playback = isFolder ? null : _relay.Register(context => ProxyGoogleAsync(account, id, context));
                    items.Add(new StorageEntry(name, id, isFolder, size, playback, account.Provider));
                }
            }
            url = json.RootElement.TryGetProperty("nextPageToken", out var page) ? "https://www.googleapis.com/drive/v3/files?q=" + Uri.EscapeDataString(query) + $"&pageSize=1000&orderBy=name&fields=nextPageToken,files(id,name,mimeType,size)&pageToken={Uri.EscapeDataString(page.GetString() ?? "")}" : "";
        }
        return items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<StorageEntry>> BrowseOneDriveAsync(CloudAccount account, string token, string? folderId, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(folderId) || folderId == "root"
            ? "https://graph.microsoft.com/v1.0/me/drive/root/children?$select=id,name,file,folder,size&$top=200"
            : $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(folderId)}/children?$select=id,name,file,folder,size&$top=200";
        var items = new List<StorageEntry>();
        while (!string.IsNullOrEmpty(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "OneDrive 목록 조회");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var file in json.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = GetString(file, "id");
                var name = GetString(file, "name");
                var isFolder = file.TryGetProperty("folder", out _);
                if (isFolder || IsVideo(name))
                {
                    long? size = long.TryParse(GetString(file, "size"), out var parsed) ? parsed : null;
                    var playback = isFolder ? null : _relay.Register(context => ProxyOneDriveAsync(account, id, context));
                    items.Add(new StorageEntry(name, id, isFolder, size, playback, account.Provider));
                }
            }
            url = json.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<StorageEntry>> BrowseDropboxAsync(CloudAccount account, string token, string? folderId, CancellationToken cancellationToken)
    {
        var currentPath = folderId ?? "";
        var items = new List<StorageEntry>();
        using var first = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder")
        {
            Content = JsonContent.Create(new { path = currentPath, recursive = false, include_non_downloadable_files = false, limit = 2000 })
        };
        first.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(first, cancellationToken);
        await EnsureSuccessAsync(response, "Dropbox 목록 조회");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        AddDropboxEntries(json.RootElement, account, items);
        var hasMore = json.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean();
        var cursor = GetString(json.RootElement, "cursor");
        while (hasMore && !string.IsNullOrEmpty(cursor))
        {
            using var nextRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder/continue") { Content = JsonContent.Create(new { cursor }) };
            nextRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var nextResponse = await Http.SendAsync(nextRequest, cancellationToken);
            await EnsureSuccessAsync(nextResponse, "Dropbox 다음 페이지 조회");
            using var nextJson = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync(cancellationToken));
            AddDropboxEntries(nextJson.RootElement, account, items);
            hasMore = nextJson.RootElement.TryGetProperty("has_more", out more) && more.GetBoolean();
            cursor = GetString(nextJson.RootElement, "cursor");
        }
        return items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void AddDropboxEntries(JsonElement root, CloudAccount account, List<StorageEntry> output)
    {
        if (!root.TryGetProperty("entries", out var entries)) return;
        foreach (var entry in entries.EnumerateArray())
        {
            var tag = GetString(entry, ".tag");
            var path = GetString(entry, "id");
            var name = GetString(entry, "name");
            var isFolder = tag == "folder";
            if (!isFolder && !IsVideo(name)) continue;
            long? size = long.TryParse(GetString(entry, "size"), out var parsed) ? parsed : null;
            var playback = isFolder ? null : _relay.Register(context => ProxyDropboxAsync(account, path, context));
            output.Add(new StorageEntry(name, path, isFolder, size, playback, account.Provider));
        }
    }

    private async Task<IReadOnlyList<StorageEntry>> BrowseMyBoxAsync(CloudAccount account, string token, string? folderId, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(folderId)
            ? "https://open-api.mybox.naver.com/v1/drive/resources?sort=name%2Casc&count=1000"
            : $"https://open-api.mybox.naver.com/v1/drive/folders/{Uri.EscapeDataString(folderId)}/resources?sort=name%2Casc&count=1000";
        var items = new List<StorageEntry>();
        while (!string.IsNullOrEmpty(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "MYBOX 폴더 목록 조회");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("resources", out var resources))
            {
                foreach (var item in resources.EnumerateArray())
                {
                    var name = GetString(item, "name");
                    var id = GetString(item, "resourceId");
                    var isFolder = GetString(item, "type").Equals("folder", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(id) || (!isFolder && !IsVideo(name))) continue;
                    long? size = item.TryGetProperty("size", out var sizeJson) && sizeJson.TryGetInt64(out var parsed) ? parsed : null;
                    var playback = isFolder ? null : _relay.Register(context => ProxyMyBoxAsync(account, id, context));
                    items.Add(new StorageEntry(name, id, isFolder, size, playback, account.Provider));
                }
            }
            var nextCursor = json.RootElement.TryGetProperty("responseMetaData", out var metadata) ? GetString(metadata, "nextCursor") : "";
            url = string.IsNullOrWhiteSpace(nextCursor) ? "" : AddQuery(url.Split('?')[0] + "?sort=name%2Casc&count=1000", "cursor", nextCursor);
        }
        return items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<string> GetAccessTokenAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        if (account.Provider == CloudProviderKind.NaverMyBox) return UserSecret.Unprotect(account.ProtectedRefreshToken);
        var cache = Tokens.GetOrAdd(TokenKey(account), _ => new TokenCache());
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.AccessToken is not null && cache.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cache.AccessToken;
            var refreshToken = UserSecret.Unprotect(account.ProtectedRefreshToken);
            using var response = await Http.PostAsync(GetTokenEndpoint(account.Provider), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = account.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }), cancellationToken);
            await EnsureSuccessAsync(response, "OAuth 토큰 갱신");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            cache.AccessToken = GetString(json.RootElement, "access_token");
            var expiresIn = int.TryParse(GetString(json.RootElement, "expires_in"), out var seconds) ? seconds : 3600;
            cache.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            var rotatedRefresh = GetString(json.RootElement, "refresh_token");
            if (!string.IsNullOrWhiteSpace(rotatedRefresh))
            {
                account.ProtectedRefreshToken = UserSecret.Protect(rotatedRefresh);
                var settings = AppSettingsStore.Load();
                var saved = settings.CloudAccounts.FirstOrDefault(item => item.Provider == account.Provider);
                if (saved is not null) { saved.ProtectedRefreshToken = account.ProtectedRefreshToken; AppSettingsStore.Save(settings); }
            }
            return cache.AccessToken;
        }
        finally { cache.Gate.Release(); }
    }

    private async Task ProxyGoogleAsync(CloudAccount account, string fileId, HttpListenerContext context)
    {
        var token = await GetAccessTokenAsync(account, CancellationToken.None);
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        CopyRange(context.Request, request);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await CopyResponseAsync(context, response);
    }

    private async Task ProxyOneDriveAsync(CloudAccount account, string itemId, HttpListenerContext context)
    {
        var token = await GetAccessTokenAsync(account, CancellationToken.None);
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(itemId)}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        CopyRange(context.Request, request);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            using var redirected = new HttpRequestMessage(HttpMethod.Get, location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location));
            CopyRange(context.Request, redirected);
            using var redirectedResponse = await Http.SendAsync(redirected, HttpCompletionOption.ResponseHeadersRead);
            await CopyResponseAsync(context, redirectedResponse);
            return;
        }
        await CopyResponseAsync(context, response);
    }

    private async Task ProxyDropboxAsync(CloudAccount account, string fileId, HttpListenerContext context)
    {
        var token = await GetAccessTokenAsync(account, CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://content.dropboxapi.com/2/files/download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = fileId }));
        CopyRange(context.Request, request);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await CopyResponseAsync(context, response);
    }

    private async Task ProxyMyBoxAsync(CloudAccount account, string fileId, HttpListenerContext context)
    {
        var token = await GetAccessTokenAsync(account, CancellationToken.None);
        using var urlRequest = new HttpRequestMessage(HttpMethod.Get, $"https://open-api.mybox.naver.com/v1/drive/files/{Uri.EscapeDataString(fileId)}/download");
        urlRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var urlResponse = await Http.SendAsync(urlRequest, CancellationToken.None);
        await EnsureSuccessAsync(urlResponse, "MYBOX 재생 URL 요청");
        using var json = JsonDocument.Parse(await urlResponse.Content.ReadAsStringAsync());
        var downloadUrl = GetString(json.RootElement, "downloadUrl");
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("MYBOX에서 유효한 HTTPS 다운로드 주소를 받지 못했습니다.");
        for (var redirects = 0; redirects <= 4; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            CopyRange(context.Request, request);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("MYBOX 재생 주소가 HTTPS 외부로 이동하려고 합니다.");
                uri = next;
                continue;
            }
            await CopyResponseAsync(context, response);
            return;
        }
        throw new HttpRequestException("MYBOX 재생 주소가 너무 여러 번 이동했습니다.");
    }

    private static async Task CopyResponseAsync(HttpListenerContext context, HttpResponseMessage upstream)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
        if (upstream.Content.Headers.ContentLength is { } length) context.Response.ContentLength64 = length;
        if (upstream.Content.Headers.ContentRange is { } range) context.Response.Headers["Content-Range"] = range.ToString();
        if (upstream.Headers.AcceptRanges.Any()) context.Response.Headers["Accept-Ranges"] = string.Join(",", upstream.Headers.AcceptRanges);
        if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) { context.Response.Close(); return; }
        await using var stream = await upstream.Content.ReadAsStreamAsync();
        await stream.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }

    private static void CopyRange(HttpListenerRequest source, HttpRequestMessage target)
    {
        var rangeText = source.Headers["Range"];
        if (!string.IsNullOrWhiteSpace(rangeText) && RangeHeaderValue.TryParse(rangeText, out var range)) target.Headers.Range = range;
    }

    private static Uri BuildAuthorizationUri(CloudProviderKind provider, string clientId, string redirectUri, string state, string challenge)
    {
        var (endpoint, scopes) = provider switch
        {
            CloudProviderKind.GoogleDrive => ("https://accounts.google.com/o/oauth2/v2/auth", "https://www.googleapis.com/auth/drive.readonly"),
            CloudProviderKind.OneDrive => ("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "offline_access User.Read Files.Read"),
            _ => ("https://www.dropbox.com/oauth2/authorize", "files.metadata.read files.content.read")
        };
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        if (provider == CloudProviderKind.GoogleDrive) { query["access_type"] = "offline"; query["prompt"] = "consent"; }
        if (provider == CloudProviderKind.Dropbox) { query["token_access_type"] = "offline"; query["redirect_uri"] = redirectUri; }
        return new Uri(endpoint + "?" + string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    private static string GetTokenEndpoint(CloudProviderKind provider) => provider switch
    {
        CloudProviderKind.GoogleDrive => "https://oauth2.googleapis.com/token",
        CloudProviderKind.OneDrive => "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        _ => "https://api.dropbox.com/oauth2/token"
    };

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync();
        if (detail.Length > 500) detail = detail[..500];
        throw new HttpRequestException($"{operation}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
    }

    private static bool IsVideo(string name) => VideoExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    private static string GetString(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static string AddQuery(string url, string key, string value) => url + (url.Contains('?') ? "&" : "?") + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
    private static string TokenKey(CloudAccount account) => $"{account.Provider}:{account.ClientId}";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed class TokenCache { public SemaphoreSlim Gate { get; } = new(1, 1); public string? AccessToken { get; set; } public DateTimeOffset ExpiresAt { get; set; } }
}

public sealed class CloudPlaybackRelay
{
    private static readonly Lazy<CloudPlaybackRelay> LazyInstance = new(() => new CloudPlaybackRelay());
    private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _handlers = new();
    private readonly HttpListener _listener = new();
    private readonly string _baseAddress;
    public static CloudPlaybackRelay Instance => LazyInstance.Value;

    private CloudPlaybackRelay()
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        _baseAddress = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_baseAddress);
        _listener.Start();
        _ = ListenAsync();
    }

    public string Register(Func<HttpListenerContext, Task> handler)
    {
        var id = Guid.NewGuid().ToString("N");
        _handlers[id] = handler;
        return _baseAddress + "media/" + id;
    }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var id = context.Request.Url?.AbsolutePath.Split('/').LastOrDefault();
        if (id is null || !_handlers.TryGetValue(id, out var handler)) { context.Response.StatusCode = 404; context.Response.Close(); return; }
        try { await handler(context); }
        catch
        {
            try { context.Response.StatusCode = 502; context.Response.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
