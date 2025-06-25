using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SmartData.Configurations;
using SmartData.Extensions;
using SmartData.Tables;
using SmartData.Vectorizer.Embedder;
using SmartData.Vectorizer.Search;

namespace SmartData.UnitTests
{
    public class Sensor
    {
        public string Id { get; set; }
        public int Temperature { get; set; }
        public string Description { get; set; }
    }

    public class AppDbContext : SqlDataContext
    {
        public SdSet<Sensor> Sensors { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider)
        {
        }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
            modelBuilder.Entity<Sensor>().Property(s => s.Id).IsRequired().HasMaxLength(50);
            modelBuilder.Entity<Sensor>().Property(s => s.Description).HasMaxLength(500);
        }
    }

    public class TableCollectionTests : IDisposable
    {
        private static readonly string _dbPath;
        private static bool _databaseInitialized;
        private static readonly object _lock = new object();

        static TableCollectionTests()
        {
            var projectRoot = GetProjectRootPath();
            _dbPath = Path.Combine(projectRoot, "unit_tests.db");
        }

        private static string GetProjectRootPath()
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\"));
            return projectRoot;
        }

        private readonly IServiceProvider _serviceProvider;
        private readonly SmartDataContext _dbContext;
        private readonly TableCollection<Sensor> _sensorTable;

        public TableCollectionTests()
        {
            lock (_lock)
            {
                if (!_databaseInitialized)
                {
                    // Cleanup existing database file only once at the start
                    if (File.Exists(_dbPath))
                    {
                        File.Delete(_dbPath);
                    }
                }
                var services = new ServiceCollection();
                services.AddLogging(builder => builder.AddConsole());
                services.AddSqlData<AppDbContext>(builder =>
                {
                    builder.WithConnectionString($"Data Source={_dbPath}")
                           .EnableEmbedding()
                           .EnableTimeseries()
                           .EnableChangeTracking()
                           .EnableIntegrityVerification()
                           .EnableSmartCalc();
                }, options => options.UseSqlite($"Data Source={_dbPath}"));

                var embedderMock = new Mock<IEmbedder>();
                embedderMock.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Returns(new float[384]);
                services.AddSingleton(embedderMock.Object);

                var faissMock = new Mock<FaissNetSearch>(384, It.IsAny<ILogger<FaissNetSearch>>());
                services.AddSingleton(faissMock.Object);

                _serviceProvider = services.BuildServiceProvider();

                _dbContext = _serviceProvider.GetRequiredService<SmartDataContext>();
                _dbContext.Database.OpenConnection();

               

                    // Check if the Sensors table exists
                    if (!TableExists("Sensors"))
                    {
                        _dbContext.Database.EnsureCreatedAsync().GetAwaiter().GetResult();
                    }

                    _databaseInitialized = true;
                

                _sensorTable = new TableCollection<Sensor>(
                    _serviceProvider,
                    "Sensors",
                    true,
                    true,
                    embedderMock.Object,
                    faissMock.Object,
                    _serviceProvider.GetService<ILogger<TableCollection<Sensor>>>());
            }
        }

        private bool TableExists(string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName",
                connection);
            command.Parameters.AddWithValue("@tableName", tableName);

            var count = Convert.ToInt32(command.ExecuteScalar());
            return count > 0;
        }

        public void Dispose()
        {
            _dbContext.Database.CloseConnection();
            _dbContext.Dispose();
        }

        [Fact]
        public async Task InsertAsync_NewSensor_SavesSuccessfully()
        {
            // Arrange
            var sensor = new Sensor { Id = "sensor1", Temperature = 20, Description = "Temperature is 20°C" };

            // Act
            var result = await _sensorTable.InsertAsync(sensor);

            // Assert
            var savedSensor = await _dbContext.Set<Sensor>().FindAsync("sensor1");
            Assert.NotNull(savedSensor);
            Assert.Equal("sensor1", savedSensor.Id);
            Assert.Equal(20, savedSensor.Temperature);
            Assert.Equal("Temperature is 20°C", savedSensor.Description);
        }
    }
}