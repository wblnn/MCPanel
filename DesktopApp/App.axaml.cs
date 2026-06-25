using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DesktopApp;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 【一体化核心逻辑】：在显示主窗口前，先确保后端在运行
        EnsureBackendRunning();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void EnsureBackendRunning()
    {
        // 先读取端口
        int backendPort = 5139;
        string portFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "port.txt");
        if (System.IO.File.Exists(portFilePath))
        {
            int.TryParse(System.IO.File.ReadAllText(portFilePath).Trim(), out backendPort);
        }

        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            // 使用动态端口进行检查
            client.GetAsync($"http://localhost:{backendPort}/api/instances").Wait(); 
        }
        catch
        {
            // 连接失败，尝试在后台启动 Manager.exe
            Console.WriteLine("后端未运行，正在尝试自动启动...");
            
            // 获取当前 exe 所在的目录
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // 兼容 Windows 和 Linux/Mac 的后端文件名
            string backendExe = System.IO.Path.Combine(appDir, "Manager.exe");
            if (!System.IO.File.Exists(backendExe)) 
            {
                // 如果是 Linux/Mac，可能没有 .exe 后缀
                backendExe = System.IO.Path.Combine(appDir, "Manager"); 
            }

            if (System.IO.File.Exists(backendExe))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = backendExe,
                    UseShellExecute = false,
                    CreateNoWindow = true, // 隐藏后端的黑框框，实现“静默启动”
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                Process.Start(startInfo);
                
                // 等待 3 秒让后端初始化数据库和端口
                System.Threading.Thread.Sleep(3000); 
            }
        }
    }
}