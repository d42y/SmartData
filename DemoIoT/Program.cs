using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;
using SmartData.Tables;

namespace DemoIoT
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var projectRoot = GetProjectRootPath();
            var dbPath = Path.Combine(projectRoot, "DemoIoT.db");
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
                       //.WithMigrationsAssembly("DemoIoT")
                       .WithLogging(loggerFactory)
                       .EnableEmbedding()
                       .EnableTimeseries().
                       EnableChangeTracking().
                       EnableIntegrityVerification();
            }, options => options.UseSqlite($"Data Source={dbPath}",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoIoT")));

            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var sqlData = scope.ServiceProvider.GetRequiredService<SqlData>();
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                // Ensure schema is created
                await sqlData.MigrateAsync();

                await RunDemoAsync(appDbContext, dbContext, scope.ServiceProvider);
            }
        }

        static async Task RunDemoAsync(AppDbContext appDbContext, SmartDataContext dbContext, IServiceProvider serviceProvider)
        {
            var sensors = appDbContext.Sensors;

            // Insert sensor data
            Console.WriteLine("Inserting sensor data...");
            var sensor1 = new Sensor { Id = "sensor1", Temperature = 70, Description = "Temperature is 70°F" };
            var sensor2 = new Sensor { Id = "sensor2", Temperature = 72, Description = "Temperature is 72°F" };
            await sensors.InsertAsync(new[] { sensor1, sensor2 });
            Console.WriteLine($"Inserted sensors: {sensor1.Id} ({sensor1.Temperature}°F), {sensor2.Id} ({sensor2.Temperature}°F)");

            // Update sensor data
            Console.WriteLine("\nUpdating sensor data...");
            sensor1.Temperature = 75;
            await sensors.UpdateAsync(sensor1);
            Console.WriteLine($"Updated sensor1 temperature to {sensor1.Temperature}°F");

            // Semantic search for RAG
            Console.WriteLine("\nSearching embeddings...");
            var matches = await sensors.SearchEmbeddings("temperature 70", topK: 2);
            foreach (var result in matches)
            {
                Console.WriteLine($"Match: Id={result.GetValue<string>("Id")}, Temperature={result.GetValue<int>("Temperature")}°F, Score={result.GetValue<float>("Score")}");
            }

            // GPT Q&A
            //Console.WriteLine("\nRunning GPT Q&A...");
            //var chatService = serviceProvider.GetRequiredService<ChatService>();
            //var response = await chatService.ChatAsync("What is the current temperature?", sensors, topK: 2);
            //Console.WriteLine($"Q: What is the current temperature?");
            //Console.WriteLine($"A: {response}");

            // Timeseries query
            Console.WriteLine("\nQuerying timeseries data...");
            var timeseries = await sensors.GetInterpolatedTimeseriesAsync(
                "sensor1", nameof(Sensor.Temperature), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, TimeSpan.FromMinutes(10), InterpolationMethod.Linear);
            foreach (var data in timeseries)
            {
                Console.WriteLine($"Timestamp: {data.Timestamp}, Temperature: {data.Value}°F");
            }

            // Query change log
            Console.WriteLine("\nQuerying change log...");
            var changes = await dbContext.ExecuteSqlQueryAsync("SELECT * FROM sysChangeLog WHERE TableName = 'Sensors' AND EntityId = 'sensor1'");
            foreach (var change in changes)
            {
                Console.WriteLine($"Change: {change.ToJson()}");
            }

            // Query integrity log
            Console.WriteLine("\nQuerying integrity log...");
            var integrityLogs = await dbContext.ExecuteSqlQueryAsync("SELECT * FROM sysIntegrityLog WHERE TableName = 'Sensors' AND EntityId = 'sensor1'");
            foreach (var log in integrityLogs)
            {
                Console.WriteLine($"Integrity Log: {log.ToJson()}");
            }

            Console.WriteLine("\nDemo completed. Press any key to exit.");
            Console.ReadKey();
        }

        static string GetProjectRootPath()
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\"));
            return projectRoot;
        }
    }

}