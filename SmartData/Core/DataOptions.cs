using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace SmartData.Core
{
    public class DataOptions
    {
        public string ConnectionString { get; private set; }
        public string MigrationsAssembly { get; private set; }
        public ILoggerFactory LoggerFactory { get; private set; }
        public bool EnableEmbeddings { get; private set; }
        public bool EnableTimeseries { get; private set; }
        public bool EnableChangeTracking { get; private set; }
        public bool EnableIntegrityVerification { get; private set; }
        public bool EnableCalculations { get; private set; }
        public Action<DbContextOptionsBuilder> DbOptions { get; private set; }

        public DataOptions WithConnectionString(string connectionString)
        {
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return this;
        }

        public DataOptions WithMigrations(string assembly = null)
        {
            MigrationsAssembly = assembly ?? Assembly.GetCallingAssembly().GetName().Name;
            return this;
        }

        public DataOptions WithLogging(ILoggerFactory loggerFactory)
        {
            LoggerFactory = loggerFactory;
            return this;
        }

        public DataOptions WithDbOptions(Action<DbContextOptionsBuilder> options)
        {
            DbOptions = options;
            return this;
        }

        public DataOptions WithEmbeddings()
        {
            EnableEmbeddings = true;
            return this;
        }

        public DataOptions WithTimeseries()
        {
            EnableTimeseries = true;
            return this;
        }

        public DataOptions WithChangeTracking()
        {
            EnableChangeTracking = true;
            return this;
        }

        public DataOptions WithIntegrityVerification()
        {
            EnableIntegrityVerification = true;
            return this;
        }

        public DataOptions WithCalculations()
        {
            EnableCalculations = true;
            return this;
        }
    }
}