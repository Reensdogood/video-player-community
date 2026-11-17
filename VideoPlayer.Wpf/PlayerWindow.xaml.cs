using LibVLCSharp.Shared;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace VideoPlayer.Wpf;

public partial class PlayerWindow : Window
{
    private enum DisplayMode { Windowed, Fullscreen, PictureInPicture }

    private readonly MediaPlayer _player;
    private readonly Action _previous;
    private readonly Action _next;
    private readonly Action _stop;
    private readonly Action _openFile;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _controlsHideTimer;
    private DisplayMode _mode = DisplayMode.Windowed;
    private Rect? _windowedBounds;
    private bool _seeking;
    private bool _changingTracks;
    private bool _selectAutoSubtitle;
    private bool _selectLastSubtitle;
    private bool _isMuted;
    private bool _platformPageMode;
    private Uri? _platformUri;
    private int _volumeBeforeMute = 80;
    private string? _localVideoPath;
    private readonly List<(long Start, long End, string Text)> _captionCues = [];
    private int _spokenCue = -1;
    private dynamic? _voice;
    private Process? _captionProcess;
    private bool _closed;
    private bool _ended;
    private CancellationTokenSource? _geminiTranslationCancellation;

    public PlayerWindow(MediaPlayer player, Action previous, Action next, Action stop, Action openFile)
    {
        InitializeComponent();
        _player = player;
        _previous = previous;
        _next = next;
        _stop = stop;
        _openFile = openFile;
        VideoView.MediaPlayer = player;
        EmptyState.Visibility = Visibility.Visible;
        _player.Playing += Player_Playing;
        _player.Paused += Player_Paused;
        _player.Stopped += Player_Stopped;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += (_, _) => { if (!_seeking && _player.Length > 0) { SeekSlider.Value = Math.Clamp(_player.Time * 1000d / _player.Length, 0, 1000); UpdateTime(); } UpdateCaptionVoice(); };
        _timer.Start();
        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2800) };
        _controlsHideTimer.Tick += (_, _) =>
        {
            _controlsHideTimer.Stop();
            if (!_player.IsPlaying || _mode == DisplayMode.PictureInPicture) return;
            if (TopChrome.IsMouseOver || PlaybackControls.IsMouseOver) { _controlsHideTimer.Start(); return; }
            TopChrome.Visibility = Visibility.Collapsed;
            PlaybackControls.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            CancelGeminiTranslation();
            try { if (_captionProcess is { HasExited: false }) _captionProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            StopCaptionVoice();
            _timer.Stop();
            _controlsHideTimer.Stop();
            _player.Stop();
            _player.Playing -= Player_Playing;
            _player.Paused -= Player_Paused;
            _player.Stopped -= Player_Stopped;
            VideoView.MediaPlayer = null;
            VideoView.Dispose();
        };
        Loaded += (_, _) => { if (_player.Media is not null) EmptyState.Visibility = Visibility.Collapsed; };
    }

    public void SetNowPlaying(string title, bool selectAutoSubtitle = false, string? localVideoPath = null, string? sourceSubtitlePath = null)
    {
        CancelGeminiTranslation();
        _ended = false;
        _localVideoPath = localVideoPath;
        _captionCues.Clear();
        _spokenCue = -1;
        StopCaptionVoice();
        if (localVideoPath is not null)
        {
            var srt = Path.ChangeExtension(localVideoPath, ".srt");
            try { if (File.Exists(srt)) LoadCaptionCues(srt); } catch (IOException) { }
        }
        if (PlatformWebView.CoreWebView2 is not null) PlatformWebView.CoreWebView2.Navigate("about:blank");
        _platformPageMode = false;
        PlatformContainer.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
        PlaybackControls.Visibility = Visibility.Visible;
        OpenExternalButton.Visibility = Visibility.Collapsed;
        NowPlayingText.Text = title;
        TranslationStatusText.Text = "";
        _selectAutoSubtitle = selectAutoSubtitle;
        if (_player.IsPlaying) RefreshTracks();
        var settings = AppSettingsStore.Load();
        if (settings.GeminiTranslationEnabled && localVideoPath is not null)
        {
            if (sourceSubtitlePath is null) TranslationStatusText.Text = "번역 켜짐 · 외부 SRT/VTT 자막을 찾지 못했습니다.";
            else Dispatcher.BeginInvoke(new Action(async () => await TranslateSubtitleAsync(sourceSubtitlePath)));
        }
    }

    public void ShowEmptyState()
    {
        if (PlatformWebView.CoreWebView2 is not null) PlatformWebView.CoreWebView2.Navigate("about:blank");
        _platformPageMode = false;
        PlatformContainer.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
        PlaybackControls.Visibility = Visibility.Visible;
        OpenExternalButton.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        NowPlayingText.Text = "재생할 영상 없음";
    }

    public async void ShowPlatformPage(Uri uri, string title)
    {
        _localVideoPath = null;
        _captionCues.Clear();
        StopCaptionVoice();
        _platformPageMode = true;
        _platformUri = uri;
        _selectAutoSubtitle = false;
        NowPlayingText.Text = title;
        EmptyState.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Collapsed;
        PlatformContainer.Visibility = Visibility.Visible;
        PlaybackControls.Visibility = Visibility.Collapsed;
        TopChrome.Visibility = Visibility.Visible;
        OpenExternalButton.Visibility = Visibility.Visible;
        _controlsHideTimer.Stop();
        try
        {
            await PlatformWebView.EnsureCoreWebView2Async();
            PlatformWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            PlatformWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PlatformWebView.Source = uri;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"플랫폼 페이지를 열지 못했습니다. WebView2 런타임 설치 여부를 확인하거나 기본 브라우저로 열어 주세요.\n\n{ex.Message}", "페이지 재생 준비 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Player_Playing(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { if (!IsLoaded) return; _ended = false; PlayPauseButton.Content = "Ⅱ 일시정지"; EmptyState.Visibility = Visibility.Collapsed; RefreshTracks(); ShowPlaybackChrome(); });
    private void Player_Paused(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { if (!IsLoaded) return; StopCaptionVoice(); _spokenCue = -1; PlayPauseButton.Content = "▶ 재생"; ShowPlaybackChrome(); });
    private void Player_Stopped(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { if (!IsLoaded) return; StopCaptionVoice(); _spokenCue = -1; PlayPauseButton.Content = _ended ? "↻ 처음부터" : "▶ 재생"; ShowPlaybackChrome(); });
    public void MarkPlaybackEnded()
    {
        if (!IsLoaded) return;
        _ended = true;
        StopCaptionVoice();
        _spokenCue = -1;
        PlayPauseButton.Content = "↻ 처음부터";
        ShowPlaybackChrome();
    }
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying) _player.Pause();
        else if (_player.Media is not null)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            if (_ended || (_player.Length > 0 && _player.Time >= _player.Length - 500))
            {
                _ended = false;
                _player.Time = 0;
            }
            _player.Play();
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => _stop();
    private void Previous_Click(object sender, RoutedEventArgs e) => _previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _next();
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = (int)e.NewValue;
        if (VolumeValue is not null) VolumeValue.Text = $"{(int)e.NewValue}%";
        if (_isMuted && e.NewValue > 0) { _isMuted = false; if (MuteButton is not null) MuteButton.Content = "음소거"; }
    }
    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMuted)
        {
            _isMuted = false;
            VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 80;
            _player.Volume = (int)VolumeSlider.Value;
            MuteButton.Content = "음소거";
            return;
        }
        _volumeBeforeMute = Math.Max((int)VolumeSlider.Value, 1);
        _isMuted = true;
        _player.Volume = 0;
        MuteButton.Content = "소리 켜기";
    }
    private void Close_Click(object sender, RoutedEventArgs e) { _stop(); Close(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OpenFile_Click(object sender, RoutedEventArgs e) => _openFile();
    private void CaptureFrame_Click(object sender, RoutedEventArgs e) => CaptureFrame();
    private void TopChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode == DisplayMode.Windowed && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_platformUri is null) return;
        Process.Start(new ProcessStartInfo(_platformUri.AbsoluteUri) { UseShellExecute = true });
    }
    private void PlayerRoot_PreviewMouseMove(object sender, MouseEventArgs e) => ShowPlaybackChrome();
    private void PlayerRoot_PreviewTouchDown(object sender, TouchEventArgs e) => ShowPlaybackChrome();
    private void ShowPlaybackChrome()
    {
        TopChrome.Visibility = Visibility.Visible;
        if (_platformPageMode)
        {
            PlaybackControls.Visibility = Visibility.Collapsed;
            _controlsHideTimer.Stop();
            return;
        }
        PlaybackControls.Visibility = Visibility.Visible;
        _controlsHideTimer.Stop();
        if (_player.IsPlaying && _mode != DisplayMode.PictureInPicture) _controlsHideTimer.Start();
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == DisplayMode.Fullscreen) { RestoreWindowed(); return; }
        if (_mode == DisplayMode.PictureInPicture) RestoreWindowed();
        _windowedBounds ??= new Rect(Left, Top, Width, Height);
        WindowState = WindowState.Maximized;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        _mode = DisplayMode.Fullscreen;
        FullscreenButton.Content = "창 모드";
    }
    private void Pip_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == DisplayMode.PictureInPicture) { RestoreWindowed(); return; }
        if (_mode == DisplayMode.Fullscreen) RestoreWindowed();
        _windowedBounds ??= new Rect(Left, Top, Width, Height);
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Width = 520; Height = 340;
        Left = SystemParameters.WorkArea.Right - Width - 18;
        Top = SystemParameters.WorkArea.Bottom - Height - 18;
        Topmost = true;
        _mode = DisplayMode.PictureInPicture;
        FullscreenButton.Content = "전체화면";
        PipButton.Content = "일반 창";
        PreviousButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        AudioTracks.Visibility = Visibility.Collapsed;
        SubtitleTracks.Visibility = Visibility.Collapsed;
        OpenSubtitleButton.Visibility = Visibility.Collapsed;
    }
    private void RestoreWindowed()
    {
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = false;
        if (_windowedBounds is { } bounds)
        {
            Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
        }
        _mode = DisplayMode.Windowed;
        FullscreenButton.Content = "전체화면";
        PipButton.Content = "PIP";
        PreviousButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        AudioTracks.Visibility = Visibility.Visible;
        SubtitleTracks.Visibility = Visibility.Visible;
        OpenSubtitleButton.Visibility = Visibility.Visible;
        ShowPlaybackChrome();
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _mode != DisplayMode.Windowed) { RestoreWindowed(); e.Handled = true; }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CaptureFrame(); e.Handled = true; }
        else if (e.Key == Key.F11 || e.Key == Key.Enter) { Fullscreen_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Space && !_platformPageMode) { ShowPlaybackChrome(); PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
    private void CaptureFrame()
    {
        if (_platformPageMode || _player.Media is null)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        try
        {
            var settings = AppSettingsStore.Load();
            var folder = settings.CaptureFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                var dialog = new OpenFolderDialog { Title = "화면 캡처 저장 폴더 선택" };
                if (dialog.ShowDialog(this) != true) return;
                folder = dialog.FolderName;
                settings.CaptureFolder = folder;
                AppSettingsStore.Save(settings);
            }
            Directory.CreateDirectory(folder);
            var baseName = string.IsNullOrWhiteSpace(_localVideoPath) ? "ohmyplayer" : Path.GetFileNameWithoutExtension(_localVideoPath);
            var path = Path.Combine(folder, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            if (!_player.TakeSnapshot(0, path, 0, 0)) throw new IOException("현재 프레임을 캡처하지 못했습니다.");
            NowPlayingText.Text = $"캡처 저장 · {Path.GetFileName(path)}";
            ShowPlaybackChrome();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"화면을 저장하지 못했습니다. 설정에서 저장 폴더를 확인하세요.\n\n{ex.Message}", "화면 캡처 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void VideoView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount == 2) Fullscreen_Click(this, new RoutedEventArgs()); }
    private void OpenSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Media is null) return;
        var dialog = new OpenFileDialog { Title = "자막 파일 선택", Filter = "자막 파일|*.srt;*.vtt;*.ass;*.ssa;*.smi;*.sub;*.idx;*.ttml|모든 파일|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            if (_player.AddSlave(MediaSlaveType.Subtitle, new Uri(dialog.FileName).AbsoluteUri, true))
            {
                if (string.Equals(Path.GetExtension(dialog.FileName), ".srt", StringComparison.OrdinalIgnoreCase)) LoadCaptionCues(dialog.FileName);
                NowPlayingText.Text = $"{NowPlayingText.Text} · {System.IO.Path.GetFileName(dialog.FileName)}";
                _selectAutoSubtitle = true;
                RefreshTracks();
                if (string.Equals(Path.GetExtension(dialog.FileName), ".srt", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(dialog.FileName), ".vtt", StringComparison.OrdinalIgnoreCase))
                    _ = TranslateSubtitleAsync(dialog.FileName);
                else if (AppSettingsStore.Load().GeminiTranslationEnabled)
                    TranslationStatusText.Text = "Gemini 번역은 SRT/VTT 자막 파일을 지원합니다.";
            }
        }
    }

    private async Task TranslateSubtitleAsync(string sourceSubtitlePath)
    {
        if (_closed || !File.Exists(sourceSubtitlePath)) return;
        var settings = AppSettingsStore.Load();
        if (!settings.GeminiTranslationEnabled) return;
        if (!string.Equals(Path.GetExtension(sourceSubtitlePath), ".srt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(sourceSubtitlePath), ".vtt", StringComparison.OrdinalIgnoreCase))
        {
            TranslationStatusText.Text = "Gemini 번역은 SRT/VTT 자막을 지원합니다.";
            return;
        }

        CancelGeminiTranslation();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _geminiTranslationCancellation = cancellation;
        var playbackPath = _localVideoPath;
        try
        {
            var apiKey = UserSecret.Unprotect(settings.ProtectedGeminiApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                TranslationStatusText.Text = "번역 설정에서 Gemini API 키를 저장해 주세요.";
                return;
            }
            TranslationStatusText.Text = "Gemini 자막 번역 준비 중…";
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                if (!_closed && ReferenceEquals(_geminiTranslationCancellation, cancellation))
                    TranslationStatusText.Text = $"Gemini 자막 번역 중 · {value.Completed:N0}/{value.Total:N0}";
            });
            var translatedPath = await new GeminiSubtitleTranslator().TranslateFileAsync(sourceSubtitlePath,
                string.IsNullOrWhiteSpace(settings.GeminiModel) ? "gemini-3.8-flash" : settings.GeminiModel,
                apiKey, settings.GeminiTargetLanguage, progress, cancellationToken);
            if (_closed || cancellation.IsCancellationRequested || !string.Equals(playbackPath, _localVideoPath, StringComparison.OrdinalIgnoreCase)) return;
            if (_player.Media is null) return;
            if (!_player.AddSlave(MediaSlaveType.Subtitle, new Uri(translatedPath).AbsoluteUri, true))
                throw new InvalidOperationException("번역 자막을 재생창에 추가하지 못했습니다.");
            LoadCaptionCues(translatedPath);
            var translationName = Path.GetFileNameWithoutExtension(translatedPath);
            var selectedTranslationTrack = false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                _selectLastSubtitle = true;
                RefreshTracks();
                if ((_player.SpuDescription ?? []).Any(track => track.Name.Contains(translationName, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedTranslationTrack = true;
                    break;
                }
                await Task.Delay(250, cancellationToken);
            }
            TranslationStatusText.Text = selectedTranslationTrack
                ? $"{settings.GeminiTargetLanguage} 번역 자막 적용됨"
                : $"{settings.GeminiTargetLanguage} 번역 완료 · 자막 메뉴에서 번역본 선택";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !cancellation.IsCancellationRequested)
                TranslationStatusText.Text = $"번역 실패 · {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_geminiTranslationCancellation, cancellation))
            {
                _geminiTranslationCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void CancelGeminiTranslation()
    {
        var cancellation = _geminiTranslationCancellation;
        _geminiTranslationCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }

    private async void GenerateCaption_Click(object sender, RoutedEventArgs e)
    {
        if (_localVideoPath is null || !File.Exists(_localVideoPath))
        {
            MessageBox.Show(this, "CC 생성은 현재 PC에 저장된 영상에서 사용할 수 있습니다.", "CC 생성", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "Tools", "cc-runtime");
        var executable = Path.Combine(runtimeDir, "ohmyplayer-cc.exe");
        var model = Path.Combine(runtimeDir, "small.pt");
        if (!File.Exists(executable) || !File.Exists(model) || !File.Exists(Path.Combine(runtimeDir, "ffmpeg.exe")))
        {
            MessageBox.Show(this, "내장 자막 생성 파일을 찾을 수 없습니다. 설치 파일을 다시 실행하세요.", "CC 생성", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = _localVideoPath;
        if (_player.IsPlaying) _player.Pause();
        var outputDir = Path.Combine(Path.GetTempPath(), "ohmyplayer-cc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        GenerateCaptionButton.IsEnabled = false;
        GenerateCaptionButton.Content = "CC 생성 중…";
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = runtimeDir };
            info.Environment["PATH"] = runtimeDir + Path.PathSeparator + (info.Environment["PATH"] ?? "");
            foreach (var arg in new[] { path, model, outputDir }) info.ArgumentList.Add(arg);
            _captionProcess = Process.Start(info) ?? throw new InvalidOperationException("음성 인식 프로그램을 실행하지 못했습니다.");
            var stdout = _captionProcess.StandardOutput.ReadToEndAsync();
            var stderr = _captionProcess.StandardError.ReadToEndAsync();
            await _captionProcess.WaitForExitAsync();
            var error = await stderr;
            await stdout;
            if (_captionProcess.ExitCode != 0) throw new InvalidOperationException(error.Trim());
            var captionFile = Path.Combine(outputDir, "caption.srt");
            if (_closed || !string.Equals(path, _localVideoPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(captionFile) || new FileInfo(captionFile).Length == 0) throw new InvalidOperationException("음성을 인식하지 못해 자막이 생성되지 않았습니다.");
            if (!_player.AddSlave(MediaSlaveType.Subtitle, new Uri(captionFile).AbsoluteUri, true)) throw new InvalidOperationException("생성된 자막을 열지 못했습니다.");
            LoadCaptionCues(captionFile);
            _selectAutoSubtitle = true;
            RefreshTracks();
            if (AppSettingsStore.Load().GeminiTranslationEnabled) await TranslateSubtitleAsync(captionFile);
            _player.Time = 0;
            _selectAutoSubtitle = true;
            _player.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!_closed) MessageBox.Show(this, $"CC 생성에 실패했습니다. 영상을 다시 재생하거나 다른 파일을 선택해 주세요.\n\n{ex.Message}", "CC 생성 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _captionProcess?.Dispose(); _captionProcess = null; if (!_closed) { GenerateCaptionButton.IsEnabled = true; GenerateCaptionButton.Content = "CC 생성"; } }
    }
    private void LoadCaptionCues(string path)
    {
        _captionCues.Clear();
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        foreach (var block in Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            var lines = block.Split('\n');
            var index = Array.FindIndex(lines, line => line.Contains(" --> "));
            if (index < 0) continue;
            var times = lines[index].Split(" --> ");
            if (times.Length != 2 || !TimeSpan.TryParse(times[0].Replace(',', '.'), out var start) || !TimeSpan.TryParse(times[1].Replace(',', '.'), out var end)) continue;
            var content = Regex.Replace(string.Join(" ", lines.Skip(index + 1)), "<[^>]*>", "").Trim();
            if (content.Length > 0) _captionCues.Add(((long)start.TotalMilliseconds, (long)end.TotalMilliseconds, content));
        }
        _spokenCue = -1;
    }
    private void CaptionVoice_Changed(object sender, RoutedEventArgs e) { _spokenCue = -1; StopCaptionVoice(); }
    private void StopCaptionVoice()
    {
        try { if (_voice is not null) _voice.Speak("", 3); } catch (System.Runtime.InteropServices.COMException) { }
    }
    private void UpdateCaptionVoice()
    {
        if (CaptionVoice.IsChecked != true || !_player.IsPlaying || _captionCues.Count == 0) return;
        var time = _player.Time;
        var index = _captionCues.FindIndex(cue => cue.Start <= time && time < cue.End);
        if (index < 0) { _spokenCue = -1; return; }
        if (index == _spokenCue) return;
        _spokenCue = index;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null) throw new InvalidOperationException("Windows 음성 기능을 사용할 수 없습니다.");
            if (_voice is null)
            {
                dynamic voice = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows 음성 기능을 사용할 수 없습니다.");
                _voice = voice;
                dynamic korean = voice.GetVoices("Language=412");
                if (korean.Count > 0) voice.Voice = korean.Item(0);
            }
            _voice?.Speak(_captionCues[index].Text, 3);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            CaptionVoice.IsChecked = false;
            MessageBox.Show(this, ex.Message, "자막 읽기 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void RefreshTracks()
    {
        _changingTracks = true;
        AudioTracks.Items.Clear(); SubtitleTracks.Items.Clear();
        AudioTracks.Items.Add(new TrackChoice(-1, "오디오"));
        foreach (var track in _player.AudioTrackDescription ?? []) AudioTracks.Items.Add(new TrackChoice(track.Id, track.Name));
        SubtitleTracks.Items.Add(new TrackChoice(-1, "자막 끔"));
        foreach (var track in _player.SpuDescription ?? []) SubtitleTracks.Items.Add(new TrackChoice(track.Id, track.Name));
        AudioTracks.SelectedIndex = 0;
        SubtitleTracks.SelectedIndex = _selectLastSubtitle && SubtitleTracks.Items.Count > 1
            ? SubtitleTracks.Items.Count - 1
            : _selectAutoSubtitle && SubtitleTracks.Items.Count > 1 ? 1 : 0;
        var subtitle = SubtitleTracks.SelectedItem as TrackChoice;
        _selectAutoSubtitle = false;
        _selectLastSubtitle = false;
        _changingTracks = false;
        if (subtitle is { Id: > 0 }) _player.SetSpu(subtitle.Id);
    }
    private void AudioTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_changingTracks && AudioTracks.SelectedItem is TrackChoice track) _player.SetAudioTrack(track.Id); }
    private void SubtitleTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_changingTracks && SubtitleTracks.SelectedItem is TrackChoice track) _player.SetSpu(track.Id); }
    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_player.Length > 0) _player.Time = (long)(SeekSlider.Value * _player.Length / 1000d);
        _seeking = false;
    }
    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_seeking) UpdateTime(); }
    private void UpdateTime()
    {
        var total = Math.Max(_player.Length, 0);
        var current = _seeking && total > 0 ? (long)(SeekSlider.Value * total / 1000d) : Math.Max(_player.Time, 0);
        TimeLabel.Text = $"{FormatTime(current)} / {FormatTime(total)}";
    }
    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}
