using System.Text.Json;

namespace CCClient;

public sealed class LastSeqStore
{
    private readonly string _path;
    private readonly Dictionary<string, long> _map;

    public LastSeqStore(string path)
    {
        _path = path;
        _map = Load(path);
    }

    public long Get(string RoomId)
        => _map.TryGetValue(RoomId, out var v) ? v : 0;

    public void Set(string RoomId, long seq)
    {
        _map[RoomId] = seq;
        Save(_path, _map);
    }

    private static Dictionary<string, long> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, long>(StringComparer.Ordinal);
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, long>>(json)
                   ?? new Dictionary<string, long>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private static void Save(string path, Dictionary<string, long> map)
    {
        try
        {
            var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // 테스트용: 저장 실패는 무시
        }
    }
}