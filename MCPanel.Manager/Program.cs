using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// ================= 1. 注册服务 =================

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(_ => true));
});

// 全局服务器状态管理器（线程安全）
var serverManager = new ServerManager();

// 从配置文件加载已保存的服务器列表
var configPath = Path.Combine(AppContext.BaseDirectory, "servers.json");
if (File.Exists(configPath))
{
    try
    {
        var saved = JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(configPath));
        if (saved != null)
        {
            foreach (var profile in saved)
                serverManager.AddProfile(profile);
            Console.WriteLine($"[配置] 已加载 {saved.Count} 个服务器配置。");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[配置] 加载失败：{ex.Message}");
    }
}

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles(); // 访问根路径时自动返回 index.html
app.UseStaticFiles();

// 持久化服务器配置（本地函数）
async Task SaveServerProfiles(ServerManager mgr, string savePath)
{
    var profiles = mgr.GetProfiles();
    var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(savePath, json);
}

// ================= 2. 实时控制台 Hub =================

app.MapHub<ServerConsoleHub>("/hubs/console");

// ================= 3. API 接口 =================

// 获取所有服务器配置及状态
app.MapGet("/api/servers", () =>
{
    var profiles = serverManager.GetProfiles();
    var result = profiles.Select(p => new
    {
        p.Id,
        p.Name,
        p.ServerPath,
        p.JarName,
        p.JavaArgs,
        Status = serverManager.IsRunning(p.Id) ? "running" : "stopped"
    });
    return Results.Ok(result);
});

// 添加或更新服务器配置
app.MapPost("/api/servers", async (ServerProfile profile) =>
{
    if (string.IsNullOrWhiteSpace(profile.Id))
        return Results.BadRequest("服务器ID不能为空");
    if (string.IsNullOrWhiteSpace(profile.ServerPath) || string.IsNullOrWhiteSpace(profile.JarName))
        return Results.BadRequest("服务器路径和Jar包名不能为空");

    serverManager.AddProfile(profile);
    await SaveServerProfiles(serverManager, configPath);
    return Results.Ok($"服务器 [{profile.Id}] 配置已保存");
});

// 删除服务器配置
app.MapDelete("/api/servers/{id}", async (string id) =>
{
    if (serverManager.IsRunning(id))
        return Results.BadRequest("服务器正在运行，无法删除");

    serverManager.RemoveProfile(id);
    await SaveServerProfiles(serverManager, configPath);
    return Results.Ok($"服务器 [{id}] 已删除");
});

// 启动服务器
app.MapPost("/api/servers/{id}/start", async (string id, IHubContext<ServerConsoleHub> hubContext) =>
{
    var profile = serverManager.GetProfile(id);
    if (profile == null)
        return Results.NotFound($"服务器 [{id}] 不存在");

    if (serverManager.IsRunning(id))
        return Results.BadRequest("服务器已经在运行");

    var javaExe = string.IsNullOrWhiteSpace(profile.JavaPath) ? "java" : profile.JavaPath;
    var args = $"{profile.JavaArgs} -jar {profile.JarName} nogui";

    var startInfo = new ProcessStartInfo
    {
        FileName = javaExe,
        Arguments = args,
        WorkingDirectory = profile.ServerPath,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

    process.OutputDataReceived += async (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            await hubContext.Clients.All.SendAsync("ReceiveLog", id, e.Data);
    };

    process.ErrorDataReceived += async (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            await hubContext.Clients.All.SendAsync("ReceiveLog", id, $"[错误] {e.Data}");
    };

    process.Exited += async (_, _) =>
    {
        serverManager.Stop(id);
        await hubContext.Clients.All.SendAsync("ServerStatusChanged", id, "stopped");
        await hubContext.Clients.All.SendAsync("ReceiveLog", id, "[系统] 服务器已停止。");
    };

    try
    {
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        serverManager.SetProcess(id, process);

        await hubContext.Clients.All.SendAsync("ServerStatusChanged", id, "running");
        await hubContext.Clients.All.SendAsync("ReceiveLog", id, "[系统] 服务器启动中...");
        return Results.Ok("启动成功！");
    }
    catch (Exception ex)
    {
        return Results.Problem($"启动失败：{ex.Message}");
    }
});

// 发送命令
app.MapPost("/api/servers/{id}/command", (string id, CommandRequest req) =>
{
    var process = serverManager.GetProcess(id);
    if (process == null || process.HasExited)
        return Results.BadRequest("服务器未运行");

    process.StandardInput.WriteLine(req.Command);
    process.StandardInput.Flush();
    return Results.Ok("指令已发送");
});

// 停止服务器
app.MapPost("/api/servers/{id}/stop", async (string id, IHubContext<ServerConsoleHub> hubContext) =>
{
    var process = serverManager.GetProcess(id);
    if (process == null || process.HasExited)
        return Results.BadRequest("服务器未运行");

    process.StandardInput.WriteLine("stop");
    process.StandardInput.Flush();
    await hubContext.Clients.All.SendAsync("ReceiveLog", id, "[系统] 正在保存数据并停止服务器...");
    return Results.Ok("停止指令已发送");
});

// 强制停止（紧急情况使用）
app.MapPost("/api/servers/{id}/forcestop", async (string id, IHubContext<ServerConsoleHub> hubContext) =>
{
    var process = serverManager.GetProcess(id);
    if (process == null || process.HasExited)
        return Results.BadRequest("服务器未运行");

    process.Kill(entireProcessTree: true);
    serverManager.Stop(id);
    await hubContext.Clients.All.SendAsync("ServerStatusChanged", id, "stopped");
    await hubContext.Clients.All.SendAsync("ReceiveLog", id, "[系统] 服务器已强制停止。");
    return Results.Ok("已强制停止");
});

// 健康检查
app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "healthy",
    Time = DateTime.Now,
    Servers = serverManager.GetProfiles().Select(p => new
    {
        Id = p.Id,
        Running = serverManager.IsRunning(p.Id)
    })
}));

// ================= 4. 启动 =================

// 自动打开浏览器（仅可执行文件模式）
var config = app.Configuration.GetSection("MCPanel");
var autoOpenBrowser = config.GetValue<bool>("AutoOpenBrowser");
var urls = app.Configuration.GetSection("Kestrel:Endpoints:Http:Url").Value ?? "http://localhost:5162";
var port = urls.Contains(':') ? urls.Split(':').Last() : "5162";

if (autoOpenBrowser)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(1500); // 等待服务启动
        try { Process.Start(new ProcessStartInfo { FileName = $"http://localhost:{port}", UseShellExecute = true }); }
        catch { /* 忽略浏览器启动失败 */ }
    });
}

Console.WriteLine($"MC Panel 已启动 → http://localhost:{port}");
Console.WriteLine("按 Ctrl+C 停止程序");

app.Run();


// ================= 辅助类 =================

// 服务器管理器
public class ServerManager
{
    private readonly ConcurrentDictionary<string, ServerProfile> _profiles = new();
    private readonly ConcurrentDictionary<string, Process> _processes = new();

    public void AddProfile(ServerProfile profile) => _profiles[profile.Id] = profile;
    public void RemoveProfile(string id) => _profiles.TryRemove(id, out _);
    public ServerProfile? GetProfile(string id) => _profiles.GetValueOrDefault(id);
    public List<ServerProfile> GetProfiles() => _profiles.Values.ToList();

    public bool IsRunning(string id)
    {
        if (_processes.TryGetValue(id, out var process) && !process.HasExited)
            return true;
        _processes.TryRemove(id, out _);
        return false;
    }

    public void SetProcess(string id, Process process) => _processes[id] = process;
    public Process? GetProcess(string id) => _processes.GetValueOrDefault(id);

    public void Stop(string id) => _processes.TryRemove(id, out _);
}

// 服务器配置
public class ServerProfile
{
    public string Id { get; set; } = "server_01";
    public string Name { get; set; } = "我的服务器";
    public string ServerPath { get; set; } = "";
    public string JarName { get; set; } = "server.jar";
    public string JavaArgs { get; set; } = "-Xms1G -Xmx2G";
    public string JavaPath { get; set; } = "";
}

// SignalR Hub
public class ServerConsoleHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("ReceiveLog", "*", "[系统] 已连接到控制台。");
        await base.OnConnectedAsync();
    }
}

// 请求参数
public record CommandRequest(string Command);
