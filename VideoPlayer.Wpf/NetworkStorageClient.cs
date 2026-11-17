using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace VideoPlayer.Wpf;

public sealed class NetworkStorageClient
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".asf", ".webm", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp" };
    private static readonly HashSet<string> ConnectedSmbShares = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true, PreAuthenticate = true }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<StorageEntry>> BrowseAsync(StorageConnection connection, string? path = null, CancellationToken cancellationToken = default)
    {
        return connection.Kind switch
        {
            StorageKind.Smb => BrowseSmb(connection, path),
            StorageKind.WebDav => await BrowseWebDavAsync(connection, path, cancellationToken),
            StorageKind.LocalFolder => BrowseLocalFolder(connection, path),
            _ => throw new NotSupportedException("지원하지 않는 저장소입니다.")
        };
    }

    private static IReadOnlyList<StorageEntry> BrowseLocalFolder(StorageConnection connection, string? path)
    {
        var root = Path.GetFullPath(connection.Endpoint);
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? root : path);
        var relative = Path.GetRelativePath(root, directory);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("선택한 동기화 폴더 바깥으로 이동할 수 없습니다.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"동기화 폴더를 찾을 수 없습니다: {directory}");
        var items = new List<StorageEntry>();
        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            items.Add(new StorageEntry(Path.GetFileName(dir), dir, true));
        foreach (var file in Directory.EnumerateFiles(directory).Where(IsVideo).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            items.Add(new StorageEntry(Path.GetFileName(file), file, false, new FileInfo(file).Length));
        return items;
    }

    public async Task<bool> TestAsync(StorageConnection connection, CancellationToken cancellationToken = default)
    {
        var entries = await BrowseAsync(connection, null, cancellationToken);
        return entries.Count >= 0;
    }

    private static IReadOnlyList<StorageEntry> BrowseSmb(StorageConnection connection, string? path)
    {
        var root = connection.Endpoint.TrimEnd('\\');
        EnsureSmbConnection(root, connection.Username, UserSecret.Unprotect(connection.ProtectedPassword));
        var directory = string.IsNullOrWhiteSpace(path) ? root : path;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("저장소 경로가 연결된 공유 폴더 범위를 벗어났습니다.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {directory}");

        var items = new List<StorageEntry>();
        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            items.Add(new StorageEntry(Path.GetFileName(dir), dir, true));
        foreach (var file in Directory.EnumerateFiles(directory).Where(IsVideo).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            items.Add(new StorageEntry(Path.GetFileName(file), file, false, new FileInfo(file).Length));
        return items;
    }

    private static async Task<IReadOnlyList<StorageEntry>> BrowseWebDavAsync(StorageConnection connection, string? path, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(path) ? connection.Endpoint : path, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("WebDAV 주소는 HTTP 또는 HTTPS 주소여야 합니다.");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidOperationException("WebDAV 폴더 주소에 쿼리 또는 fragment를 사용할 수 없습니다.");

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.Add("Depth", "1");
        request.Headers.Add("User-Agent", "ohmyplayer/0.1");
        request.Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>");
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        if (!string.IsNullOrWhiteSpace(connection.Username))
        {
            var userPass = $"{connection.Username}:{UserSecret.Unprotect(connection.ProtectedPassword)}";
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userPass)));
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.MultiStatus && !response.IsSuccessStatusCode)
            throw new HttpRequestException($"WebDAV 응답 오류: {(int)response.StatusCode} {response.ReasonPhrase}");
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var xmlReader = XmlReader.Create(responseStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = await XDocument.LoadAsync(xmlReader, LoadOptions.None, cancellationToken);
        XNamespace dav = "DAV:";
        var entries = new List<StorageEntry>();
        foreach (var item in document.Descendants(dav + "response"))
        {
            var href = item.Element(dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(uri, href, out var itemUri)
                || (string.Equals(itemUri.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(itemUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(itemUri.AbsolutePath.TrimEnd('/'), uri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) continue;
            if (!IsWithinEndpoint(connection.Endpoint, itemUri)) continue;
            var prop = item.Descendants(dav + "prop").FirstOrDefault();
            var isFolder = prop?.Element(dav + "resourcetype")?.Element(dav + "collection") is not null;
            var name = Uri.UnescapeDataString(itemUri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? itemUri.Host);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!isFolder && !IsVideo(name)) continue;
            long? size = long.TryParse(prop?.Element(dav + "getcontentlength")?.Value, out var parsed) ? parsed : null;
            entries.Add(new StorageEntry(name, itemUri.AbsoluteUri, isFolder, size));
        }
        return entries.OrderByDescending(entry => entry.IsFolder).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsWithinEndpoint(string endpoint, Uri target)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var root)) return false;
        return string.Equals(root.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(root.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && root.Port == target.Port
            && target.AbsolutePath.StartsWith(root.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private static void EnsureSmbConnection(string path, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var share = GetShareRoot(path);
        if (ConnectedSmbShares.Contains(share)) return;
        var resource = new NetResource { Type = ResourceType.Disk, RemoteName = share };
        var result = WNetAddConnection2(ref resource, password, username, 0);
        if (result != 0 && result != 1219) throw new IOException($"SMB 인증에 실패했습니다. Windows 네트워크 오류 {result}.");
        if (result == 1219) throw new IOException("같은 SMB 서버에 다른 계정이 이미 연결되어 있습니다. Windows에서 기존 연결을 해제하거나 같은 계정 정보를 사용하세요.");
        ConnectedSmbShares.Add(share);
    }

    private static string GetShareRoot(string path)
    {
        if (!path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidOperationException("SMB 경로는 \\\\서버\\공유 형식이어야 합니다.");
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidOperationException("SMB 경로에 서버 이름과 공유 이름을 모두 입력하세요.");
        return $"\\\\{parts[0]}\\{parts[1]}";
    }

    private enum ResourceType : uint { Any = 0, Disk = 1, Print = 2 }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public ResourceType Scope;
        public ResourceType Type;
        public uint DisplayType;
        public uint Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, uint flags);
}
