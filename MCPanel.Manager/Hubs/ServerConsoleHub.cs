using Microsoft.AspNetCore.SignalR;

/// <summary>
/// SignalR 实时控制台 Hub —— 向前端推送 MC 服务器日志
/// </summary>
public class ServerConsoleHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("ReceiveLog", "*", "[系统] 已连接到控制台。");
        await base.OnConnectedAsync();
    }
}
