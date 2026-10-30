using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;

namespace PadTas.App.Services;

/// <summary>
/// 再生に必要な ViGEmBus ドライバの検出と自動インストール。
///
/// 安全性の担保:
/// - 取得元は公式 GitHub リリース (nefarius/ViGEmBus v1.22.0) のみ
/// - ダウンロード後にサイズと SHA-256 を照合し、一致しないファイルは実行しない
///   (期待値は公式リリースの実ファイルをこちらで取得して算出したもの)
/// - 実行時は Windows の UAC 昇格ダイアログが必ず出る (ドライバ導入には管理者が必要)
///
/// 導入の流れ: サイレント実行を試行 → 検出できなければ通常ウィザードで起動し直す。
/// PC 再起動が必要な環境では、導入後も検出できない場合がある (その旨を UI で案内する)。
/// </summary>
public static class ViGEmDriverService
{
    public const string InstallerUrl =
        "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
    public const string InstallerSha256 =
        "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a";
    public const long InstallerSize = 6_278_576;

    /// <summary>ViGEmBus が使える状態か。仮想バスへの接続を試して判定する。</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var client = new ViGEmClient();
            return true;
        }
        catch (VigemBusNotFoundException) { return false; }
        catch (Exception) { return false; }
    }

    public enum InstallResult
    {
        Installed,      // 導入され、その場で検出できた
        NeedsReboot,    // インストーラーは終わったが未検出 (再起動が必要な可能性)
        Cancelled,      // UAC を断るなどして実行できなかった
        Failed,         // ダウンロード/照合/実行のいずれかで失敗
    }

    public static async Task<InstallResult> InstallAsync(Action<string>? report = null)
    {
        string installer;
        try
        {
            report?.Invoke("公式サイトから ViGEmBus ドライバをダウンロード中…");
            installer = await DownloadAndVerifyAsync();
        }
        catch (Exception ex)
        {
            report?.Invoke($"ダウンロードに失敗しました: {ex.Message}");
            return InstallResult.Failed;
        }

        // 1) サイレント実行 (WiX Burn の標準スイッチ)
        report?.Invoke("ドライバをインストール中… (管理者許可のダイアログが出たら「はい」を押してください)");
        var code = await RunElevatedAsync(installer, "/install /quiet /norestart");
        if (code == -2) return InstallResult.Cancelled;
        if (IsInstalled()) return InstallResult.Installed;

        // 2) サイレントが効かなかった場合は通常のウィザードで起動し直す
        if (code != 0)
        {
            report?.Invoke("通常のインストーラーを開きます。画面の案内に従って進めてください。");
            var code2 = await RunElevatedAsync(installer, "");
            if (code2 == -2) return InstallResult.Cancelled;
            if (IsInstalled()) return InstallResult.Installed;
        }
        return InstallResult.NeedsReboot;
    }

    private static async Task<string> DownloadAndVerifyAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PadTas");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ViGEmBus_1.22.0_x64_x86_arm64.exe");

        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(InstallerUrl);
        if (bytes.LongLength != InstallerSize)
            throw new InvalidDataException($"サイズが想定と違います ({bytes.LongLength} bytes)");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != InstallerSha256)
            throw new InvalidDataException("SHA-256 が一致しませんでした。安全のため実行しません。");

        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static Task<int> RunElevatedAsync(string file, string arguments) => Task.Run(() =>
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                Verb = "runas", // UAC 昇格
                UseShellExecute = true,
            });
            if (process is null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception) { return -2; } // UAC キャンセル等
    });
}
