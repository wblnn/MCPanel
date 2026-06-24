using Microsoft.EntityFrameworkCore;

namespace Manager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    
    // 告诉 EF Core 我们有一张 Instances 表
    public DbSet<MinecraftInstance> Instances { get; set; }
}