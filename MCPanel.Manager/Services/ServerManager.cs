using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// MC 服务器管理器 —— 线程安全地管理多个 Java 进程
/// </summary>
public class ServerManager
{
    private readonly ConcurrentDictionary<string, ServerProfile> _profiles = new();
    private readonly ConcurrentDictionary<string, Process> _processes = new();

    // ── 配置档案 ──
    public void AddProfile(ServerProfile profile) => _profiles[profile.Id] = profile;
    public void RemoveProfile(string id) => _profiles.TryRemove(id, out _);
    public ServerProfile? GetProfile(string id) => _profiles.GetValueOrDefault(id);
    public List<ServerProfile> GetProfiles() => _profiles.Values.ToList();

    // ── 进程状态 ──
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

    // ── 持久化 ──
    public void LoadFromFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(path));
            if (saved != null)
            {
                foreach (var profile in saved)
                    AddProfile(profile);
                Console.WriteLine($"[配置] 已加载 {saved.Count} 个服务器配置。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[配置] 加载失败：{ex.Message}");
        }
    }

    public async Task SaveToFileAsync(string path)
    {
        var profiles = GetProfiles();
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }
}
