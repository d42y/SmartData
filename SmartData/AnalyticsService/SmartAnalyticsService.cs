using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using SmartData.Core;
using SmartData.Data;
using SmartData.Models;
using SqlKata;
using SqlKata.Compilers;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartData.AnalyticsService
{
    public class ScriptGlobals
    {
        public Dictionary<string, object> Context { get; set; }
        public Dictionary<string, object> context
        {
            get => Context;
            set => Context = value;
        }
    }

    public enum AnalyticsStepType
    {
        SqlQuery,
        CSharp,
        Condition,
        Variable,
        Timeseries
    }

    public class AnalyticsStepConfig
    {
        public AnalyticsStepType Type { get; set; }
        public string Config { get; set; }
        public string OutputVariable { get; set; }
        public int MaxLoop { get; set; } = 10;
    }

    public class AnalyticsConfig
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public int Interval { get; set; }
        public bool Embeddable { get; set; }
        public List<AnalyticsStepConfig> Steps { get; set; } = new();
    }

    public class SmartAnalyticsService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly DataOptions _options;
        private readonly ILogger<SmartAnalyticsService> _logger;
        private readonly IEventBus _eventBus;
        private readonly ScriptOptions _scriptOptions;
        private readonly Compiler _sqlCompiler;
        private readonly ConcurrentDictionary<Guid, HashSet<(string Table, string Property)>> _analyticsTriggers;
        private readonly ConcurrentDictionary<Guid, DateTime> _lastRunTimes; // Track last run time
        private readonly TimeSpan _minimumRunInterval = TimeSpan.FromSeconds(10); // 10-second minimum interval

        public SmartAnalyticsService(
            IServiceProvider serviceProvider,
            DataOptions options,
            IEventBus eventBus,
            ILogger<SmartAnalyticsService> logger = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger;
            _analyticsTriggers = new ConcurrentDictionary<Guid, HashSet<(string, string)>>();
            _lastRunTimes = new ConcurrentDictionary<Guid, DateTime>();
            _scriptOptions = ScriptOptions.Default
                .WithReferences(typeof(List<>).Assembly, typeof(System.Linq.Enumerable).Assembly, typeof(SmartAnalyticsService).Assembly)
                .WithImports("System.Collections.Generic", "System.Linq", "SmartData.AnalyticsService");
            _sqlCompiler = new SqlServerCompiler();

            // Subscribe to entity change events
            _eventBus.Subscribe(async changeEvent => await HandleEntityChangeAsync(changeEvent));
        }

        private async Task HandleEntityChangeAsync(EntityChangeEvent changeEvent)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
                var analytics = await dbContext.Set<Analytics>()
                    .Where(a => a.Interval < 0)
                    .ToListAsync();

                foreach (var analytic in analytics)
                {
                    // Check if enough time has elapsed since last run
                    if (_lastRunTimes.TryGetValue(analytic.Id, out var lastRun) &&
                        (DateTime.UtcNow - lastRun) < _minimumRunInterval)
                    {
                        _logger?.LogDebug("Skipping analytics {AnalyticsName} due to minimum run interval not met", analytic.Name);
                        continue;
                    }

                    if (_analyticsTriggers.TryGetValue(analytic.Id, out var triggers))
                    {
                        bool shouldRun = changeEvent.Operation == EntityOperation.Update
                            ? triggers.Any(t => t.Table.Equals(changeEvent.TableName, StringComparison.OrdinalIgnoreCase) &&
                                               changeEvent.ChangedProperties.ContainsKey(t.Property))
                            : triggers.Any(t => t.Table.Equals(changeEvent.TableName, StringComparison.OrdinalIgnoreCase));

                        if (shouldRun)
                        {
                            try
                            {
                                var oldValue = analytic.Value;
                                analytic.Value = await RunAnalyticsAsync(analytic, dbContext, CancellationToken.None);
                                analytic.LastRun = DateTime.UtcNow;
                                analytic.Status = "OK"; // Set status to OK on success
                                _lastRunTimes[analytic.Id] = DateTime.UtcNow;

                                await dbContext.SaveChangesAsync();

                                if (_options.EnableChangeTracking && oldValue != analytic.Value)
                                {
                                    await dbContext.AddAsync(new ChangeLogRecord
                                    {
                                        Id = Guid.NewGuid(),
                                        TableName = "sysAnalytics",
                                        EntityId = analytic.Id.ToString(),
                                        ChangedBy = "System",
                                        ChangedAt = DateTime.UtcNow,
                                        OriginalValue = oldValue,
                                        NewValue = analytic.Value,
                                        ChangeType = "Update",
                                        PropertyName = "Value"
                                    });
                                    await dbContext.SaveChangesAsync();
                                }
                                _logger?.LogInformation("Triggered analytics {AnalyticsName} due to {Operation} on {TableName}",
                                    analytic.Name, changeEvent.Operation, changeEvent.TableName);
                            }
                            catch (Exception ex)
                            {
                                analytic.Status = $"Runtime Error: {ex.Message}";
                                await dbContext.SaveChangesAsync();
                                _logger?.LogError(ex, "Error running analytics {AnalyticsName}", analytic.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling entity change event for {TableName}", changeEvent.TableName);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.EnableCalculations) return;

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytics = await dbContext.Set<Analytics>().ToListAsync(stoppingToken);
            foreach (var analytic in analytics)
            {
                await UpdateAnalyticsTriggersAsync(analytic.Id, dbContext);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var timeBasedAnalytics = await dbContext.Set<Analytics>()
                        .Where(a => a.Interval > 0)
                        .ToListAsync(stoppingToken);

                    foreach (var analytic in timeBasedAnalytics)
                    {
                        if (await ShouldRunAsync(analytic, dbContext, stoppingToken))
                        {
                            try
                            {
                                var oldValue = analytic.Value;
                                analytic.Value = await RunAnalyticsAsync(analytic, dbContext, stoppingToken);
                                analytic.LastRun = DateTime.UtcNow;
                                analytic.Status = "OK";
                                _lastRunTimes[analytic.Id] = DateTime.UtcNow;

                                await dbContext.SaveChangesAsync(stoppingToken);

                                if (_options.EnableChangeTracking && oldValue != analytic.Value)
                                {
                                    await dbContext.AddAsync(new ChangeLogRecord
                                    {
                                        Id = Guid.NewGuid(),
                                        TableName = "sysAnalytics",
                                        EntityId = analytic.Id.ToString(),
                                        ChangedBy = "System",
                                        ChangedAt = DateTime.UtcNow,
                                        OriginalValue = oldValue,
                                        NewValue = analytic.Value,
                                        ChangeType = "Update",
                                        PropertyName = "Value"
                                    });
                                    await dbContext.SaveChangesAsync(stoppingToken);
                                }
                            }
                            catch (Exception ex)
                            {
                                analytic.Status = $"Runtime Error: {ex.Message}";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger?.LogError(ex, "Error running analytics {AnalyticsName}", analytic.Name);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in analytics loop");
                }
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        private async Task<bool> ShouldRunAsync(Analytics analytic, DataContext dbContext, CancellationToken ct)
        {
            if (analytic.Interval > 0)
            {
                if (_lastRunTimes.TryGetValue(analytic.Id, out var lastRun) &&
                    (DateTime.UtcNow - lastRun) < _minimumRunInterval)
                {
                    _logger?.LogDebug("Skipping analytics {AnalyticsName} due to minimum run interval not met", analytic.Name);
                    return false;
                }
                return !analytic.LastRun.HasValue || (DateTime.UtcNow - analytic.LastRun.Value).TotalSeconds >= analytic.Interval;
            }
            return false;
        }

        private async Task UpdateAnalyticsTriggersAsync(Guid analyticId, DataContext dbContext)
        {
            var steps = await dbContext.Set<AnalyticsStep>()
                .Where(s => s.AnalyticsId == analyticId)
                .ToListAsync();
            var triggers = new HashSet<(string Table, string Property)>();

            foreach (var step in steps)
            {
                if (step.Operation == AnalyticsStepType.SqlQuery.ToString() || step.Operation == AnalyticsStepType.Timeseries.ToString())
                {
                    var tablesAndProperties = ExtractTableAndProperties(step.Expression);
                    foreach (var (table, property) in tablesAndProperties)
                    {
                        triggers.Add((table, property));
                    }
                }
            }

            _analyticsTriggers[analyticId] = triggers;
        }

        private List<(string Table, string Property)> ExtractTableAndProperties(string expression)
        {
            var result = new List<(string, string)>();
            try
            {
                if (expression.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    var query = new Query().FromRaw(expression);
                    var compiled = _sqlCompiler.Compile(query);

                    // Define SQL reserved keywords to exclude
                    var sqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER",
                "WHERE", "GROUP", "ORDER", "HAVING", "SET", "WITH", "FROM", "JOIN",
                "INNER", "OUTER", "LEFT", "RIGHT", "FULL", "ON", "AS", "UNION",
                "INTERSECT", "EXCEPT", "INTO", "VALUES"
            };

                    // Match table names after FROM or JOIN, excluding SQL keywords
                    var tableMatches = Regex.Matches(compiled.Sql,
                        @"(?:FROM|JOIN)\s+([a-zA-Z_][a-zA-Z0-9_]*)(?:\s+AS\s+\w+)?",
                        RegexOptions.IgnoreCase);
                    var tables = tableMatches.Cast<Match>()
                        .Select(m => m.Groups[1].Value)
                        .Where(t => !sqlKeywords.Contains(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var table in tables)
                    {
                        var propertyMatches = Regex.Matches(expression,
                            @"\b(?:AVG|SUM|COUNT|MIN|MAX)\s*\(\s*(\w+)\s*\)",
                            RegexOptions.IgnoreCase);
                        var properties = propertyMatches.Cast<Match>()
                            .Select(m => m.Groups[1].Value)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        foreach (var property in properties)
                            result.Add((table, property));
                    }
                }
                else if (expression.Contains(","))
                {
                    var parts = expression.Split(',');
                    if (parts.Length >= 3)
                    {
                        var table = parts[0].Trim();
                        var property = parts[2].Trim();
                        result.Add((table, property));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract tables and properties from expression: {Expression}", expression);
            }
            return result;
        }

        private async Task<string> RunAnalyticsAsync(Analytics analytic, DataContext dbContext, CancellationToken ct)
        {
            var steps = await dbContext.Set<AnalyticsStep>()
                .Where(s => s.AnalyticsId == analytic.Id)
                .OrderBy(s => s.Order)
                .ToListAsync(ct);

            var context = new Dictionary<string, object>();
            var loopCounts = new Dictionary<Guid, int>();
            object lastResult = null;
            int currentStepIndex = 0;

            while (currentStepIndex < steps.Count)
            {
                var step = steps[currentStepIndex];
                ct.ThrowIfCancellationRequested();

                if (step.Operation == AnalyticsStepType.Condition.ToString())
                {
                    loopCounts.TryAdd(step.Id, 0);
                    if (loopCounts[step.Id] >= step.MaxLoop)
                    {
                        _logger?.LogWarning("Max loop count {MaxLoop} reached for Condition step {StepId}", step.MaxLoop, step.Id);
                        currentStepIndex++;
                        continue;
                    }
                }

                var result = await ExecuteStepAsync(step, dbContext, context, ct);
                lastResult = result;

                if (step.Operation == AnalyticsStepType.Condition.ToString())
                {
                    if (result is bool conditionResult && conditionResult && int.TryParse(step.ResultVariable, out var goToStep) && goToStep >= 1 && goToStep <= steps.Count && goToStep - 1 != currentStepIndex)
                    {
                        loopCounts[step.Id]++;
                        currentStepIndex = goToStep - 1;
                        continue;
                    }
                    currentStepIndex++;
                }
                else
                {
                    if (!string.IsNullOrEmpty(step.ResultVariable))
                    {
                        var indexMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                        if (indexMatch.Success)
                        {
                            var arrayName = indexMatch.Groups[1].Value;
                            var index = int.Parse(indexMatch.Groups[2].Value);
                            if (!context.TryGetValue(arrayName, out var arrayObj) || arrayObj is not List<object> array)
                            {
                                array = new List<object>();
                                context[arrayName] = array;
                            }
                            while (array.Count <= index)
                                array.Add(null);
                            array[index] = result;
                        }
                        else
                        {
                            context[step.ResultVariable] = result;
                        }
                    }
                    currentStepIndex++;
                }

                if (currentStepIndex == steps.Count)
                {
                    if (step.Operation == AnalyticsStepType.SqlQuery.ToString())
                    {
                        if (result is List<Dictionary<string, object>> queryResults && queryResults.Any())
                        {
                            var firstRow = queryResults.First();
                            if (!string.IsNullOrEmpty(step.ResultVariable) && firstRow.ContainsKey(step.ResultVariable))
                                return firstRow[step.ResultVariable]?.ToString() ?? string.Empty;
                            return firstRow.Values.FirstOrDefault()?.ToString() ?? string.Empty;
                        }
                        return string.Empty;
                    }
                    else if (step.Operation == AnalyticsStepType.Timeseries.ToString())
                    {
                        if (result is List<TimeseriesResult> timeseriesResults && timeseriesResults.Any())
                        {
                            return timeseriesResults.Last().Value;
                        }
                        return string.Empty;
                    }
                    else if (!string.IsNullOrEmpty(step.ResultVariable))
                    {
                        var indexMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                        if (indexMatch.Success)
                        {
                            var arrayName = indexMatch.Groups[1].Value;
                            var index = int.Parse(indexMatch.Groups[2].Value);
                            if (context.TryGetValue(arrayName, out var arrayObj) && arrayObj is List<object> array && index < array.Count)
                                return array[index]?.ToString() ?? string.Empty;
                        }
                        else if (context.TryGetValue(step.ResultVariable, out var finalResult))
                        {
                            return finalResult?.ToString() ?? string.Empty;
                        }
                    }
                }
            }

            return lastResult?.ToString() ?? string.Empty;
        }

        private async Task<object> ExecuteStepAsync(AnalyticsStep step, DataContext dbContext, Dictionary<string, object> context, CancellationToken ct)
        {
            var stepType = Enum.Parse<AnalyticsStepType>(step.Operation);
            var (expression, parameters) = stepType == AnalyticsStepType.SqlQuery || stepType == AnalyticsStepType.Timeseries
                ? ReplaceVariables(step.Expression, context)
                : (step.Expression, new List<object>());

            try
            {
                switch (stepType)
                {
                    case AnalyticsStepType.SqlQuery:
                        var typedParameters = System.Linq.Enumerable.Select<object, object>(parameters, p => p switch
                        {
                            double d => d,
                            int i => i,
                            string s => s,
                            _ => throw new InvalidOperationException($"Unsupported parameter type {p?.GetType()?.Name} for SQL query.")
                        }).ToArray();
                        var results = await dbContext.ExecuteSqlQueryAsync(expression, typedParameters);
                        return System.Linq.Enumerable.Select<QueryResult, Dictionary<string, object>>(results, r => r.Data).ToList();

                    case AnalyticsStepType.Timeseries:
                        var timeseriesParams = ParseTimeseriesExpression(expression, parameters);
                        List<TimeseriesResult> timeseriesResults;
                        if (timeseriesParams.InterpolationMethod == InterpolationMethod.None)
                        {
                            timeseriesResults = await dbContext.GetTimeseriesAsync(
                                timeseriesParams.TableName, timeseriesParams.EntityId, timeseriesParams.PropertyName,
                                timeseriesParams.Start, timeseriesParams.End);
                        }
                        else
                        {
                            timeseriesResults = await dbContext.GetInterpolatedTimeseriesAsync(
                                timeseriesParams.TableName, timeseriesParams.EntityId, timeseriesParams.PropertyName,
                                timeseriesParams.Start, timeseriesParams.End, timeseriesParams.Interval, timeseriesParams.InterpolationMethod);
                        }
                        return timeseriesResults;

                    case AnalyticsStepType.CSharp:
                    case AnalyticsStepType.Variable:
                        return await ExecuteCSharpAsync(expression, context);

                    case AnalyticsStepType.Condition:
                        var conditionResult = await ExecuteCSharpAsync(expression, context);
                        if (conditionResult is not bool)
                            throw new InvalidOperationException($"Condition step {step.Id} must return a boolean value.");
                        return conditionResult;

                    default:
                        throw new InvalidOperationException($"Unsupported step type: {stepType}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing step {StepId} of type {StepType}", step.Id, stepType);
                throw;
            }
        }

        private (string TableName, string EntityId, string PropertyName, DateTime Start, DateTime End, TimeSpan Interval, InterpolationMethod InterpolationMethod)
            ParseTimeseriesExpression(string expression, List<object> parameters)
        {
            var parts = expression.Split(',');
            if (parts.Length < 5 || parts.Length > 7)
                throw new InvalidOperationException("Timeseries expression must have 5 to 7 components: tableName,entityId,propertyName,start,end[,interval,method]");

            int paramIndex = 0;
            var tableName = ReplaceParameter(parts[0], parameters, ref paramIndex);
            var entityId = ReplaceParameter(parts[1], parameters, ref paramIndex);
            var propertyName = ReplaceParameter(parts[2], parameters, ref paramIndex);

            if (!DateTime.TryParse(ReplaceParameter(parts[3], parameters, ref paramIndex), out var start))
                throw new InvalidOperationException("Invalid start date format.");

            if (!DateTime.TryParse(ReplaceParameter(parts[4], parameters, ref paramIndex), out var end))
                throw new InvalidOperationException("Invalid end date format.");

            TimeSpan interval = TimeSpan.FromSeconds(1);
            InterpolationMethod method = InterpolationMethod.None;

            if (parts.Length > 5)
            {
                if (!TimeSpan.TryParse(ReplaceParameter(parts[5], parameters, ref paramIndex), out interval))
                    throw new InvalidOperationException("Invalid interval format.");
            }

            if (parts.Length > 6)
            {
                var methodStr = ReplaceParameter(parts[6], parameters, ref paramIndex);
                if (!Enum.TryParse<InterpolationMethod>(methodStr, true, out method))
                    throw new InvalidOperationException($"Invalid interpolation method: {methodStr}");
            }

            return (tableName, entityId, propertyName, start, end, interval, method);
        }

        private string ReplaceParameter(string part, List<object> parameters, ref int paramIndex)
        {
            if (Regex.IsMatch(part, @"^@p\d+$"))
            {
                if (paramIndex >= parameters.Count)
                    throw new InvalidOperationException($"Missing parameter for {part}");
                return parameters[paramIndex++].ToString();
            }
            return part.Trim();
        }

        private async Task<object> ExecuteCSharpAsync(string script, Dictionary<string, object> context)
        {
            try
            {
                _logger?.LogDebug("Compiling C# script: {Script}, Context keys: {Keys}",
                    script, string.Join(", ", context.Keys));
                var compilation = CSharpScript.Create(script, _scriptOptions, typeof(ScriptGlobals));
                var diagnostics = compilation.GetCompilation().GetDiagnostics();
                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var errorMessages = string.Join("; ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                    _logger?.LogError("C# script compilation failed for script: {Script}. Errors: {Errors}", script, errorMessages);
                    throw new InvalidOperationException($"C# script compilation failed: {errorMessages}");
                }
                var globals = new ScriptGlobals { Context = context };
                var scriptState = await compilation.RunAsync(globals);
                return scriptState.ReturnValue;
            }
            catch (CompilationErrorException ex)
            {
                var errorMessages = string.Join("; ", ex.Diagnostics.Select(d => d.ToString()));
                _logger?.LogError(ex, "C# script compilation error for script: {Script}. Errors: {Errors}", script, errorMessages);
                throw new InvalidOperationException($"C# script compilation failed: {errorMessages}", ex);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error executing C# script: {Script}", script);
                throw new InvalidOperationException($"Unexpected error executing C# script: {ex.Message}", ex);
            }
        }

        private (string Expression, List<object> Parameters) ReplaceVariables(string expression, Dictionary<string, object> context)
        {
            var parameters = new List<object>();
            var parameterIndex = 0;

            var result = Regex.Replace(expression, @"\{([^{}]+)\}", m =>
            {
                var varName = m.Groups[1].Value;
                if (context.TryGetValue(varName, out var value))
                {
                    parameters.Add(value);
                    return $"@p{parameterIndex++}";
                }
                return string.Empty;
            });

            return (result, parameters);
        }

        private bool ValidateSqlQuery(string sqlQuery, out string error)
        {
            try
            {
                if (Regex.IsMatch(sqlQuery, @"\b(INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|SET|WITH)\b", RegexOptions.IgnoreCase))
                {
                    error = "Only SELECT queries are allowed.";
                    return false;
                }

                var query = new Query().FromRaw(sqlQuery);
                var compiled = _sqlCompiler.Compile(query);
                if (!compiled.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Only SELECT queries are allowed.";
                    return false;
                }
                if (sqlQuery.Contains(";") || sqlQuery.Contains("EXEC", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Multi-statement queries or stored procedures are not allowed.";
                    return false;
                }

                //// Simulate execution to check for single-value output in the last step
                //if (/* is last step */)
                //{
                //    using var scope = _serviceProvider.CreateScope();
                //    var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
                //    var results = await dbContext.ExecuteSqlQueryAsync(sqlQuery, new object[] { });
                //    if (results.Count > 1 || (results.Any() && results.First().Data.Count > 1))
                //    {
                //        error = "Last SQL query step must return a single value.";
                //        return false;
                //    }
                //}

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid SQL query: {ex.Message}";
                return false;
            }
        }

        private bool ValidateTimeseriesExpression(string expression, out string error)
        {
            try
            {
                var parts = expression.Split(',');
                if (parts.Length < 5 || parts.Length > 7)
                {
                    error = "Timeseries expression must have 5 to 7 components: tableName,entityId,propertyName,start,end[,interval,method]";
                    return false;
                }

                if (parts.Length > 5 && !TimeSpan.TryParse(parts[5], out _))
                {
                    error = "Invalid interval format in timeseries expression.";
                    return false;
                }

                if (parts.Length > 6 && !Enum.TryParse<InterpolationMethod>(parts[6], true, out _))
                {
                    error = "Invalid interpolation method in timeseries expression.";
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid timeseries expression: {ex.Message}";
                return false;
            }
        }

        private bool ValidateCSharpScript(string script, out string error)
        {
            if (string.IsNullOrEmpty(script))
            {
                error = "Script cannot be null or empty.";
                return false;
            }
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(script);
                var root = syntaxTree.GetRoot();
                var dangerousNamespaces = new[] { "System.IO", "System.Net", "System.Reflection", "System.Threading", "System.Diagnostics" };
                var hasDangerousCalls = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
                    .Any(n => dangerousNamespaces.Any(ns => n.ToString().StartsWith(ns, StringComparison.OrdinalIgnoreCase)));

                if (hasDangerousCalls)
                {
                    error = "Script contains prohibited namespace usage (e.g., System.IO, System.Net).";
                    return false;
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid C# script: {ex.Message}";
                return false;
            }
        }

        public async Task AddAnalyticsAsync(AnalyticsConfig config, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();

            if (await dbContext.Set<Analytics>().AnyAsync(c => c.Name == config.Name, ct))
                throw new InvalidOperationException($"Analytics {config.Name} exists.");

            var (isValid, errors) = await VerifyAnalyticsAsync(config.Id == Guid.Empty ? Guid.NewGuid() : config.Id, config, ct);
            if (!isValid)
            {
                var errorMessage = $"Validation Failed: {string.Join("; ", errors)}";
                throw new InvalidOperationException(errorMessage);
            }

            var analytic = new Analytics
            {
                Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id,
                Name = config.Name,
                Interval = config.Interval,
                Embeddable = config.Embeddable,
                Value = "0",
                Status = "OK"
            };
            await dbContext.AddAsync(analytic, ct);

            for (int i = 0; i < config.Steps.Count; i++)
            {
                var step = config.Steps[i];
                await dbContext.AddAsync(new AnalyticsStep
                {
                    Id = Guid.NewGuid(),
                    AnalyticsId = analytic.Id,
                    Order = i + 1,
                    Operation = step.Type.ToString(),
                    Expression = step.Config,
                    ResultVariable = step.OutputVariable,
                    MaxLoop = step.Type == AnalyticsStepType.Condition ? step.MaxLoop : 10
                }, ct);
            }

            await dbContext.SaveChangesAsync(ct);
            if (config.Interval < 0)
                await UpdateAnalyticsTriggersAsync(analytic.Id, dbContext);
        }

        public async Task DeleteAnalyticsAsync(Guid analyticId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytic = await dbContext.Set<Analytics>().FirstOrDefaultAsync(c => c.Id == analyticId, ct);
            if (analytic == null)
                throw new InvalidOperationException($"Analytics {analyticId} does not exist.");

            dbContext.Remove(analytic);
            await dbContext.SaveChangesAsync(ct);
            _analyticsTriggers.TryRemove(analyticId, out _);
            _lastRunTimes.TryRemove(analyticId, out _);
        }


        //private bool ValidateCSharpScript(string script, out string error, HashSet<string> availableVariables)
        //{
        //    try
        //    {
        //        var syntaxTree = CSharpSyntaxTree.ParseText(script);
        //        var root = syntaxTree.GetRoot();
        //        var dangerousNamespaces = new[] { "System.IO", "System.Net", "System.Reflection", "System.Threading", "System.Diagnostics" };
        //        var hasDangerousCalls = root.DescendantNodes()
        //            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
        //            .Any(n => dangerousNamespaces.Any(ns => n.ToString().StartsWith(ns, StringComparison.OrdinalIgnoreCase)));

        //        if (hasDangerousCalls)
        //        {
        //            error = "Script contains prohibited namespace usage (e.g., System.IO, System.Net).";
        //            return false;
        //        }

        //        // Check for variable references in the script
        //        var variableReferences = root.DescendantNodes()
        //            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>()
        //            .Select(n => n.Identifier.Text)
        //            .Where(n => n != "context" && !n.StartsWith("@") && availableVariables != null && !availableVariables.Contains(n))
        //            .Distinct()
        //            .ToList();

        //        if (variableReferences.Any())
        //        {
        //            error = $"Script references undefined variables: {string.Join(", ", variableReferences)}.";
        //            return false;
        //        }

        //        error = null;
        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        error = $"Invalid C# script: {ex.Message}";
        //        return false;
        //    }
        //}

        //public async Task<(bool IsValid, List<string> Errors)> VerifyAnalyticsAsync(Guid analyticId, AnalyticsConfig config = null, CancellationToken ct = default)
        //{
        //    var errors = new List<string>();
        //    using var scope = _serviceProvider.CreateScope();
        //    var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();

        //    Analytics analytic = null;
        //    List<AnalyticsStep> steps;

        //    if (config == null)
        //    {
        //        analytic = await dbContext.Set<Analytics>().FirstOrDefaultAsync(c => c.Id == analyticId, ct);
        //        if (analytic == null)
        //        {
        //            errors.Add($"Analytics {analyticId} does not exist.");
        //            return (false, errors);
        //        }
        //        steps = await dbContext.Set<AnalyticsStep>()
        //            .Where(s => s.AnalyticsId == analyticId)
        //            .OrderBy(s => s.Order)
        //            .ToListAsync(ct);
        //    }
        //    else
        //    {
        //        analytic = new Analytics { Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id };
        //        steps = config.Steps.Select((step, i) => new AnalyticsStep
        //        {
        //            Id = Guid.NewGuid(),
        //            AnalyticsId = analytic.Id,
        //            Order = i + 1,
        //            Operation = step.Type.ToString(),
        //            Expression = step.Config,
        //            ResultVariable = step.OutputVariable,
        //            MaxLoop = step.Type == AnalyticsStepType.Condition ? step.MaxLoop : 10
        //        }).ToList();
        //    }

        //    if (!steps.Any())
        //    {
        //        errors.Add("Analytics must have at least one step.");
        //        if (config == null)
        //        {
        //            analytic.Status = $"Validation Failed: Analytics must have at least one step.";
        //            await dbContext.SaveChangesAsync(ct);
        //        }
        //        return (false, errors);
        //    }

        //    var tableNames = GetTables(dbContext);
        //    var variables = new HashSet<string>();
        //    var reachableSteps = new HashSet<int>();
        //    var simulatedContext = new Dictionary<string, object>();

        //    for (int i = 0; i < steps.Count; i++)
        //    {
        //        var step = steps[i];
        //        reachableSteps.Add(i + 1);

        //        if (step.Order != i + 1)
        //            errors.Add($"Step {i + 1} has incorrect order {step.Order}.");

        //        if (!Enum.TryParse<AnalyticsStepType>(step.Operation, true, out var stepType))
        //            errors.Add($"Step {step.Order}: Invalid step type {step.Operation}.");

        //        var matches = Regex.Matches(step.Expression, @"\{([^{}]+)\}");
        //        foreach (Match match in matches)
        //        {
        //            var varName = match.Groups[1].Value;
        //            if (!variables.Contains(varName) && i > 0)
        //                errors.Add($"Step {step.Order}: Unknown variable {varName}.");
        //        }

        //        if (!string.IsNullOrEmpty(step.ResultVariable) && stepType != AnalyticsStepType.Condition)
        //        {
        //            var indexMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
        //            if (indexMatch.Success)
        //            {
        //                var arrayName = indexMatch.Groups[1].Value;
        //                if (i > 0 && !variables.Contains(arrayName))
        //                    errors.Add($"Step {step.Order}: Array {arrayName} must be defined before index access.");
        //            }
        //        }

        //        switch (stepType)
        //        {
        //            case AnalyticsStepType.SqlQuery:
        //                if (!ValidateSqlQuery(step.Expression, out var sqlError))
        //                    errors.Add($"Step {step.Order}: {sqlError}");
        //                var tablesAndProperties = ExtractTableAndProperties(step.Expression);
        //                foreach (var (table, property) in tablesAndProperties)
        //                {
        //                    if (!tableNames.Contains(table))
        //                        errors.Add($"Step {step.Order}: Invalid table reference {table}.");
        //                    var entityType = dbContext.Model.GetEntityTypes()
        //                        .FirstOrDefault(t => t.GetTableName().Equals(table, StringComparison.OrdinalIgnoreCase))
        //                        ?.ClrType;
        //                    if (entityType != null)
        //                    {
        //                        var prop = entityType.GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        //                        if (prop == null)
        //                            errors.Add($"Step {step.Order}: Property {property} does not exist on table {table}.");
        //                    }
        //                }
        //                if (i == steps.Count - 1 && !Regex.IsMatch(step.Expression, @"SELECT\s+(AVG|SUM|COUNT|MIN|MAX)\s*\(", RegexOptions.IgnoreCase))
        //                    errors.Add($"Step {step.Order}: Last SqlQuery step must return a single value (e.g., use AVG, SUM, COUNT, MAX).");
        //                break;

        //            case AnalyticsStepType.Timeseries:
        //                if (!ValidateTimeseriesExpression(step.Expression, out var timeseriesError))
        //                    errors.Add($"Step {step.Order}: {timeseriesError}");
        //                var timeseriesTables = ExtractTableAndProperties(step.Expression);
        //                foreach (var (table, _) in timeseriesTables)
        //                    if (!tableNames.Contains(table))
        //                        errors.Add($"Step {step.Order}: Invalid table reference {table} in timeseries expression.");
        //                if (i == steps.Count - 1 && string.IsNullOrEmpty(step.ResultVariable))
        //                    errors.Add($"Step {step.Order}: Last Timeseries step must have a ResultVariable.");
        //                break;

        //            case AnalyticsStepType.CSharp:
        //            case AnalyticsStepType.Variable:
        //                if (!ValidateCSharpScript(step.Expression, out var scriptError, variables))
        //                    errors.Add($"Step {step.Order}: {scriptError}");
        //                else
        //                {
        //                    try
        //                    {
        //                        // Initialize context with dummy values for known variables to prevent null references
        //                        var tempContext = new Dictionary<string, object>(simulatedContext);
        //                        foreach (var varName in variables)
        //                        {
        //                            if (!tempContext.ContainsKey(varName))
        //                            {
        //                                // Assume scalar or list based on ResultVariable pattern
        //                                var indexMatch = Regex.Match(step.ResultVariable ?? "", @"^(\w+)\[\d+\]$");
        //                                if (indexMatch.Success && indexMatch.Groups[1].Value == varName)
        //                                    tempContext[varName] = new List<object> { 0 }; // Initialize as list
        //                                else
        //                                    tempContext[varName] = 0; // Initialize as scalar
        //                            }
        //                        }
        //                        var globals = new ScriptGlobals { Context = tempContext };
        //                        _logger?.LogDebug("Evaluating C# script for step {StepOrder}: {Script}, Context: {ContextKeys}", step.Order, step.Expression, string.Join(", ", tempContext.Keys));
        //                        var scriptState = await CSharpScript.EvaluateAsync(step.Expression, _scriptOptions, globals);
        //                        _logger?.LogDebug("C# script evaluation for step {StepOrder} returned: {Result}", step.Order, scriptState?.ToString() ?? "null");
        //                    }
        //                    catch (CompilationErrorException ex)
        //                    {
        //                        errors.Add($"Step {step.Order}: Invalid {stepType} script: {ex.Message}");
        //                    }
        //                    catch (Exception ex)
        //                    {
        //                        errors.Add($"Step {step.Order}: Script execution failed: {ex.Message}");
        //                    }
        //                }
        //                if (i == steps.Count - 1 && string.IsNullOrEmpty(step.ResultVariable))
        //                    errors.Add($"Step {step.Order}: Last {stepType} step must have a ResultVariable.");
        //                break;

        //            case AnalyticsStepType.Condition:
        //                if (!ValidateCSharpScript(step.Expression, out var conditionScriptError, variables))
        //                    errors.Add($"Step {step.Order}: {conditionScriptError}");
        //                else
        //                {
        //                    try
        //                    {
        //                        var tempContext = new Dictionary<string, object>(simulatedContext);
        //                        foreach (var varName in variables)
        //                        {
        //                            if (!tempContext.ContainsKey(varName))
        //                            {
        //                                tempContext[varName] = 0; // Initialize with default value
        //                            }
        //                        }
        //                        var globals = new ScriptGlobals { Context = tempContext };
        //                        var scriptState = await CSharpScript.EvaluateAsync(step.Expression, _scriptOptions, globals);
        //                        if (scriptState is not bool)
        //                            errors.Add($"Step {step.Order}: Condition script must return a boolean value.");
        //                    }
        //                    catch (CompilationErrorException ex)
        //                    {
        //                        errors.Add($"Step {step.Order}: Invalid condition script: {ex.Message}");
        //                    }
        //                    catch (Exception ex)
        //                    {
        //                        errors.Add($"Step {step.Order}: Condition script execution failed: {ex.Message}");
        //                    }
        //                }
        //                if (!int.TryParse(step.ResultVariable, out var goToStep) || goToStep < 1 || goToStep > steps.Count || goToStep == i + 1)
        //                    errors.Add($"Step {step.Order}: Invalid GoTo step number {step.ResultVariable}. Must be between 1 and {steps.Count}, not current step.");
        //                else
        //                    reachableSteps.Add(goToStep);
        //                if (step.MaxLoop <= 0)
        //                    errors.Add($"Step {step.Order}: MaxLoop must be positive.");
        //                break;
        //        }

        //        if (stepType != AnalyticsStepType.Condition && !string.IsNullOrEmpty(step.ResultVariable))
        //        {
        //            var indexMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
        //            var varName = indexMatch.Success ? indexMatch.Groups[1].Value : step.ResultVariable;
        //            variables.Add(varName);
        //            if (!simulatedContext.ContainsKey(varName))
        //            {
        //                if (indexMatch.Success)
        //                    simulatedContext[varName] = new List<object> { 0 }; // Initialize as list for array access
        //                else
        //                    simulatedContext[varName] = 0; // Initialize with default scalar value
        //            }
        //        }
        //    }

        //    for (int i = 1; i <= steps.Count; i++)
        //    {
        //        if (!reachableSteps.Contains(i))
        //            errors.Add($"Step {i}: Unreachable due to loop configuration.");
        //    }

        //    if (errors.Any() && config == null)
        //    {
        //        analytic.Status = $"Validation Failed: {string.Join("; ", errors)}";
        //        await dbContext.SaveChangesAsync(ct);
        //    }
        //    else if (config == null)
        //    {
        //        analytic.Status = "OK";
        //        await dbContext.SaveChangesAsync(ct);
        //    }

        //    return (errors.Count == 0, errors);
        //}

        public async Task<(bool IsValid, List<string> Errors)> VerifyAnalyticsAsync(Guid analyticId, AnalyticsConfig config = null, CancellationToken ct = default)
        {
            var errors = new List<string>();
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();

            Analytics analytic = null;
            List<AnalyticsStep> steps;

            if (config == null)
            {
                analytic = await dbContext.Set<Analytics>().FirstOrDefaultAsync(c => c.Id == analyticId, ct);
                if (analytic == null)
                {
                    errors.Add($"Analytics {analyticId} does not exist.");
                    return (false, errors);
                }
                steps = await dbContext.Set<AnalyticsStep>()
                    .Where(s => s.AnalyticsId == analyticId)
                    .OrderBy(s => s.Order)
                    .ToListAsync(ct);
            }
            else
            {
                analytic = new Analytics { Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id };
                steps = config.Steps.Select((step, i) => new AnalyticsStep
                {
                    Id = Guid.NewGuid(),
                    AnalyticsId = analytic.Id,
                    Order = i + 1,
                    Operation = step.Type.ToString(),
                    Expression = step.Config,
                    ResultVariable = step.OutputVariable,
                    MaxLoop = step.Type == AnalyticsStepType.Condition ? step.MaxLoop : 10
                }).ToList();
            }

            if (!steps.Any())
            {
                errors.Add("Analytics must have at least one step.");
                if (config == null)
                {
                    analytic.Status = "Validation Failed: Analytics must have at least one step.";
                    await dbContext.SaveChangesAsync(ct);
                }
                return (false, errors);
            }

            var tableNames = GetTables(dbContext);
            var variables = new HashSet<string>();
            var reachableSteps = new HashSet<int>();
            var simulatedContext = new Dictionary<string, object>();

            for (int i = 0; i < steps.Count; i++)
            {
                reachableSteps.Add(i + 1);
                var step = steps[i];

                if (step.Order != i + 1)
                    errors.Add($"Step {step.Order} ({step.Operation}): Incorrect order {step.Order}. Expected {i + 1}.");

                if (!Enum.TryParse<AnalyticsStepType>(step.Operation, true, out var stepType))
                    errors.Add($"Step {step.Order} ({step.Operation}): Invalid step type {step.Operation}.");

                if (string.IsNullOrEmpty(step.Expression))
                {
                    errors.Add($"Step {step.Order} ({step.Operation}): Expression cannot be null or empty.");
                    continue;
                }

                var matches = Regex.Matches(step.Expression, @"\{([^{}]+)\}");
                if (stepType == AnalyticsStepType.CSharp || stepType == AnalyticsStepType.Condition)
                {
                    foreach (Match match in matches)
                    {
                        var varName = match.Groups[1].Value;
                        if (!variables.Contains(varName) && i > 0)
                            errors.Add($"Step {step.Order} ({step.Operation}): Unknown variable {varName} in expression '{step.Expression}'. Ensure it is defined in a previous step or provided at runtime.");
                    }
                }

                if (!string.IsNullOrEmpty(step.ResultVariable) && stepType != AnalyticsStepType.Condition)
                {
                    var indexMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                    if (indexMatch.Success)
                    {
                        var arrayName = indexMatch.Groups[1].Value;
                        if (i > 0 && !variables.Contains(arrayName))
                            errors.Add($"Step {step.Order} ({step.Operation}): Array {arrayName} must be defined before index access in expression '{step.Expression}'.");
                    }
                }

                switch (stepType)
                {
                    case AnalyticsStepType.SqlQuery:
                        if (!ValidateSqlQuery(step.Expression, out var sqlError))
                            errors.Add($"Step {step.Order} ({step.Operation}): {sqlError} in expression '{step.Expression}'");
                        var tablesAndProperties = ExtractTableAndProperties(step.Expression);
                        foreach (var (table, property) in tablesAndProperties)
                        {
                            if (!tableNames.Contains(table))
                                errors.Add($"Step {step.Order} ({step.Operation}): Invalid table reference {table} in expression '{step.Expression}'.");
                            var entityType = dbContext.Model.GetEntityTypes()
                                .FirstOrDefault(t => t.GetTableName().Equals(table, StringComparison.OrdinalIgnoreCase))
                                ?.ClrType;
                            if (entityType != null)
                            {
                                var prop = entityType.GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                if (prop == null)
                                    errors.Add($"Step {step.Order} ({step.Operation}): Property {property} does not exist on table {table} in expression '{step.Expression}'.");
                            }
                        }
                        break;

                    case AnalyticsStepType.Timeseries:
                        if (!ValidateTimeseriesExpression(step.Expression, out var timeseriesError))
                            errors.Add($"Step {step.Order} ({step.Operation}): {timeseriesError} in expression '{step.Expression}'");
                        var timeseriesTables = ExtractTableAndProperties(step.Expression);
                        foreach (var (table, _) in timeseriesTables)
                            if (!tableNames.Contains(table))
                                errors.Add($"Step {step.Order} ({step.Operation}): Invalid table reference {table} in expression '{step.Expression}'.");
                        if (i == steps.Count - 1 && string.IsNullOrEmpty(step.ResultVariable))
                            errors.Add($"Step {step.Order} ({step.Operation}): Last Timeseries step must have a ResultVariable in expression '{step.Expression}'.");
                        break;

                    case AnalyticsStepType.CSharp:
                    case AnalyticsStepType.Variable:
                        if (!ValidateCSharpScript(step.Expression, out var scriptError))
                            errors.Add($"Step {step.Order} ({step.Operation}): {scriptError} in expression '{step.Expression}'");
                        else
                        {
                            try
                            {
                                var syntaxTree = CSharpSyntaxTree.ParseText(step.Expression);
                                var compilation = CSharpScript.Create(step.Expression, _scriptOptions, typeof(ScriptGlobals));
                                var diagnostics = compilation.GetCompilation().GetDiagnostics();
                                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                                {
                                    var errorMessages = string.Join("; ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                                    errors.Add($"Step {step.Order} ({step.Operation}): Invalid {stepType} script: {errorMessages} in expression '{step.Expression}'");
                                }
                                else //if (stepType == AnalyticsStepType.Variable)
                                {
                                    // Simulate execution to determine return type
                                    var globals = new ScriptGlobals
                                    {
                                        Context = new Dictionary<string, object>(simulatedContext)
                                    };
                                    var scriptState = await CSharpScript.EvaluateAsync(step.Expression, _scriptOptions, globals);
                                    if (scriptState != null && !string.IsNullOrEmpty(step.ResultVariable))
                                    {
                                        var varName = Regex.Match(step.ResultVariable, @"^(\w+)\[\d+\]$").Success
                                            ? Regex.Match(step.ResultVariable, @"^(\w+)\[\d+\]$").Groups[1].Value
                                            : step.ResultVariable;
                                        simulatedContext[varName] = scriptState; // Store actual value
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Step {step.Order} ({step.Operation}): {stepType} script validation failed: {ex.Message} in expression '{step.Expression}'");
                            }
                        }
                        if (i == steps.Count - 1 && string.IsNullOrEmpty(step.ResultVariable))
                            errors.Add($"Step {step.Order} ({step.Operation}): Last {stepType} step must have a ResultVariable in expression '{step.Expression}'.");
                        break;

                    case AnalyticsStepType.Condition:
                        if (!ValidateCSharpScript(step.Expression, out var conditionScriptError))
                            errors.Add($"Step {step.Order} ({step.Operation}): {conditionScriptError} in expression '{step.Expression}'");
                        else
                        {
                            try
                            {
                                var syntaxTree = CSharpSyntaxTree.ParseText(step.Expression);
                                var compilation = CSharpScript.Create(step.Expression, _scriptOptions, typeof(ScriptGlobals));
                                var diagnostics = compilation.GetCompilation().GetDiagnostics();
                                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                                {
                                    var errorMessages = string.Join("; ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                                    errors.Add($"Step {step.Order} ({step.Operation}): Invalid condition script: {errorMessages} in expression '{step.Expression}'");
                                }
                                else
                                {
                                    var globals = new ScriptGlobals
                                    {
                                        Context = new Dictionary<string, object>(simulatedContext)
                                    };
                                    try
                                    {
                                        var scriptState = await CSharpScript.EvaluateAsync(step.Expression, _scriptOptions, globals);
                                        if (scriptState is not bool)
                                            errors.Add($"Step {step.Order} ({step.Operation}): Condition script must return a boolean value in expression '{step.Expression}'");
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add($"Step {step.Order} ({step.Operation}): Condition script execution failed: {ex.Message} in expression '{step.Expression}'");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Step {step.Order} ({step.Operation}): Condition script validation failed: {ex.Message} in expression '{step.Expression}'");
                            }
                        }
                        if (!int.TryParse(step.ResultVariable, out var goToStep) || goToStep < 1 || goToStep > steps.Count || goToStep == i + 1)
                            errors.Add($"Step {step.Order} ({step.Operation}): Invalid GoTo step number {step.ResultVariable}. Must be between 1 and {steps.Count}, not current step in expression '{step.Expression}'.");
                        else
                            reachableSteps.Add(goToStep);
                        if (step.MaxLoop <= 0)
                            errors.Add($"Step {step.Order} ({step.Operation}): MaxLoop must be positive in expression '{step.Expression}'.");
                        break;
                }

                if (stepType != AnalyticsStepType.Condition && !string.IsNullOrEmpty(step.ResultVariable))
                {
                    var indexMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                    var varName = indexMatch.Success ? indexMatch.Groups[1].Value : step.ResultVariable;
                    variables.Add(varName);
                    if (!simulatedContext.ContainsKey(varName))
                    {
                        if (indexMatch.Success)
                            simulatedContext[varName] = new List<object>();
                        else if (stepType == AnalyticsStepType.Variable)
                            simulatedContext[varName] = 0; // Default to 0 for Variable steps
                        else
                            simulatedContext[varName] = null;
                    }
                }
            }

            for (int i = 1; i <= steps.Count; i++)
            {
                if (!reachableSteps.Contains(i))
                    errors.Add($"Step {i}: Unreachable due to loop configuration.");
            }

            if (errors.Any() && config == null)
            {
                var errorMessage = $"Validation Failed: {string.Join("; ", errors)}";
                _logger?.LogWarning("Analytics {AnalyticsId} validation failed: {Errors}", analyticId, errorMessage);
                analytic.Status = errorMessage;
                await dbContext.SaveChangesAsync(ct);
            }
            else if (config == null)
            {
                analytic.Status = "OK";
                await dbContext.SaveChangesAsync(ct);
            }

            return (errors.Count == 0, errors);
        }

        private HashSet<string> GetTables(DataContext dbContext)
        {
            var systemTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sysChangeLog",
                "sysEmbeddings",
                "sysTimeseriesBaseValues",
                "sysTimeseriesDeltas",
                "sysIntegrityLog",
                "sysAnalytics",
                "sysAnalyticsSteps"
            };

            var tableNames = dbContext.Model.GetEntityTypes()
                .Select(t => t.GetTableName())
                .Where(t => !systemTables.Contains(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return tableNames;
        }
        public async Task<string> ExportAnalyticsAsync(Guid analyticId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytic = await dbContext.Set<Analytics>().FirstOrDefaultAsync(c => c.Id == analyticId, ct);
            if (analytic == null)
                throw new InvalidOperationException($"Analytics {analyticId} does not exist.");

            var steps = await dbContext.Set<AnalyticsStep>()
                .Where(s => s.AnalyticsId == analyticId)
                .OrderBy(s => s.Order)
                .ToListAsync(ct);

            var export = new AnalyticsConfig
            {
                Id = analytic.Id,
                Name = analytic.Name,
                Interval = analytic.Interval,
                Embeddable = analytic.Embeddable,
                Steps = steps.Select(s => new AnalyticsStepConfig
                {
                    Type = Enum.Parse<AnalyticsStepType>(s.Operation),
                    Config = s.Expression,
                    OutputVariable = s.ResultVariable,
                    MaxLoop = s.MaxLoop
                }).ToList()
            };

            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }

        public async Task ImportAnalyticsAsync(string json, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var config = JsonSerializer.Deserialize<AnalyticsConfig>(json);
            if (config == null || string.IsNullOrEmpty(config.Name))
                throw new InvalidOperationException("Invalid JSON format for analytics import.");

            await AddAnalyticsAsync(config, ct);
        }

        public async Task<string> ExecuteAnalyticsAsync(Guid analyticId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytic = await dbContext.Set<Analytics>().FirstOrDefaultAsync(c => c.Id == analyticId, ct);
            if (analytic == null)
                throw new InvalidOperationException($"Analytics {analyticId} does not exist.");

            try
            {
                if (_lastRunTimes.TryGetValue(analytic.Id, out var lastRun) &&
                    (DateTime.UtcNow - lastRun) < _minimumRunInterval)
                {
                    _logger?.LogDebug("Skipping analytics {AnalyticsName} due to minimum run interval not met", analytic.Name);
                    return analytic.Value;
                }

                analytic.Value = await RunAnalyticsAsync(analytic, dbContext, ct);
                analytic.LastRun = DateTime.UtcNow;
                analytic.Status = "OK";
                _lastRunTimes[analytic.Id] = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(ct);
                return analytic.Value;
            }
            catch (Exception ex)
            {
                analytic.Status = $"Runtime Error: {ex.Message}";
                await dbContext.SaveChangesAsync(ct);
                _logger?.LogError(ex, "Error executing analytics {AnalyticsName}", analytic.Name);
                throw;
            }
        }
    }
}