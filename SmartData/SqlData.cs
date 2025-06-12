using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Configurations;
using SmartData.Tables;
using System.Collections.Concurrent;

namespace SmartData
{
    public class SqlData : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<Type, ITableCollection> _tables = new();
        private bool _disposed;

        public SqlData(IServiceProvider serviceProvider, SqlDataContext context)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            context.ConfigureTables(this);
        }

        internal ITableCollection<T> RegisterTable<T>(string tableName) where T : class
        {
            return (ITableCollection<T>)_tables.GetOrAdd(typeof(T), _ =>
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
                dbContext.RegisterEntity(typeof(T), tableName);
                dbContext.Database.EnsureCreated();
                return new TableCollection<T>(dbContext, tableName);
            });
        }


        public async Task ExecuteSqlCommandAsync(string sql, params object[] parameters)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(sql, parameters);
        }

        public async Task MigrateAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
