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
        public bool EmbeddingEnabled { get; private set; }
        public bool TimeseriesEnabled { get; private set; }
        public bool ChangeTrackingEnabled { get; private set; } 
        public bool IntegrityVerificationEnabled { get; private set; }
        public bool SmartCalcEnabled { get; private set; }

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

        public SqlDataBuilder EnableEmbedding()
        {
            EmbeddingEnabled = true;
            return this;
        }

        public SqlDataBuilder EnableTimeseries()
        {
            TimeseriesEnabled = true;
            return this;
        }

        public SqlDataBuilder EnableChangeTracking()
        {
            ChangeTrackingEnabled = true;
            return this;
        }

        public SqlDataBuilder EnableIntegrityVerification()
        {
            IntegrityVerificationEnabled = true;
            return this;
        }

        public SqlDataBuilder EnableSmartCalc()
        {
            SmartCalcEnabled = true;
            return this;
        }

    }
}