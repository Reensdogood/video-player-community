using LibVLCSharp.Shared;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VideoPlayer.Wpf;

public sealed record PlaylistItem(string Mrl, string Title, bool IsRemote, string? Username = null, string? Password = null, string? PlaybackUri = null, string? LocationLabel = null, bool IsPlatformPage = false)
{
    public string DisplayLocation => LocationLabel ?? Mrl;
}
public sealed record TrackChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly ObservableCollection<PlaylistItem> _playlist = [];
    private readonly NetworkStorageClient _storageClient = new();
    private readonly CloudMediaClient _cloudClient = new();
    private readonly Stack<string> _storagePathHistory = new();
    private readonly Stack<string> _cloudPathHistory = new();
    private readonly SavedSettings _settings;
    private PlayerWindow? _playerWindow;
    private bool _preferAutoSubtitle;
    private int _currentIndex = -1;
    private StorageConnection? _activeStorage;
    private string? _activeStoragePath;
    private CloudAccount? _activeCloudAccount;
    private string? _activeCloudFolderId;
    private bool _loadingStorage;
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    public MainWindow()
    {
        InitializeComponent();
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet");
        _player = new MediaPlayer(_libVlc) { Volume = 80 };
        _settings = AppSettingsStore.Load();
        RefreshStorageSources();
        RefreshCloudSourceButtons();
        PlaylistList.ItemsSource = _playlist;
        Loaded += async (_, _) => await UpdateService.CheckInBackgroundAsync(this);
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_currentIndex >= 0 && _currentIndex + 1 < _playlist.Count) PlayAt(_currentIndex + 1);
            else _playerWindow?.MarkPlaybackEnded();
        });
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => MessageBox.Show(this, "미디어를 열 수 없습니다. URL이 직접 재생 가능한 미디어 주소인지, 네트워크 접근이 가능한지 확인하세요.", "재생 오류", MessageBoxButton.OK, MessageBoxImage.Warning));
        Closed += (_, _) =>
        {
            _playerWindow?.Close();
            _player.Stop();
            _player.Dispose();
            _libVlc.Dispose();
        };
    }

    private void OpenFiles_Click(object sender, RoutedEventArgs e)
        => OpenVideoFiles(this);

    private void OpenFilesFromPlayer() => OpenVideoFiles((Window?)_playerWindow ?? this);

    private void OpenVideoFiles(Window owner)
    {
        var dialog = new OpenFileDialog { Title = "영상 파일 선택", Multiselect = true, Filter = "미디어 파일|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.asf;*.webm;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg;*.flv;*.ogv;*.3gp|모든 파일|*.*" };
        if (dialog.ShowDialog(owner) == true) AddItems(dialog.FileNames.Select(path => new PlaylistItem(path, Path.GetFileName(path), false)));
    }

    private void AddItems(IEnumerable<PlaylistItem> items, bool playNow = false)
    {
        var added = items.ToList();
        foreach (var item in added) _playlist.Add(item);
        RefreshPlaylistUi();
        if (playNow && added.Count > 0) PlayAt(_playlist.Count - added.Count);
    }

    private void RefreshPlaylistUi()
    {
        PlaylistCount.Text = $"{_playlist.Count}개";
        PlaylistEmptyState.Visibility = _playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearPlaylistButton.Visibility = _playlist.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemovePlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistItem item }) return;
        var index = _playlist.IndexOf(item);
        if (index < 0) return;

        var removesCurrent = index == _currentIndex;
        var precedesCurrent = index < _currentIndex;
        _playlist.RemoveAt(index);
        if (removesCurrent) PlaylistList.SelectedIndex = -1;
        else if (precedesCurrent) PlaylistList.SelectedIndex = _currentIndex - 1;

        if (removesCurrent)
        {
            _currentIndex = -1;
            _player.Stop();
            _playerWindow?.ShowEmptyState();
        }
        else if (precedesCurrent)
        {
            _currentIndex--;
        }

        RefreshPlaylistUi();
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.Count == 0) return;
        var result = MessageBox.Show(this, "재생목록을 모두 비울까요? 원본 동영상 파일은 삭제되지 않습니다.", "재생목록 비우기", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        PlaylistList.SelectedIndex = -1;
        _playlist.Clear();
        _currentIndex = -1;
        _player.Stop();
        _playerWindow?.ShowEmptyState();
        RefreshPlaylistUi();
    }

    private void PlayUrl_Click(object sender, RoutedEventArgs e) => PlayUrl();
    private void UrlBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) PlayUrl(); }
    private void PlayUrl()
    {
        var text = UrlBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "HTTP 또는 HTTPS 영상 주소를 입력하세요.", "영상 주소 필요", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TryGetPlatformName(uri, out var platform))
        {
            AddItems([new PlaylistItem(uri.AbsoluteUri, $"{platform} 영상", true, LocationLabel: platform, IsPlatformPage: true)], playNow: true);
            return;
        }
        var filename = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        AddItems([new PlaylistItem(uri.AbsoluteUri, filename.Length > 0 ? filename : uri.Host, true)], playNow: true);
    }

    private static bool TryGetPlatformName(Uri uri, out string platform)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtube.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal)) platform = "YouTube";
        else if (host == "vimeo.com" || host.EndsWith(".vimeo.com", StringComparison.Ordinal)) platform = "Vimeo";
        else if (host == "x.com" || host.EndsWith(".x.com", StringComparison.Ordinal) || host == "twitter.com" || host.EndsWith(".twitter.com", StringComparison.Ordinal) || host == "t.co") platform = "X";
        else if (host == "instagram.com" || host.EndsWith(".instagram.com", StringComparison.Ordinal) || host == "instagr.am" || host.EndsWith(".instagr.am", StringComparison.Ordinal)) platform = "Instagram";
        else if (host == "facebook.com" || host.EndsWith(".facebook.com", StringComparison.Ordinal) || host == "fb.watch") platform = "Facebook";
        else if (host == "threads.net" || host.EndsWith(".threads.net", StringComparison.Ordinal)) platform = "Threads";
        else platform = "";
        return platform.Length > 0;
    }

    private async void DownloadUrl_Click(object sender, RoutedEventArgs e)
    {
        var text = UrlBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "HTTP 또는 HTTPS 직접 영상 파일 주소를 입력하세요.", "영상 주소 필요", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TryGetPlatformName(uri, out var platform))
        {
            MessageBox.Show(this, $"{platform} 페이지 주소에서는 다운로드를 제공하지 않습니다. 서비스가 공식적으로 내려받기를 허용하고 앱에서 제공하는 직접 파일 주소가 있을 때만 저장할 수 있습니다.", "다운로드 제한", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var filename = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(filename)) filename = "video";
        var dialog = new SaveFileDialog { Title = "영상 저장", FileName = filename, Filter = "모든 파일|*.*", AddExtension = false };
        if (dialog.ShowDialog(this) != true) return;
        var partialPath = dialog.FileName + $".{Guid.NewGuid():N}.download";
        try
        {
            using var response = await DownloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("주소가 동영상 파일이 아니라 웹 페이지, API 응답 또는 스트리밍 목록을 반환했습니다.");
            using var source = await response.Content.ReadAsStreamAsync();
            await using (var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(target);
            File.Move(partialPath, dialog.FileName, overwrite: true);
            MessageBox.Show(this, "영상 파일을 저장했습니다.", "다운로드 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch (IOException) { }
            MessageBox.Show(this, $"파일을 저장하지 못했습니다. 직접 영상 파일 URL인지 확인하세요.\n\n{ex.Message}", "다운로드 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;
        _currentIndex = index;
        PlaylistList.SelectedIndex = index;
        var item = _playlist[index];
        if (item.IsPlatformPage)
        {
            _player.Stop();
            ShowPlayerWindow();
            _playerWindow?.ShowPlatformPage(new Uri(item.Mrl), item.Title);
            return;
        }
        var mediaUri = item.PlaybackUri ?? item.Mrl;
        using var media = item.IsRemote ? new Media(_libVlc, new Uri(mediaUri)) : new Media(_libVlc, mediaUri, FromType.FromPath);
        if (item.IsRemote && !string.IsNullOrWhiteSpace(item.Username))
        {
            media.AddOption($":http-user={item.Username}");
            media.AddOption($":http-pwd={item.Password ?? ""}");
        }
        var playerSettings = AppSettingsStore.Load();
        media.AddOption($":freetype-font={playerSettings.SubtitleFontFamily}");
        media.AddOption($":freetype-fontsize={Math.Clamp(playerSettings.SubtitleFontSize, 12, 72)}");
        if (playerSettings.SubtitleBold) media.AddOption(":freetype-bold");
        media.AddOption($":freetype-outline-thickness={playerSettings.SubtitleOutline switch { "없음" => "0", "두껍게" => "0.2", _ => "0.1" }}");
        _preferAutoSubtitle = false;
        string? translationSubtitlePath = null;
        if (!item.IsRemote)
        {
            foreach (var subtitle in FindMatchingSubtitles(item.Mrl))
            {
                _preferAutoSubtitle |= media.AddSlave(MediaSlaveType.Subtitle, 2, new Uri(subtitle));
                var extension = Path.GetExtension(subtitle);
                if (translationSubtitlePath is null && (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))) translationSubtitlePath = subtitle;
            }
        }
        ShowPlayerWindow();
        _playerWindow?.SetNowPlaying(item.Title, _preferAutoSubtitle, item.IsRemote ? null : item.Mrl, translationSubtitlePath);
        // Show the WPF VideoView before starting LibVLC. Starting playback first can make
        // LibVLC create its own native video window, leaving the in-window controls blank.
        _player.Play(media);
    }

    private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(PlaylistList, source) is not ListBoxItem { DataContext: PlaylistItem item }) return;
        PlayAt(_playlist.IndexOf(item));
    }
    private void PlayPlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaylistItem item }) PlayAt(_playlist.IndexOf(item));
        e.Handled = true;
    }
    private void Next() { if (_playlist.Count > 0) PlayAt((_currentIndex + 1) % _playlist.Count); }
    private void Previous() { if (_playlist.Count > 0) PlayAt((_currentIndex - 1 + _playlist.Count) % _playlist.Count); }
    private void StopPlayback() { _player.Stop(); _playerWindow?.ShowEmptyState(); }

    private void Window_DragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var videoExt = new HashSet<string>([".mp4", ".mkv", ".avi", ".mov", ".wmv", ".asf", ".webm", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp"], StringComparer.OrdinalIgnoreCase);
            AddItems(files.Where(File.Exists).Where(f => videoExt.Contains(Path.GetExtension(f))).Select(f => new PlaylistItem(f, Path.GetFileName(f), false)));
        }
    }

    private static IEnumerable<string> FindMatchingSubtitles(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) yield break;
        var extensionOrder = new[] { ".srt", ".vtt", ".ass", ".ssa", ".smi", ".sami", ".sub", ".idx", ".ttml", ".usf" };
        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (IOException) { yield break; }
        foreach (var path in files
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), videoName, StringComparison.OrdinalIgnoreCase))
            .Where(path => extensionOrder.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => Array.FindIndex(extensionOrder, ext => string.Equals(ext, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase))))
            yield return path;
    }

    private void LocalSource_Click(object sender, RoutedEventArgs e)
    {
        LocalContent.Visibility = Visibility.Visible;
        CloudContent.Visibility = Visibility.Collapsed;
        LocalSourceButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 238, 242));
        LocalSourceButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(134, 175, 194));
    }
    private async void CloudSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string provider }) return;
        if (provider == "PikPak")
        {
            var pikpak = _settings.StorageConnections.FirstOrDefault(connection => connection.Kind == StorageKind.WebDav && connection.Name.Contains("PikPak", StringComparison.OrdinalIgnoreCase));
            if (pikpak is not null) { await OpenStorageAsync(pikpak); return; }
            LocalContent.Visibility = Visibility.Collapsed;
            CloudContent.Visibility = Visibility.Visible;
            CloudTitle.Text = "PikPak";
            CloudFilesList.ItemsSource = null;
            CloudStatusText.Text = "PikPak Premium 설정에서 WebDAV를 켜고 전용 자격 증명을 만든 뒤 ‘저장소 연결’에서 WebDAV 주소를 추가하세요.";
            return;
        }
        _activeStorage = null;
        _activeStoragePath = null;
        _activeCloudAccount = null;
        LocalContent.Visibility = Visibility.Collapsed;
        CloudContent.Visibility = Visibility.Visible;
        CloudTitle.Text = provider;
        CloudFilesList.ItemsSource = null;
        CloudBackButton.Visibility = Visibility.Collapsed;
        var cloudProvider = provider switch { "Google Drive" => CloudProviderKind.GoogleDrive, "OneDrive" => CloudProviderKind.OneDrive, "Dropbox" => CloudProviderKind.Dropbox, "Naver MYBOX" => CloudProviderKind.NaverMyBox, _ => (CloudProviderKind?)null };
        var account = cloudProvider is null ? null : _settings.CloudAccounts.FirstOrDefault(item => item.Provider == cloudProvider.Value);
        if (account is not null)
        {
            _activeStorage = null;
            _activeStoragePath = null;
            _activeCloudAccount = account;
            _activeCloudFolderId = null;
            _cloudPathHistory.Clear();
            await BrowseActiveCloudAsync();
        }
        else
        {
            var syncFolder = cloudProvider is null ? null : _settings.StorageConnections.FirstOrDefault(item => item.Kind == StorageKind.LocalFolder && item.CloudProvider == cloudProvider.Value);
            if (syncFolder is not null) await OpenStorageAsync(syncFolder);
            else if (cloudProvider is not null) await ChooseCloudSyncFolderAsync(cloudProvider.Value);
            else CloudStatusText.Text = "클라우드 연결 방법을 선택하세요.";
        }
        LocalSourceButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 255, 255));
        LocalSourceButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 208, 222));
    }
    private void CloudSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.SelectCloudTab();
        settings.ShowDialog();
        ReloadConnections();
    }

    private void ManageStorage_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.SelectStorageTab();
        settings.ShowDialog();
        ReloadConnections();
    }

    private void ReloadConnections()
    {
        var latest = AppSettingsStore.Load();
        _settings.StorageConnections = latest.StorageConnections;
        _settings.CloudAccounts = latest.CloudAccounts;
        RefreshStorageSources();
        RefreshCloudSourceButtons();
    }

    private void RefreshCloudSourceButtons()
    {
        GoogleSourceButton.Content = $"Google Drive · {CloudStatus(CloudProviderKind.GoogleDrive)}";
        OneDriveSourceButton.Content = $"OneDrive · {CloudStatus(CloudProviderKind.OneDrive)}";
        DropboxSourceButton.Content = $"Dropbox · {CloudStatus(CloudProviderKind.Dropbox)}";
        MyBoxSourceButton.Content = $"Naver MYBOX · {CloudStatus(CloudProviderKind.NaverMyBox)}";
    }

    private string CloudStatus(CloudProviderKind provider) => _settings.CloudAccounts.Any(item => item.Provider == provider)
        || _settings.StorageConnections.Any(item => item.Kind == StorageKind.LocalFolder && item.CloudProvider == provider)
        ? "연결됨" : "폴더 연결";

    private void RefreshStorageSources()
    {
        StorageSourceButtons.Children.Clear();
        foreach (var connection in _settings.StorageConnections.Where(connection => connection.Kind != StorageKind.LocalFolder))
        {
            var button = new Button
            {
                Content = $"{connection.Name} · {(connection.Kind == StorageKind.Smb ? "SMB" : "WebDAV")}",
                Tag = connection,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 5)
            };
            button.Click += StorageSource_Click;
            StorageSourceButtons.Children.Add(button);
        }
    }

    private async Task ChooseCloudSyncFolderAsync(CloudProviderKind provider)
    {
        var providerName = provider switch
        {
            CloudProviderKind.GoogleDrive => "Google Drive",
            CloudProviderKind.OneDrive => "OneDrive",
            CloudProviderKind.Dropbox => "Dropbox",
            _ => "Naver MYBOX"
        };
        var dialog = new OpenFolderDialog { Title = $"{providerName} PC 동기화 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            CloudStatusText.Text = "폴더 선택을 취소했습니다. 연결하려면 설정에서 다시 시도하세요.";
            return;
        }
        var path = Path.GetFullPath(dialog.FolderName);
        if (!Directory.Exists(path))
        {
            CloudStatusText.Text = "선택한 폴더에 접근할 수 없습니다.";
            return;
        }
        var connection = _settings.StorageConnections.FirstOrDefault(item => item.Kind == StorageKind.LocalFolder && item.CloudProvider == provider);
        if (connection is null)
        {
            connection = new StorageConnection { Kind = StorageKind.LocalFolder, CloudProvider = provider };
            _settings.StorageConnections.Add(connection);
        }
        connection.Name = $"{providerName} 동기화 폴더";
        connection.Endpoint = path;
        AppSettingsStore.Save(_settings);
        RefreshCloudSourceButtons();
        CloudStatusText.Text = "동기화 폴더에서 파일 목록을 읽는 중…";
        await OpenStorageAsync(connection);
    }

    private async void StorageSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StorageConnection connection }) return;
        await OpenStorageAsync(connection);
    }

    private async Task OpenStorageAsync(StorageConnection connection)
    {
        _activeStorage = connection;
        _activeCloudAccount = null;
        _activeStoragePath = null;
        _storagePathHistory.Clear();
        _cloudPathHistory.Clear();
        LocalContent.Visibility = Visibility.Collapsed;
        CloudContent.Visibility = Visibility.Visible;
        CloudTitle.Text = connection.Name;
        CloudBackButton.Visibility = Visibility.Collapsed;
        await BrowseActiveStorageAsync();
    }

    private async void CloudBack_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCloudAccount is not null)
        {
            if (_cloudPathHistory.Count == 0) return;
            _activeCloudFolderId = _cloudPathHistory.Pop();
            CloudBackButton.Visibility = _cloudPathHistory.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            await BrowseActiveCloudAsync();
            return;
        }
        if (_storagePathHistory.Count == 0) return;
        _activeStoragePath = _storagePathHistory.Pop();
        CloudBackButton.Visibility = _storagePathHistory.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        await BrowseActiveStorageAsync();
    }

    private async Task BrowseActiveStorageAsync()
    {
        if (_activeStorage is null) return;
        _loadingStorage = true;
        CloudFilesList.ItemsSource = null;
        CloudStatusText.Text = "폴더 목록을 불러오는 중…";
        try
        {
            CloudFilesList.ItemsSource = await _storageClient.BrowseAsync(_activeStorage, _activeStoragePath);
            var count = CloudFilesList.Items.Count;
            CloudStatusText.Text = count == 0 ? "재생 가능한 영상이나 하위 폴더가 없습니다." : $"{count}개 항목 · 폴더를 선택해 이동하고 영상을 선택해 재생합니다.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            CloudStatusText.Text = $"목록을 불러오지 못했습니다: {ex.Message}";
        }
        finally { _loadingStorage = false; }
    }

    private async Task BrowseActiveCloudAsync()
    {
        if (_activeCloudAccount is null) return;
        _loadingStorage = true;
        CloudFilesList.ItemsSource = null;
        CloudStatusText.Text = "클라우드 폴더를 불러오는 중…";
        try
        {
            CloudFilesList.ItemsSource = await _cloudClient.BrowseAsync(_activeCloudAccount, _activeCloudFolderId);
            CloudStatusText.Text = CloudFilesList.Items.Count == 0 ? "재생 가능한 영상이나 하위 폴더가 없습니다." : $"{CloudFilesList.Items.Count}개 항목 · 공식 읽기 API로 표시";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            CloudStatusText.Text = $"클라우드 목록을 불러오지 못했습니다: {ex.Message}";
        }
        finally { _loadingStorage = false; }
    }

    private async void CloudFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStorage || CloudFilesList.SelectedItem is not StorageEntry entry) return;
        if (_activeCloudAccount is not null)
        {
            if (entry.IsFolder)
            {
                _cloudPathHistory.Push(_activeCloudFolderId ?? "");
                _activeCloudFolderId = entry.Path;
                CloudBackButton.Visibility = Visibility.Visible;
                await BrowseActiveCloudAsync();
                return;
            }
            if (string.IsNullOrWhiteSpace(entry.PlaybackUri)) { CloudStatusText.Text = "재생 주소를 만들지 못했습니다. 다시 로그인하세요."; return; }
            var cloudName = _activeCloudAccount.Provider switch { CloudProviderKind.GoogleDrive => "Google Drive", CloudProviderKind.OneDrive => "OneDrive", CloudProviderKind.Dropbox => "Dropbox", _ => "Naver MYBOX" };
            AddItems([new PlaylistItem(entry.Path, entry.Name, true, PlaybackUri: entry.PlaybackUri, LocationLabel: $"{cloudName} · {entry.Name}")], playNow: true);
            CloudFilesList.SelectedIndex = -1;
            return;
        }
        if (_activeStorage is null) return;
        if (entry.IsFolder)
        {
            _storagePathHistory.Push(_activeStoragePath ?? _activeStorage.Endpoint);
            _activeStoragePath = entry.Path;
            CloudBackButton.Visibility = Visibility.Visible;
            await BrowseActiveStorageAsync();
            return;
        }

        var isWebDav = _activeStorage.Kind == StorageKind.WebDav;
        var item = new PlaylistItem(entry.Path, entry.Name, isWebDav,
            isWebDav ? _activeStorage.Username : null,
            isWebDav ? UserSecret.Unprotect(_activeStorage.ProtectedPassword) : null,
            LocationLabel: $"{_activeStorage.Name} · {entry.Path}");
        AddItems([item], playNow: true);
        CloudFilesList.SelectedIndex = -1;
    }

    private void ShowPlayerWindow()
    {
        if (_playerWindow is null)
        {
            _playerWindow = new PlayerWindow(_player, Previous, Next, StopPlayback, OpenFilesFromPlayer) { Owner = this };
            _playerWindow.Closed += (_, _) => { _playerWindow = null; _currentIndex = -1; };
        }
        if (!_playerWindow.IsVisible) _playerWindow.Show();
        _playerWindow.Activate();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        ReloadConnections();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => await UpdateService.CheckManuallyAsync(this);

    private void WindowTitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
