using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Microsoft.AspNetCore.SignalR.Client;

namespace DesktopApp;

public partial class MainWindow : Window
{
    private HubConnection _connection = null!;
    private HttpClient _http = null!;
    private List<InstanceDto> _instances = new();
    private string _currentInstanceId = "";
    private string _currentFileInstanceId = "";
    private string _currentFilePath = "";

    public MainWindow()
    {
        InitializeComponent();
        InitializeBackendConnection();
        
        // 让标题栏可拖拽（简化版）
        var titleBar = this.FindControl<Grid>("TitleBar");
        if (titleBar != null)
        {
            titleBar.PointerPressed += (s, e) =>
            {
                // 检查点击源是否是按钮，如果是则不触发拖拽
                if (e.Source is Button)
                {
                    return; // 是按钮点击，不执行拖拽
                }
                
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    this.BeginMoveDrag(e);
                }
            };
        }
    }

    // ================= 1. 后端连接与初始化 =================
    private async void InitializeBackendConnection()
    {
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
    
    private async void BtnConsole_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentInstanceId))
        {
            await ShowMessageAsync("提示", "请先在实例列表中选择一个实例！");
            return;
        }
        SwitchView("Console");
    }
    
    private async void BtnFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFileInstanceId))
        {
            await ShowMessageAsync("提示", "请先在实例列表中选择一个实例！");
            return;
        }
        OpenFileManager();
    }

    private void BtnMinimize_Click(object? sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void SwitchView(string viewName)
    {
        var viewList = this.FindControl<StackPanel>("ViewList");
        var viewConsole = this.FindControl<Grid>("ViewConsole");
        var viewFiles = this.FindControl<Grid>("ViewFiles");

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
        // 【修复】动态创建一个无边框的粉色弹窗，不再使用 new MainWindow()
        var dialog = new Window
        {
            Title = "新建实例",
            Width = 400,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#fff0f5"),
            CanResize = false,
            WindowDecorations = WindowDecorations.None
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        
        panel.Children.Add(new TextBlock 
        { 
            Text = "✨ 新建 MC 实例", 
            FontSize = 18, 
            FontWeight = FontWeight.Bold, 
            Foreground = Brush.Parse("#d63384"), 
            Margin = new Thickness(0, 0, 0, 15) 
        });

        var txtName = new TextBox { Watermark = "实例名称 (如: 我的生存服)", Margin = new Thickness(0, 0, 0, 10), Background = Brushes.White, Foreground = Brush.Parse("#333333") };
        panel.Children.Add(txtName);

        var txtPath = new TextBox { Watermark = "服务端路径 (如: D:/MCServer)", Margin = new Thickness(0, 0, 0, 10), Background = Brushes.White, Foreground = Brush.Parse("#333333") };
        panel.Children.Add(txtPath);

        var txtJar = new TextBox { Watermark = "核心文件名", Text = "server.jar", Margin = new Thickness(0, 0, 0, 20), Background = Brushes.White, Foreground = Brush.Parse("#333333") };
        panel.Children.Add(txtJar);

        var btnCreate = new Button 
        { 
            Content = "✅ 创建实例", 
            Background = Brush.Parse("#4caf50"), 
            Foreground = Brushes.White, 
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10),
            FontWeight = FontWeight.Bold
        };
        
        btnCreate.Click += async (s, ev) =>
        {
            if (string.IsNullOrWhiteSpace(txtName.Text) || string.IsNullOrWhiteSpace(txtPath.Text))
            {
                txtName.Watermark = "⚠️ 请填写名称和路径！";
                return;
            }

            var newInstance = new InstanceDto
            {
                Name = txtName.Text,
                ServerPath = txtPath.Text,
                JarName = string.IsNullOrWhiteSpace(txtJar.Text) ? "server.jar" : txtJar.Text,
                JvmArgs = "-Xms1G -Xmx2G",
                Status = "Stopped"
            };

            try 
            {
                var jsonData = JsonSerializer.Serialize(newInstance);
                var content = new StringContent(jsonData);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var response = await _http.PostAsync("/api/instances", content);
                
                if (response.IsSuccessStatusCode) 
                {
                    await LoadInstances();
                }
            } 
            catch (Exception ex) 
            {
                System.Console.WriteLine($"创建失败: {ex.Message}");
            }

            dialog.Close();
        };

        panel.Children.Add(btnCreate);
        
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private void InstanceCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        
        if (sender is Border border && border.DataContext is InstanceDto inst)
        {
            _currentInstanceId = inst.Id;
            _currentFileInstanceId = inst.Id;
            
            var logBlock = this.FindControl<TextBlock>("LogTextBlock");
            if (logBlock != null)
            {
                logBlock.Text = $"[系统] 已选中实例: {inst.Name}\n";
            }
        }
    }

    private void BtnCardConsole_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _currentInstanceId = id;
            SwitchView("Console");
        }
    }

    private void BtnCardFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _currentFileInstanceId = id;
            OpenFileManager();
        }
    }

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await ControlInstance(id, "start");
        }
    }

    private async void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            await ControlInstance(id, "stop");
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

    // ================= 4. 控制台指令发送 =================
    private async void BtnSendCmd_Click(object? sender, RoutedEventArgs e) => await SendCommand();
    
    private async void CmdInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendCommand();
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

    // ================= 5. 文件管理功能 =================
    public void OpenFileManager()
    {
        if (!string.IsNullOrWhiteSpace(_currentFileInstanceId))
        {
            var inst = _instances.Find(i => i.Id == _currentFileInstanceId);
            if (inst != null)
            {
                _currentFilePath = inst.ServerPath;
                _ = LoadFiles(inst.ServerPath);
                SwitchView("Files");
            }
        }
    }

    private async System.Threading.Tasks.Task LoadFiles(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        
        try
        {
            _currentFilePath = path;
            
            var txtPath = this.FindControl<TextBox>("TxtCurrentPath");
            if (txtPath != null)
            {
                txtPath.Text = path;
            }
            
            var response = await _http.GetAsync($"/api/files?path={Uri.EscapeDataString(path)}");
            if (!response.IsSuccessStatusCode) return;
            
            var json = await response.Content.ReadAsStringAsync();
            var files = JsonSerializer.Deserialize<List<FileItemDto>>(json, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            }) ?? new List<FileItemDto>();
            
            files = files.OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name).ToList();
            
            var listBox = this.FindControl<ListBox>("FileListBox");
            if (listBox != null)
            {
                listBox.ItemsSource = files;
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"加载文件失败: {ex.Message}");
        }
    }

    private void BtnBack_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath)) return;
        
        var parentDir = System.IO.Path.GetDirectoryName(_currentFilePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            _ = LoadFiles(parentDir);
        }
    }

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
        {
            _ = LoadFiles(_currentFilePath);
        }
    }

    private async void FileListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (this.FindControl<ListBox>("FileListBox")?.SelectedItem is FileItemDto file)
        {
            if (file.IsDirectory)
            {
                await LoadFiles(file.Path);
            }
        }
    }

    private async void BtnDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
        {
            try
            {
                var response = await _http.GetAsync($"/api/files/download?path={Uri.EscapeDataString(filePath)}");
                if (!response.IsSuccessStatusCode) return;
                
                var fileName = System.IO.Path.GetFileName(filePath);
                
                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider == null) return;
                
                var savePath = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = $"保存文件: {fileName}",
                    SuggestedFileName = fileName,
                    DefaultExtension = System.IO.Path.GetExtension(fileName),
                    FileTypeChoices = new[] { new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } } }
                });
                
                if (savePath != null)
                {
                    var fileContent = await response.Content.ReadAsByteArrayAsync();
                    await System.IO.File.WriteAllBytesAsync(savePath.Path.LocalPath, fileContent);
                    
                    Title = $"✅ 文件已下载";
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"下载失败: {ex.Message}");
            }
        }
    }

    // ================= 6. 辅助方法 =================
    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#fff0f5"),
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            SizeToContent = SizeToContent.Manual
        };

        var grid = new Grid();
        grid.RowDefinitions = new RowDefinitions("30,*");
        
        var titleGrid = new Grid();
        titleGrid.Background = Brush.Parse("#d63384");
        Grid.SetRow(titleGrid, 0);
        
        var titleText = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        
        var closeButton = new Button
        {
            Content = "✕",
            Width = 30,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeight.Bold
        };
        closeButton.Click += (s, e) => dialog.Close();
        
        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(closeButton);
        grid.Children.Add(titleGrid);
        
        var contentPanel = new StackPanel();
        contentPanel.Margin = new Thickness(20);
        Grid.SetRow(contentPanel, 1);
        
        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = Brush.Parse("#333333"),
            TextWrapping = TextWrapping.Wrap
        };
        
        var okButton = new Button
        {
            Content = "确定",
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(30, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brush.Parse("#d63384"),
            Foreground = Brushes.White
        };
        okButton.Click += (s, e) => dialog.Close();
        
        contentPanel.Children.Add(messageText);
        contentPanel.Children.Add(okButton);
        grid.Children.Add(contentPanel);
        
        dialog.Content = grid;
        await dialog.ShowDialog(this);
    }
}

public class InstanceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Stopped";
    public string ServerPath { get; set; } = "";
    public string JarName { get; set; } = "server.jar";
    public string JvmArgs { get; set; } = "-Xms1G -Xmx2G";
}

public class FileItemDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeText 
    { 
        get 
        {
            if (IsDirectory) return "文件夹";
            return FormatFileSize(Size);
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}