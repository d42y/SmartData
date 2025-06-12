using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Configurations
{
    public class SqlDataBuilder
    {
        public string ConnectionString { get; set; }
        public string MigrationsAssembly { get; set; }
        public Action<DbContextOptionsBuilder> OptionsBuilder { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }

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
    }
}
