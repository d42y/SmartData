using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SmartData.Configurations
{
    public class SqlDataBuilder
    {
        public string ConnectionString { get; set; }
        public string MigrationsAssembly { get; set; }
        public Action<DbContextOptionsBuilder> OptionsBuilder { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
        public bool EmbeddingEnabled { get; private set; } // Embedding toggle
        public bool TimeseriesEnabled { get; private set; } // Timeseries toggle

        public SqlDataBuilder WithConnectionString(string connectionString)
        {
            ConnectionString = connectionString;
            return this;
        }

        public SqlDataBuilder WithMigrationsAssembly(string migrationsAssembly)
        {
            MigrationsAssembly = migrationsAssembly;
            return this;
        }

        public SqlDataBuilder WithOptions(Action<DbContextOptionsBuilder> optionsBuilder)
        {
            OptionsBuilder = optionsBuilder;
            return this;
        }

        public SqlDataBuilder WithLogging(ILoggerFactory loggerFactory)
        {
            LoggerFactory = loggerFactory;
            return this;
        }

        // Enable embedding feature
        public SqlDataBuilder EnableEmbedding()
        {
            EmbeddingEnabled = true;
            return this;
        }

        // Enable timeseries feature
        public SqlDataBuilder EnableTimeseries()
        {
            TimeseriesEnabled = true;
            return this;
        }
    }
}