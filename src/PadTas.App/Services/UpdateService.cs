using Velopack;
using Velopack.Sources;

namespace PadTas.App.Services;

/// <summary>
/// アプリ内自動更新 (Velopack + GitHub Releases)。
/// 流れ: GitHub Releases を確認 → 差分 (delta) をダウンロード → 再起動して適用。
/// リポジトリを公開したら、下の URL を実際のものに変えること (1か所だけ)。
/// </summary>
public static class UpdateService
{
    public const string GitHubRepoUrl = "https://github.com/Ayufa/padtas";

    private static UpdateManager CreateManager()
        => new(new GithubSource(GitHubRepoUrl, null, false));

    public static bool IsInstalled
    {
        get
        {
            try { return CreateManager().IsInstalled; }
            catch { return false; } // 開発実行 (未インストール) 時は false
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try { return CreateManager().CurrentVersion?.ToString() ?? "dev"; }
            catch { return "dev"; }
        }
    }

    /// <summary>新しいバージョンがあればその情報を返す。なければ null。</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        var manager = CreateManager();
        if (!manager.IsInstalled) return null;
        return await manager.CheckForUpdatesAsync();
    }

    /// <summary>ダウンロードして再起動して適用する。</summary>
    public static async Task DownloadAndApplyAsync(UpdateInfo update, Action<int>? progress = null)
    {
        var manager = CreateManager();
        await manager.DownloadUpdatesAsync(update, progress);
        manager.ApplyUpdatesAndRestart(update);
    }
}
