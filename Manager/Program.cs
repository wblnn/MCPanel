using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Manager.Data;

var builder = WebApplication.CreateBuilder(args);

// ================= 1. 注册服务 =================
builder.Services.AddSignalR();

// 注册 SQLite 数据库
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=mcpanel.db"));

// 内存字典：专门用来存“正在运行的活进程”
var runningProcesses = new ConcurrentDictionary<string, Process>();

var app = builder.Build();

// ================= 2. 初始化数据库 =================
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated(); // 如果没有数据库，自动创建表和文件
}

// ================= 3. 中间件 =================
app.UseDefaultFiles(); 
app.UseStaticFiles(); 
app.MapHub<ConsoleHub>("/hubs/console");

// ================= 4. 实例管理 API (CRUD) =================

// 获取所有实例列表 (从数据库读)
app.MapGet("/api/instances", async (AppDbContext db) =>
{
    // 顺便把内存中的真实运行状态同步给数据库返回
    var instances = await db.Instances.ToListAsync();
    foreach (var inst in instances)
    {
        inst.Status = runningProcesses.ContainsKey(inst.Id) ? "Running" : "Stopped";
    }
    return instances;
});

// 创建新实例 (写入数据库)
app.MapPost("/api/instances", async (MinecraftInstance newInstance, AppDbContext db) =>
{
    newInstance.Id = Guid.NewGuid().ToString();
    newInstance.Status = "Stopped";
    db.Instances.Add(newInstance);
    await db.SaveChangesAsync();
    return Results.Created($"/api/instances/{newInstance.Id}", newInstance);
});

// 删除实例
app.MapDelete("/api/instances/{id}", async (string id, AppDbContext db) =>
{
    if (runningProcesses.ContainsKey(id)) return Results.BadRequest("请先停止服务器再删除！");
    var inst = await db.Instances.FindAsync(id);
    if (inst == null) return Results.NotFound();
    db.Instances.Remove(inst);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ================= 5. 进程控制 API (结合内存与数据库) =================

// 启动实例


// 发送指令
app.MapPost("/api/instances/{id}/command", (string id, CommandRequest req) =>
{
    if (runningProcesses.TryGetValue(id, out var p) && !p.HasExited) {
        p.StandardInput.WriteLine(req.Command);
        p.StandardInput.Flush();
        return Results.Ok();
    }
    return Results.BadRequest("未运行");
});

// 停止实例 (升级版：带日志 + 强制清理)
app.MapPost("/api/instances/{id}/stop", (string id) =>
{
    if (runningProcesses.TryGetValue(id, out var p)) 
    {
        if (!p.HasExited) 
        {
            Console.WriteLine($"[停止指令] 正在向实例 {id} 发送 stop 命令...");
            p.StandardInput.WriteLine("stop");
            p.StandardInput.Flush();
            return Results.Ok("停止指令已发送");
        } 
        else 
        {
            // 如果进程其实已经死了，但内存里还记着，就清理掉
            Console.WriteLine($"[清理僵尸] 实例 {id} 进程已退出，清理内存状态。");
            runningProcesses.TryRemove(id, out _);
            return Results.Ok("已清理僵尸状态");
        }
    }
    Console.WriteLine($"[停止失败] 找不到实例 {id} 的进程记录。");
    return Results.BadRequest("未运行或找不到实例");
});


// 启动实例 (升级版：智能路径解析 + 错误打印)
app.MapPost("/api/instances/{id}/start", async (string id, AppDbContext db, IHubContext<ConsoleHub> hub) =>
{
    if (runningProcesses.ContainsKey(id)) return Results.BadRequest("已在运行");
    
    var inst = await db.Instances.FindAsync(id);
    if (inst == null) return Results.NotFound();

    // ================= 商品级容错：智能解析路径 =================
    string workDir = inst.ServerPath;
    string jarFile = inst.JarName;

    // 如果用户不小心填成了文件的绝对路径（比如 D:/mc/server.jar），我们自动拆解！
    if (File.Exists(workDir)) 
    {
        jarFile = Path.GetFileName(workDir);
        workDir = Path.GetDirectoryName(workDir);
        Console.WriteLine($"[智能解析] 识别到文件路径，自动拆分 -> 目录: {workDir}, 核心: {jarFile}");
    }
    
    // 检查目录是否存在
    if (!Directory.Exists(workDir))
    {
        Console.WriteLine($"[启动报错] 目录不存在: {workDir}");
        return Results.Problem($"服务器目录不存在: {workDir}");
    }

    // 检查核心文件是否存在
    string fullJarPath = Path.Combine(workDir, jarFile);
    if (!File.Exists(fullJarPath))
    {
        Console.WriteLine($"[启动报错] 找不到核心文件: {fullJarPath}");
        return Results.Problem($"找不到核心文件: {jarFile}，请检查路径或文件名。");
    }
    // =========================================================

    var startInfo = new ProcessStartInfo
    {
        FileName = "java",
        Arguments = $"{inst.JvmArgs} -jar \"{jarFile}\" nogui", // 加上双引号防止路径有空格
        WorkingDirectory = workDir, // 使用解析后的正确目录
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };

    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    
    process.OutputDataReceived += async (s, e) => {
        if(!string.IsNullOrEmpty(e.Data)) await hub.Clients.All.SendAsync("ReceiveLog", id, e.Data);
    };
    process.ErrorDataReceived += async (s, e) => {
        if(!string.IsNullOrEmpty(e.Data)) await hub.Clients.All.SendAsync("ReceiveLog", id, $"[ERR] {e.Data}");
    };
    process.Exited += async (s, e) => {
        runningProcesses.TryRemove(id, out _);
        await hub.Clients.All.SendAsync("ReceiveLog", id, "[系统] 进程已退出。");
        await hub.Clients.All.SendAsync("InstanceStatusChanged", id, "Stopped");
    };

    try {
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        runningProcesses.TryAdd(id, process);
        
        Console.WriteLine($"[启动成功] 实例 {inst.Name} 已启动，PID: {process.Id}"); // 打印成功信息
        
        await hub.Clients.All.SendAsync("InstanceStatusChanged", id, "Running");
        return Results.Ok("启动成功");
    } catch (Exception ex) {
        // 【关键修改】：把错误打印在后端黑框框里，让你能看到！
        Console.WriteLine($"[启动异常] {ex.Message}"); 
        return Results.Problem(ex.Message);
    }
});

// ================= 6. 文件管理基础 API =================

// 获取目录列表
app.MapGet("/api/files", (string path) =>
{
    if (!Directory.Exists(path)) return Results.BadRequest("路径不存在");
    var dirs = Directory.GetDirectories(path).Select(d => new { name = Path.GetFileName(d), path = d, isDir = true, size = 0L });
    var files = Directory.GetFiles(path).Select(f => new { name = Path.GetFileName(f), path = f, isDir = false, size = new FileInfo(f).Length });
    return Results.Ok(dirs.Concat(files));
});

// 下载文件
app.MapGet("/api/files/download", (string path) =>
{
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(path, "application/octet-stream", Path.GetFileName(path));
});

app.Run();

// ================= 辅助类 =================
public class ConsoleHub : Hub {
    public override async Task OnConnectedAsync() {
        await Clients.Caller.SendAsync("ReceiveLog", "system", "[系统] 连接成功！");
        await base.OnConnectedAsync();
    }
}
public record CommandRequest(string Command);