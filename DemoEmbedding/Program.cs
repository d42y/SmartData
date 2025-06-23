using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;

namespace DemoEmbedding
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var projectRoot = GetProjectRootPath();
            var dbPath = Path.Combine(projectRoot, "DemoEmbedding.db");
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
                       //.WithMigrationsAssembly("DemoEmbedding")
                       .WithLogging(loggerFactory)
                       .EnableEmbedding();
            }, options => options.UseSqlite($"Data Source={dbPath}",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoEmbedding")));
            services.AddSingleton<ChatService>();

            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var sqlData = scope.ServiceProvider.GetRequiredService<SqlData>();
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                Console.WriteLine("Applying migrations...");
                await sqlData.MigrateAsync();
                var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();
                Console.WriteLine($"Applied migrations: {string.Join(", ", appliedMigrations)}");
                var tables = await dbContext.ExecuteSqlQueryAsync("SELECT name FROM sqlite_master WHERE type='table'");
                Console.WriteLine("Tables in database: " + string.Join(", ", tables.Select(t => t.GetValue<string>("name"))));

                await RunDemoAsync(appDbContext, dbContext, serviceProvider);
            }
        }

        static async Task RunDemoAsync(AppDbContext appDbContext, SmartDataContext dbContext, IServiceProvider serviceProvider)
        {
            var sensors = appDbContext.Sensors;

            // Insert sample sensor data
            Console.WriteLine("Inserting sensor data...");
            var sensorData = new[]
            {
                new Sensor { Id = "sensor1", Temperature = 70, Description = "Temperature is 70°F" },
                new Sensor { Id = "sensor2", Temperature = 72, Description = "Temperature is 72°F" },
                new Sensor { Id = "sensor3", Temperature = 68, Description = "Temperature is 68°F" }
            };

            foreach (var sensor in sensorData)
            {
                await sensors.UpsertAsync(sensor);
                Console.WriteLine($"Inserted sensor {sensor.Id} with temperature {sensor.Temperature}°F");
            }

            // Search embeddings directly
            Console.WriteLine("\nSearching embeddings directly...");
            var matches = await sensors.SearchEmbeddings("sensor3?", topK: 2);
            foreach (var result in matches)
            {
                var entityId = result.GetValue<string>("Id"); // Sensor.Id is string
                var temperature = result.GetValue<int>("Temperature");
                var score = result.GetValue<float>("Score");
                Console.WriteLine($"Match: EntityId={entityId}, Temperature={temperature}°F, Score={score}");
            }

            // Query current temperature using ChatService
            Console.WriteLine("\nQuerying current temperature with ChatService...");
            using (var scope = serviceProvider.CreateScope())
            {
                var chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
                var query = "What is the current temperature?";
                var response = await chatService.ChatAsync(query, sensors, topK: 2);
                Console.WriteLine($"Query: {query}");
                Console.WriteLine($"Response: {response}");
            }
        }

        static string GetProjectRootPath()
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\"));
            return projectRoot;
        }
    }
}