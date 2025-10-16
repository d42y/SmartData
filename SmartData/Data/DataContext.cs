using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using SmartData.Core;
using SmartData.Models;

namespace SmartData.Data
{
    public abstract class CustomDataContext : DbContext
    {
        protected CustomDataContext(DbContextOptions options) : base(options) { }
        public virtual void OnModelCreating(ModelBuilder modelBuilder) { }
    }

    public partial class DataContext : DbContext
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
                _options.EnableIntegrityVerification, _options.EnableAnalytics);
        }

        public DbSet<ChangeLogRecord> ChangeLogRecords { get; set; }
        public DbSet<EmbeddingRecord> EmbeddingRecords { get; set; }
        public DbSet<TimeseriesBaseValue> TimeseriesBaseValues { get; set; }
        public DbSet<TimeseriesDelta> TimeseriesDeltas { get; set; }
        public DbSet<IntegrityLogRecord> IntegrityLogRecords { get; set; }
        public DbSet<SmartData.Models.Analytics> Analytics { get; set; }
        public DbSet<AnalyticsStep> AnalyticsSteps { get; set; }
        public DbSet<AnalyticsBinding> AnalyticsBindings { get; set; }
        public DbSet<AnalyticsRun> AnalyticsRuns { get; set; }
        public DbSet<AnalyticsTrigger> AnalyticsTriggers { get; set; }

        public void RegisterEntity(Type entityType, string tableName)
        {
            _entityTypes.TryAdd(entityType, tableName);
            _logger?.LogInformation("Registered entity {EntityType} with table name {TableName}", entityType.Name, tableName);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _logger?.LogInformation("Configuring model for DataContext...");
            modelBuilder.Entity<ChangeLogRecord>().ToTable("__sysChangeLog");
            modelBuilder.Entity<EmbeddingRecord>().ToTable("__sysEmbeddings");
            modelBuilder.Entity<TimeseriesBaseValue>().ToTable("__sysTimeseriesBaseValues");
            modelBuilder.Entity<TimeseriesDelta>().ToTable("__sysTimeseriesDeltas");
            modelBuilder.Entity<IntegrityLogRecord>().ToTable("__sysIntegrityLog");
            modelBuilder.Entity<Models.Analytics>().ToTable("__sysAnalytics");
            modelBuilder.Entity<AnalyticsStep>().ToTable("__sysAnalyticsSteps");
            modelBuilder.Entity<AnalyticsBinding>().ToTable("__sysAnalyticsBindings");
            modelBuilder.Entity<AnalyticsRun>().ToTable("__sysAnalyticsRuns");
            modelBuilder.Entity<AnalyticsTrigger>().ToTable("__sysAnalyticsTriggers");

            if (_options.EnableEmbeddings)
            {
                modelBuilder.Entity<EmbeddingRecord>(entity =>
                {
                    entity.ToTable("__sysEmbeddings");
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
                    entity.ToTable("__sysTimeseriesBaseValues");
                    entity.HasKey(e => e.Id);

                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.Value).IsRequired();      // NVARCHAR(MAX) is fine
                    entity.Property(e => e.Timestamp).IsRequired();  // anchor for this base

                    // Optional, but useful for reads by time
                    entity.HasIndex(e => new { e.TableName, e.EntityId, e.PropertyName, e.Timestamp })
                          .HasDatabaseName("IX_TimeseriesBaseValue_Time");
                    // NOTE: No ValueHash, no unique index — multiple bases per value are allowed by design.
                });

                modelBuilder.Entity<TimeseriesDelta>(entity =>
                {
                    entity.ToTable("__sysTimeseriesDeltas");
                    entity.HasKey(e => e.Id);

                    entity.Property(e => e.BaseValueId).IsRequired();
                    entity.Property(e => e.Deltas).IsRequired().HasColumnType("varbinary(max)");
                    entity.Property(e => e.LastTimestamp).IsRequired(); // int seconds from base
                    entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();

                    entity.HasIndex(e => e.BaseValueId);
                    entity.HasOne<TimeseriesBaseValue>()
                          .WithMany()
                          .HasForeignKey(e => e.BaseValueId)
                          .OnDelete(DeleteBehavior.Cascade);
                });
            }




            if (_options.EnableChangeTracking)
            {
                modelBuilder.Entity<ChangeLogRecord>(entity =>
                {
                    entity.ToTable("__sysChangeLog");
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
                    entity.ToTable("__sysIntegrityLog");
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

            if (_options.EnableAnalytics)
            {
                modelBuilder.Entity<Analytics>(entity =>
                {
                    entity.ToTable("__sysAnalytics");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.TenantId).IsRequired();                 
                    entity.Property(e => e.Enabled).IsRequired().HasDefaultValue(true); 
                    entity.Property(e => e.OutputMode).IsRequired().HasMaxLength(16).HasDefaultValue("Memory"); // NEW
                    entity.Property(e => e.OutputTable).HasMaxLength(128);         
                    entity.Property(e => e.NextRun).IsRequired(false);            

                    entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                    entity.Property(e => e.Value).HasMaxLength(4000);
                    entity.Property(e => e.Status).HasMaxLength(4000).IsRequired(false);
                    entity.HasIndex(e => new { e.TenantId, e.Name }).IsUnique(); // tenant-scoped uniqueness
                });

                modelBuilder.Entity<AnalyticsStep>(entity =>
                {
                    entity.ToTable("__sysAnalyticsSteps");
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

                modelBuilder.Entity<AnalyticsBinding>(entity =>
                {
                    entity.ToTable("__sysAnalyticsBindings");
                    entity.HasKey(e => new { e.TenantId, e.NodeId, e.AnalyticsId });
                    entity.Property(e => e.Enabled).IsRequired().HasDefaultValue(true);
                    entity.HasIndex(e => new { e.TenantId, e.AnalyticsId });
                });

                modelBuilder.Entity<AnalyticsRun>(entity =>
                {
                    entity.ToTable("__sysAnalyticsRuns");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.TenantId).IsRequired();
                    entity.Property(e => e.AnalyticsId).IsRequired();
                    entity.Property(e => e.Status).IsRequired().HasMaxLength(32);
                    entity.Property(e => e.Error).HasMaxLength(2000);
                    entity.Property(e => e.StartedAt).IsRequired();
                    entity.Property(e => e.FinishedAt);
                    entity.HasIndex(e => new { e.TenantId, e.AnalyticsId, e.StartedAt });
                });

                modelBuilder.Entity<AnalyticsTrigger>(entity =>
                {
                    entity.ToTable("__sysAnalyticsTriggers");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.AnalyticsId).IsRequired();
                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.EntityId).HasMaxLength(128);
                    entity.Property(e => e.ChangeType).HasMaxLength(50);

                    entity.HasIndex(e => e.AnalyticsId);
                    entity.HasIndex(e => new { e.TableName, e.PropertyName });
                    entity.HasOne<Analytics>().WithMany()
                          .HasForeignKey(e => e.AnalyticsId)
                          .OnDelete(DeleteBehavior.Cascade);
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

        
    }
}