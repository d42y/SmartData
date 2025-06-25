using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Tables;
using SmartData.Tables.Models;
using SmartData.Vectorizer.Embedder;
using SmartData.Vectorizer.Search;
using System.Linq.Expressions;

namespace SmartData.Configurations
{
    public class SdSet<T> : DbSet<T>, IQueryable<T>, IDisposable where T : class
    {
        private readonly SqlData _sqlData;
        private readonly ITableCollection<T> _table;
        private readonly IServiceProvider _serviceProvider;
        private readonly FaissNetSearch _faissIndex;
        private readonly IQueryable<T> _queryable;

        public SdSet(SqlData sqlData, IServiceProvider serviceProvider, FaissNetSearch faissIndex, string tableName)
        {
            _sqlData = sqlData ?? throw new ArgumentNullException(nameof(sqlData));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _faissIndex = faissIndex ?? throw new ArgumentNullException(nameof(faissIndex));
            _table = sqlData.RegisterTable<T>(tableName);

            // Initialize queryable using SmartDataContext's DbSet
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            _queryable = dbContext.Set<T>();
        }

        // IQueryable implementation
        public Type ElementType => _queryable.ElementType;
        public Expression Expression => _queryable.Expression;
        public IQueryProvider Provider => _queryable.Provider;

        // IEntityType implementation
        public override IEntityType EntityType
        {
            get
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                return dbContext.Model.FindEntityType(typeof(T))
                    ?? throw new InvalidOperationException($"Entity type {typeof(T).Name} is not found in the model.");
            }
        }

        // DbSet-like methods
        public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
            => await _table.InsertAsync(entity);

        public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
            => await _table.InsertAsync(entities);

        public void Remove(T entity)
        {
            var id = typeof(T).GetProperty("Id")?.GetValue(entity)
                ?? throw new InvalidOperationException("Entity must have an Id property.");
            _table.DeleteAsync(id).GetAwaiter().GetResult();
        }

        public void RemoveRange(IEnumerable<T> entities)
        {
            foreach (var entity in entities)
            {
                Remove(entity);
            }
        }

        public async Task<T> FindAsync(params object[] keyValues)
            => await _table.FindByIdAsync(keyValues[0]);

        public async Task<T> FindAsync(object[] keyValues, CancellationToken cancellationToken)
            => await _table.FindByIdAsync(keyValues[0]);

        // Existing SdSet methods
        public async Task<T> InsertAsync(T entity) => await _table.InsertAsync(entity);
        public async Task InsertAsync(IEnumerable<T> entities) => await _table.InsertAsync(entities);
        public async Task<bool> UpdateAsync(T entity) => await _table.UpdateAsync(entity);
        public async Task<bool> ExistsAsync(object id) => await _table.ExistsAsync(id);
        public async Task<T> UpsertAsync(T entity) => await _table.UpsertAsync(entity);
        public async Task<bool> DeleteAsync(object id) => await _table.DeleteAsync(id);
        public async Task<int> DeleteAllAsync() => await _table.DeleteAllAsync();
        public async Task<T> FindByIdAsync(object id) => await _table.FindByIdAsync(id);
        public async Task<List<T>> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue)
            => await _table.FindAsync(predicate, skip, limit);
        public async Task<List<T>> FindAllAsync() => await _table.FindAllAsync();
        public async Task<long> CountAsync(Expression<Func<T, bool>> predicate = null)
            => await _table.CountAsync(predicate);
        public async Task<List<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters)
            => await _table.ExecuteSqlQueryAsync(sql, parameters);
        public async Task<List<TimeseriesResult>> GetTimeseriesAsync(string entityId, string propertyName, DateTime startTime, DateTime endTime)
            => await _table.GetTimeseriesAsync(entityId, propertyName, startTime, endTime);
        public async Task<List<TimeseriesResult>> GetInterpolatedTimeseriesAsync(string entityId, string propertyName, DateTime startTime, DateTime endTime, TimeSpan interval, InterpolationMethod method)
            => await _table.GetInterpolatedTimeseriesAsync(entityId, propertyName, startTime, endTime, interval, method);

        public async Task<List<QueryResult>> SearchEmbeddings(string query, int topK = 1)
        {
            if (string.IsNullOrEmpty(query))
                throw new ArgumentException("Query cannot be empty.", nameof(query));
            if (topK < 1)
                throw new ArgumentException("TopK must be at least 1.", nameof(topK));

            using var scope = _serviceProvider.CreateScope();
            var embedder = scope.ServiceProvider.GetService<IEmbedder>();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            if (embedder == null)
            {
                return new List<QueryResult>();
            }

            var queryEmbedding = embedder.GenerateEmbedding(query).ToArray();
            var embeddingIds = _faissIndex.Search(queryEmbedding, topK);

            var results = new List<QueryResult>();
            if (!embeddingIds.Any())
            {
                return results;
            }

            var embeddingRecords = await dbContext.Set<EmbeddingRecord>()
                .Where(e => embeddingIds.Contains(e.Id))
                .Select(e => new { e.Id, e.TableName, e.EntityId })
                .ToListAsync();

            foreach (var embeddingRecord in embeddingRecords)
            {
                try
                {
                    var entity = await _table.FindByIdAsync(embeddingRecord.EntityId);
                    if (entity != null)
                    {
                        var data = new Dictionary<string, object>();
                        foreach (var prop in typeof(T).GetProperties())
                        {
                            data[prop.Name] = prop.GetValue(entity);
                        }
                        data["Score"] = 1.0f; // Placeholder score
                        results.Add(new QueryResult(data));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to retrieve record for EmbeddingId {embeddingRecord.Id}: {ex.Message}");
                }
            }

            return results;
        }

        public void Dispose() => _sqlData.Dispose();
    }
}