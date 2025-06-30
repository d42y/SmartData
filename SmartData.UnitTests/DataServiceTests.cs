using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog;
using SmartData.Core;
using SmartData.Data;
using SmartData.Models;
using SmartData.Vectorizer;
using System.Linq.Expressions;
using System.Text.Json;

namespace SmartData.UnitTests
{
    public class Sensor
    {
        public string Id { get; set; }
        [Timeseries]
        public int Temperature { get; set; }
        [Embeddable("Sensor {0}")]
        [TrackChange]
        [EnsureIntegrity]
        public string Description { get; set; }
    }

    public class AppDbContext : DataContext
    {
        public DbSet<Sensor> Sensors { get; set; }

        public AppDbContext(DbContextOptions options, DataOptions dataOptions, ILogger<DataContext> logger)
            : base(options, dataOptions, null, logger)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
            modelBuilder.Entity<Sensor>().Property(s => s.Id).IsRequired().HasMaxLength(50);
            modelBuilder.Entity<Sensor>().Property(s => s.Description).HasMaxLength(500);
            base.OnModelCreating(modelBuilder);
        }
    }

    public class DataServiceTests_AutoSchema : IDisposable
    {
        private readonly string _dbPath;
        private readonly Mock<IEmbeddingProvider> _embedderMock;
        private readonly Mock<IFaissSearch> _faissMock;
        private readonly IServiceProvider _serviceProvider;
        private readonly AppDbContext _dbContext;
        private readonly DataService<Sensor> _sensorService;
        private readonly ILogger<DataServiceTests_AutoSchema> _logger;

        public DataServiceTests_AutoSchema()
        {
            try
            {
                // Generate unique database path using timestamp
                var timestamp = DateTime.Now.Ticks;
                _dbPath = Path.Combine(GetProjectRootPath(), $"unit_tests_auto_{timestamp}.db");

                // Delete database file if it exists
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                    Log.Information("Deleted existing database file: {DbPath}", _dbPath);
                }

                // Use timestamp for log file
                var logPath = Path.Combine(GetProjectRootPath(), $"test_log_{timestamp}.txt");
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                    .CreateLogger();

                var services = new ServiceCollection();
                var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
                services.AddLogging(builder => builder.AddSerilog());
                _logger = loggerFactory.CreateLogger<DataServiceTests_AutoSchema>();
                services.AddSingleton(_logger);

                _logger.LogInformation("Creating new DataServiceTests_AutoSchema instance...");

                _embedderMock = new Mock<IEmbeddingProvider>();
                _embedderMock.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Returns(new float[384]);
                services.AddSingleton(_embedderMock.Object);

                _faissMock = new Mock<IFaissSearch>();
                services.AddSingleton(_faissMock.Object);

                var dataOptions = new DataOptions();
                services.AddSmartData<AppDbContext>(options =>
                {
                    options.WithConnectionString($"Data Source={_dbPath}")
                           .WithDbOptions(opt => opt.UseSqlite($"Data Source={_dbPath}"))
                           .WithEmbeddings()
                           .WithTimeseries()
                           .WithChangeTracking()
                           .WithIntegrityVerification()
                           .WithCalculations();
                    _logger.LogInformation("DataOptions configured: Embeddings={0}, Timeseries={1}, ChangeTracking={2}, IntegrityVerification={3}, Calculations={4}",
                        options.EnableEmbeddings, options.EnableTimeseries, options.EnableChangeTracking,
                        options.EnableIntegrityVerification, options.EnableCalculations);
                });

                _logger.LogInformation("Building service provider...");
                _serviceProvider = services.BuildServiceProvider();

                _logger.LogInformation("Resolving AppDbContext...");
                _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();

                _logger.LogInformation("Opening database connection for {DbPath}...", _dbPath);
                _dbContext.Database.OpenConnection();

                _logger.LogInformation("Creating database schema for {DbPath}...", _dbPath);
                _dbContext.EnsureSchemaCreatedAsync().GetAwaiter().GetResult();
                _logger.LogInformation("Schema creation completed.");

                Assert.True(TableExists("sysEmbeddings"), "sysEmbeddings table was not created.");
                Assert.True(TableExists("sysTimeseriesBaseValues"), "sysTimeseriesBaseValues table was not created.");
                Assert.True(TableExists("sysTimeseriesDeltas"), "sysTimeseriesDeltas table was not created.");
                Assert.True(TableExists("sysChangeLog"), "sysChangeLog table was not created.");
                Assert.True(TableExists("sysIntegrityLog"), "sysIntegrityLog table was not created.");
                Assert.True(TableExists("sysAnalytics"), "sysAnalytics table was not created.");
                Assert.True(TableExists("sysAnalyticsSteps"), "sysAnalyticsSteps table was not created.");

                _logger.LogInformation("Initializing DataService<Sensor>...");
                _sensorService = new DataService<Sensor>(
                    _serviceProvider,
                    _serviceProvider.GetRequiredService<DataOptions>(),
                    "Sensors",
                    _embedderMock.Object,
                    _faissMock.Object,
                    _serviceProvider.GetRequiredService<IEventBus>(),
                    _serviceProvider.GetService<ILogger<DataService<Sensor>>>());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize DataServiceTests_AutoSchema.");
                throw new InvalidOperationException("Test setup failed due to initialization error.", ex);
            }
        }

        private static string GetProjectRootPath()
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\"));
            return projectRoot;
        }

        private bool TableExists(string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName",
                connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            var result = Convert.ToInt32(command.ExecuteScalar());
            _logger.LogInformation("Checking table {TableName}: Exists = {Exists}", tableName, result > 0);
            return result > 0;
        }

        public void Dispose()
        {
            if (_logger != null)
            {
                _logger.LogInformation("Disposing DataServiceTests_AutoSchema...");
            }
            _dbContext?.Database.CloseConnection();
            _dbContext?.Dispose();
            if (File.Exists(_dbPath))
            {
                try
                {
                    File.Delete(_dbPath);
                    if (_logger != null)
                    {
                        _logger.LogInformation("Deleted database file: {DbPath}", _dbPath);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                    {
                        _logger.LogError(ex, "Failed to delete database file: {DbPath}", _dbPath);
                    }
                }
            }
            Log.CloseAndFlush();
        }

        [Fact]
        public async Task InsertAsync_NewSensor_SavesSuccessfully()
        {
            _logger.LogInformation("Starting InsertAsync_NewSensor test...");
            var sensor = new Sensor { Id = "sensor1", Temperature = 20, Description = "Temperature is 20°C" };
            var result = await _sensorService.InsertAsync(sensor);

            var savedSensor = await _dbContext.Sensors.FindAsync("sensor1");
            Assert.NotNull(savedSensor);
            Assert.Equal("sensor1", savedSensor.Id);
            Assert.Equal(20, savedSensor.Temperature);
            Assert.Equal("Temperature is 20°C", savedSensor.Description);

            var changeLog = await _dbContext.ChangeLogRecords
                .Where(c => c.TableName == "Sensors" && c.EntityId == "sensor1" && c.ChangeType == "Insert")
                .FirstOrDefaultAsync();
            Assert.NotNull(changeLog);
            Assert.Equal("Description", changeLog.PropertyName);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 20°C"), changeLog.NewValue);

            var timeseries = await _dbContext.TimeseriesBaseValues
                .Where(t => t.TableName == "Sensors" && t.EntityId == "sensor1" && t.PropertyName == "Temperature")
                .FirstOrDefaultAsync();
            Assert.NotNull(timeseries);
            Assert.Equal("20", timeseries.Value);

            var integrityLog = await _dbContext.IntegrityLogRecords
                .Where(i => i.TableName == "Sensors" && i.EntityId == "sensor1" && i.PropertyName == "Description")
                .FirstOrDefaultAsync();
            Assert.NotNull(integrityLog);
            Assert.NotEmpty(integrityLog.Hash);

            var embedding = await _dbContext.EmbeddingRecords
                .Where(e => e.TableName == "Sensors" && e.EntityId == "sensor1")
                .FirstOrDefaultAsync();
            Assert.NotNull(embedding);
            Assert.Equal(384, embedding.Embedding.Length);
        }

        [Fact]
        public async Task InsertAsync_MultipleSensors_SavesSuccessfully()
        {
            _logger.LogInformation("Starting InsertAsync_MultipleSensors test...");
            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor2", Temperature = 22, Description = "Temperature is 22°C" },
                new Sensor { Id = "sensor3", Temperature = 24, Description = "Temperature is 24°C" }
            };

            await _sensorService.InsertAsync(sensors);

            var savedSensor2 = await _dbContext.Set<Sensor>().FindAsync("sensor2");
            Assert.NotNull(savedSensor2);
            Assert.Equal("sensor2", savedSensor2.Id);
            Assert.Equal(22, savedSensor2.Temperature);
            Assert.Equal("Temperature is 22°C", savedSensor2.Description);

            var savedSensor3 = await _dbContext.Set<Sensor>().FindAsync("sensor3");
            Assert.NotNull(savedSensor3);
            Assert.Equal("sensor3", savedSensor3.Id);
            Assert.Equal(24, savedSensor3.Temperature);
            Assert.Equal("Temperature is 24°C", savedSensor3.Description);

            var changeLogs = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && (c.EntityId == "sensor2" || c.EntityId == "sensor3") && c.ChangeType == "Insert")
                .ToListAsync();
            Assert.Equal(2, changeLogs.Count);
            Assert.Contains(changeLogs, c => c.EntityId == "sensor2" && c.NewValue == JsonSerializer.Serialize("Temperature is 22°C"));
            Assert.Contains(changeLogs, c => c.EntityId == "sensor3" && c.NewValue == JsonSerializer.Serialize("Temperature is 24°C"));

            var timeseries = await _dbContext.Set<TimeseriesBaseValue>()
                .Where(t => t.TableName == "Sensors" && (t.EntityId == "sensor2" || t.EntityId == "sensor3") && t.PropertyName == "Temperature")
                .ToListAsync();
            Assert.Equal(2, timeseries.Count);
            Assert.Contains(timeseries, t => t.EntityId == "sensor2" && t.Value == "22");
            Assert.Contains(timeseries, t => t.EntityId == "sensor3" && t.Value == "24");

            var integrityLogs = await _dbContext.Set<IntegrityLogRecord>()
                .Where(i => i.TableName == "Sensors" && (i.EntityId == "sensor2" || i.EntityId == "sensor3") && i.PropertyName == "Description")
                .ToListAsync();
            Assert.Equal(2, integrityLogs.Count);
            Assert.Contains(integrityLogs, i => i.EntityId == "sensor2" && !string.IsNullOrEmpty(i.Hash));
            Assert.Contains(integrityLogs, i => i.EntityId == "sensor3" && !string.IsNullOrEmpty(i.Hash));

            var embeddings = await _dbContext.Set<EmbeddingRecord>()
                .Where(e => e.TableName == "Sensors" && (e.EntityId == "sensor2" || e.EntityId == "sensor3"))
                .ToListAsync();
            Assert.Equal(2, embeddings.Count);
            Assert.Contains(embeddings, e => e.EntityId == "sensor2" && e.Embedding.Length == 384);
            Assert.Contains(embeddings, e => e.EntityId == "sensor3" && e.Embedding.Length == 384);
        }

        [Fact]
        public async Task UpdateAsync_ExistingSensor_UpdatesSuccessfully()
        {
            _logger.LogInformation("Starting UpdateAsync_ExistingSensor test...");
            var sensor = new Sensor { Id = "sensor4", Temperature = 25, Description = "Temperature is 25°C" };
            await _sensorService.InsertAsync(sensor);

            sensor.Temperature = 26;
            sensor.Description = "Temperature is 26°C";
            var result = await _sensorService.UpdateAsync(sensor);
            
            Assert.True(result);

            var updatedSensor = await _dbContext.Set<Sensor>().FindAsync("sensor4");
            Assert.NotNull(updatedSensor);
            Assert.Equal("sensor4", updatedSensor.Id);
            Assert.Equal(26, updatedSensor.Temperature);
            Assert.Equal("Temperature is 26°C", updatedSensor.Description);

            var changeLog = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && c.EntityId == "sensor4" && c.ChangeType == "Update")
                .FirstOrDefaultAsync();
            Assert.NotNull(changeLog);
            Assert.Equal("Description", changeLog.PropertyName);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 25°C"), changeLog.OriginalValue);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 26°C"), changeLog.NewValue);

            var timeseries = await _dbContext.Set<TimeseriesBaseValue>()
                .Where(t => t.TableName == "Sensors" && t.EntityId == "sensor4" && t.PropertyName == "Temperature")
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefaultAsync();
            Assert.NotNull(timeseries);
            Assert.Equal("26", timeseries.Value);

            var integrityLog = await _dbContext.Set<IntegrityLogRecord>()
                .Where(i => i.TableName == "Sensors" && i.EntityId == "sensor4" && i.PropertyName == "Description")
                .OrderByDescending(i => i.Timestamp)
                .FirstOrDefaultAsync();
            Assert.NotNull(integrityLog);
            Assert.NotEmpty(integrityLog.Hash);

            var embedding = await _dbContext.Set<EmbeddingRecord>()
                .Where(e => e.TableName == "Sensors" && e.EntityId == "sensor4")
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync();
            Assert.NotNull(embedding);
            Assert.Equal(384, embedding.Embedding.Length);
        }

        [Fact]
        public async Task UpdateAsync_NonExistentSensor_ReturnsFalse()
        {
            _logger.LogInformation("Starting UpdateAsync_NonExistentSensor test...");
            var sensor = new Sensor { Id = "sensor999", Temperature = 30, Description = "Temperature is 30°C" };
            var result = await _sensorService.UpdateAsync(sensor);

            Assert.False(result);
        }

        [Fact]
        public async Task UpsertAsync_NewSensor_InsertsSuccessfully()
        {
            _logger.LogInformation("Starting UpsertAsync_NewSensor test...");
            var sensor = new Sensor { Id = "sensor5", Temperature = 27, Description = "Temperature is 27°C" };
            var result = await _sensorService.UpsertAsync(sensor);

            Assert.NotNull(result);
            Assert.Equal("sensor5", result.Id);
            Assert.Equal(27, result.Temperature);
            Assert.Equal("Temperature is 27°C", result.Description);

            var savedSensor = await _dbContext.Set<Sensor>().FindAsync("sensor5");
            Assert.NotNull(savedSensor);
            Assert.Equal("sensor5", savedSensor.Id);
            Assert.Equal(27, savedSensor.Temperature);
            Assert.Equal("Temperature is 27°C", savedSensor.Description);

            var changeLog = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && c.EntityId == "sensor5" && c.ChangeType == "Insert")
                .FirstOrDefaultAsync();
            Assert.NotNull(changeLog);
            Assert.Equal("Description", changeLog.PropertyName);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 27°C"), changeLog.NewValue);
        }

        [Fact]
        public async Task UpsertAsync_ExistingSensor_UpdatesSuccessfully()
        {
            _logger.LogInformation("Starting UpsertAsync_ExistingSensor test...");
            var sensor = new Sensor { Id = "sensor6", Temperature = 28, Description = "Temperature is 28°C" };
            await _sensorService.InsertAsync(sensor);

            sensor.Temperature = 29;
            sensor.Description = "Temperature is 29°C";
            var result = await _sensorService.UpsertAsync(sensor);

            Assert.NotNull(result);
            Assert.Equal("sensor6", result.Id);
            Assert.Equal(29, result.Temperature);
            Assert.Equal("Temperature is 29°C", result.Description);

            var updatedSensor = await _dbContext.Set<Sensor>().FindAsync("sensor6");
            Assert.NotNull(updatedSensor);
            Assert.Equal("sensor6", updatedSensor.Id);
            Assert.Equal(29, updatedSensor.Temperature);
            Assert.Equal("Temperature is 29°C", updatedSensor.Description);

            var changeLog = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && c.EntityId == "sensor6" && c.ChangeType == "Update")
                .FirstOrDefaultAsync();
            Assert.NotNull(changeLog);
            Assert.Equal("Description", changeLog.PropertyName);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 28°C"), changeLog.OriginalValue);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 29°C"), changeLog.NewValue);
        }

        [Fact]
        public async Task UpsertAsync_MultipleSensors_UpsertsSuccessfully()
        {
            _logger.LogInformation("Starting UpsertAsync_MultipleSensors test...");
            var sensor1 = new Sensor { Id = "sensor7", Temperature = 30, Description = "Temperature is 30°C" };
            await _sensorService.InsertAsync(sensor1);

            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor7", Temperature = 31, Description = "Temperature is 31°C" },
                new Sensor { Id = "sensor8", Temperature = 32, Description = "Temperature is 32°C" }
            };

            var results = await _sensorService.UpsertAsync(sensors);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, s => s.Id == "sensor7" && s.Temperature == 31 && s.Description == "Temperature is 31°C");
            Assert.Contains(results, s => s.Id == "sensor8" && s.Temperature == 32 && s.Description == "Temperature is 32°C");

            var updatedSensor7 = await _dbContext.Set<Sensor>().FindAsync("sensor7");
            Assert.NotNull(updatedSensor7);
            Assert.Equal(31, updatedSensor7.Temperature);
            Assert.Equal("Temperature is 31°C", updatedSensor7.Description);

            var savedSensor8 = await _dbContext.Set<Sensor>().FindAsync("sensor8");
            Assert.NotNull(savedSensor8);
            Assert.Equal(32, savedSensor8.Temperature);
            Assert.Equal("Temperature is 32°C", savedSensor8.Description);

            var changeLogs = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && (c.EntityId == "sensor7" || c.EntityId == "sensor8"))
                .ToListAsync();
            Assert.Contains(changeLogs, c => c.EntityId == "sensor7" && c.ChangeType == "Update" && c.NewValue == JsonSerializer.Serialize("Temperature is 31°C"));
            Assert.Contains(changeLogs, c => c.EntityId == "sensor8" && c.ChangeType == "Insert" && c.NewValue == JsonSerializer.Serialize("Temperature is 32°C"));
        }

        [Fact]
        public async Task DeleteAsync_ExistingSensor_DeletesSuccessfully()
        {
            _logger.LogInformation("Starting DeleteAsync_ExistingSensor test...");
            var sensor = new Sensor { Id = "sensor9", Temperature = 33, Description = "Temperature is 33°C" };
            await _sensorService.InsertAsync(sensor);

            var result = await _sensorService.DeleteAsync("sensor9");
            Assert.True(result);

            var deletedSensor = await _dbContext.Set<Sensor>().FindAsync("sensor9");
            Assert.Null(deletedSensor);

            var changeLog = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && c.EntityId == "sensor9" && c.ChangeType == "Delete")
                .FirstOrDefaultAsync();
            Assert.NotNull(changeLog);
            Assert.Equal("Description", changeLog.PropertyName);
            Assert.Equal(JsonSerializer.Serialize("Temperature is 33°C"), changeLog.OriginalValue);
            Assert.Null(changeLog.NewValue);
        }

        [Fact]
        public async Task DeleteAsync_NonExistentSensor_ReturnsFalse()
        {
            _logger.LogInformation("Starting DeleteAsync_NonExistentSensor test...");
            var result = await _sensorService.DeleteAsync("sensor999");
            Assert.False(result);
        }

        [Fact]
        public async Task DeleteAllAsync_DeletesAllSensors()
        {
            _logger.LogInformation("Starting DeleteAllAsync test...");
            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor10", Temperature = 34, Description = "Temperature is 34°C" },
                new Sensor { Id = "sensor11", Temperature = 35, Description = "Temperature is 35°C" }
            };
            await _sensorService.InsertAsync(sensors);

            var deletedCount = await _sensorService.DeleteAllAsync();
            Assert.Equal(2, deletedCount);

            var remainingSensors = await _dbContext.Set<Sensor>().ToListAsync();
            Assert.Empty(remainingSensors);

            var changeLogs = await _dbContext.Set<ChangeLogRecord>()
                .Where(c => c.TableName == "Sensors" && (c.EntityId == "sensor10" || c.EntityId == "sensor11") && c.ChangeType == "Delete")
                .ToListAsync();
            Assert.Equal(2, changeLogs.Count);

            var embeddings = await _dbContext.Set<EmbeddingRecord>()
                .Where(e => e.TableName == "Sensors" && (e.EntityId == "sensor10" || e.EntityId == "sensor11"))
                .ToListAsync();
            Assert.Empty(embeddings);

            var timeseries = await _dbContext.Set<TimeseriesBaseValue>()
                .Where(t => t.TableName == "Sensors" && (t.EntityId == "sensor10" || t.EntityId == "sensor11"))
                .ToListAsync();
            Assert.Empty(timeseries);
        }

        [Fact]
        public async Task FindByIdAsync_ExistingSensor_ReturnsSensor()
        {
            _logger.LogInformation("Starting FindByIdAsync_ExistingSensor test...");
            var sensor = new Sensor { Id = "sensor12", Temperature = 36, Description = "Temperature is 36°C" };
            await _sensorService.InsertAsync(sensor);

            var foundSensor = await _sensorService.FindByIdAsync("sensor12");
            Assert.NotNull(foundSensor);
            Assert.Equal("sensor12", foundSensor.Id);
            Assert.Equal(36, foundSensor.Temperature);
            Assert.Equal("Temperature is 36°C", foundSensor.Description);
        }

        [Fact]
        public async Task FindByIdAsync_NonExistentSensor_ReturnsNull()
        {
            _logger.LogInformation("Starting FindByIdAsync_NonExistentSensor test...");
            var foundSensor = await _sensorService.FindByIdAsync("sensor999");
            Assert.Null(foundSensor);
        }

        [Fact]
        public async Task FindAsync_WithPredicate_ReturnsMatchingSensors()
        {
            _logger.LogInformation("Starting FindAsync_WithPredicate test...");
            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor13", Temperature = 37, Description = "Temperature is 37°C" },
                new Sensor { Id = "sensor14", Temperature = 38, Description = "Temperature is 38°C" },
                new Sensor { Id = "sensor15", Temperature = 39, Description = "Temperature is 39°C" }
            };
            await _sensorService.InsertAsync(sensors);

            Expression<Func<Sensor, bool>> predicate = s => s.Temperature > 37;
            var foundSensors = await _sensorService.FindAsync(predicate, 0, 10);

            Assert.Equal(2, foundSensors.Count);
            Assert.Contains(foundSensors, s => s.Id == "sensor14");
            Assert.Contains(foundSensors, s => s.Id == "sensor15");
        }

        [Fact]
        public async Task FindAllAsync_ReturnsAllSensors()
        {
            _logger.LogInformation("Starting FindAllAsync test...");
            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor16", Temperature = 40, Description = "Temperature is 40°C" },
                new Sensor { Id = "sensor17", Temperature = 41, Description = "Temperature is 41°C" }
            };
            await _sensorService.InsertAsync(sensors);

            var allSensors = await _sensorService.FindAllAsync();
            Assert.Equal(2, allSensors.Count);
            Assert.Contains(allSensors, s => s.Id == "sensor16");
            Assert.Contains(allSensors, s => s.Id == "sensor17");
        }

        [Fact]
        public async Task CountAsync_WithPredicate_ReturnsCorrectCount()
        {
            _logger.LogInformation("Starting CountAsync_WithPredicate test...");
            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor18", Temperature = 42, Description = "Temperature is 42°C" },
                new Sensor { Id = "sensor19", Temperature = 43, Description = "Temperature is 43°C" },
                new Sensor { Id = "sensor20", Temperature = 44, Description = "Temperature is 44°C" }
            };
            await _sensorService.InsertAsync(sensors);

            Expression<Func<Sensor, bool>> predicate = s => s.Temperature > 42;
            var count = await _sensorService.CountAsync(predicate);
            Assert.Equal(2, count);

            var totalCount = await _sensorService.CountAsync();
            Assert.Equal(3, totalCount);
        }

        [Fact]
        public async Task ExecuteSqlQueryAsync_ReturnsMatchingSensors()
        {
            _logger.LogInformation("Starting ExecuteSqlQueryAsync test...");
            var sensor = new Sensor { Id = "sensor21", Temperature = 45, Description = "Temperature is 45°C" };
            await _sensorService.InsertAsync(sensor);

            var results = await _sensorService.ExecuteSqlQueryAsync("SELECT * FROM Sensors WHERE Id = @p0", "sensor21");
            Assert.Single(results);
            Assert.Equal("sensor21", results[0].Id);
            Assert.Equal(45, results[0].Temperature);
            Assert.Equal("Temperature is 45°C", results[0].Description);
        }

        [Fact]
        public async Task SearchAsync_WithValidQuery_ReturnsMatchingResults()
        {
            var embedding = new float[384]; // Mocked embedding vector
            _embedderMock.Setup(e => e.GenerateEmbedding("Sensor Temperature is 46°C")).Returns(embedding);
            _embedderMock.Setup(e => e.GenerateEmbedding("Temperature is 46°C")).Returns(embedding);

            var sensor = new Sensor { Id = "sensor22", Temperature = 46, Description = "Temperature is 46°C" };
            await _sensorService.InsertAsync(sensor);

            // Verify embedding was stored
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var embeddingRecord = await dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == "Sensors" && e.EntityId == "sensor22");
            Assert.NotNull(embeddingRecord);
            Assert.Equal(384, embeddingRecord.Embedding.Length);

            // Mock FAISS search
            _faissMock.Setup(f => f.AddEmbedding(It.IsAny<Guid>(), It.IsAny<float[]>())).Verifiable();
            _faissMock.Setup(f => f.Search(It.Is<float[]>(e => e.SequenceEqual(embedding)), 1)).Returns(new Guid[] { embeddingRecord.Id });

            var results = await _sensorService.SearchAsync("Temperature is 46°C", 1);
            Assert.Single(results);
            Assert.Equal("sensor22", results[0].Data["Id"]);
            Assert.Equal(1.0f, results[0].Data["Score"]);
        }

        [Fact]
        public async Task SearchAsync_EmptyQuery_ReturnsEmptyList()
        {
            _logger.LogInformation("Starting SearchAsync_EmptyQuery test...");
            var results = await _sensorService.SearchAsync("", 1);
            Assert.Empty(results);
        }

        [Fact]
        public async Task GetTimeseriesAsync_ReturnsTimeseriesData()
        {
            _logger.LogInformation("Starting GetTimeseriesAsync test...");
            var sensor = new Sensor { Id = "sensor23", Temperature = 47, Description = "Temperature is 47°C" };
            await _sensorService.InsertAsync(sensor);

            var start = DateTime.UtcNow.AddHours(-1);
            var end = DateTime.UtcNow.AddHours(1);
            var timeseries = await _sensorService.GetTimeseriesAsync("sensor23", "Temperature", start, end);

            Assert.NotEmpty(timeseries);
            Assert.Contains(timeseries, t => t.Value == "47");
        }

        [Fact]
        public async Task GetInterpolatedTimeseriesAsync_LinearInterpolation_ReturnsInterpolatedData()
        {
            _logger.LogInformation("Starting GetInterpolatedTimeseriesAsync_LinearInterpolation test...");
            var sensor = new Sensor { Id = "sensor24", Temperature = 48, Description = "Temperature is 48°C" };
            await _sensorService.InsertAsync(sensor);

            var start = DateTime.UtcNow.AddHours(-1);
            var end = DateTime.UtcNow.AddHours(1);
            var interval = TimeSpan.FromMinutes(30);
            var timeseries = await _sensorService.GetInterpolatedTimeseriesAsync("sensor24", "Temperature", start, end, interval, InterpolationMethod.Linear);

            Assert.NotEmpty(timeseries);
            Assert.Contains(timeseries, t => t.Value == "48.00");
        }

        
    }
}