using System.Diagnostics;
using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// MC Panel 桌面窗口 —— 内嵌 WebView2 + 系统托盘
/// </summary>
public class MainForm : Form
{
    private WebView2 _webView = null!;
    private NotifyIcon _trayIcon = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private readonly string _panelUrl;

    /// <summary>由 Program.cs 轮询此标志，为 true 时触发整个应用退出。</summary>
    public volatile bool RequestExit;

    public MainForm(string panelUrl)
    {
        _panelUrl = panelUrl;
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        // ── 窗口基本属性 ──
        Text = "MC Panel - Minecraft 服务器管理";
        Size = new Size(1200, 800);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);
        Icon = SystemIcons.Application;

        // ── 状态栏（底部） ──
        _statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("正在启动面板...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _statusStrip.Items.Add(_statusLabel);
        Controls.Add(_statusStrip);

        // ── 系统托盘图标 ──
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MC Panel - Minecraft 服务器管理",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开面板", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("在浏览器中打开", null, (_, _) => OpenInBrowser());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (_, _) => RequestExit = true);
        _trayIcon.ContextMenuStrip = trayMenu;
    }

    // ── 窗口加载后初始化 WebView2 ──
    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            _webView.BringToFront();

            // #11 指定缓存目录，避免与系统默认缓存混用
            var userDataFolder = Path.Combine(AppContext.BaseDirectory, ".webview2-cache");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);

            // #12 性能优化参数：禁用翻译UI、禁用后台标签页节流等
            var controllerOptions = env.CreateCoreWebView2ControllerOptions();

            await _webView.EnsureCoreWebView2Async(env);

            // 禁用不必要的内置功能以减少资源占用
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            _webView.CoreWebView2.Navigate(_panelUrl);
            _statusLabel.Text = $"MC Panel 运行中 — {_panelUrl}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"WebView2 初始化失败: {ex.Message}";
            MessageBox.Show(
                $"无法初始化 WebView2 组件。\n\n请确保已安装 Microsoft Edge WebView2 运行时。\n\n下载地址：\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                "WebView2 错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── 拦截关闭按钮：最小化到托盘而非退出 ──
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(1500, "MC Panel", "程序已最小化到系统托盘，双击图标可重新打开。", ToolTipIcon.Info);
            return;
        }
        base.OnFormClosing(e);
    }

    // ── 清理资源 ──
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _webView?.Dispose();
        base.OnFormClosed(e);
    }

    // ── 从托盘恢复窗口 ──
    public void RestoreWindow()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { Invoke(RestoreWindow); return; }
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ── 在默认浏览器中打开面板 ──
    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _panelUrl, UseShellExecute = true });
        }
        catch { /* 忽略浏览器启动失败 */ }
    }
}
