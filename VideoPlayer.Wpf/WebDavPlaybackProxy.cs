using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace VideoPlayer.Wpf;

/// <summary>
/// Relays HTTP range requests through HttpClient for WebDAV servers whose redirects or
/// authentication are not handled consistently by the native player.
/// </summary>
public sealed class WebDavPlaybackProxy : IDisposable
{
    private readonly Uri _remoteUri;
    private readonly HttpClient _http;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _route;
    private bool _disposed;

    public string LocalMediaUri { get; }
    public string? LastError { get; private set; }

    public WebDavPlaybackProxy(Uri remoteUri, string? username, string? password)
    {
        _remoteUri = remoteUri;
        var handler = new HttpClientHandler { AllowAutoRedirect = true, PreAuthenticate = true };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _route = Guid.NewGuid().ToString("N");
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalMediaUri = $"http://127.0.0.1:{port}/{_route}/media";
        if (!string.IsNullOrWhiteSpace(username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ohmyplayer/0.1.17");
        _ = AcceptClientsAsync(_shutdown.Token);
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = RelayClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private async Task RelayClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var local = client.GetStream();
                using var reader = new StreamReader(local, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { await WriteErrorAsync(local, 400, "Bad Request", cancellationToken); return; }
                var method = parts[0].ToUpperInvariant();
                var target = parts[1];
                string? range = null;
                string? ifRange = null;
                string? accept = null;
                string? userAgent = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) range = value;
                    else if (name.Equals("If-Range", StringComparison.OrdinalIgnoreCase)) ifRange = value;
                    else if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase)) accept = value;
                    else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) userAgent = value;
                }

                var expectedPath = $"/{_route}/media";
                var requestPath = Uri.TryCreate(target, UriKind.Absolute, out var absoluteTarget)
                    ? absoluteTarget.AbsolutePath
                    : target.Split('?', 2)[0];
                if (!string.Equals(requestPath, expectedPath, StringComparison.Ordinal) || method is not ("GET" or "HEAD"))
                {
                    await WriteErrorAsync(local, method is "GET" or "HEAD" ? 404 : 405, method is "GET" or "HEAD" ? "Not Found" : "Method Not Allowed", cancellationToken);
                    return;
                }

                // A one-byte GET provides the full object length for local HEAD requests.
                var isHead = method == "HEAD";
                using var request = new HttpRequestMessage(HttpMethod.Get, _remoteUri);
                if (isHead) request.Headers.Range = new RangeHeaderValue(0, 0);
                else if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);
                if (!string.IsNullOrWhiteSpace(ifRange)) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
                if (!string.IsNullOrWhiteSpace(accept)) request.Headers.TryAddWithoutValidation("Accept", accept);
                if (!string.IsNullOrWhiteSpace(userAgent)) request.Headers.UserAgent.ParseAdd(userAgent);

                using var remote = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var remoteRange = remote.Content.Headers.ContentRange;
                if (!remote.IsSuccessStatusCode && remote.StatusCode != HttpStatusCode.PartialContent)
                {
                    LastError = $"PikPak WebDAV 서버가 {(int)remote.StatusCode} {remote.ReasonPhrase} 응답을 반환했습니다. 계정의 WebDAV 주소와 접근 권한을 확인하세요.";
                }
                var statusCode = isHead && remote.IsSuccessStatusCode ? 200 : (int)remote.StatusCode;
                var contentLength = isHead && remoteRange?.Length is long fullLength
                    ? fullLength
                    : remote.Content.Headers.ContentLength;
                var responseHeaders = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(ReasonPhrase(statusCode)).Append("\r\n")
                    .Append("Connection: close\r\n")
                    .Append("Accept-Ranges: bytes\r\n");
                var mediaType = remote.Content.Headers.ContentType?.ToString();
                if (!string.IsNullOrWhiteSpace(mediaType)) responseHeaders.Append("Content-Type: ").Append(mediaType).Append("\r\n");
                if (contentLength is long length) responseHeaders.Append("Content-Length: ").Append(length).Append("\r\n");
                if (!isHead && remoteRange is not null) responseHeaders.Append("Content-Range: ").Append(remoteRange).Append("\r\n");
                if (remote.Headers.ETag is not null) responseHeaders.Append("ETag: ").Append(remote.Headers.ETag).Append("\r\n");
                responseHeaders.Append("\r\n");
                await local.WriteAsync(Encoding.ASCII.GetBytes(responseHeaders.ToString()), cancellationToken);
                await local.FlushAsync(cancellationToken);
                if (!isHead && remote.Content.Headers.ContentLength != 0)
                {
                    await using var remoteStream = await remote.Content.ReadAsStreamAsync(cancellationToken);
                    await remoteStream.CopyToAsync(local, 81920, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { await WriteErrorAsync(client.GetStream(), 502, "Bad Gateway", cancellationToken); }
                catch (Exception) { }
            }
        }
    }

    private static async Task WriteErrorAsync(NetworkStream stream, int statusCode, string reason, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(reason);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {reason}\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        206 => "Partial Content",
        302 => "Found",
        304 => "Not Modified",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Upstream Response"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _listener.Stop();
        _http.Dispose();
        _shutdown.Dispose();
    }
}
