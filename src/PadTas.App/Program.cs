using Velopack;

namespace PadTas.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 自動更新フックはアプリ本体より先に動かす必要がある (Velopack の作法)。
        // インストール / 更新 / アンインストール時の引数はここで処理される。
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
