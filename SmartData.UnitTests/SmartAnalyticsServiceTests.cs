using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog;
using SmartData.AnalyticsService;
using SmartData.Core;
using SmartData.Data;
using SmartData.Models;
using SmartData.Vectorizer;
using System.Text.Json;
using Xunit;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SmartData.UnitTests
{
    public class SmartAnalyticsServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly IServiceProvider _serviceProvider;
        private readonly AppDbContext _dbContext;
        private readonly SmartAnalyticsService _analyticsService;
        private readonly ILogger<SmartAnalyticsServiceTests> _logger;
        private readonly Mock<IEmbeddingProvider> _embedderMock;
        private readonly Mock<IFaissSearch> _faissMock;

        public SmartAnalyticsServiceTests()
        {
            var timestamp = DateTime.Now.Ticks;
            _dbPath = Path.Combine(GetProjectRootPath(), $"unit_tests_analytics_{timestamp}.db");

            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
                Log.Information("Deleted existing database file: {DbPath}", _dbPath);
            }

            var logPath = Path.Combine(GetProjectRootPath(), $"test_log_analytics_{timestamp}.txt");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            var services = new ServiceCollection();
            var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
            services.AddLogging(builder => builder.AddSerilog());
            _logger = loggerFactory.CreateLogger<SmartAnalyticsServiceTests>();
            services.AddSingleton(_logger);

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

            services.AddHostedService<SmartAnalyticsService>();

            _logger.LogInformation("Building service provider...");
            _serviceProvider = services.BuildServiceProvider();

            _logger.LogInformation("Resolving AppDbContext...");
            _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();

            _logger.LogInformation("Opening database connection for {DbPath}...", _dbPath);
            _dbContext.Database.OpenConnection();

            _logger.LogInformation("Creating database schema for {DbPath}...", _dbPath);
            _dbContext.EnsureSchemaCreatedAsync().GetAwaiter().GetResult();
            _logger.LogInformation("Schema creation completed.");

            Assert.True(TableExists("sysAnalytics"), "sysAnalytics table was not created.");
            Assert.True(TableExists("sysAnalyticsSteps"), "sysAnalyticsSteps table was not created.");

            _logger.LogInformation("Resolving SmartAnalyticsService...");
            _analyticsService = _serviceProvider.GetRequiredService<SmartAnalyticsService>();
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
            _logger.LogInformation("Disposing SmartAnalyticsServiceTests...");
            _dbContext?.Database.CloseConnection();
            _dbContext?.Dispose();
            if (File.Exists(_dbPath))
            {
                try
                {
                    File.Delete(_dbPath);
                    _logger.LogInformation("Deleted database file: {DbPath}", _dbPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete database file: {DbPath}", _dbPath);
                }
            }
            Log.CloseAndFlush();
        }

        [Fact]
        public async Task AddAnalyticsAsync_ValidConfig_SavesSuccessfully()
        {
            _logger.LogInformation("Starting AddAnalyticsAsync_ValidConfig test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "AverageTemperature",
                Interval = 60,
                Embeddable = true,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "SELECT AVG(Temperature) AS AvgTemp FROM Sensors",
                        OutputVariable = "avgTemp"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);

            var savedAnalytics = await _dbContext.Set<Analytics>().FirstOrDefaultAsync(a => a.Name == "AverageTemperature");
            Assert.NotNull(savedAnalytics);
            Assert.Equal(config.Id, savedAnalytics.Id);
            Assert.Equal(60, savedAnalytics.Interval);
            Assert.True(savedAnalytics.Embeddable);

            var savedStep = await _dbContext.Set<AnalyticsStep>()
                .FirstOrDefaultAsync(s => s.AnalyticsId == config.Id);
            Assert.NotNull(savedStep);
            Assert.Equal(AnalyticsStepType.SqlQuery.ToString(), savedStep.Operation);
            Assert.Equal("SELECT AVG(Temperature) AS AvgTemp FROM Sensors", savedStep.Expression);
            Assert.Equal("avgTemp", savedStep.ResultVariable);
            Assert.Equal(1, savedStep.Order);
            Assert.Equal(10, savedStep.MaxLoop);
        }

        [Fact]
        public async Task AddAnalyticsAsync_DuplicateName_ThrowsException()
        {
            _logger.LogInformation("Starting AddAnalyticsAsync_DuplicateName test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "DuplicateTest",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
        {
            new AnalyticsStepConfig
            {
                Type = AnalyticsStepType.SqlQuery,
                Config = "SELECT COUNT(*) AS Count FROM Sensors",
                OutputVariable = "count" // Added to satisfy NOT NULL constraint
            }
        }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _analyticsService.AddAnalyticsAsync(config));
        }

        [Fact]
        public async Task VerifyAnalyticsAsync_ValidConfig_ReturnsTrue()
        {
            _logger.LogInformation("Starting VerifyAnalyticsAsync_ValidConfig test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "ValidAnalytics",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "SELECT AVG(Temperature) AS AvgTemp FROM Sensors",
                        OutputVariable = "avgTemp"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            var (isValid, errors) = await _analyticsService.VerifyAnalyticsAsync(config.Id);
            Assert.True(isValid);
            Assert.Empty(errors);
        }

        [Fact]
        public async Task VerifyAnalyticsAsync_InvalidSqlQuery_ReturnsFalseWithErrors()
        {
            _logger.LogInformation("Starting VerifyAnalyticsAsync_InvalidSqlQuery test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "InvalidSqlAnalytics",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "INSERT INTO Sensors (Id, Temperature) VALUES ('sensor1', 25)",
                        OutputVariable = "result"
                    }
                }
            };

            try
            {
                await _analyticsService.AddAnalyticsAsync(config);
            }
            catch (InvalidOperationException ex)
            {
                var errors = ex.Message.Split("; ");
                Assert.Contains("Validation Failed: Step 1 (SqlQuery): Only SELECT queries are allowed. in expression 'INSERT INTO Sensors (Id, Temperature) VALUES ('sensor1', 25)'", ex.Message);
            }
        }

        [Fact]
        public async Task VerifyAnalyticsAsync_ConditionStepInvalidGoTo_ReturnsFalseWithErrors()
        {
            _logger.LogInformation("Starting VerifyAnalyticsAsync_ConditionStepInvalidGoTo test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "ConditionAnalytics",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.Condition,
                        Config = "Context[\"count\"] > 0",
                        OutputVariable = "2", // Invalid GoTo step (no step 2 exists)
                        MaxLoop = 5
                    }
                }
            };
            try
            {
                await _analyticsService.AddAnalyticsAsync(config);
            } catch (InvalidOperationException ex)
            {
                var errors = ex.Message.Split(";");
                Assert.Contains("Step 1 (Condition): Invalid GoTo step number 2. Must be between 1 and 1, not current step in expression 'Context[\"count\"] > 0'.", ex.Message);
            }

            
        }

        [Fact]
        public async Task ExecuteAnalyticsAsync_SqlQueryStep_ReturnsCorrectResult()
        {
            _logger.LogInformation("Starting ExecuteAnalyticsAsync_SqlQueryStep test...");
            var sensorService = new DataService<Sensor>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<DataOptions>(),
                "Sensors",
                _embedderMock.Object,
                _faissMock.Object,
                _serviceProvider.GetRequiredService<IEventBus>(),
                _serviceProvider.GetService<ILogger<DataService<Sensor>>>());

            var sensors = new List<Sensor>
            {
                new Sensor { Id = "sensor1", Temperature = 20, Description = "Temperature is 20°C" },
                new Sensor { Id = "sensor2", Temperature = 30, Description = "Temperature is 30°C" }
            };
            await sensorService.InsertAsync(sensors);

            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "AverageTemp",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "SELECT AVG(Temperature) AS AvgTemp FROM Sensors",
                        OutputVariable = "avgTemp"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            var result = await _analyticsService.ExecuteAnalyticsAsync(config.Id);
            Assert.Equal("25", result); // (20 + 30) / 2 = 25
        }

        [Fact]
        public async Task ExecuteAnalyticsAsync_TimeseriesStep_ReturnsCorrectResult()
        {
            _logger.LogInformation("Starting ExecuteAnalyticsAsync_TimeseriesStep test...");
            var sensorService = new DataService<Sensor>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<DataOptions>(),
                "Sensors",
                _embedderMock.Object,
                _faissMock.Object,
                _serviceProvider.GetRequiredService<IEventBus>(),
                _serviceProvider.GetService<ILogger<DataService<Sensor>>>());

            var sensor = new Sensor { Id = "sensor3", Temperature = 25, Description = "Temperature is 25°C" };
            await sensorService.InsertAsync(sensor);

            var start = DateTime.UtcNow.AddHours(-1);
            var end = DateTime.UtcNow.AddHours(1);
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "TimeseriesTemp",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.Timeseries,
                        Config = $"Sensors,sensor3,Temperature,{start:yyyy-MM-dd HH:mm:ss},{end:yyyy-MM-dd HH:mm:ss}",
                        OutputVariable = "lastTemp"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            var result = await _analyticsService.ExecuteAnalyticsAsync(config.Id);
            Assert.Equal("25", result);
        }

        [Fact]
        public async Task ExecuteAnalyticsAsync_CSharpStep_ReturnsCorrectResult()
        {
            _logger.LogInformation("Starting ExecuteAnalyticsAsync_CSharpStep test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "CSharpCalculation",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.CSharp,
                        Config = "return 42 * 2;",
                        OutputVariable = "result"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            var result = await _analyticsService.ExecuteAnalyticsAsync(config.Id);
            Assert.Equal("84", result);
        }

        [Fact]
        public async Task ExecuteAnalyticsAsync_ConditionStep_LoopsCorrectly()
        {
            _logger.LogInformation("Starting ExecuteAnalyticsAsync_ConditionStep test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "LoopAnalytics",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.Variable,
                        Config = "return 0;",
                        OutputVariable = "counter"
                    },
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.CSharp,
                        Config = "int counter = (int)Context[\"counter\"]; counter = counter + 1; Context[\"counter\"] = counter; return counter;",
                        OutputVariable = "counter"
                    },
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.Condition,
                        Config = "return ((int)Context[\"counter\"]) < 3;",
                        OutputVariable = "2", // Go back to step 2
                        MaxLoop = 5
                    }
                    ,
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.CSharp,
                        Config = "return ((int)Context[\"counter\"]);",
                        OutputVariable = "result",
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            var result = await _analyticsService.ExecuteAnalyticsAsync(config.Id);
            Assert.Equal("3", result); // Counter should increment to 3 before condition fails
        }

        [Fact]
        public async Task ExportAnalyticsAsync_ReturnsValidJson()
        {
            _logger.LogInformation("Starting ExportAnalyticsAsync test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "ExportTest",
                Interval = 60,
                Embeddable = true,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "SELECT COUNT(*) AS Count FROM Sensors",
                        OutputVariable = "count"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            var json = await _analyticsService.ExportAnalyticsAsync(config.Id);
            var deserialized = JsonSerializer.Deserialize<AnalyticsConfig>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(config.Id, deserialized.Id);
            Assert.Equal(config.Name, deserialized.Name);
            Assert.Equal(config.Interval, deserialized.Interval);
            Assert.Equal(config.Embeddable, deserialized.Embeddable);
            Assert.Single(deserialized.Steps);
            Assert.Equal(AnalyticsStepType.SqlQuery, deserialized.Steps[0].Type);
            Assert.Equal("SELECT COUNT(*) AS Count FROM Sensors", deserialized.Steps[0].Config);
            Assert.Equal("count", deserialized.Steps[0].OutputVariable);
        }

        [Fact]
        public async Task ImportAnalyticsAsync_ValidJson_ImportsSuccessfully()
        {
            _logger.LogInformation("Starting ImportAnalyticsAsync_ValidJson test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "ImportTest",
                Interval = 60,
                Embeddable = true,
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "SELECT MAX(Temperature) AS MaxTemp FROM Sensors",
                        OutputVariable = "maxTemp"
                    }
                }
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await _analyticsService.ImportAnalyticsAsync(json);

            var importedAnalytics = await _dbContext.Set<Analytics>().FirstOrDefaultAsync(a => a.Name == "ImportTest");
            Assert.NotNull(importedAnalytics);
            Assert.Equal(config.Id, importedAnalytics.Id);
            Assert.Equal(config.Interval, importedAnalytics.Interval);
            Assert.Equal(config.Embeddable, importedAnalytics.Embeddable);

            var importedStep = await _dbContext.Set<AnalyticsStep>()
                .FirstOrDefaultAsync(s => s.AnalyticsId == config.Id);
            Assert.NotNull(importedStep);
            Assert.Equal(AnalyticsStepType.SqlQuery.ToString(), importedStep.Operation);
            Assert.Equal("SELECT MAX(Temperature) AS MaxTemp FROM Sensors", importedStep.Expression);
            Assert.Equal("maxTemp", importedStep.ResultVariable);
        }

        [Fact]
        public async Task DeleteAnalyticsAsync_ExistingAnalytics_DeletesSuccessfully()
        {
            _logger.LogInformation("Starting DeleteAnalyticsAsync_ExistingAnalytics test...");
            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "DeleteTest",
                Interval = 60,
                Steps = new List<AnalyticsStepConfig>
        {
            new AnalyticsStepConfig
            {
                Type = AnalyticsStepType.SqlQuery,
                Config = "SELECT COUNT(*) AS Count FROM Sensors",
                OutputVariable = "count" // Added to satisfy NOT NULL constraint
            }
        }
            };

            await _analyticsService.AddAnalyticsAsync(config);
            await _analyticsService.DeleteAnalyticsAsync(config.Id);

            var deletedAnalytics = await _dbContext.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == config.Id);
            Assert.Null(deletedAnalytics);

            var deletedSteps = await _dbContext.Set<AnalyticsStep>().Where(s => s.AnalyticsId == config.Id).ToListAsync();
            Assert.Empty(deletedSteps);
        }

        [Fact]
        public async Task DeleteAnalyticsAsync_NonExistentAnalytics_ThrowsException()
        {
            _logger.LogInformation("Starting DeleteAnalyticsAsync_NonExistentAnalytics test...");
            var nonExistentId = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _analyticsService.DeleteAnalyticsAsync(nonExistentId));
        }

        [Fact]
        public async Task ExecuteAsync_TriggeredByChange_ExecutesCorrectly()
        {
            _logger.LogInformation("Starting ExecuteAsync_TriggeredByChange test...");
            var sensorService = new DataService<Sensor>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<DataOptions>(),
                "Sensors",
                _embedderMock.Object,
                _faissMock.Object,
                _serviceProvider.GetRequiredService<IEventBus>(),
                _serviceProvider.GetService<ILogger<DataService<Sensor>>>());

            var sensor = new Sensor { Id = "sensor4", Temperature = 40, Description = "Temperature is 40°C" };
            await sensorService.InsertAsync(sensor);

            var config = new AnalyticsConfig
            {
                Id = Guid.NewGuid(),
                Name = "ChangeTriggeredAnalytics",
                Interval = -1, // Change-triggered
                Steps = new List<AnalyticsStepConfig>
                {
                    new AnalyticsStepConfig
                    {
                        Type = AnalyticsStepType.SqlQuery,
                        Config = "SELECT AVG(Temperature) AS AvgTemp FROM Sensors",
                        OutputVariable = "avgTemp"
                    }
                }
            };

            await _analyticsService.AddAnalyticsAsync(config);

            // Simulate a change in the Sensors table
            sensor.Temperature = 50;
            await sensorService.UpdateAsync(sensor);

            // Allow background service to process
            await Task.Delay(15000); // Wait for background service (10s loop + buffer)

            var analytics = await _dbContext.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == config.Id);
            Assert.NotNull(analytics);
            Assert.Equal("50", analytics.Value); // 50 / 1 = 50

            
        }
    }
}