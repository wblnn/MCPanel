/// <summary>
/// MC 服务器配置档案（持久化到 servers.json）
/// </summary>
public class ServerProfile
{
    public string Id { get; set; } = "server_01";
    public string Name { get; set; } = "我的服务器";
    public string ServerPath { get; set; } = "";
    public string JarName { get; set; } = "server.jar";
    public string JavaArgs { get; set; } = "-Xms1G -Xmx2G";
    public string JavaPath { get; set; } = "";
}
