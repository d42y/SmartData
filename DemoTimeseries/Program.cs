using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Attributes;
using SmartData.Configurations;
using SmartData.Extensions;
using SmartData.Tables;

namespace DemoTimeseries
{
    public class Sensor
    {
        public string Id { get; set; }

        [Timeseries]
        public int Temperature { get; set; }
    }

    public class AppDbContext : SqlDataContext
    {

        public SdSet<Sensor> Sensors { get; private set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            var projectRoot = GetProjectRootPath();
            var dbPath = Path.Combine(projectRoot, "DemoTimeseries.db");
            Console.WriteLine($"Using database: {dbPath}");

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.AddConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                });
            });
            var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
            services.AddSqlData<AppDbContext>(builder =>
            {
                builder.WithConnectionString($"Data Source={dbPath}")
                       //.WithMigrationsAssembly("DemoSqlite")
                       .WithLogging(loggerFactory);
            }, options => options.UseSqlite($"Data Source={dbPath}",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoTimeseries")));

            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var sqlData = scope.ServiceProvider.GetRequiredService<SqlData>();
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Verify migrations and tables
                Console.WriteLine("Applying migrations...");
                await sqlData.MigrateAsync();
                var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();
                Console.WriteLine($"Applied migrations: {string.Join(", ", appliedMigrations)}");
                var tables = await dbContext.ExecuteSqlQueryAsync("SELECT name FROM sqlite_master WHERE type='table'");
                Console.WriteLine("Tables in database: " + string.Join(", ", tables.Select(t => t.GetValue<string>("name"))));

                await RunDemoAsync(appDbContext, dbContext);
            }
        }

        static async Task RunDemoAsync(AppDbContext appDbContext, SmartDataContext dbContext)
        {
            var sensors = appDbContext.Sensors;
            var random = new Random();

            // Simulate sensor data: 100 entries per temperature value in the next 5 minutes
            var sensorId = "sensor1";
            var temperatures = new[] { 70, 71, 72, 71, 70 };
            var startTime = DateTime.UtcNow;
            var endTime = startTime.AddMinutes(5);
            var totalEntries = 100 * temperatures.Length;
            var intervalMs = (int)((endTime - startTime).TotalMilliseconds / totalEntries);

            Console.WriteLine("Inserting sensor data...");
            for (int i = 0; i < totalEntries; i++)
            {
                var temperature = temperatures[i % temperatures.Length];
                var sensor = new Sensor
                {
                    Id = sensorId,
                    Temperature = temperature
                };

                await sensors.UpsertAsync(sensor);
                Console.WriteLine("Inserted sensor {0} with temperature {1} at {2}",
                    sensorId, sensor.Temperature, DateTime.UtcNow);

                await Task.Delay(random.Next(1, intervalMs * 2));
            }

            // Query timeseries data at 30-second intervals
            Console.WriteLine("Querying timeseries data at 30-second intervals...");
            var timeseries = await sensors.GetInterpolatedTimeseriesAsync(sensorId, nameof(Sensor.Temperature),
                startTime, endTime, TimeSpan.FromSeconds(30), InterpolationMethod.Linear);

            Console.WriteLine("Retrieved {0} timeseries records:", timeseries.Count);
            foreach (var data in timeseries)
            {
                Console.WriteLine("Timestamp: {0}, Value: {1}", data.Timestamp, data.Value);
            }
        }

        static string GetProjectRootPath()
        {
            // Start from the executing assembly's location (e.g., bin/Debug)
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            // Navigate up to the project root (assuming bin/Debug is 2-3 levels deep)
            var projectRoot = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\"));
            return projectRoot;
        }
    }
}