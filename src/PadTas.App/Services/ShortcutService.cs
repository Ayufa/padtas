using System.IO;
using System.Runtime.InteropServices;

namespace PadTas.App.Services;

/// <summary>
/// スタートメニュー検索に確実に出るよう、アプリ側でショートカットを自己修復する。
/// Velopack のインストーラーもショートカットを作るが、環境によって
/// 見つからない場合があるため、起動時に不足していれば作り直す。
/// ショートカットの飛び先は Velopack のスタブ exe (常に最新版を起動する)。
/// </summary>
public static class ShortcutService
{
    public static void EnsureStartMenuShortcut()
    {
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(programs)) return;
            var linkPath = Path.Combine(programs, "PadTAS.lnk");
            if (File.Exists(linkPath)) return;

            // インストール先ルート: ...\<packId>\current\<exe> の 1 つ上がルート。
            // ルート直下のスタブ exe があればそれを、無ければ自分自身を指す。
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var root = Directory.GetParent(baseDir)?.FullName;
            var stub = root is null ? null : Path.Combine(root, "PadTas.exe");
            var target = stub is not null && File.Exists(stub)
                ? stub
                : Environment.ProcessPath ?? Path.Combine(baseDir, "PadTas.exe");

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? baseDir);
            link.SetDescription("PadTAS - コントローラー TAS レコーダー");
            var icon = Path.Combine(baseDir, "Assets", "icon.ico");
            if (File.Exists(icon)) link.SetIconLocation(icon, 0);

            ((IPersistFile)link).Save(linkPath, true);
            CrashLog.Log($"スタートメニューのショートカットを作成: {linkPath} -> {target}");
        }
        catch (Exception ex)
        {
            CrashLog.LogException("ショートカット作成に失敗 (無視)", ex);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
