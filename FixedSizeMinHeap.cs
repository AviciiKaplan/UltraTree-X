namespace UltraTree;

// Keeps only the largest K items using a min-heap.
// Thread-safe via a lock.
public sealed class FixedSizeMinHeap
{
    private readonly int _k;
    private readonly List<(long Bytes, string Path)> _heap = new();
    private readonly object _lockObj = new();

    public FixedSizeMinHeap(int topK) => _k = Math.Max(10, topK);

    public void Consider(string path, long bytes)
    {
        if (bytes <= 0) return;

        lock (_lockObj)
        {
            if (_heap.Count < _k)
            {
                _heap.Add((bytes, path));
                SiftUp(_heap.Count - 1);
                return;
            }

            if (_heap[0].Bytes >= bytes) return;

            _heap[0] = (bytes, path);
            SiftDown(0);
        }
    }

    public IEnumerable<(string Path, long Bytes)> GetTopDescending(int n)
    {
        lock (_lockObj)
        {
            return _heap
                .OrderByDescending(x => x.Bytes)
                .Take(n)
                .Select(x => (x.Path, x.Bytes))
                .ToArray();
        }
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (_heap[p].Bytes <= _heap[i].Bytes) break;
            (_heap[p], _heap[i]) = (_heap[i], _heap[p]);
            i = p;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1;
            int right = left + 1;
            int smallest = i;

            if (left < _heap.Count && _heap[left].Bytes < _heap[smallest].Bytes) smallest = left;
            if (right < _heap.Count && _heap[right].Bytes < _heap[smallest].Bytes) smallest = right;

            if (smallest == i) break;
            (_heap[smallest], _heap[i]) = (_heap[i], _heap[smallest]);
            i = smallest;
        }
    }
}
