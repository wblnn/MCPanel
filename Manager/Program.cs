using System.Collections.Concurrent; 
using System.Diagnostics;            
using Microsoft.AspNetCore.SignalR;  

var builder = WebApplication.CreateBuilder(args);

// 注册 SignalR 服务（实时对讲机）
builder.Services.AddSignalR();

// 用来记住正在运行的服务器进程
var runningServers = new ConcurrentDictionary<string, Process>();

var app = builder.Build();

// 1. 先告诉 C#：如果有人访问根目录，请自动寻找 index.html 或 default.html
app.UseDefaultFiles(); 

// 2. 然后再告诉 C#：允许外界访问 wwwroot 文件夹里的静态文件
app.UseStaticFiles(); 

// 定义对讲机频道
app.MapHub<ServerConsoleHub>("/hubs/console");

// ================= 接口定义 =================

// 1. 启动服务器
app.MapPost("/api/start", async (StartRequest req, IHubContext<ServerConsoleHub> hubContext) =>
{
    string serverId = "server_01"; 

    if (runningServers.ContainsKey(serverId))
        return Results.BadRequest("服务器已经在运行啦！");

    var startInfo = new ProcessStartInfo
    {
        FileName = "java", 
        Arguments = $"-Xms1G -Xmx2G -jar {req.JarName} nogui", 
        WorkingDirectory = req.ServerPath, 
        RedirectStandardInput = true,  
        RedirectStandardOutput = true, 
        RedirectStandardError = true,  
        UseShellExecute = false,       
        CreateNoWindow = true,         
        StandardOutputEncoding = System.Text.Encoding.UTF8, 
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };

    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    
    // 监听日志并广播给网页
    process.OutputDataReceived += async (sender, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            await hubContext.Clients.All.SendAsync("ReceiveLog", e.Data);
    };
    process.ErrorDataReceived += async (sender, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            await hubContext.Clients.All.SendAsync("ReceiveLog", $"[错误] {e.Data}");
    };

    // 监听进程退出
    process.Exited += async (sender, e) =>
    {
        runningServers.TryRemove(serverId, out _);
        await hubContext.Clients.All.SendAsync("ReceiveLog", "[系统] 服务器已停止。");
    };

    try
    {
        process.Start();
        process.BeginOutputReadLine(); 
        process.BeginErrorReadLine();
        
        runningServers.TryAdd(serverId, process); 
        await hubContext.Clients.All.SendAsync("ReceiveLog", "[系统] 正在启动服务器...");
        return Results.Ok("启动指令已发送！");
    }
    catch (Exception ex)
    {
        return Results.Problem($"启动失败：{ex.Message}");
    }
});

// 2. 发送指令
app.MapPost("/api/command", (CommandRequest req) =>
{
    string serverId = "server_01";
    if (runningServers.TryGetValue(serverId, out var process) && !process.HasExited)
    {
        process.StandardInput.WriteLine(req.Command);
        process.StandardInput.Flush();
        return Results.Ok("指令已发送");
    }
    return Results.BadRequest("服务器没在运行！");
});

// 3. 停止服务器
app.MapPost("/api/stop", async (IHubContext<ServerConsoleHub> hubContext) =>
{
    string serverId = "server_01";
    if (runningServers.TryGetValue(serverId, out var process) && !process.HasExited)
    {
        process.StandardInput.WriteLine("stop");
        process.StandardInput.Flush();
        await hubContext.Clients.All.SendAsync("ReceiveLog", "[系统] 正在保存数据并停止...");
        return Results.Ok("停止指令已发送");
    }
    return Results.BadRequest("服务器本来就没运行");
});

app.Run();

// ================= 辅助类 =================
public class ServerConsoleHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("ReceiveLog", "[系统] 成功连接到控制面板！");
        await base.OnConnectedAsync();
    }
}

public record StartRequest(string ServerPath, string JarName);
public record CommandRequest(string Command);