using System.Collections.Concurrent; // 引入并发字典，用来安全地存储多个正在运行的MC服务器
using System.Diagnostics;            // 引入进程管理，用来启动和控制 Java 进程
using Microsoft.AspNetCore.SignalR;  // 引入 SignalR，用来做实时对讲机

var builder = WebApplication.CreateBuilder(args);

// ================= 1. 注册服务（把工具准备好） =================

// 注册 SignalR（对讲机服务）
builder.Services.AddSignalR();

// 创建一个全局的“字典”，用来记住哪些服务器正在运行。
// Key 是服务器ID，Value 是 Java 进程对象。
var runningServers = new ConcurrentDictionary<string, Process>();

var app = builder.Build();

// ================= 2. 定义“对讲机”频道 (Hub) =================

// 这个类就是“对讲机频道”，前端连上来后，通过这个频道收发消息
app.MapHub<ServerConsoleHub>("/hubs/console");

// ================= 3. 定义“前台平板”能调用的接口 (API) =================

// 接口1：启动服务器
// 网址：POST /api/start
app.MapPost("/api/start", async (StartRequest req, IHubContext<ServerConsoleHub> hubContext) =>
{
    string serverId = "server_01"; // 暂时写死一个ID，以后可以做成动态的

    // 如果已经在运行，就拒绝
    if (runningServers.ContainsKey(serverId))
        return Results.BadRequest("服务器已经在运行啦！");

    // 配置怎么启动 Java（也就是怎么把厨师叫进厨房）
    var startInfo = new ProcessStartInfo
    {
        FileName = "java", // 调用 java 命令
        Arguments = $"-Xms1G -Xmx2G -jar {req.JarName} nogui", // 内存参数和jar包名，nogui表示无界面
        WorkingDirectory = req.ServerPath, // 服务器文件所在的文件夹
        RedirectStandardInput = true,  // 允许我们往里面输入指令（递菜单）
        RedirectStandardOutput = true, // 允许我们读取它的输出（听厨师说话）
        RedirectStandardError = true,  // 允许我们读取它的报错
        UseShellExecute = false,       // 必须为false才能重定向
        CreateNoWindow = true          // 不要弹出黑框框
    };

    // 启动进程！
    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    
    // 监听厨师的输出（日志）
    process.OutputDataReceived += async (sender, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            // 把日志通过“对讲机”广播给所有连着的前端网页
            await hubContext.Clients.All.SendAsync("ReceiveLog", e.Data);
        }
    };
    process.ErrorDataReceived += async (sender, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            await hubContext.Clients.All.SendAsync("ReceiveLog", $"[错误] {e.Data}");
        }
    };

    // 监听厨师下班（进程退出）
    process.Exited += async (sender, e) =>
    {
        runningServers.TryRemove(serverId, out _);
        await hubContext.Clients.All.SendAsync("ReceiveLog", "[系统] 服务器已停止。");
    };

    try
    {
        process.Start();
        process.BeginOutputReadLine(); // 开始异步读取日志
        process.BeginErrorReadLine();
        
        runningServers.TryAdd(serverId, process); // 记入小本本
        await hubContext.Clients.All.SendAsync("ReceiveLog", "[系统] 服务器启动中...");
        return Results.Ok("启动成功！");
    }
    catch (Exception ex)
    {
        return Results.Problem($"启动失败：{ex.Message}");
    }
});

// 接口2：向服务器发送指令（比如在控制台输入 say 大家好）
// 网址：POST /api/command
app.MapPost("/api/command", (CommandRequest req) =>
{
    string serverId = "server_01";
    if (runningServers.TryGetValue(serverId, out var process) && !process.HasExited)
    {
        // 把指令写进 Java 进程的输入流里
        process.StandardInput.WriteLine(req.Command);
        process.StandardInput.Flush();
        return Results.Ok("指令已发送");
    }
    return Results.BadRequest("服务器没在运行，发个锤子指令");
});

// 接口3：停止服务器（优雅地让厨师下班）
// 网址：POST /api/stop
app.MapPost("/api/stop", async (IHubContext<ServerConsoleHub> hubContext) =>
{
    string serverId = "server_01";
    if (runningServers.TryGetValue(serverId, out var process) && !process.HasExited)
    {
        // 商品级细节：不要直接杀进程，而是输入 "stop" 命令，让 MC 自己保存数据并退出
        process.StandardInput.WriteLine("stop");
        process.StandardInput.Flush();
        await hubContext.Clients.All.SendAsync("ReceiveLog", "[系统] 正在保存数据并停止服务器...");
        return Results.Ok("停止指令已发送");
    }
    return Results.BadRequest("服务器本来就没运行");
});

app.UseStaticFiles();

// ================= 4. 启动程序 =================
app.Run();


// ================= 下面是辅助类（对讲机频道定义和请求参数） =================

// 对讲机频道定义
public class ServerConsoleHub : Hub
{
    // 前端连上来时触发
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("ReceiveLog", "[系统] 成功连接到控制台！");
        await base.OnConnectedAsync();
    }
}

// 启动服务器需要的参数
public record StartRequest(string ServerPath, string JarName);

// 发送指令需要的参数
public record CommandRequest(string Command);