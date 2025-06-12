using FaissNet;

public class FaissNetSearch : IDisposable
{
    private readonly Dictionary<string, FaissNet.Index> _indices;
    private readonly Dictionary<string, Dictionary<long, Guid>> _idToText;
    private readonly Dictionary<string, long> _nextIds;
    private readonly int _dimension;
    private bool _disposed;
    private readonly object _lock = new();

    public FaissNetSearch(int dimension = 384) // Default for all-MiniLM-L6-v2
    {
        _dimension = dimension;
        _indices = new Dictionary<string, FaissNet.Index>();
        _idToText = new Dictionary<string, Dictionary<long, Guid>>();
        _nextIds = new Dictionary<string, long>();
    }

    public void AddEmbedding(string databaseId, Guid textId, float[] embedding)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FaissNetSearch));

        if (string.IsNullOrEmpty(databaseId))
            throw new ArgumentNullException(nameof(databaseId));

        if (embedding.Length != _dimension)
            throw new ArgumentException($"Embedding dimension must be {_dimension}.", nameof(embedding));

        lock (_lock)
        {
            EnsureDatabase(databaseId);

            var index = _indices[databaseId];
            var idToText = _idToText[databaseId];
            var nextId = _nextIds[databaseId];

            long faissId = nextId++;
            index.AddWithIds(new[] { embedding }, new[] { faissId });
            idToText[faissId] = textId;

            _nextIds[databaseId] = nextId;
        }
    }

    public void UpdateEmbedding(string databaseId, Guid textId, float[] embedding)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FaissNetSearch));

        lock (_lock)
        {
            EnsureDatabase(databaseId);

            var index = _indices[databaseId];
            var idToText = _idToText[databaseId];

            var existingId = idToText.FirstOrDefault(kvp => kvp.Value == textId).Key;
            if (idToText.ContainsKey(existingId))
            {
                index.RemoveIds(new[] { existingId });
                idToText.Remove(existingId);
            }

            AddEmbedding(databaseId, textId, embedding);
        }
    }

    public void RemoveEmbedding(string databaseId, Guid textId)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FaissNetSearch));

        lock (_lock)
        {
            if (!_indices.ContainsKey(databaseId))
                return;

            var index = _indices[databaseId];
            var idToText = _idToText[databaseId];

            var id = idToText.FirstOrDefault(kvp => kvp.Value == textId).Key;
            if (idToText.ContainsKey(id))
            {
                index.RemoveIds(new[] { id });
                idToText.Remove(id);
            }
        }
    }

    public Guid[] Search(string databaseId, float[] queryEmbedding, int k = 1)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FaissNetSearch));

        if (string.IsNullOrEmpty(databaseId))
            throw new ArgumentNullException(nameof(databaseId));

        if (queryEmbedding.Length != _dimension)
            throw new ArgumentException($"Query embedding dimension must be {_dimension}.", nameof(queryEmbedding));

        lock (_lock)
        {
            if (!_indices.ContainsKey(databaseId))
                return Array.Empty<Guid>();

            var index = _indices[databaseId];
            var idToText = _idToText[databaseId];

            var (distances, ids) = index.SearchFlat(queryEmbedding.Length, queryEmbedding, k);
            var results = new List<Guid>();

            for (int i = 0; i < ids.Length; i++)
            {
                if (idToText.TryGetValue(ids[i], out var text))
                {
                    results.Add(text);
                }
            }

            return results.ToArray();
        }
    }

    private void EnsureDatabase(string databaseId)
    {
        if (!_indices.ContainsKey(databaseId))
        {
            _indices[databaseId] = FaissNet.Index.Create(_dimension, "IVF256,Flat", MetricType.METRIC_L2);
            _idToText[databaseId] = new Dictionary<long, Guid>();
            _nextIds[databaseId] = 0;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                foreach (var index in _indices.Values)
                {
                    index?.Dispose();
                }
                _indices.Clear();
                _idToText.Clear();
                _nextIds.Clear();
            }
            _disposed = true;
        }
    }
}