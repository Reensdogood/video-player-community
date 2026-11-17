using System.Diagnostics;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace VideoPlayer.Wpf;

internal static class UpdateService
{
    private const string ReleaseRepository = "https://github.com/Reensdogood/video-player-community";
    private const string ReleasesPage = ReleaseRepository + "/releases/latest";

    public static Task CheckInBackgroundAsync(Window owner) => CheckAsync(owner, manual: false);
    public static Task CheckManuallyAsync(Window owner) => CheckAsync(owner, manual: true);

    private static async Task CheckAsync(Window owner, bool manual)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleaseRepository, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                if (manual) ShowReleasePageChoice(owner, "이 실행본은 자동 업데이트가 연결된 설치본이 아닙니다. 최신 설치 파일 페이지를 열까요?");
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                if (manual) MessageBox.Show(owner, "현재 설치된 버전이 최신입니다.", "업데이트 확인", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await manager.DownloadUpdatesAsync(update);
            var version = update.TargetFullRelease.Version.ToString();
            var choice = MessageBox.Show(
                owner,
                $"다봐 새 버전 {version}을 준비했습니다. 지금 다시 시작해 적용할까요?",
                "업데이트 준비 완료",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes) manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            if (manual && owner.IsVisible)
                ShowReleasePageChoice(owner, "새 버전을 확인하지 못했습니다. 인터넷 연결을 확인하거나 최신 설치 파일 페이지를 열어 주세요.");
        }
    }

    private static void ShowReleasePageChoice(Window owner, string message)
    {
        if (MessageBox.Show(owner, message, "업데이트 확인", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
    }
}
