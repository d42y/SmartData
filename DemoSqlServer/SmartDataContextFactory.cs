using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Configurations;
using SmartData.Extensions;
using SmartData;

namespace DemoSqlServer
{
    public class SmartDataContextFactory : IDesignTimeDbContextFactory<SmartDataContext>
    {
        public SmartDataContext CreateDbContext(string[] args)
        {
            var services = new ServiceCollection();
            services.AddSqlData<AppDbContext>(builder =>
            {
                builder.WithConnectionString("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
                       .WithMigrationsAssembly("DemoSqlServer");
            }, options => options.UseSqlServer("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlServer")));

            var serviceProvider = services.BuildServiceProvider();
            var appDbContext = serviceProvider.GetRequiredService<AppDbContext>();
            var sqlData = serviceProvider.GetRequiredService<SqlData>();


            var optionsBuilder = new DbContextOptionsBuilder<SmartDataContext>();
            optionsBuilder.UseSqlServer("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlServer"));

            var smartDataOptions = serviceProvider.GetRequiredService<SmartDataOptions>(); // NEW: Resolve SmartDataOptions
            var dbContext = new SmartDataContext(optionsBuilder.Options, smartDataOptions, migrationsAssembly: "DemoSqlite", sqlDataContext: appDbContext);

            return dbContext;
        }
    }
}