using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartData.Attributes;
using SmartData.Extensions;
using SmartData.Tables;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Tasks;

namespace SmartData.Configurations
{
    public class SmartDataContext : DbContext
    {
        private readonly ConcurrentDictionary<Type, string> _entityTypes = new();
        private readonly string _migrationsAssembly;
        private readonly SqlDataContext _sqlDataContext;
        private readonly SmartDataOptions _options;

        public SmartDataContext(DbContextOptions<SmartDataContext> options, SmartDataOptions smartDataOptions, string migrationsAssembly = null, SqlDataContext sqlDataContext = null)
            : base(options)
        {
            _migrationsAssembly = migrationsAssembly;
            _sqlDataContext = sqlDataContext;
            _options = smartDataOptions ?? throw new ArgumentNullException(nameof(smartDataOptions));
        }

        public void RegisterEntity(Type entityType, string tableName)
        {
            _entityTypes.TryAdd(entityType, tableName);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (_options.EmbeddingEnabled)
            {
                modelBuilder.Entity<EmbeddingRecord>(entity =>
                {
                    entity.ToTable("sysEmbeddings");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);

                    var embeddingConverter = new ValueConverter<float[], byte[]>(
                        v => FloatArrayToBytes(v),
                        v => BytesToFloatArray(v));

                    var embeddingComparer = new ValueComparer<float[]>(
                        (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                        c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())) : 0,
                        c => c != null ? (float[])c.Clone() : null);

                    entity.Property(e => e.Embedding)
                        .IsRequired()
                        .HasConversion(embeddingConverter)
                        .Metadata.SetValueComparer(embeddingComparer);

                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.HasIndex(e => new { e.TableName, e.EntityId });
                });
            }

            if (_options.TimeseriesEnabled)
            {
                modelBuilder.Entity<TsBaseValue<string>>(entity =>
                {
                    entity.ToTable("sysTsBaseValues");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                    entity.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
                    entity.Property(e => e.Value).IsRequired();
                    entity.Property(e => e.StartTime).IsRequired();
                    entity.HasIndex(e => new { e.TableName, e.EntityId, e.PropertyName, e.StartTime });
                });

                modelBuilder.Entity<TsDeltaT>(entity =>
                {
                    entity.ToTable("sysTsDeltaT");
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.BaseValueId).IsRequired();
                    entity.Property(e => e.CompressedDeltas).IsRequired();
                    entity.Property(e => e.LastTimestamp).IsRequired();
                    entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();
                    entity.HasIndex(e => e.BaseValueId);
                    entity.HasOne<TsBaseValue<string>>()
                        .WithMany()
                        .HasForeignKey(e => e.BaseValueId)
                        .OnDelete(DeleteBehavior.Cascade);
                });
            }

            foreach (var (entityType, tableName) in _entityTypes)
            {
                var entityBuilder = modelBuilder.Entity(entityType);
                entityBuilder.ToTable(tableName);

                var idProperty = entityType.GetProperties()
                    .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                         p.GetCustomAttribute<KeyAttribute>() != null);
                if (idProperty == null)
                    throw new InvalidOperationException($"Entity {entityType.Name} must have an Id property.");

                entityBuilder.Property(idProperty.Name).ValueGeneratedOnAdd();
                entityBuilder.HasKey(idProperty.Name);

                if (_options.EmbeddingEnabled)
                {
                    var embeddableProperties = entityType.GetProperties()
                        .Where(p => p.GetCustomAttribute<EmbeddableAttribute>() != null);
                    foreach (var prop in embeddableProperties)
                    {
                        entityBuilder.Property(prop.Name).IsRequired(false);
                    }
                }

                var foreignKeyProperties = entityType.GetProperties()
                    .Where(p => p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && p.Name != "Id");
                foreach (var prop in foreignKeyProperties)
                {
                    entityBuilder.Property(prop.Name).IsRequired();
                }
            }

            _sqlDataContext?.OnModelCreating(modelBuilder);

            base.OnModelCreating(modelBuilder);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured && !string.IsNullOrEmpty(_migrationsAssembly))
            {
                // No provider specified; rely on DI configuration
            }
            base.OnConfiguring(optionsBuilder);
        }

        // NEW: Ensure database schema is created automatically
        public async Task EnsureSchemaCreatedAsync()
        {
            if (_options.EmbeddingEnabled || _options.TimeseriesEnabled || _entityTypes.Any())
            {
                await Database.EnsureCreatedAsync();
            }
        }

        private static byte[] FloatArrayToBytes(float[] floats)
        {
            if (floats == null) return Array.Empty<byte>();
            using var stream = new MemoryStream(floats.Length * sizeof(float));
            using var writer = new BinaryWriter(stream);
            foreach (var f in floats)
            {
                writer.Write(f);
            }
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
            {
                floats[i] = reader.ReadSingle();
            }
            return floats;
        }
    }
}