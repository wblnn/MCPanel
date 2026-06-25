using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace DesktopApp;

public partial class MainWindow : Window
{
    private HubConnection _connection = null!;
    private HttpClient _http = null!;
    private List<InstanceDto> _instances = new();
    private string _currentInstanceId = null!;

    public MainWindow()
    {
        InitializeComponent();
        InitializeBackendConnection();
    }

    // ================= 1. 后端连接与初始化 =================
    private async void InitializeBackendConnection()
    {
        // 读取端口
        int backendPort = 5139;
        string portFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "port.txt");
        if (System.IO.File.Exists(portFilePath))
        {
            int.TryParse(System.IO.File.ReadAllText(portFilePath).Trim(), out backendPort);
        }

        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{backendPort}") };

        _connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost:{backendPort}/hubs/console")
            .WithAutomaticReconnect()
            .Build();

        // 监听日志
        _connection.On<string, string>("ReceiveLog", (instanceId, message) =>
        {
            if (instanceId == _currentInstanceId)
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var logBlock = this.FindControl<TextBlock>("LogTextBlock");
                    var scrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
                    if (logBlock != null)
                    {
                        logBlock.Text += message + "\n";
                        scrollViewer?.ScrollToEnd();
                    }
                });
            }
        });

        // 监听状态变化
        _connection.On<string, string>("InstanceStatusChanged", (id, status) =>
        {
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await LoadInstances();
            });
        });

        try
        {
            await _connection.StartAsync();
            var statusBlock = this.FindControl<TextBlock>("TxtStatus");
            if (statusBlock != null) statusBlock.Text = "✅ 已连接到后端！";
            await LoadInstances();
        }
        catch (Exception ex)
        {
            var statusBlock = this.FindControl<TextBlock>("TxtStatus");
            if (statusBlock != null) statusBlock.Text = $"❌ 连接失败: {ex.Message}";
        }
    }

    // ================= 2. 视图切换逻辑 =================
    private void BtnList_Click(object? sender, RoutedEventArgs e) => SwitchView("List");
    private void BtnConsole_Click(object? sender, RoutedEventArgs e) => SwitchView("Console");
    private void BtnFiles_Click(object? sender, RoutedEventArgs e) => SwitchView("Files");

    private void SwitchView(string viewName)
    {
        var viewList = this.FindControl<StackPanel>("ViewList");
        var viewConsole = this.FindControl<Grid>("ViewConsole");
        var viewFiles = this.FindControl<StackPanel>("ViewFiles");

        if (viewList != null) viewList.IsVisible = (viewName == "List");
        if (viewConsole != null) viewConsole.IsVisible = (viewName == "Console");
        if (viewFiles != null) viewFiles.IsVisible = (viewName == "Files");
    }

    // ================= 3. 实例列表管理 =================
    private async System.Threading.Tasks.Task LoadInstances()
    {
        try
        {
            var response = await _http.GetStringAsync("/api/instances");
            _instances = JsonSerializer.Deserialize<List<InstanceDto>>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            
            // 使用 ListBox 显示实例
            var listBox = this.FindControl<ListBox>("InstanceListBox");
            if (listBox != null)
            {
                listBox.ItemsSource = _instances;
            }
        }
        catch { }
    }

    private async void BtnAddInstance_Click(object? sender, RoutedEventArgs e)
    {
        // 简单演示：创建一个测试实例
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { Name = "测试服_" + DateTime.Now.Second, ServerPath = "D:/test", JarName = "server.jar", JvmArgs = "-Xms1G -Xmx2G" }), 
                Encoding.UTF8, "application/json");
            await _http.PostAsync("/api/instances", content);
            await LoadInstances();
        }
        catch { }
    }

    private async void InstanceListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is InstanceDto inst)
        {
            _currentInstanceId = inst.Id;
            var logBlock = this.FindControl<TextBlock>("LogTextBlock");
            if (logBlock != null)
            {
                logBlock.Text = $"[系统] 已选择实例: {inst.Name}\n";
            }
            SwitchView("Console");
        }
    }

    private async System.Threading.Tasks.Task ControlInstance(string id, string action)
    {
        try
        {
            await _http.PostAsync($"/api/instances/{id}/{action}", null);
            await LoadInstances();
        }
        catch { }
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await ControlInstance(id, "start");
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await ControlInstance(id, "stop");
        }
    }

    // ================= 4. 控制台指令发送 =================
    private async void BtnSendCmd_Click(object? sender, RoutedEventArgs e) => await SendCommand();
    
    private async void CmdInput_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) await SendCommand();
    }

    private async System.Threading.Tasks.Task SendCommand()
    {
        var cmdInput = this.FindControl<TextBox>("CmdInput");
        var logBlock = this.FindControl<TextBlock>("LogTextBlock");
        
        if (cmdInput == null || logBlock == null || string.IsNullOrWhiteSpace(cmdInput.Text) || _currentInstanceId == null) return;
        
        var cmd = cmdInput.Text;
        logBlock.Text += $"> {cmd}\n";
        cmdInput.Text = "";

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(new { Command = cmd }), Encoding.UTF8, "application/json");
            await _http.PostAsync($"/api/instances/{_currentInstanceId}/command", content);
        }
        catch { }
    }
}

public class InstanceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Stopped";
    public string ServerPath { get; set; } = "";
}