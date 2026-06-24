using System.ComponentModel.DataAnnotations;

namespace Manager.Data;

public class MinecraftInstance
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString(); // 主键
    public string Name { get; set; } = "新建实例";               // 实例名称
    public string ServerPath { get; set; } = string.Empty;      // 服务端路径
    public string JarName { get; set; } = "server.jar";         // Jar包名
    public string JvmArgs { get; set; } = "-Xms1G -Xmx2G";      // JVM参数
    
    // 运行状态（注意：这个状态在内存中维护，数据库只存最后一次的快照）
    public string Status { get; set; } = "Stopped"; 
}