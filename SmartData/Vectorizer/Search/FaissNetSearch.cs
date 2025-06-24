using FaissNet;
using Microsoft.Extensions.Logging;

namespace SmartData.Vectorizer.Search
{
    public class FaissNetSearch : IDisposable
    {
        private readonly FaissNet.Index _index;
        private readonly Dictionary<long, Guid> _idToEmbeddingId;
        private long _nextId;
        private readonly int _dimension;
        private readonly ILogger _logger;
        private bool _disposed;
        private readonly object _lock = new();

        public FaissNetSearch(int dimension = 384, ILogger logger = null)
        {
            _dimension = dimension;
            _logger = logger;
            _index = FaissNet.Index.CreateDefault(_dimension, MetricType.METRIC_L2); // NEW: Use FlatL2
            _idToEmbeddingId = new Dictionary<long, Guid>();
            _nextId = 0;
            _logger?.LogDebug("Initialized FaissNetSearch with dimension {Dimension} and index type FlatL2", _dimension);
        }

        // NEW: Removed Train method as FlatL2 does not require training
        // Retained for potential future use with other index types
        public void Train(IEnumerable<float[]> embeddings)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to train index after disposal");
                throw new ObjectDisposedException(nameof(FaissNetSearch));
            }

            lock (_lock)
            {
                _logger?.LogDebug("Training not required for FlatL2 index, skipping");
            }
        }

        public void AddEmbedding(Guid embeddingId, float[] embedding)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to add embedding after disposal");
                throw new ObjectDisposedException(nameof(FaissNetSearch));
            }

            if (embedding == null || embedding.Length != _dimension)
            {
                _logger?.LogError("Invalid embedding: Length {Length}, Expected {Dimension}", embedding?.Length ?? 0, _dimension);
                throw new ArgumentException($"Embedding dimension must be {_dimension}.", nameof(embedding));
            }

            lock (_lock)
            {
                long faissId = _nextId++;
                try
                {
                    _index.AddWithIds(new[] { embedding }, new[] { faissId });
                    _idToEmbeddingId[faissId] = embeddingId;
                    _logger?.LogDebug("Added embedding for EmbeddingId {EmbeddingId} with FaissId {FaissId}", embeddingId, faissId);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to add embedding for EmbeddingId {EmbeddingId}", embeddingId);
                    throw;
                }
            }
        }

        public void UpdateEmbedding(Guid embeddingId, float[] embedding)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to update embedding after disposal");
                throw new ObjectDisposedException(nameof(FaissNetSearch));
            }

            lock (_lock)
            {
                var existingId = _idToEmbeddingId.FirstOrDefault(kvp => kvp.Value == embeddingId).Key;
                if (_idToEmbeddingId.ContainsKey(existingId))
                {
                    try
                    {
                        _index.RemoveIds(new[] { existingId });
                        _idToEmbeddingId.Remove(existingId);
                        _logger?.LogDebug("Removed old embedding for EmbeddingId {EmbeddingId} with FaissId {FaissId}", embeddingId, existingId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to remove old embedding for EmbeddingId {EmbeddingId}", embeddingId);
                        throw;
                    }
                }

                AddEmbedding(embeddingId, embedding);
            }
        }

        public void RemoveEmbedding(Guid embeddingId)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to remove embedding after disposal");
                throw new ObjectDisposedException(nameof(FaissNetSearch));
            }

            lock (_lock)
            {
                var id = _idToEmbeddingId.FirstOrDefault(kvp => kvp.Value == embeddingId).Key;
                if (_idToEmbeddingId.ContainsKey(id))
                {
                    try
                    {
                        _index.RemoveIds(new[] { id });
                        _idToEmbeddingId.Remove(id);
                        _logger?.LogDebug("Removed embedding for EmbeddingId {EmbeddingId} with FaissId {FaissId}", embeddingId, id);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to remove embedding for EmbeddingId {EmbeddingId}", embeddingId);
                        throw;
                    }
                }
            }
        }

        public Guid[] Search(float[] queryEmbedding, int k = 1)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to search after disposal");
                throw new ObjectDisposedException(nameof(FaissNetSearch));
            }

            if (queryEmbedding == null || queryEmbedding.Length != _dimension)
            {
                _logger?.LogError("Invalid query embedding: Length {Length}, Expected {Dimension}", queryEmbedding?.Length ?? 0, _dimension);
                throw new ArgumentException($"Query embedding dimension must be {_dimension}.", nameof(queryEmbedding));
            }

            lock (_lock)
            {
                if (_idToEmbeddingId.Count == 0)
                {
                    _logger?.LogWarning("Search attempted on empty index, returning empty results");
                    return Array.Empty<Guid>();
                }

                try
                {
                    var (distances, ids) = _index.SearchFlat(queryEmbedding.Length, queryEmbedding, k);
                    var results = new List<Guid>();

                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (_idToEmbeddingId.TryGetValue(ids[i], out var entityId))
                        {
                            results.Add(entityId);
                        }
                    }

                    _logger?.LogDebug("Search returned {Count} results for topK={TopK}", results.Count, k);
                    return results.ToArray();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to perform search with topK={TopK}", k);
                    throw;
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_lock)
                {
                    try
                    {
                        _index?.Dispose();
                        _idToEmbeddingId.Clear();
                        _logger?.LogDebug("Disposed FaissNetSearch");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error during disposal");
                    }
                    _disposed = true;
                }
            }
        }
    }
}