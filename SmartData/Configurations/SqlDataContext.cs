using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace SmartData.Configurations
{
    public abstract class SqlDataContext
    {
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

                // Use reflection to call generic RegisterTable<T>
                var registerMethod = typeof(SqlData)
                    .GetMethod(nameof(SqlData.RegisterTable), BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.MakeGenericMethod(entityType)
                    ?? throw new InvalidOperationException($"Method {nameof(SqlData.RegisterTable)} not found on {nameof(SqlData)}.");
                var table = registerMethod.Invoke(manager, new object[] { tableName });

                // Create DataSet<T> with correct constructor parameters
                var dataSetType = typeof(SdSet<>).MakeGenericType(entityType);
                var faissIndex = manager.FaissIndex; // Get FaissNetSearch from SqlData
                try
                {
                    var dataSet = Activator.CreateInstance(dataSetType, manager, serviceProvider, faissIndex, tableName)
                        ?? throw new InvalidOperationException($"Failed to create DataSet<{entityType.Name}> instance.");
                    prop.SetValue(this, dataSet);
                    logger?.LogDebug("Created DataSet<{EntityType}> for table {TableName} with FaissNetSearch instance {FaissInstanceId}", entityType.Name, tableName, faissIndex?.GetHashCode() ?? 0);
                }
                catch (MissingMethodException ex)
                {
                    logger?.LogError(ex, "Failed to find constructor for DataSet<{EntityType}> with parameters SqlData, IServiceProvider, FaissNetSearch, string for table {TableName}", entityType.Name, tableName);
                    throw;
                }
            }
        }

        protected internal virtual void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Override in derived classes (e.g., AppDbContext) to configure relationships
        }
    }
}