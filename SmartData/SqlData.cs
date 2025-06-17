using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Configurations;
using SmartData.GPT.Embedder;
using SmartData.GPT.Search;
using SmartData.Tables;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SmartData
{
    public class SqlData : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IEmbedder _embedder;
        private readonly ConcurrentDictionary<Type, ITableCollection> _tables = new();
        private bool _disposed;
        private readonly string _migrationsAssembly;
        private readonly bool _embeddingEnabled;
        private readonly bool _timeseriesEnabled;
        private readonly FaissNetSearch _faissIndex;
        private readonly ILogger<SqlData> _logger;

        public FaissNetSearch FaissIndex => _faissIndex;

        public SqlData(IServiceProvider serviceProvider, IEmbedder embedder, FaissNetSearch faissIndex, SqlDataContext context, ILogger<SqlData> logger = null, string migrationsAssembly = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _embedder = embedder;
            _faissIndex = faissIndex ?? throw new ArgumentNullException(nameof(faissIndex));
            _migrationsAssembly = migrationsAssembly;
            _logger = logger;
            var options = serviceProvider.GetRequiredService<SmartDataOptions>();
            _embeddingEnabled = options.EmbeddingEnabled;
            _timeseriesEnabled = options.TimeseriesEnabled;
            InitializeDatabaseAndFaissIndex().GetAwaiter().GetResult();
            context.ConfigureTables(this, serviceProvider, logger);
            _logger?.LogDebug("Initialized SqlData with FaissNetSearch instance {InstanceId}", _faissIndex.GetHashCode());
        
        }

        private async Task InitializeDatabaseAndFaissIndex()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            // Ensure schema is created if migrations are not used
            await dbContext.EnsureSchemaCreatedAsync();
            _logger?.LogDebug("Ensured database schema created");

            if (!_embeddingEnabled) return;

            try
            {
                var embeddings = await dbContext.Set<EmbeddingRecord>()
                    .Select(e => new { e.Id, e.Embedding })
                    .ToListAsync();

                if (embeddings.Any())
                {
                    foreach (var embedding in embeddings)
                    {
                        if (embedding.Id != Guid.Empty)
                        {
                            _faissIndex.AddEmbedding(embedding.Id, embedding.Embedding);
                            _logger?.LogDebug("Initialized FaissNetSearch with embedding for EmbeddingId {Entity.Id}", embedding.Id);
                        }
                    }
                }
                else
                {
                    _logger?.LogInformation("No embeddings found in sysEmbeddings, index remains empty");
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                _logger?.LogWarning(ex, "sysEmbeddings table not found during initialization. Assuming new database.");
                // Table may be newly created; no embeddings to load
            }
        }

        internal ITableCollection<T> RegisterTable<T>(string tableName) where T : class
        {
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Table name cannot be empty.", nameof(tableName));
            if (!Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));

            return (ITableCollection<T>)_tables.GetOrAdd(typeof(T), _ =>
            {
                var dbContext = _serviceProvider.GetRequiredService<SmartDataContext>();
                dbContext.RegisterEntity(typeof(T), tableName);
                _logger?.LogDebug("Registering table {TableName} with FaissNetSearch instance {InstanceId}", tableName, _faissIndex.GetHashCode());
                return new TableCollection<T>(_serviceProvider, tableName, _embeddingEnabled, _timeseriesEnabled, _embedder, _faissIndex, _logger);
            });
        }

        public async Task ExecuteSqlCommandAsync(string sql, params object[] parameters)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            await dbContext.Database.ExecuteSqlRawAsync(sql, parameters);
        }

        public async Task MigrateAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            // NEW: Only apply migrations if assembly is specified
            if (!string.IsNullOrEmpty(_migrationsAssembly))
            {
                var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();
                var allMigrations = dbContext.Database.GetMigrations();

                if (appliedMigrations.Count() >= allMigrations.Count())
                {
                    _logger?.LogInformation("No migrations to apply.");
                    return;
                }

                await dbContext.Database.MigrateAsync();
                _logger?.LogInformation("Applied migrations successfully.");
            }
            else
            {
                await dbContext.EnsureSchemaCreatedAsync();
                _logger?.LogInformation("Ensured schema created without migrations.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _faissIndex?.Dispose();
            _tables.Clear();
            _disposed = true;
            _logger?.LogDebug("Disposed SqlData");
        }
    }
}