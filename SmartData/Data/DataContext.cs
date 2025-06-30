using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartData.Core;
using SmartData.Models;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Linq.Expressions;

namespace SmartData.Data
{
    public abstract class CustomDataContext : DbContext
    {
        protected CustomDataContext(DbContextOptions options) : base(options) { }
        public virtual void OnModelCreating(ModelBuilder modelBuilder) { }
    }

    public class DataContext : DbContext
    {
        private readonly ConcurrentDictionary<Type, string> _entityTypes = new();
        private readonly DataOptions _options;
        private readonly CustomDataContext _customDataContext;
        private readonly ILogger<DataContext> _logger;

        public DataContext(DbContextOptions options, DataOptions dataOptions, CustomDataContext customDataContext = null, ILogger<DataContext> logger = null)
            : base(options)
        {
            _options = dataOptions ?? throw new ArgumentNullException(nameof(dataOptions));
            _customDataContext = customDataContext;
            _logger = logger;
            _logger?.LogInformation("DataContext instantiated with options: Embeddings={0}, Timeseries={1}, ChangeTracking={2}, IntegrityVerification={3}, Calculations={4}",
                _options.EnableEmbeddings, _options.EnableTimeseries, _options.EnableChangeTracking,
                _options.EnableIntegrityVerification, _options.EnableCalculations);
        }

        public DbSet<ChangeLogRecord> ChangeLogRecords { get; set; }
        public DbSet<EmbeddingRecord> EmbeddingRecords { get; set; }
        public DbSet<TimeseriesBaseValue> TimeseriesBaseValues { get; set; }
        public DbSet<TimeseriesDelta> TimeseriesDeltas { get; set; }
        public DbSet<IntegrityLogRecord> IntegrityLogRecords { get; set; }
        public DbSet<SmartData.Models.Analytics> Analytics { get; set; }
        public DbSet<AnalyticsStep> AnalyticsSteps { get; set; }

        public void RegisterEntity(Type entityType, string tableName)
        {
            _entityTypes.TryAdd(entityType, tableName);
            _logger?.LogInformation("Registered entity {EntityType} with table name {TableName}", entityType.Name, tableName);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _logger?.LogInformation("Configuring model for DataContext...");
            modelBuilder.Entity<ChangeLogRecord>().ToTable("sysChangeLog");
            modelBuilder.Entity<EmbeddingRecord>().ToTable("sysEmbeddings");
            modelBuilder.Entity<TimeseriesBaseValue>().ToTable("sysTimeseriesBaseValues");
            modelBuilder.Entity<TimeseriesDelta>().ToTable("sysTimeseriesDeltas");
            modelBuilder.Entity<IntegrityLogRecord>().ToTable("sysIntegrityLog");
            modelBuilder.Entity<Models.Analytics>().ToTable("sysAnalytics");
            modelBuilder.Entity<AnalyticsStep>().ToTable("sysAnalyticsSteps");

            if (_options.EnableEmbeddings)
            {
                modelBuilder.Entity<EmbeddingRecord>(entity =>
                {
                    entity.ToTable("sysEmbeddings");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.Embedding).IsRequired()
                        .HasConversion(new ValueConverter<float[], byte[]>(
                            v => FloatArrayToBytes(v),
                            v => BytesToFloatArray(v)))
                        .Metadata.SetValueComparer(new ValueComparer<float[]>(
                            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                            c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())) : 0,
                            c => c != null ? (float[])c.Clone() : null));
                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.HasIndex(e => new { e.TableName, e.EntityId });
                });
                _logger?.LogInformation("Configured sysEmbeddings table.");
            }

            if (_options.EnableTimeseries)
            {
                modelBuilder.Entity<TimeseriesBaseValue>(entity =>
                {
                    entity.ToTable("sysTimeseriesBaseValues");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.Value).IsRequired();
                    entity.Property(e => e.Timestamp).IsRequired();
                    entity.HasIndex(e => new { e.TableName, e.EntityId, e.PropertyName, e.Timestamp });
                });

                modelBuilder.Entity<TimeseriesDelta>(entity =>
                {
                    entity.ToTable("sysTimeseriesDeltas");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.BaseValueId).IsRequired();
                    entity.Property(e => e.Deltas).IsRequired();
                    entity.Property(e => e.LastTimestamp).IsRequired();
                    entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();
                    entity.HasIndex(e => e.BaseValueId);
                    entity.HasOne<TimeseriesBaseValue>().WithMany()
                        .HasForeignKey(e => e.BaseValueId).OnDelete(DeleteBehavior.Cascade);
                });
                _logger?.LogInformation("Configured sysTimeseriesBaseValues and sysTimeseriesDeltas tables.");
            }

            if (_options.EnableChangeTracking)
            {
                modelBuilder.Entity<ChangeLogRecord>(entity =>
                {
                    entity.ToTable("sysChangeLog");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.ChangedBy).IsRequired().HasMaxLength(100);
                    entity.Property(e => e.ChangedAt).IsRequired();
                    entity.Property(e => e.OriginalValue).HasMaxLength(4000).IsRequired(false); // Allow null
                    entity.Property(e => e.NewValue).HasMaxLength(4000).IsRequired(false); // Allow null
                    entity.Property(e => e.ChangeType).IsRequired().HasMaxLength(50);
                    entity.HasIndex(e => new { e.TableName, e.EntityId, e.ChangedAt });
                });
                _logger?.LogInformation("Configured sysChangeLog table.");
            }

            if (_options.EnableIntegrityVerification)
            {
                modelBuilder.Entity<IntegrityLogRecord>(entity =>
                {
                    entity.ToTable("sysIntegrityLog");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.Hash).IsRequired().HasMaxLength(64);
                    entity.Property(e => e.PreviousHash).HasMaxLength(64);
                    entity.Property(e => e.Timestamp).IsRequired();
                    entity.HasIndex(e => new { e.TableName, e.EntityId, e.PropertyName, e.Timestamp });
                });
                _logger?.LogInformation("Configured sysIntegrityLog table.");
            }

            if (_options.EnableCalculations)
            {
                modelBuilder.Entity<Analytics>(entity =>
                {
                    entity.ToTable("sysAnalytics");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                    entity.Property(e => e.Value).HasMaxLength(4000);
                    entity.Property(e => e.Status).HasMaxLength(4000).IsRequired(false);
                    entity.HasIndex(e => e.Name).IsUnique();
                });

                modelBuilder.Entity<AnalyticsStep>(entity =>
                {
                    entity.ToTable("sysAnalyticsSteps");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.AnalyticsId).IsRequired();
                    entity.Property(e => e.Order).IsRequired();
                    entity.Property(e => e.Operation).IsRequired().HasMaxLength(50);
                    entity.Property(e => e.Expression).IsRequired().HasMaxLength(1000);
                    entity.Property(e => e.ResultVariable).HasMaxLength(100);
                    entity.Property(e => e.MaxLoop).IsRequired().HasDefaultValue(10);
                    entity.HasIndex(e => new { e.AnalyticsId, e.Order });
                    entity.HasOne<Analytics>().WithMany()
                        .HasForeignKey(e => e.AnalyticsId).OnDelete(DeleteBehavior.Cascade);
                });
                _logger?.LogInformation("Configured sysAnalytics and sysAnalyticsSteps tables.");
            }

            foreach (var (entityType, tableName) in _entityTypes)
            {
                var entityBuilder = modelBuilder.Entity(entityType).ToTable(tableName);
                var idProperty = entityType.GetProperties()
                    .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                         p.GetCustomAttribute<KeyAttribute>() != null)
                    ?? throw new InvalidOperationException($"Entity {entityType.Name} must have an Id property.");
                entityBuilder.Property(idProperty.Name).ValueGeneratedOnAdd();
                entityBuilder.HasKey(idProperty.Name);

                if (_options.EnableEmbeddings)
                {
                    var embeddableProperties = entityType.GetProperties()
                        .Where(p => p.GetCustomAttribute<EmbeddableAttribute>() != null);
                    foreach (var prop in embeddableProperties)
                        entityBuilder.Property(prop.Name).IsRequired(false);
                }

                var foreignKeyProperties = entityType.GetProperties()
                    .Where(p => p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && p.Name != "Id");
                foreach (var prop in foreignKeyProperties)
                    entityBuilder.Property(prop.Name).IsRequired();
                _logger?.LogInformation("Configured entity {EntityType} with table {TableName}", entityType.Name, tableName);
            }

            _customDataContext?.OnModelCreating(modelBuilder);

            base.OnModelCreating(modelBuilder);
            _logger?.LogInformation("Model configuration completed.");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _logger?.LogInformation("Entering OnConfiguring...");
            if (_options.DbOptions != null)
            {
                _logger?.LogInformation("Applying DbOptions to configure DbContext...");
                try
                {
                    _options.DbOptions(optionsBuilder);
                    _logger?.LogInformation("DbContext configuration completed with DbOptions.");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to configure DbContext with DbOptions.");
                    throw;
                }
            }
            else
            {
                _logger?.LogWarning("DbOptions not provided in DataOptions.");
            }
            _logger?.LogInformation("Exiting OnConfiguring...");
            base.OnConfiguring(optionsBuilder);
        }

        public async Task EnsureSchemaCreatedAsync()
        {
            _logger?.LogInformation("Ensuring schema creation for database...");
            try
            {
                await Database.OpenConnectionAsync();
                _logger?.LogInformation("Database connection opened.");
                await Database.EnsureCreatedAsync();
                _logger?.LogInformation("Schema created successfully using EnsureCreatedAsync.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to create schema using EnsureCreatedAsync.");
                throw;
            }
            finally
            {
                _logger?.LogInformation("Schema creation attempt completed.");
            }
        }

        public async Task<List<QueryResult>> ExecuteSqlQueryAsync(string sqlQuery, params object[] parameters)
        {
            var results = new List<QueryResult>();
            using var command = Database.GetDbConnection().CreateCommand();
            command.CommandText = sqlQuery;
            command.Parameters.AddRange(parameters);

            await Database.OpenConnectionAsync();
            try
            {
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader[i];
                    results.Add(new QueryResult(row));
                }
            }
            finally
            {
                await Database.CloseConnectionAsync();
            }
            return results;
        }

        public async Task<List<TimeseriesResult>> GetTimeseriesAsync(string tableName, string entityId, string propertyName, DateTime start, DateTime end)
        {
            if (!_options.EnableTimeseries)
            {
                _logger?.LogWarning("Timeseries feature is disabled.");
                return new List<TimeseriesResult>();
            }

            var baseValues = await Set<TimeseriesBaseValue>()
                .Where(b => b.TableName == tableName && b.EntityId == entityId && b.PropertyName == propertyName && b.Timestamp <= end)
                .OrderBy(b => b.Timestamp)
                .ToListAsync();

            var results = new List<TimeseriesResult>();
            foreach (var baseValue in baseValues)
            {
                var delta = await Set<TimeseriesDelta>()
                    .FirstOrDefaultAsync(d => d.BaseValueId == baseValue.Id);
                if (delta == null) continue;

                var deltas = delta.GetDeltas();
                int currentTime = 0;
                foreach (var d in deltas)
                {
                    currentTime += d;
                    var timestamp = baseValue.Timestamp.AddMilliseconds(currentTime);
                    if (timestamp >= start && timestamp <= end)
                        results.Add(new TimeseriesResult { Timestamp = timestamp, Value = baseValue.Value });
                }
            }
            return results.OrderBy(r => r.Timestamp).ToList();
        }

        public async Task<List<TimeseriesResult>> GetInterpolatedTimeseriesAsync(
            string tableName, string entityId, string propertyName, DateTime start, DateTime end,
            TimeSpan interval, InterpolationMethod method)
        {
            if (!_options.EnableTimeseries)
            {
                _logger?.LogWarning("Timeseries feature is disabled.");
                return new List<TimeseriesResult>();
            }

            var timeseries = await GetTimeseriesAsync(tableName, entityId, propertyName, start, end);
            var result = new List<TimeseriesResult>();

            if (!timeseries.Any()) return result;

            for (var currentTime = start; currentTime <= end; currentTime = currentTime.Add(interval))
            {
                if (method == InterpolationMethod.None)
                {
                    var exactMatch = timeseries.FirstOrDefault(t => t.Timestamp == currentTime);
                    if (exactMatch != null)
                        result.Add(new TimeseriesResult { Timestamp = currentTime, Value = exactMatch.Value });
                }
                else
                {
                    double? value = null;
                    var previous = timeseries.LastOrDefault(t => t.Timestamp <= currentTime);
                    var next = timeseries.FirstOrDefault(t => t.Timestamp >= currentTime);

                    if (previous == null && next == null) continue;

                    switch (method)
                    {
                        case InterpolationMethod.Linear:
                            if (previous != null && next != null)
                            {
                                if (double.TryParse(previous.Value, out var prevValue) && double.TryParse(next.Value, out var nextValue))
                                {
                                    var totalTime = (next.Timestamp - previous.Timestamp).TotalMilliseconds;
                                    if (totalTime > 0)
                                    {
                                        var elapsed = (currentTime - previous.Timestamp).TotalMilliseconds;
                                        var fraction = elapsed / totalTime;
                                        value = prevValue + (nextValue - prevValue) * fraction;
                                    }
                                }
                            }
                            else if (previous != null)
                            {
                                if (double.TryParse(previous.Value, out var prevValue))
                                    value = prevValue;
                            }
                            else if (next != null)
                            {
                                if (double.TryParse(next.Value, out var nextValue))
                                    value = nextValue;
                            }
                            break;
                        case InterpolationMethod.Nearest:
                            if (previous != null && next != null)
                            {
                                var prevDiff = Math.Abs((currentTime - previous.Timestamp).TotalMilliseconds);
                                var nextDiff = Math.Abs((currentTime - next.Timestamp).TotalMilliseconds);
                                value = prevDiff <= nextDiff ? double.Parse(previous.Value) : double.Parse(next.Value);
                            }
                            else if (previous != null)
                                value = double.Parse(previous.Value);
                            else if (next != null)
                                value = double.Parse(next.Value);
                            break;
                        case InterpolationMethod.Previous when previous != null:
                            value = double.Parse(previous.Value);
                            break;
                        case InterpolationMethod.Next when next != null:
                            value = double.Parse(next.Value);
                            break;
                    }

                    if (value.HasValue)
                        result.Add(new TimeseriesResult { Timestamp = currentTime, Value = value.Value.ToString("F2") });
                }
            }
            return result;
        }

        private static byte[] FloatArrayToBytes(float[] floats)
        {
            if (floats == null) return Array.Empty<byte>();
            using var stream = new MemoryStream(floats.Length * sizeof(float));
            using var writer = new BinaryWriter(stream);
            foreach (var f in floats) writer.Write(f);
            return stream.ToArray();
        }

        private static float[] BytesToFloatArray(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<float>();
            if (bytes.Length % sizeof(float) != 0)
                throw new ArgumentException("Byte array length must be a multiple of 4 for float array conversion.");
            var floats = new float[bytes.Length / sizeof(float)];
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < floats.Length; i++)
                floats[i] = reader.ReadSingle();
            return floats;
        }
    }
}