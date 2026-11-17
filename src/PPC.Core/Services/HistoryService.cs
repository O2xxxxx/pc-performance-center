using Ppc.Core.Abstractions;
using Ppc.Core.Models;

namespace Ppc.Core.Services;

public sealed class HistoryService : IHistoryService
{
    private readonly Dictionary<Metric, HistoryBuffer> _buffers = new();
    private readonly object _gate = new();
    private int _capacity;

    public HistoryService(int capacity = 300) => _capacity = capacity;

    public void SetCapacity(int capacity)
    {
        lock (_gate)
        {
            if (capacity == _capacity) return;
            _capacity = capacity;
            _buffers.Clear();
        }
    }

    public void Append(HardwareSnapshot snapshot)
    {
        lock (_gate)
        {
            foreach (var pair in snapshot.Values)
            {
                if (!_buffers.TryGetValue(pair.Key, out var buffer))
                {
                    buffer = new HistoryBuffer(_capacity);
                    _buffers[pair.Key] = buffer;
                }
                buffer.Add(snapshot.Timestamp, pair.Value.Value);
            }
        }
    }

    public IReadOnlyList<(DateTimeOffset Stamp, double? Value)> GetSeries(Metric metric)
    {
        lock (_gate)
        {
            return _buffers.TryGetValue(metric, out var buffer) ? buffer.ToList() : Array.Empty<(DateTimeOffset, double?)>();
        }
    }

    public void Clear()
    {
        lock (_gate) _buffers.Clear();
    }
}
