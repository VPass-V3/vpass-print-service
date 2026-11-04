using System.Text.Json;

namespace VPass.PrintService.Core;

/// <summary>
/// Remembers the last N job ids this device has printed (or acked), persisted to acked.json.
/// The server re-delivers a job 2 minutes after delivery if no ack arrived, so a lost ack (network
/// blip right after the print) would otherwise print the badge twice. Lookup here is what makes
/// delivery at-least-once but printing (effectively) exactly-once.
/// </summary>
public sealed class AckLedger
{
    public const int DefaultCapacity = 500;
    private readonly string _path;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<long> _order = new();
    private readonly HashSet<long> _set = new();

    public AckLedger(string path, int capacity = DefaultCapacity)
    {
        _path = path;
        _capacity = capacity;
        Load();
    }

    public int Count { get { lock (_gate) { return _set.Count; } } }

    public bool Contains(long jobId)
    {
        lock (_gate) { return _set.Contains(jobId); }
    }

    public void Add(long jobId)
    {
        lock (_gate)
        {
            if (!_set.Add(jobId))
            {
                return;
            }
            _order.Enqueue(jobId);
            while (_order.Count > _capacity)
            {
                _set.Remove(_order.Dequeue());
            }
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            long[]? ids = JsonSerializer.Deserialize<long[]>(File.ReadAllText(_path));
            if (ids == null) return;
            foreach (long id in ids.TakeLast(_capacity))
            {
                if (_set.Add(id)) _order.Enqueue(id);
            }
        }
        catch
        {
            // unreadable ledger: start empty (worst case one duplicate print)
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_order.ToArray()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }
}
