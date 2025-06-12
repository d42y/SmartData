using Microsoft.EntityFrameworkCore;
using SmartData.Attributes;
using SmartData.Tables;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace SmartData.Configurations
{
    public class DynamicDbContext : DbContext
    {
        private readonly ConcurrentDictionary<Type, string> _entityTypes = new();
        private readonly string _migrationsAssembly;

        public DynamicDbContext(DbContextOptions<DynamicDbContext> options, string migrationsAssembly = null)
            : base(options)
        {
            _migrationsAssembly = migrationsAssembly;
        }

        public void RegisterEntity(Type entityType, string tableName)
        {
            _entityTypes.TryAdd(entityType, tableName);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EmbeddingRecord>(entity =>
            {
                entity.ToTable("Embeddings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EntityId).IsRequired();
                entity.Property(e => e.Embedding).IsRequired();
                entity.Property(e => e.TableName).IsRequired().HasMaxLength(255);
                entity.HasIndex(e => new { e.TableName, e.EntityId });
            });

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

                var embeddableProperties = entityType.GetProperties()
                    .Where(p => p.GetCustomAttribute<EmbeddableAttribute>() != null);
                foreach (var prop in embeddableProperties)
                {
                    entityBuilder.Property(prop.Name).IsRequired(false);
                }
            }

            base.OnModelCreating(modelBuilder);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!string.IsNullOrEmpty(_migrationsAssembly))
            {
                optionsBuilder.UseSqlServer(options => options.MigrationsAssembly(_migrationsAssembly));
            }
            base.OnConfiguring(optionsBuilder);
        }
    }
}
