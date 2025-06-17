using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;

namespace DemoSqlite
{
    public class SmartDataContextFactory : IDesignTimeDbContextFactory<SmartDataContext>
    {
        public SmartDataContext CreateDbContext(string[] args)
        {
            var services = new ServiceCollection();
            services.AddSqlData<AppDbContext>(builder =>
            {
                builder.WithConnectionString("Data Source=DemoSqlite.db")
                       .WithMigrationsAssembly("DemoSqlite")
                       .EnableEmbedding() // NEW: Enable embedding for migrations
                       .EnableTimeseries(); // NEW: Enable timeseries for migrations
            }, options => options.UseSqlite("Data Source=DemoSqlite.db",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlite")));

            var serviceProvider = services.BuildServiceProvider();
            var appDbContext = serviceProvider.GetRequiredService<AppDbContext>();
            var sqlData = serviceProvider.GetRequiredService<SqlData>();

            var optionsBuilder = new DbContextOptionsBuilder<SmartDataContext>();
            optionsBuilder.UseSqlite("Data Source=DemoSqlite.db",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlite"));

            var smartDataOptions = serviceProvider.GetRequiredService<SmartDataOptions>(); // NEW: Resolve SmartDataOptions
            var dbContext = new SmartDataContext(optionsBuilder.Options, smartDataOptions, migrationsAssembly: "DemoSqlite", sqlDataContext: appDbContext);

            return dbContext;
        }
    }
}