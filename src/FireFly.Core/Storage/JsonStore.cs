using System.Text.Json;
using FireFly.Core.Models;

namespace FireFly.Core.Storage;

/// <summary>小体量状态的 JSON 文件持久化：先写临时文件再改名覆盖，避免半写的文件。</summary>
public sealed class JsonStore<T>
    where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private T _value;

    public JsonStore(string path)
    {
        _path = path;
        _value = Read();
    }

    public TResult Read<TResult>(Func<T, TResult> read)
    {
        lock (_gate)
        {
            return read(_value);
        }
    }

    public TResult Update<TResult>(Func<T, TResult> update)
    {
        lock (_gate)
        {
            var result = update(_value);
            Write();
            return result;
        }
    }

    private T Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) ?? new T() : new T();
        }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
            return new T();
        }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_value, Options));
        File.Move(temp, _path, overwrite: true);
    }
}

public sealed class TaskList
{
    public List<DownloadTask> Tasks { get; set; } = new();
}
