using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace VideoPlayer.Wpf;

public sealed record ProviderStatus(string Name, string Note);
public sealed record LocalModel(string Name, string Family, string ParameterSize, string Quantization, long Size)
{
    public override string ToString() => $"{Name}   ·   {Family}   ·   {ParameterSize}   ·   {FormatBytes(Size)}";
    private static string FormatBytes(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
}

public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ObservableCollection<LocalModel> _models = [];
    private readonly SavedSettings _settings;
    private readonly CloudMediaClient _cloudClient = new();
    private string? _selectedModel;
    private bool _loadingPlayerPreferences;

    public SettingsWindow()
    {
        InitializeComponent();
        ModelList.ItemsSource = _models;
        _settings = AppSettingsStore.Load();
        _selectedModel = _settings.SelectedLocalModel;
        StorageList.ItemsSource = _settings.StorageConnections;
        GeminiTranslationEnabledBox.IsChecked = _settings.GeminiTranslationEnabled;
        LocalTranslationEnabledBox.IsChecked = _settings.LocalTranslationEnabled;
        LocalTargetLanguageBox.SelectedItem = LocalTargetLanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _settings.LocalTranslationTargetLanguage) ?? LocalTargetLanguageBox.Items[0];
        OllamaUrlBox.Text = string.IsNullOrWhiteSpace(_settings.LocalModelEndpoint) ? "http://localhost:11434" : _settings.LocalModelEndpoint;
        LocalTranslationStatus.Text = _settings.LocalTranslationEnabled ? "저장됨 · 선택한 Ollama 모델로 다음 자막을 번역합니다." : "모델을 선택한 뒤 설정을 저장하세요.";
        GeminiModelBox.Text = string.IsNullOrWhiteSpace(_settings.GeminiModel) ? "gemini-3.8-flash" : _settings.GeminiModel;
        GeminiTargetLanguageBox.SelectedItem = GeminiTargetLanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _settings.GeminiTargetLanguage) ?? GeminiTargetLanguageBox.Items[0];
        try
        {
            GeminiApiKeyBox.Password = UserSecret.Unprotect(_settings.ProtectedGeminiApiKey);
            GeminiStatusText.Text = string.IsNullOrWhiteSpace(GeminiApiKeyBox.Password) ? "API 키를 입력하고 설정을 저장하세요." : "API 키가 이 Windows 사용자 계정으로 보호되어 저장돼 있습니다.";
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            GeminiStatusText.Text = "저장된 API 키를 읽지 못했습니다. 새 키를 입력해 저장하세요.";
        }
        _loadingPlayerPreferences = true;
        SubtitleFontBox.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies.Select(font => font.Source).OrderBy(name => name).ToList();
        SubtitleFontBox.SelectedItem = SubtitleFontBox.Items.Contains(_settings.SubtitleFontFamily) ? _settings.SubtitleFontFamily : "Segoe UI";
        SubtitleSizeBox.SelectedIndex = Math.Max(0, SubtitleSizeBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => item.Tag?.ToString() == _settings.SubtitleFontSize.ToString()));
        SubtitleBoldBox.IsChecked = _settings.SubtitleBold;
        SubtitleOutlineBox.SelectedIndex = _settings.SubtitleOutline switch { "없음" => 0, "두껍게" => 2, _ => 1 };
        CaptureFolderBox.Text = _settings.CaptureFolder ?? "";
        _loadingPlayerPreferences = false;
        RefreshCloudStatus();
    }

    public void SelectCloudTab() => SettingsTabs.SelectedIndex = 2;
    public void SelectStorageTab() => SettingsTabs.SelectedIndex = 3;
    public void PreparePikPakConnection()
    {
        SelectStorageTab();
        StorageKindBox.SelectedIndex = 1;
        StorageNameBox.Text = "PikPak";
        StorageEndpointBox.Text = "https://dav.mypikpak.com";
        StorageStatus.Text = "PikPak WebDAV 사용자 이름과 비밀번호를 입력한 뒤 ‘연결 확인 후 저장’을 누르세요. 저장되면 메인 메뉴에서 목록이 열립니다.";
    }

    private void CaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "화면 캡처 저장 폴더 선택", Multiselect = false };
        if (Directory.Exists(CaptureFolderBox.Text)) dialog.FolderName = CaptureFolderBox.Text;
        if (dialog.ShowDialog(this) == true) { CaptureFolderBox.Text = dialog.FolderName; SavePlayerPreferences(); }
    }

    private void PlayerPreference_Changed(object sender, SelectionChangedEventArgs e) => SavePlayerPreferences();
    private void SubtitleBold_Changed(object sender, RoutedEventArgs e) => SavePlayerPreferences();
    private void SavePlayerPreferences()
    {
        if (_loadingPlayerPreferences || SubtitleFontBox is null) return;
        if (SubtitleFontBox.SelectedItem is string font) _settings.SubtitleFontFamily = font;
        if (SubtitleSizeBox.SelectedItem is ComboBoxItem sizeItem && int.TryParse(sizeItem.Tag?.ToString(), out var size)) _settings.SubtitleFontSize = size;
        _settings.SubtitleBold = SubtitleBoldBox.IsChecked == true;
        if (SubtitleOutlineBox.SelectedItem is ComboBoxItem outline) _settings.SubtitleOutline = outline.Content?.ToString() ?? "표준";
        _settings.CaptureFolder = string.IsNullOrWhiteSpace(CaptureFolderBox.Text) ? null : CaptureFolderBox.Text.Trim();
        try { AppSettingsStore.Save(_settings); } catch (IOException) { }
    }

    private CloudAccount? GetCloudAccount(CloudProviderKind provider) => _settings.CloudAccounts.FirstOrDefault(account => account.Provider == provider);

    private void RefreshCloudStatus()
    {
        SetOAuthStatus(CloudProviderKind.GoogleDrive, GoogleConnectButton, GoogleDisconnectButton, GoogleStatusText);
        SetOAuthStatus(CloudProviderKind.OneDrive, OneDriveConnectButton, OneDriveDisconnectButton, OneDriveStatusText);
        SetOAuthStatus(CloudProviderKind.Dropbox, DropboxConnectButton, DropboxDisconnectButton, DropboxStatusText);
        var myBoxConnected = GetCloudAccount(CloudProviderKind.NaverMyBox) is not null;
        var myBoxFolder = GetCloudFolder(CloudProviderKind.NaverMyBox);
        MyBoxStatusText.Text = myBoxConnected ? "연결됨 · MYBOX 파일 읽기" : myBoxFolder is not null ? $"PC 동기화 폴더 연결됨 · {myBoxFolder.Endpoint}" : "동기화 폴더 또는 Open API 연결 필요";
        MyBoxDisconnectButton.Visibility = myBoxConnected || myBoxFolder is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetOAuthStatus(CloudProviderKind provider, Button connectButton, Button disconnectButton, TextBlock status)
    {
        var connected = GetCloudAccount(provider) is not null;
        var folder = GetCloudFolder(provider);
        var configured = OAuthProviderConfig.GetClientId(provider) is not null;
        connectButton.Visibility = Visibility.Visible;
        connectButton.Content = configured ? "로그인 연결" : folder is null ? "동기화 폴더 선택" : "폴더 변경";
        disconnectButton.Visibility = connected || folder is not null ? Visibility.Visible : Visibility.Collapsed;
        status.Text = connected ? "연결됨 · 클라우드 파일 읽기" : folder is not null
            ? $"PC 동기화 폴더 연결됨 · {folder.Endpoint}"
            : configured ? "브라우저 로그인으로 연결할 수 있습니다" : "PC 동기화 폴더를 선택해 연결하세요";
    }

    private StorageConnection? GetCloudFolder(CloudProviderKind provider) => _settings.StorageConnections.FirstOrDefault(connection => connection.Kind == StorageKind.LocalFolder && connection.CloudProvider == provider);

    private async void GoogleConnect_Click(object sender, RoutedEventArgs e) => await ConnectCloudAsync(CloudProviderKind.GoogleDrive);
    private async void OneDriveConnect_Click(object sender, RoutedEventArgs e) => await ConnectCloudAsync(CloudProviderKind.OneDrive);
    private async void DropboxConnect_Click(object sender, RoutedEventArgs e) => await ConnectCloudAsync(CloudProviderKind.Dropbox);
    private async void GoogleDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectCloudAsync(CloudProviderKind.GoogleDrive);
    private async void OneDriveDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectCloudAsync(CloudProviderKind.OneDrive);
    private async void DropboxDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectCloudAsync(CloudProviderKind.Dropbox);
    private async void MyBoxConnect_Click(object sender, RoutedEventArgs e)
    {
        CloudAuthStatus.Text = "MYBOX 토큰을 확인하는 중…";
        try
        {
            await _cloudClient.ConnectMyBoxAsync(MyBoxTokenBox.Password);
            MyBoxTokenBox.Clear();
            _settings.CloudAccounts = AppSettingsStore.Load().CloudAccounts;
            RefreshCloudStatus();
            CloudAuthStatus.Text = "MYBOX 연결을 확인했습니다. 메인 화면에서 MYBOX를 열어 파일을 선택하세요.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            CloudAuthStatus.Text = $"연결하지 못했습니다: {ex.Message}";
        }
    }
    private async void MyBoxDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectCloudAsync(CloudProviderKind.NaverMyBox);
    private void MyBoxFolderConnect_Click(object sender, RoutedEventArgs e) => ConnectSyncFolder(CloudProviderKind.NaverMyBox);

    private async Task ConnectCloudAsync(CloudProviderKind provider)
    {
        var clientId = OAuthProviderConfig.GetClientId(provider);
        if (clientId is null)
        {
            ConnectSyncFolder(provider);
            return;
        }
        CloudAuthStatus.Text = "기본 브라우저에서 안전한 로그인을 기다리는 중… 앱으로 돌아오면 연결을 마칩니다.";
        try
        {
            await _cloudClient.ConnectAsync(provider, clientId);
            _settings.CloudAccounts = AppSettingsStore.Load().CloudAccounts;
            RefreshCloudStatus();
            CloudAuthStatus.Text = "계정이 연결되었습니다. 메인 창에서 해당 클라우드를 선택해 영상을 찾아보세요.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or UriFormatException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            CloudAuthStatus.Text = $"연결하지 못했습니다: {ex.Message}";
        }
    }

    private void ConnectSyncFolder(CloudProviderKind provider)
    {
        var providerName = ProviderName(provider);
        var dialog = new OpenFolderDialog { Title = $"{providerName} PC 동기화 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            CloudAuthStatus.Text = "폴더 선택을 취소했습니다. 선택한 폴더는 저장되지 않았습니다.";
            return;
        }
        try
        {
            var path = Path.GetFullPath(dialog.FolderName);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("선택한 폴더에 접근할 수 없습니다.");
            var connection = GetCloudFolder(provider);
            if (connection is null)
            {
                connection = new StorageConnection { Kind = StorageKind.LocalFolder, CloudProvider = provider };
                _settings.StorageConnections.Add(connection);
            }
            connection.Name = $"{providerName} 동기화 폴더";
            connection.Endpoint = path;
            AppSettingsStore.Save(_settings);
            StorageList.Items.Refresh();
            RefreshCloudStatus();
            CloudAuthStatus.Text = $"{providerName} 폴더를 연결했습니다. 메인 화면에서 클라우드를 누르면 폴더와 동영상 목록을 읽습니다. 온라인 전용 파일은 공급자 동기화 앱의 다운로드 상태에 따라 재생이 달라질 수 있습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            CloudAuthStatus.Text = $"폴더 연결에 실패했습니다: {ex.Message}";
        }
    }

    private static string ProviderName(CloudProviderKind provider) => provider switch
    {
        CloudProviderKind.GoogleDrive => "Google Drive",
        CloudProviderKind.OneDrive => "OneDrive",
        CloudProviderKind.Dropbox => "Dropbox",
        _ => "Naver MYBOX"
    };

    private async Task DisconnectCloudAsync(CloudProviderKind provider)
    {
        await _cloudClient.DisconnectAsync(provider);
        var folder = GetCloudFolder(provider);
        if (folder is not null) _settings.StorageConnections.Remove(folder);
        AppSettingsStore.Save(_settings);
        StorageList.Items.Refresh();
        _settings.CloudAccounts = AppSettingsStore.Load().CloudAccounts;
        RefreshCloudStatus();
        CloudAuthStatus.Text = "앱에 저장된 연결을 해제했습니다. 공급자 계정 설정의 앱 권한은 별도로 관리할 수 있습니다.";
    }

    private async void AddStorage_Click(object sender, RoutedEventArgs e)
    {
        StorageStatus.Text = "연결 확인 중…";
        try
        {
            var kind = StorageKindBox.SelectedIndex == 1 ? StorageKind.WebDav : StorageKind.Smb;
            var endpoint = StorageEndpointBox.Text.Trim();
            var name = StorageNameBox.Text.Trim();
            var isPikPakEndpoint = kind == StorageKind.WebDav && Uri.TryCreate(endpoint, UriKind.Absolute, out var webDavUri)
                && webDavUri.Host.Equals("dav.mypikpak.com", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(name)) name = isPikPakEndpoint ? "PikPak" : kind == StorageKind.Smb ? "SMB 공유" : "WebDAV";
            if (isPikPakEndpoint && name.Equals("WebDAV", StringComparison.OrdinalIgnoreCase)) name = "PikPak";
            if (kind == StorageKind.Smb && !endpoint.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidOperationException("SMB 경로는 \\\\서버\\공유 형식으로 입력하세요.");
            if (kind == StorageKind.WebDav && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new InvalidOperationException("WebDAV 주소는 http:// 또는 https:// 형식이어야 합니다.");

            var connection = new StorageConnection
            {
                Name = name,
                Kind = kind,
                Endpoint = endpoint,
                Username = StorageUsernameBox.Text.Trim(),
                ProtectedPassword = UserSecret.Protect(StoragePasswordBox.Password)
            };
            await new NetworkStorageClient().TestAsync(connection);
            if (_settings.StorageConnections.Any(existing => existing.Kind == kind && string.Equals(existing.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("같은 경로의 연결이 이미 저장되어 있습니다.");

            _settings.StorageConnections.Add(connection);
            AppSettingsStore.Save(_settings);
            StorageList.Items.Refresh();
            StoragePasswordBox.Clear();
            StorageStatus.Text = "연결을 확인하고 저장했습니다.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or UriFormatException or System.Xml.XmlException or System.Security.Cryptography.CryptographicException)
        {
            StorageStatus.Text = $"연결 실패: {ex.Message}";
        }
    }

    private void RemoveStorage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var connection = _settings.StorageConnections.FirstOrDefault(item => item.Id == id);
        if (connection is null) return;
        _settings.StorageConnections.Remove(connection);
        AppSettingsStore.Save(_settings);
        StorageList.Items.Refresh();
        StorageStatus.Text = "저장된 연결을 삭제했습니다.";
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(OllamaUrlBox.Text.Trim().TrimEnd('/') + "/api/tags", UriKind.Absolute, out var uri)) { ModelStatus.Text = "서버 주소가 올바르지 않습니다."; return; }
        _settings.LocalModelEndpoint = OllamaUrlBox.Text.Trim().TrimEnd('/');
        ModelStatus.Text = "Ollama 모델 목록 확인 중…";
        try
        {
            using var response = await Http.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            _models.Clear();
            if (json.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var item in models.EnumerateArray())
                {
                    var details = item.TryGetProperty("details", out var d) ? d : default;
                    _models.Add(new LocalModel(GetString(item, "name"), GetString(details, "family"), GetString(details, "parameter_size"), GetString(details, "quantization_level"), GetLong(item, "size")));
                }
            }
            ModelStatus.Text = _models.Count == 0 ? "설치된 모델이 없습니다." : $"{_models.Count}개 모델을 찾았습니다. 번역 기능 지원 여부는 개별 검증이 필요합니다.";
            if (_selectedModel is not null) ModelList.SelectedItem = _models.FirstOrDefault(m => m.Name == _selectedModel);
            if (_models.Count == 0) ModelDetails.Text = "Ollama에 설치된 모델이 없습니다.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _models.Clear(); ModelStatus.Text = "Ollama에 연결하지 못했습니다. 앱과 같은 PC에서 Ollama를 실행한 뒤 새로고침하세요."; ModelDetails.Text = ex is JsonException ? "Ollama 응답을 읽지 못했습니다." : "연결 실패: 번역 모델 설정은 저장되지 않았습니다.";
        }
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is not LocalModel model) return;
        _selectedModel = model.Name;
        ModelDetails.Text = $"이름: {model.Name}\n계열: {Empty(model.Family)}\n파라미터: {Empty(model.ParameterSize)}\n양자화: {Empty(model.Quantization)}\n\n연결 기능: 선택한 모델을 Ollama /api/chat으로 호출해 SRT/VTT 자막을 번역합니다.\n미확인: 번역 품질, 속도, PC 성능은 이 모델 메타데이터만으로 보장할 수 없습니다. 음성 전사는 Whisper CC 생성, 음성 읽기는 Windows 음성 기능을 사용합니다.";
        SaveSettings();
    }

    private void GeminiGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://reensdogood.github.io/video-player-community/GEMINI_API_SETUP_GUIDE.html",
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "기본 브라우저를 열지 못했습니다. 아래 주소를 복사해 브라우저에서 열어주세요.\nhttps://reensdogood.github.io/video-player-community/GEMINI_API_SETUP_GUIDE.html", "Gemini API 안내", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private (string Model, string ApiKey, string TargetLanguage) ReadGeminiForm()
    {
        var model = GeminiModelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Gemini 모델 이름을 입력해 주세요.");
        var target = (GeminiTargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "한국어";
        var apiKey = GeminiApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(_settings.ProtectedGeminiApiKey))
            apiKey = UserSecret.Unprotect(_settings.ProtectedGeminiApiKey);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Google AI Studio에서 발급한 Gemini API 키를 입력해 주세요.");
        return (model, apiKey, target);
    }

    private void SaveGeminiSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.GeminiTranslationEnabled = GeminiTranslationEnabledBox.IsChecked == true;
            if (_settings.GeminiTranslationEnabled)
            {
                _settings.LocalTranslationEnabled = false;
                LocalTranslationEnabledBox.IsChecked = false;
                LocalTranslationStatus.Text = "Gemini 번역을 선택했습니다.";
            }
            _settings.GeminiModel = GeminiModelBox.Text.Trim();
            _settings.GeminiTargetLanguage = (GeminiTargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "한국어";
            if (!string.IsNullOrWhiteSpace(GeminiApiKeyBox.Password)) _settings.ProtectedGeminiApiKey = UserSecret.Protect(GeminiApiKeyBox.Password);
            if (_settings.GeminiTranslationEnabled && string.IsNullOrWhiteSpace(_settings.ProtectedGeminiApiKey))
                throw new InvalidOperationException("자동 번역을 켜려면 먼저 Gemini API 키를 입력하고 저장하세요.");
            AppSettingsStore.Save(_settings);
            GeminiStatusText.Text = _settings.GeminiTranslationEnabled
                ? "저장됨 · 다음 자막부터 Gemini 번역을 사용합니다."
                : "저장됨 · 자동 번역이 꺼졌습니다.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            GeminiStatusText.Text = ex.Message;
        }
    }

    private void SaveLocalTranslation_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = OllamaUrlBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            LocalTranslationStatus.Text = "Ollama 서버 주소를 확인해 주세요.";
            return;
        }
        if (LocalTranslationEnabledBox.IsChecked == true && string.IsNullOrWhiteSpace(_selectedModel))
        {
            LocalTranslationStatus.Text = "번역에 사용할 Ollama 모델을 먼저 선택해 주세요.";
            return;
        }
        _settings.LocalModelEndpoint = endpoint;
        _settings.LocalTranslationEnabled = LocalTranslationEnabledBox.IsChecked == true;
        _settings.LocalTranslationTargetLanguage = (LocalTargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "한국어";
        if (_settings.LocalTranslationEnabled)
        {
            _settings.GeminiTranslationEnabled = false;
            GeminiTranslationEnabledBox.IsChecked = false;
            GeminiStatusText.Text = "로컬 Ollama 번역을 선택했습니다.";
        }
        try
        {
            AppSettingsStore.Save(_settings);
            LocalTranslationStatus.Text = _settings.LocalTranslationEnabled ? $"저장됨 · {_selectedModel} 모델로 다음 자막을 번역합니다." : "저장됨 · 로컬 자동 번역을 껐습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalTranslationStatus.Text = $"설정을 저장하지 못했습니다: {ex.Message}";
        }
    }

    private async void TestLocalTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedModel)) { LocalTranslationStatus.Text = "먼저 Ollama 모델을 선택해 주세요."; return; }
        var endpoint = OllamaUrlBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint + "/api/chat", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { LocalTranslationStatus.Text = "Ollama 서버 주소를 확인해 주세요."; return; }
        LocalTranslationStatus.Text = "Ollama 모델에 짧은 문장을 보내 연결을 확인하는 중…";
        try
        {
            var language = (LocalTargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "한국어";
            var result = await new LocalSubtitleTranslator().TranslateTextsAsync(["The train leaves in five minutes."], uri, _selectedModel, language);
            LocalTranslationStatus.Text = $"연결 성공 · 번역 예시: {result[0]}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            LocalTranslationStatus.Text = $"연결 확인 실패 · {ex.Message}";
        }
    }

    private async void TestGemini_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (model, apiKey, target) = ReadGeminiForm();
            GeminiStatusText.Text = "짧은 문장으로 Gemini 연결을 확인하는 중…";
            var result = await new GeminiSubtitleTranslator().TranslateTextsAsync(["Welcome. Subtitles are ready."], model, apiKey, target);
            GeminiStatusText.Text = $"연결 성공 · 번역 예시: {result[0]}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            GeminiStatusText.Text = $"연결 확인 실패 · {ex.Message}";
        }
    }

    private void ClearGeminiKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.ProtectedGeminiApiKey = "";
        GeminiApiKeyBox.Clear();
        _settings.GeminiTranslationEnabled = false;
        GeminiTranslationEnabledBox.IsChecked = false;
        try
        {
            AppSettingsStore.Save(_settings);
            GeminiStatusText.Text = "저장된 Gemini API 키를 삭제하고 자동 번역을 껐습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GeminiStatusText.Text = $"키 삭제를 저장하지 못했습니다: {ex.Message}";
        }
    }

    private static string Empty(string? value) => string.IsNullOrWhiteSpace(value) ? "정보 없음" : value;
    private static string GetString(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    private static long GetLong(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) && v.TryGetInt64(out var value) ? value : 0;

    private void SaveSettings()
    {
        _settings.SelectedLocalModel = _selectedModel;
        _settings.LocalModelEndpoint = OllamaUrlBox.Text.Trim().TrimEnd('/');
        try { AppSettingsStore.Save(_settings); }
        catch (IOException ex) { ModelStatus.Text = $"선택을 저장하지 못했습니다: {ex.Message}"; }
        catch (UnauthorizedAccessException) { ModelStatus.Text = "Windows 사용자 설정 폴더에 저장 권한이 없습니다."; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void WindowTitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
