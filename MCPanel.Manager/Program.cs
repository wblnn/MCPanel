using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;

// ================= 1. 服务注册 =================

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(_ => true));
});

// 注册 ServerManager 为单例（全局共享，线程安全）
builder.Services.AddSingleton<ServerManager>();

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// 从配置文件加载已保存的服务器列表
var configPath = Path.Combine(AppContext.BaseDirectory, "servers.json");
var serverManager = app.Services.GetRequiredService<ServerManager>();
serverManager.LoadFromFile(configPath);

// ================= 2. SignalR Hub =================

app.MapHub<ServerConsoleHub>("/hubs/console");

// ================= 3. API 路由 =================

// 获取所有服务器配置及状态
app.MapGet("/api/servers", (ServerManager mgr) =>
{
    var result = mgr.GetProfiles().Select(p => new
    {
        p.Id, p.Name, p.ServerPath, p.JarName, p.JavaArgs,
        Status = mgr.IsRunning(p.Id) ? "running" : "stopped"
    });
    return Results.Ok(result);
});

// 添加或更新服务器配置
app.MapPost("/api/servers", async (ServerProfile profile, ServerManager mgr) =>
{
    if (string.IsNullOrWhiteSpace(profile.Id))
        return Results.BadRequest("服务器ID不能为空");
    if (string.IsNullOrWhiteSpace(profile.ServerPath) || string.IsNullOrWhiteSpace(profile.JarName))
        return Results.BadRequest("服务器路径和Jar包名不能为空");

    mgr.AddProfile(profile);
    await mgr.SaveToFileAsync(configPath);
    return Results.Ok($"服务器 [{profile.Id}] 配置已保存");
});

// 删除服务器配置
app.MapDelete("/api/servers/{id}", async (string id, ServerManager mgr) =>
{
    if (mgr.IsRunning(id))
        return Results.BadRequest("服务器正在运行，无法删除");

    mgr.RemoveProfile(id);
    await mgr.SaveToFileAsync(configPath);
    return Results.Ok($"服务器 [{id}] 已删除");
});

// 启动服务器
app.MapPost("/api/servers/{id}/start", async (string id, ServerManager mgr, IHubContext<ServerConsoleHub> hub) =>
{
    var profile = mgr.GetProfile(id);
    if (profile == null)
        return Results.NotFound($"服务器 [{id}] 不存在");
    if (mgr.IsRunning(id))
        return Results.BadRequest("服务器已经在运行");

    var javaExe = string.IsNullOrWhiteSpace(profile.JavaPath) ? "java" : profile.JavaPath;
    var startInfo = new ProcessStartInfo
    {
        FileName = javaExe,
        Arguments = $"{profile.JavaArgs} -jar {profile.JarName} nogui",
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
            await hub.Clients.All.SendAsync("ReceiveLog", id, e.Data);
    };
    process.ErrorDataReceived += async (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            await hub.Clients.All.SendAsync("ReceiveLog", id, $"[错误] {e.Data}");
    };
    process.Exited += async (_, _) =>
    {
        mgr.Stop(id);
        process.Dispose(); // #2 显式释放进程句柄，防止句柄泄漏
        await hub.Clients.All.SendAsync("ServerStatusChanged", id, "stopped");
        await hub.Clients.All.SendAsync("ReceiveLog", id, "[系统] 服务器已停止。");
    };

    try
    {
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        mgr.SetProcess(id, process);

        await hub.Clients.All.SendAsync("ServerStatusChanged", id, "running");
        await hub.Clients.All.SendAsync("ReceiveLog", id, "[系统] 服务器启动中...");
        return Results.Ok("启动成功！");
    }
    catch (Exception ex)
    {
        return Results.Problem($"启动失败：{ex.Message}");
    }
});

// 发送命令
app.MapPost("/api/servers/{id}/command", (string id, CommandRequest req, ServerManager mgr) =>
{
    var process = mgr.GetProcess(id);
    if (process == null || process.HasExited)
        return Results.BadRequest("服务器未运行");

    process.StandardInput.WriteLine(req.Command);
    process.StandardInput.Flush();
    return Results.Ok("指令已发送");
});

// 停止服务器
app.MapPost("/api/servers/{id}/stop", async (string id, ServerManager mgr, IHubContext<ServerConsoleHub> hub) =>
{
    var process = mgr.GetProcess(id);
    if (process == null || process.HasExited)
        return Results.BadRequest("服务器未运行");

    process.StandardInput.WriteLine("stop");
    process.StandardInput.Flush();
    await hub.Clients.All.SendAsync("ReceiveLog", id, "[系统] 正在保存数据并停止服务器...");
    return Results.Ok("停止指令已发送");
});

// 强制停止
app.MapPost("/api/servers/{id}/forcestop", async (string id, ServerManager mgr, IHubContext<ServerConsoleHub> hub) =>
{
    var process = mgr.GetProcess(id);
    if (process == null || process.HasExited)
        return Results.BadRequest("服务器未运行");

    process.Kill(entireProcessTree: true);
    mgr.Stop(id);
    await hub.Clients.All.SendAsync("ServerStatusChanged", id, "stopped");
    await hub.Clients.All.SendAsync("ReceiveLog", id, "[系统] 服务器已强制停止。");
    return Results.Ok("已强制停止");
});

// 健康检查
app.MapGet("/api/health", (ServerManager mgr) => Results.Ok(new
{
    Status = "healthy",
    Time = DateTime.Now,
    Servers = mgr.GetProfiles().Select(p => new { Id = p.Id, Running = mgr.IsRunning(p.Id) })
}));

// ================= 4. 启动 =================

var urls = app.Configuration.GetSection("Kestrel:Endpoints:Http:Url").Value ?? "http://localhost:5162";
var port = urls.Contains(':') ? urls.Split(':').Last().TrimEnd('/') : "5162";
var panelUrl = $"http://localhost:{port}";

// 创建桌面窗口实例
var mainForm = new MainForm(panelUrl);

// 启动 WinForms UI 线程（STA）
var uiThread = new Thread(() =>
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(mainForm);
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.IsBackground = true;
uiThread.Start();

Console.WriteLine($"MC Panel 已启动 → {panelUrl}");
Console.WriteLine("关闭窗口将最小化到系统托盘。按 Ctrl+C 停止程序。");

// 后台监控退出请求（来自托盘菜单「退出」）
_ = Task.Run(async () =>
{
    while (!mainForm.RequestExit)
        await Task.Delay(200);

    Console.WriteLine("[系统] 收到退出指令，正在关闭...");
    await app.StopAsync();
    if (mainForm.InvokeRequired)
        mainForm.Invoke(mainForm.Close);
    else
        mainForm.Close();
});

app.Run();
