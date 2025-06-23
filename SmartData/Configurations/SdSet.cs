using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartData.GPT.Embedder;
using SmartData.GPT.Search;
using SmartData.Tables;
using SmartData.Tables.Models;
using System.Linq.Expressions;

namespace SmartData.Configurations
{
    public class SdSet<T> : IDisposable where T : class
    {
        private readonly SqlData _sqlData;
        private readonly ITableCollection<T> _table;
        private readonly IServiceProvider _serviceProvider;
        private readonly FaissNetSearch _faissIndex; // NEW: Store FaissNetSearch instance

        public SdSet(SqlData sqlData, IServiceProvider serviceProvider, FaissNetSearch faissIndex, string tableName)
        {
            _sqlData = sqlData ?? throw new ArgumentNullException(nameof(sqlData));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _faissIndex = faissIndex ?? throw new ArgumentNullException(nameof(faissIndex));
            _table = sqlData.RegisterTable<T>(tableName);
        }

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

            // Query sysEmbeddings for TableName and EntityId
            var embeddingRecords = await dbContext.Set<EmbeddingRecord>()
                .Where(e => embeddingIds.Contains(e.Id))
                .Select(e => new { e.Id, e.TableName, e.EntityId })
                .ToListAsync();

            foreach (var embeddingRecord in embeddingRecords)
            {
                try
                {
                    // Dynamically query the target table using TableName and EntityId
                    var tableType = typeof(T); // Assume T matches TableName (e.g., Sensor for "Sensors")
                    var entity = await _table.FindByIdAsync(embeddingRecord.EntityId);
                    //var entitySet = dbContext.Set(tableType);
                    //var entity = await entitySet
                    //    .FindAsync(embeddingRecord.EntityId);

                    if (entity != null)
                    {
                        // Create QueryResult with record data
                        var data = new Dictionary<string, object>();
                        foreach (var prop in tableType.GetProperties())
                        {
                            data[prop.Name] = prop.GetValue(entity);
                        }
                        data["Score"] = 1.0f; // Placeholder score
                        results.Add(new QueryResult(data));
                    }
                }
                catch (Exception ex)
                {
                    // Log error and continue with other records
                    Console.WriteLine($"Failed to retrieve record for EmbeddingId {embeddingRecord.Id}: {ex.Message}");
                }
            }

            return results;
        }

        public void Dispose() => _sqlData.Dispose();
    }
}