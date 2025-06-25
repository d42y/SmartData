using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Extensions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SmartData.Configurations
{
    public abstract class SqlDataContext : DbContext
    {
        private readonly IServiceProvider _serviceProvider;

        protected SqlDataContext(DbContextOptions options, IServiceProvider serviceProvider)
            : base(options)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        internal void ConfigureTables(SqlData manager, IServiceProvider serviceProvider, ILogger logger = null)
        {
            if (serviceProvider == null)
                throw new ArgumentNullException(nameof(serviceProvider));
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));

            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(SdSet<>));

            foreach (var prop in properties)
            {
                var entityType = prop.PropertyType.GetGenericArguments()[0];
                var tableName = prop.Name;

                var registerMethod = typeof(SqlData)
                    .GetMethod(nameof(SqlData.RegisterTable), BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.MakeGenericMethod(entityType)
                    ?? throw new InvalidOperationException($"Method {nameof(SqlData.RegisterTable)} not found on {nameof(SqlData)}.");
                var table = registerMethod.Invoke(manager, new object[] { tableName });

                var dataSetType = typeof(SdSet<>).MakeGenericType(entityType);
                var faissIndex = manager.FaissIndex;
                try
                {
                    var dataSet = Activator.CreateInstance(dataSetType, manager, serviceProvider, faissIndex, tableName)
                        ?? throw new InvalidOperationException($"Failed to create SdSet<{entityType.Name}> instance.");
                    prop.SetValue(this, dataSet);
                    logger?.LogDebug("Created SdSet<{EntityType}> for table {TableName} with FaissNetSearch instance {FaissInstanceId}", entityType.Name, tableName, faissIndex?.GetHashCode() ?? 0);
                }
                catch (MissingMethodException ex)
                {
                    logger?.LogError(ex, "Failed to find constructor for SdSet<{EntityType}> with parameters SqlData, IServiceProvider, FaissNetSearch, string for table {TableName}", entityType.Name, tableName);
                    throw;
                }
            }
        }

        protected internal virtual void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Override in derived classes (e.g., AppDbContext) to configure relationships
        }

        // EF Core-like methods
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            return dbContext.SaveChangesAsync(cancellationToken);
        }

        public async Task<int> ExecuteSqlRawAsync(string sql, params object[] parameters)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            return await dbContext.Database.ExecuteSqlRawAsync(sql, parameters);
        }

        public async Task<List<QueryResult>> ExecuteSqlQueryAsync(string sqlQuery, params object[] parameters)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            return await dbContext.ExecuteSqlQueryAsync(sqlQuery, parameters);
        }
    }
}