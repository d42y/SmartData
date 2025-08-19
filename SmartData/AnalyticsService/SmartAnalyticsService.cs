using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlKata;
using SqlKata.Compilers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using SmartData.Core;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.AnalyticsService
{
    public class ScriptGlobals
    {
        public Dictionary<string, object> Context { get; set; } = new();
        // alias for convenience in scripts
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
        public string Config { get; set; } = string.Empty;
        public string OutputVariable { get; set; } = string.Empty;
        public int MaxLoop { get; set; } = 10;
    }

    public class AnalyticsConfig
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Interval { get; set; }
        public List<AnalyticsStepConfig> Steps { get; set; } = new();
    }

    /// <summary>
    /// SmartAnalyticsService:
    /// - Timer loop for Interval > 0 analytics
    /// - ChangeLog-trigger loop driven by explicit triggers in __sysAnalyticsTriggers
    /// - Idle/backoff if nothing is configured
    /// </summary>
    public class SmartAnalyticsService : BackgroundService
    {
        public readonly record struct ExecResult(string Final, IReadOnlyDictionary<string, object> Vars);

        private readonly IServiceProvider _serviceProvider;
        private readonly DataOptions _options;
        private readonly ILogger<SmartAnalyticsService>? _logger;

        private readonly ScriptOptions _scriptOptions;
        private readonly Compiler _sqlCompiler;

        // Per-analytic guard to avoid hyper-frequency
        private readonly ConcurrentDictionary<Guid, DateTime> _lastRunTimes = new();
        private readonly TimeSpan _minimumRunInterval = TimeSpan.FromSeconds(10);

        // ChangeLog polling watermark (per analytic)
        private readonly ConcurrentDictionary<Guid, DateTime> _clWatermark = new();
        private readonly TimeSpan _changePollInterval = TimeSpan.FromSeconds(3);

        // Global DB concurrency gate to avoid pool exhaustion
        private static readonly int _maxConcurrentDb = Math.Max(4, Environment.ProcessorCount);
        private static readonly SemaphoreSlim _dbGate = new(_maxConcurrentDb, _maxConcurrentDb);

        private static readonly ConcurrentDictionary<(Guid StepId, string Expr), ScriptRunner<object?>> _scriptCache = new();

        public SmartAnalyticsService(
            IServiceProvider serviceProvider,
            DataOptions options,
            ILogger<SmartAnalyticsService>? logger = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;

            _scriptOptions = ScriptOptions.Default
                .WithReferences(typeof(List<>).Assembly,
                                typeof(Enumerable).Assembly,
                                typeof(SmartAnalyticsService).Assembly)
                .WithImports("System",
                             "System.Collections.Generic",
                             "System.Linq",
                             "SmartData.AnalyticsService");

            _sqlCompiler = new SqlServerCompiler();
        }

        // ======================= Main =======================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.EnableAnalytics) return;

            var timerLoop = TimerLoopAsync(stoppingToken);
            var changeLoop = ChangeLogLoopAsync(stoppingToken);
            await Task.WhenAll(timerLoop, changeLoop);
        }

        // ======================= Timer analytics loop =======================
        private async Task TimerLoopAsync(CancellationToken ct)
        {
            int idleBackoffSeconds = 5;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _dbGate.WaitAsync(ct);
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<DataContext>();

                        // Early check to idle when nothing exists
                        bool anyTimer = await db.Set<Analytics>()
                                                .AsNoTracking()
                                                .AnyAsync(a => a.Interval > 0, ct);
                        if (!anyTimer)
                        {
                            idleBackoffSeconds = Math.Min(60, idleBackoffSeconds * 2);
                            try { await Task.Delay(TimeSpan.FromSeconds(idleBackoffSeconds), ct); } catch { }
                            continue;
                        }
                        idleBackoffSeconds = 5;

                        var list = await db.Set<Analytics>()
                                           .AsNoTracking()
                                           .Where(a => a.Interval > 0)
                                           .ToListAsync(ct);

                        foreach (var analytic in list)
                        {
                            if (!await ShouldRunAsync(analytic, ct)) continue;

                            // use a write scope/context
                            using var wscope = _serviceProvider.CreateScope();
                            var wdb = wscope.ServiceProvider.GetRequiredService<DataContext>();
                            var tracked = await wdb.Set<Analytics>().FirstAsync(a => a.Id == analytic.Id, ct);

                            var oldValue = tracked.Value;
                            var exec = await RunAnalyticsAsync(tracked, wdb, ct);

                            tracked.Value = exec.Final;
                            tracked.LastRun = DateTime.UtcNow;
                            tracked.Status = "OK";
                            _lastRunTimes[tracked.Id] = DateTime.UtcNow;

                            if (_options.EnableChangeTracking && oldValue != tracked.Value)
                            {
                                await wdb.AddAsync(new ChangeLogRecord
                                {
                                    Id = Guid.NewGuid(),
                                    TableName = "sysAnalytics",
                                    EntityId = tracked.Id.ToString(),
                                    ChangedBy = "System",
                                    ChangedAt = DateTime.UtcNow,
                                    OriginalValue = oldValue,
                                    NewValue = tracked.Value,
                                    ChangeType = "Update",
                                    PropertyName = "Value"
                                }, ct);
                            }
                            await wdb.SaveChangesAsync(ct);
                        }
                    }
                    finally { _dbGate.Release(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Timer analytics loop failed; backing off 5s.");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { }
            }
        }

        // ======================= ChangeLog-trigger loop =======================
        private async Task ChangeLogLoopAsync(CancellationToken ct)
        {
            int idleBackoffSeconds = 5;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _dbGate.WaitAsync(ct);
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<DataContext>();

                        // Load triggers; if none -> idle
                        var triggers = await db.Set<AnalyticsTrigger>()
                                               .AsNoTracking()
                                               .ToListAsync(ct);

                        if (triggers.Count == 0)
                        {
                            idleBackoffSeconds = Math.Min(60, idleBackoffSeconds * 2);
                            try { await Task.Delay(TimeSpan.FromSeconds(idleBackoffSeconds), ct); } catch { }
                            continue;
                        }
                        idleBackoffSeconds = 5;

                        // Group triggers per analytics
                        var groups = triggers.GroupBy(t => t.AnalyticsId).ToList();

                        foreach (var grp in groups)
                        {
                            var analyticsId = grp.Key;
                            var since = _clWatermark.GetOrAdd(analyticsId, DateTime.UtcNow.AddSeconds(-1));

                            var tables = grp.Select(g => g.TableName)
                                            .Where(s => !string.IsNullOrWhiteSpace(s))
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToList();
                            var props = grp.Select(g => g.PropertyName)
                                           .Where(s => !string.IsNullOrWhiteSpace(s))
                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                           .ToList();

                            if (tables.Count == 0 || props.Count == 0)
                            {
                                // invalid triggers won't match anything; skip this analytic
                                continue;
                            }

                            // Pull candidate change rows since watermark (cap to avoid long scans)
                            var candidates = await db.Set<ChangeLogRecord>()
                                .AsNoTracking()
                                .Where(c => c.ChangedAt > since &&
                                            tables.Contains(c.TableName) &&
                                            props.Contains(c.PropertyName))
                                .OrderBy(c => c.ChangedAt)
                                .Take(500)
                                .ToListAsync(ct);

                            if (candidates.Count == 0) continue;

                            bool matched = false;
                            foreach (var row in candidates)
                            {
                                // advance watermark regardless, so we don't re-scan forever
                                _clWatermark[analyticsId] = row.ChangedAt;

                                foreach (var tr in grp)
                                {
                                    if (!row.TableName.Equals(tr.TableName, StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!row.PropertyName.Equals(tr.PropertyName, StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!string.IsNullOrWhiteSpace(tr.EntityId) &&
                                        !string.Equals(tr.EntityId, row.EntityId, StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!string.IsNullOrWhiteSpace(tr.ChangeType) &&
                                        !string.Equals(tr.ChangeType, row.ChangeType, StringComparison.OrdinalIgnoreCase)) continue;

                                    matched = true;
                                    break;
                                }
                                if (matched) break;
                            }

                            if (!matched) continue;

                            // Run this analytic once (coalesced), respecting min interval
                            using var wscope = _serviceProvider.CreateScope();
                            var wdb = wscope.ServiceProvider.GetRequiredService<DataContext>();
                            var analytic = await wdb.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticsId, ct);
                            if (analytic == null) continue;

                            if (_lastRunTimes.TryGetValue(analytic.Id, out var last) &&
                                (DateTime.UtcNow - last) < _minimumRunInterval)
                                continue;

                            var oldValue = analytic.Value;
                            var exec = await RunAnalyticsAsync(analytic, wdb, ct);

                            analytic.Value = exec.Final;
                            analytic.LastRun = DateTime.UtcNow;
                            analytic.Status = "OK";
                            _lastRunTimes[analytic.Id] = DateTime.UtcNow;

                            if (_options.EnableChangeTracking && oldValue != analytic.Value)
                            {
                                await wdb.AddAsync(new ChangeLogRecord
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
                                }, ct);
                            }
                            await wdb.SaveChangesAsync(ct);
                        }
                    }
                    finally { _dbGate.Release(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "ChangeLog analytics loop failed; backing off 5s.");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
                }

                try { await Task.Delay(_changePollInterval, ct); } catch { }
            }
        }

        // ======================= Execution helpers =======================
        private async Task<bool> ShouldRunAsync(Analytics analytic, CancellationToken ct)
        {
            if (analytic.Interval <= 0) return false;

            if (_lastRunTimes.TryGetValue(analytic.Id, out var last) &&
                (DateTime.UtcNow - last) < _minimumRunInterval)
                return false;

            return !analytic.LastRun.HasValue ||
                   (DateTime.UtcNow - analytic.LastRun.Value).TotalSeconds >= analytic.Interval;
        }

        private Task<ExecResult> RunAnalyticsAsync(
            Analytics analytic,
            DataContext dbContext,
            CancellationToken ct,
            IDictionary<string, object>? seedContext)
            => RunAnalyticsCoreAsync(analytic, dbContext, seedContext, ct);

        private Task<ExecResult> RunAnalyticsAsync(
            Analytics analytic,
            DataContext dbContext,
            CancellationToken ct)
            => RunAnalyticsCoreAsync(analytic, dbContext, null, ct);

        private async Task<ExecResult> RunAnalyticsCoreAsync(
            Analytics analytic,
            DataContext dbContext,
            IDictionary<string, object>? seedContext,
            CancellationToken ct)
        {
            var steps = await dbContext.Set<AnalyticsStep>()
                .Where(s => s.AnalyticsId == analytic.Id)
                .OrderBy(s => s.Order)
                .ToListAsync(ct);

            // case-insensitive context
            var context = seedContext != null
                ? new Dictionary<string, object>(seedContext, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            var loopCounts = new Dictionary<Guid, int>();
            object? lastResult = null;
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
                        _logger?.LogWarning("Max loop {MaxLoop} reached for Condition step {StepId}", step.MaxLoop, step.Id);
                        currentStepIndex++;
                        continue;
                    }
                }

                var result = await ExecuteStepAsync(step, dbContext, context, ct);
                lastResult = result;

                if (step.Operation == AnalyticsStepType.Condition.ToString())
                {
                    if (result is bool cond &&
                        cond &&
                        int.TryParse(step.ResultVariable, out var goTo) &&
                        goTo >= 1 && goTo <= steps.Count &&
                        goTo - 1 != currentStepIndex)
                    {
                        loopCounts[step.Id]++;
                        currentStepIndex = goTo - 1;
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
                            var idx = int.Parse(indexMatch.Groups[2].Value);
                            if (!context.TryGetValue(arrayName, out var arrayObj) || arrayObj is not List<object> arr)
                            {
                                arr = new List<object>();
                                context[arrayName] = arr;
                            }
                            while (arr.Count <= idx) arr.Add(null!);
                            arr[idx] = result!;
                        }
                        else
                        {
                            context[step.ResultVariable] = result!;
                        }
                    }
                    currentStepIndex++;
                }

                // Finalization
                if (currentStepIndex == steps.Count)
                {
                    if (step.Operation == AnalyticsStepType.SqlQuery.ToString())
                    {
                        if (result is List<Dictionary<string, object>> rows && rows.Any())
                        {
                            var first = rows.First();
                            if (!string.IsNullOrEmpty(step.ResultVariable) && first.ContainsKey(step.ResultVariable))
                                return new ExecResult(first[step.ResultVariable]?.ToString() ?? string.Empty, context);
                            return new ExecResult(first.Values.FirstOrDefault()?.ToString() ?? string.Empty, context);
                        }
                        return new ExecResult(string.Empty, context);
                    }
                    else if (step.Operation == AnalyticsStepType.Timeseries.ToString())
                    {
                        if (result is List<TimeseriesResult> ts && ts.Any())
                            return new ExecResult(ts.Last().Value, context);
                        return new ExecResult(string.Empty, context);
                    }
                    else if (!string.IsNullOrEmpty(step.ResultVariable))
                    {
                        var m = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                        if (m.Success)
                        {
                            var arrName = m.Groups[1].Value;
                            var idx = int.Parse(m.Groups[2].Value);
                            if (context.TryGetValue(arrName, out var arrObj) && arrObj is List<object> arr && idx < arr.Count)
                                return new ExecResult(arr[idx]?.ToString() ?? string.Empty, context);
                        }
                        else if (context.TryGetValue(step.ResultVariable, out var final))
                        {
                            return new ExecResult(final?.ToString() ?? string.Empty, context);
                        }
                    }
                }
            }

            return new ExecResult(lastResult?.ToString() ?? string.Empty, context);
        }

        private async Task<object> ExecuteStepAsync(
            AnalyticsStep step,
            DataContext dbContext,
            Dictionary<string, object> context,
            CancellationToken ct)
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
                        {
                            var typed = parameters.Select(p => p switch
                            {
                                double d => (object)d,
                                int i => i,
                                string s => s,
                                decimal m => m,
                                long l => l,
                                _ => p?.ToString() ?? string.Empty
                            }).ToArray();

                            var results = await dbContext.ExecuteSqlQueryAsync(expression, typed);
                            return results.Select(r => r.Data).ToList();
                        }

                    case AnalyticsStepType.Timeseries:
                        {
                            var ts = ParseTimeseriesExpression(expression, parameters);
                            List<TimeseriesResult> data;
                            if (ts.InterpolationMethod == InterpolationMethod.None)
                            {
                                data = await dbContext.GetTimeseriesAsync(
                                    ts.TableName, ts.EntityId, ts.PropertyName, ts.Start, ts.End);
                            }
                            else
                            {
                                data = await dbContext.GetInterpolatedTimeseriesAsync(
                                    ts.TableName, ts.EntityId, ts.PropertyName, ts.Start, ts.End, ts.Interval, ts.InterpolationMethod);
                            }
                            return data;
                        }

                    case AnalyticsStepType.CSharp:
                    case AnalyticsStepType.Variable:
                        return await ExecuteCSharpAsync(step.Id, expression, context);

                    case AnalyticsStepType.Condition:
                        {
                            var cond = await ExecuteCSharpAsync(step.Id, expression, context);
                            if (cond is not bool) throw new InvalidOperationException($"Condition step {step.Id} must return boolean.");
                            return cond;
                        }

                    default:
                        throw new InvalidOperationException($"Unsupported step type: {stepType}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing step {StepId} ({Type})", step.Id, stepType);
                throw;
            }
        }

        private (string TableName, string EntityId, string PropertyName, DateTime Start, DateTime End, TimeSpan Interval, InterpolationMethod InterpolationMethod)
            ParseTimeseriesExpression(string expression, List<object> parameters)
        {
            var parts = expression.Split(',');
            if (parts.Length < 5 || parts.Length > 7)
                throw new InvalidOperationException("Timeseries: table,entity,property,start,end[,interval,method]");

            int pi = 0;
            var table = ReplaceParameter(parts[0], parameters, ref pi);
            var entity = ReplaceParameter(parts[1], parameters, ref pi);
            var prop = ReplaceParameter(parts[2], parameters, ref pi);

            if (!DateTime.TryParse(ReplaceParameter(parts[3], parameters, ref pi), out var start))
                throw new InvalidOperationException("Invalid start date.");
            if (!DateTime.TryParse(ReplaceParameter(parts[4], parameters, ref pi), out var end))
                throw new InvalidOperationException("Invalid end date.");

            TimeSpan interval = TimeSpan.FromSeconds(1);
            InterpolationMethod method = InterpolationMethod.None;

            if (parts.Length > 5)
            {
                if (!TimeSpan.TryParse(ReplaceParameter(parts[5], parameters, ref pi), out interval))
                    throw new InvalidOperationException("Invalid interval.");
            }
            if (parts.Length > 6)
            {
                var m = ReplaceParameter(parts[6], parameters, ref pi);
                if (!Enum.TryParse<InterpolationMethod>(m, true, out method))
                    throw new InvalidOperationException($"Invalid interpolation method: {m}");
            }

            return (table, entity, prop, start, end, interval, method);
        }

        private string ReplaceParameter(string part, List<object> parameters, ref int pi)
        {
            if (Regex.IsMatch(part, @"^@p\d+$"))
            {
                if (pi >= parameters.Count)
                    throw new InvalidOperationException($"Missing parameter for {part}");
                return parameters[pi++]?.ToString() ?? string.Empty;
            }
            return part.Trim();
        }

        private async Task<object?> ExecuteCSharpAsync(Guid stepId, string script, Dictionary<string, object> context)
        {
            try
            {
                if (!_scriptCache.TryGetValue((stepId, script), out var runner))
                {
                    var compiled = CSharpScript.Create<object?>(script, _scriptOptions, typeof(ScriptGlobals));
                    var diags = compiled.GetCompilation().GetDiagnostics();
                    if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        var msg = string.Join("; ", diags.Where(d => d.Severity == DiagnosticSeverity.Error));
                        throw new InvalidOperationException($"C# script compilation failed: {msg}");
                    }
                    runner = compiled.CreateDelegate();
                    _scriptCache[(stepId, script)] = runner;
                }

                var globals = new ScriptGlobals { Context = context };
                return await runner(globals);
            }
            catch (CompilationErrorException ex)
            {
                var msg = string.Join("; ", ex.Diagnostics.Select(d => d.ToString()));
                throw new InvalidOperationException($"C# script compilation failed: {msg}", ex);
            }
        }

        private (string Expression, List<object> Parameters) ReplaceVariables(string expression, Dictionary<string, object> context)
        {
            var parameters = new List<object>();
            int idx = 0;

            var sql = Regex.Replace(expression, @"\{([^{}]+)\}", m =>
            {
                var name = m.Groups[1].Value;
                if (context.TryGetValue(name, out var val))
                {
                    parameters.Add(val!);
                    return $"@p{idx++}";
                }
                return string.Empty; // missing var becomes empty (safe for expressions you control)
            });

            return (sql, parameters);
        }

        // ======================= Validation & CRUD helpers =======================
        private bool ValidateSqlQuery(string sqlQuery, out string? error)
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

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid SQL query: {ex.Message}";
                return false;
            }
        }

        private bool ValidateTimeseriesExpression(string expression, out string? error)
        {
            try
            {
                var parts = expression.Split(',');
                if (parts.Length < 5 || parts.Length > 7)
                {
                    error = "Timeseries expression must have 5–7 parts: table,entity,property,start,end[,interval,method]";
                    return false;
                }

                if (parts.Length > 5 && !TimeSpan.TryParse(parts[5], out _))
                {
                    error = "Invalid interval format.";
                    return false;
                }
                if (parts.Length > 6 && !Enum.TryParse<InterpolationMethod>(parts[6], true, out _))
                {
                    error = "Invalid interpolation method.";
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

        private bool ValidateCSharpScript(string script, out string? error)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                error = "Script cannot be empty.";
                return false;
            }

            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(script);
                var root = syntaxTree.GetRoot();
                var dangerous = new[] { "System.IO", "System.Net", "System.Reflection", "System.Threading", "System.Diagnostics" };
                var hasDanger = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
                    .Any(n => dangerous.Any(ns => n.ToString().StartsWith(ns, StringComparison.OrdinalIgnoreCase)));

                if (hasDanger)
                {
                    error = "Script uses prohibited namespaces (IO/Net/Reflection/Threading/Diagnostics).";
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
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            if (await db.Set<Analytics>().AnyAsync(a => a.Name == config.Name, ct))
                throw new InvalidOperationException($"Analytics '{config.Name}' exists.");

            var (ok, errors) = await VerifyAnalyticsAsync(config.Id == Guid.Empty ? Guid.NewGuid() : config.Id, config, ct);
            if (!ok) throw new InvalidOperationException($"Validation Failed: {string.Join("; ", errors)}");

            var analytic = new Analytics
            {
                Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id,
                Name = config.Name,
                Interval = config.Interval,
                Value = "0",
                Status = "OK"
            };
            await db.AddAsync(analytic, ct);

            for (int i = 0; i < config.Steps.Count; i++)
            {
                var s = config.Steps[i];
                await db.AddAsync(new AnalyticsStep
                {
                    Id = Guid.NewGuid(),
                    AnalyticsId = analytic.Id,
                    Order = i + 1,
                    Operation = s.Type.ToString(),
                    Expression = s.Config,
                    ResultVariable = s.OutputVariable,
                    MaxLoop = s.Type == AnalyticsStepType.Condition ? s.MaxLoop : 10
                }, ct);
            }

            await db.SaveChangesAsync(ct);
        }

        public async Task DeleteAnalyticsAsync(Guid analyticId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            var entity = await db.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticId, ct)
                        ?? throw new InvalidOperationException($"Analytics {analyticId} does not exist.");

            db.Remove(entity);
            await db.SaveChangesAsync(ct);

            _lastRunTimes.TryRemove(analyticId, out _);
            _clWatermark.TryRemove(analyticId, out _);
        }

        public async Task<(bool IsValid, List<string> Errors)> VerifyAnalyticsAsync(Guid analyticId, AnalyticsConfig? config = null, CancellationToken ct = default)
        {
            var errors = new List<string>();
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            Analytics? analytic;
            List<AnalyticsStep> steps;

            if (config == null)
            {
                analytic = await db.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticId, ct);
                if (analytic == null)
                {
                    errors.Add($"Analytics {analyticId} does not exist.");
                    return (false, errors);
                }
                steps = await db.Set<AnalyticsStep>()
                                .Where(s => s.AnalyticsId == analyticId)
                                .OrderBy(s => s.Order)
                                .ToListAsync(ct);
            }
            else
            {
                analytic = new Analytics { Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id, Interval = config.Interval, Name = config.Name };
                steps = config.Steps.Select((s, i) => new AnalyticsStep
                {
                    Id = Guid.NewGuid(),
                    AnalyticsId = analytic.Id,
                    Order = i + 1,
                    Operation = s.Type.ToString(),
                    Expression = s.Config,
                    ResultVariable = s.OutputVariable,
                    MaxLoop = s.Type == AnalyticsStepType.Condition ? s.MaxLoop : 10
                }).ToList();
            }

            if (!steps.Any())
            {
                errors.Add("Analytics must have at least one step.");
                if (config == null)
                {
                    analytic!.Status = "Validation Failed: Analytics must have at least one step.";
                    await db.SaveChangesAsync(ct);
                }
                return (false, errors);
            }

            var tableNames = GetTables(db);
            var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reachable = new HashSet<int>();

            var simContext = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < steps.Count; i++)
            {
                reachable.Add(i + 1);
                var step = steps[i];

                if (step.Order != i + 1) errors.Add($"Step {step.Order} out of order (expected {i + 1}).");

                if (!Enum.TryParse<AnalyticsStepType>(step.Operation, true, out var stepType))
                    errors.Add($"Step {step.Order}: Invalid step type '{step.Operation}'.");

                if (string.IsNullOrWhiteSpace(step.Expression))
                {
                    errors.Add($"Step {step.Order}: Expression cannot be empty.");
                    continue;
                }

                var varRefs = Regex.Matches(step.Expression, @"\{([^{}]+)\}")
                                   .Select(m => m.Groups[1].Value);

                if (stepType is AnalyticsStepType.CSharp or AnalyticsStepType.Condition)
                {
                    foreach (var name in varRefs)
                        if (!variables.Contains(name) && i > 0)
                            errors.Add($"Step {step.Order}: Unknown variable '{name}'.");
                }

                if (!string.IsNullOrEmpty(step.ResultVariable) && stepType != AnalyticsStepType.Condition)
                {
                    var idxMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                    if (idxMatch.Success)
                    {
                        var arrName = idxMatch.Groups[1].Value;
                        if (i > 0 && !variables.Contains(arrName))
                            errors.Add($"Step {step.Order}: Array '{arrName}' must be defined earlier.");
                    }
                }

                switch (stepType)
                {
                    case AnalyticsStepType.SqlQuery:
                        {
                            if (!ValidateSqlQuery(step.Expression, out var sqlErr))
                                errors.Add($"Step {step.Order} (SqlQuery): {sqlErr}");

                            foreach (var (table, prop) in ExtractTableAndProperties(step.Expression))
                            {
                                if (!tableNames.Contains(table))
                                    errors.Add($"Step {step.Order}: Unknown table '{table}'.");
                                var et = db.Model.GetEntityTypes()
                                    .FirstOrDefault(t => t.GetTableName().Equals(table, StringComparison.OrdinalIgnoreCase))
                                    ?.ClrType;
                                if (et != null)
                                {
                                    var pi = et.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                    if (pi == null)
                                        errors.Add($"Step {step.Order}: Property '{prop}' not found on '{table}'.");
                                }
                            }
                            break;
                        }

                    case AnalyticsStepType.Timeseries:
                        {
                            if (!ValidateTimeseriesExpression(step.Expression, out var tsErr))
                                errors.Add($"Step {step.Order} (Timeseries): {tsErr}");
                            foreach (var (table, _) in ExtractTableAndProperties(step.Expression))
                                if (!tableNames.Contains(table))
                                    errors.Add($"Step {step.Order}: Unknown table '{table}'.");
                            if (i == steps.Count - 1 && string.IsNullOrEmpty(step.ResultVariable))
                                errors.Add($"Step {step.Order}: Last Timeseries step must have a ResultVariable.");
                            break;
                        }

                    case AnalyticsStepType.CSharp:
                    case AnalyticsStepType.Variable:
                        {
                            if (!ValidateCSharpScript(step.Expression, out var scErr))
                                errors.Add($"Step {step.Order} ({stepType}): {scErr}");
                            else
                            {
                                try
                                {
                                    var compilation = CSharpScript.Create(step.Expression, _scriptOptions, typeof(ScriptGlobals));
                                    var diags = compilation.GetCompilation().GetDiagnostics();
                                    if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
                                    {
                                        var msgs = string.Join("; ", diags.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                                        errors.Add($"Step {step.Order} ({stepType}): {msgs}");
                                    }
                                    else
                                    {
                                        var globals = new ScriptGlobals { Context = new Dictionary<string, object>(simContext) };
                                        var value = await CSharpScript.EvaluateAsync(step.Expression, _scriptOptions, globals);
                                        if (value != null && !string.IsNullOrEmpty(step.ResultVariable))
                                        {
                                            var m = Regex.Match(step.ResultVariable, @"^(\w+)\[\d+\]$");
                                            var varName = m.Success ? m.Groups[1].Value : step.ResultVariable;
                                            simContext[varName] = value;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"Step {step.Order} ({stepType}) validation failed: {ex.Message}");
                                }
                            }
                            if (i == steps.Count - 1 && string.IsNullOrEmpty(step.ResultVariable))
                                errors.Add($"Step {step.Order} ({stepType}): Last step must set ResultVariable.");
                            break;
                        }

                    case AnalyticsStepType.Condition:
                        {
                            if (!ValidateCSharpScript(step.Expression, out var condErr))
                                errors.Add($"Step {step.Order} (Condition): {condErr}");
                            else
                            {
                                try
                                {
                                    var compilation = CSharpScript.Create(step.Expression, _scriptOptions, typeof(ScriptGlobals));
                                    var diags = compilation.GetCompilation().GetDiagnostics();
                                    if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
                                    {
                                        var msgs = string.Join("; ", diags.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                                        errors.Add($"Step {step.Order} (Condition): {msgs}");
                                    }
                                    else
                                    {
                                        var globals = new ScriptGlobals { Context = new Dictionary<string, object>(simContext) };
                                        var res = await CSharpScript.EvaluateAsync(step.Expression, _scriptOptions, globals);
                                        if (res is not bool) errors.Add($"Step {step.Order} (Condition): must return boolean.");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"Step {step.Order} (Condition) validation failed: {ex.Message}");
                                }
                            }
                            if (!int.TryParse(step.ResultVariable, out var goTo) ||
                                goTo < 1 || goTo > steps.Count || goTo == i + 1)
                                errors.Add($"Step {step.Order} (Condition): invalid GoTo '{step.ResultVariable}'.");
                            else
                                reachable.Add(goTo);
                            if (step.MaxLoop <= 0)
                                errors.Add($"Step {step.Order} (Condition): MaxLoop must be positive.");
                            break;
                        }
                }

                if (stepType != AnalyticsStepType.Condition && !string.IsNullOrEmpty(step.ResultVariable))
                {
                    var idxMatch = Regex.Match(step.ResultVariable, @"^(\w+)\[(\d+)\]$");
                    var name = idxMatch.Success ? idxMatch.Groups[1].Value : step.ResultVariable;
                    variables.Add(name);
                    if (!simContext.ContainsKey(name))
                    {
                        if (idxMatch.Success) simContext[name] = new List<object>();
                        else if (stepType == AnalyticsStepType.Variable) simContext[name] = 0;
                        else simContext[name] = null!;
                    }
                }
            }

            for (int i = 1; i <= steps.Count; i++)
                if (!reachable.Contains(i))
                    errors.Add($"Step {i}: Unreachable due to loop configuration.");

            if (config == null)
            {
                analytic!.Status = errors.Any() ? $"Validation Failed: {string.Join("; ", errors)}" : "OK";
                await db.SaveChangesAsync(ct);
            }

            return (errors.Count == 0, errors);
        }

        private HashSet<string> GetTables(DataContext db)
        {
            var system = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "__sysChangeLog",
                "__sysEmbeddings",
                "__sysTimeseriesBaseValues",
                "__sysTimeseriesDeltas",
                "__sysIntegrityLog",
                "__sysAnalytics",
                "__sysAnalyticsSteps",
                "__sysAnalyticsTriggers"
            };

            return db.Model.GetEntityTypes()
                .Select(t => t.GetTableName())
                .Where(t => !system.Contains(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

                    var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "SELECT","INSERT","UPDATE","DELETE","CREATE","DROP","ALTER",
                        "WHERE","GROUP","ORDER","HAVING","SET","WITH","FROM","JOIN",
                        "INNER","OUTER","LEFT","RIGHT","FULL","ON","AS","UNION",
                        "INTERSECT","EXCEPT","INTO","VALUES"
                    };

                    var tableMatches = Regex.Matches(compiled.Sql,
                        @"(?:FROM|JOIN)\s+([a-zA-Z_][a-zA-Z0-9_]*)(?:\s+AS\s+\w+)?",
                        RegexOptions.IgnoreCase);

                    var tables = tableMatches.Cast<Match>()
                                             .Select(m => m.Groups[1].Value)
                                             .Where(t => !keywords.Contains(t))
                                             .Distinct(StringComparer.OrdinalIgnoreCase)
                                             .ToList();

                    foreach (var table in tables)
                    {
                        var propMatches = Regex.Matches(expression,
                            @"\b(?:AVG|SUM|COUNT|MIN|MAX)\s*\(\s*(\w+)\s*\)",
                            RegexOptions.IgnoreCase);
                        var props = propMatches.Cast<Match>()
                                               .Select(m => m.Groups[1].Value)
                                               .Distinct(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in props) result.Add((table, p));
                    }
                }
                else if (expression.Contains(","))
                {
                    var parts = expression.Split(',');
                    if (parts.Length >= 3)
                        result.Add((parts[0].Trim(), parts[2].Trim()));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract tables/properties from: {Expression}", expression);
            }
            return result;
        }

        // ======================= Export / Import / RunOnce =======================
        public async Task<string> ExportAnalyticsAsync(Guid analyticId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytic = await db.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticId, ct)
                          ?? throw new InvalidOperationException($"Analytics {analyticId} does not exist.");

            var steps = await db.Set<AnalyticsStep>()
                .Where(s => s.AnalyticsId == analyticId)
                .OrderBy(s => s.Order)
                .ToListAsync(ct);

            var export = new AnalyticsConfig
            {
                Id = analytic.Id,
                Name = analytic.Name,
                Interval = analytic.Interval,
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
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var config = JsonSerializer.Deserialize<AnalyticsConfig>(json);
            if (config == null || string.IsNullOrWhiteSpace(config.Name))
                throw new InvalidOperationException("Invalid analytics JSON.");

            await AddAnalyticsAsync(config, ct);
        }

        public async Task<string> ExecuteAnalyticsAsync(Guid analyticId, CancellationToken ct = default)
        {
            var res = await ExecuteAnalyticsWithContextAsync(analyticId, ct);
            return res.Final;
        }

        public async Task<(string Final, IReadOnlyDictionary<string, object> Context)> ExecuteAnalyticsWithContextAsync(Guid analyticId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytic = await db.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticId, ct)
                          ?? throw new InvalidOperationException($"Analytics {analyticId} does not exist.");

            var exec = await RunAnalyticsAsync(analytic, db, ct);
            analytic.Value = exec.Final;
            analytic.LastRun = DateTime.UtcNow;
            analytic.Status = "OK";
            _lastRunTimes[analytic.Id] = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return (exec.Final, exec.Vars);
        }

        public async Task RunOnceAsync(
            Guid tenantId,
            Guid analyticsId,
            bool isTimeDriven,
            string? triggerTable,
            IReadOnlyDictionary<string, object>? variables,
            CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var analytic = await db.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticsId, ct)
                          ?? throw new InvalidOperationException($"Analytics {analyticsId} does not exist.");

            // Seeded context (reserved/system fields + caller variables)
            var seed = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["__tenantId"] = tenantId.ToString(),
                ["__isTimeDriven"] = isTimeDriven,
                ["__nowUtc"] = DateTime.UtcNow
            };
            if (!string.IsNullOrWhiteSpace(triggerTable))
                seed["__triggerTable"] = triggerTable;
            if (variables != null)
                foreach (var kv in variables) seed[kv.Key] = kv.Value;

            if (_lastRunTimes.TryGetValue(analytic.Id, out var last) &&
                (DateTime.UtcNow - last) < _minimumRunInterval)
            {
                _logger?.LogDebug("RunOnce skipped for {Name} (min interval guard).", analytic.Name);
                return;
            }

            var oldValue = analytic.Value;
            var exec = await RunAnalyticsAsync(analytic, db, ct, seed);

            analytic.Value = exec.Final;
            analytic.LastRun = DateTime.UtcNow;
            analytic.Status = "OK";
            _lastRunTimes[analytic.Id] = DateTime.UtcNow;

            if (_options.EnableChangeTracking && oldValue != analytic.Value)
            {
                await db.AddAsync(new ChangeLogRecord
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
                }, ct);
            }
            await db.SaveChangesAsync(ct);
        }

        // ======================= Trigger management API =======================
        public async Task<AnalyticsTrigger> AddTriggerAsync(
            Guid analyticsId,
            string tableName,
            string propertyName,
            string? entityId = null,
            string? changeType = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentNullException(nameof(propertyName));

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            var exists = await db.Set<Analytics>().AnyAsync(a => a.Id == analyticsId, ct);
            if (!exists) throw new InvalidOperationException($"Analytics {analyticsId} does not exist.");

            var trig = new AnalyticsTrigger
            {
                Id = Guid.NewGuid(),
                AnalyticsId = analyticsId,
                TableName = tableName,
                PropertyName = propertyName,
                EntityId = string.IsNullOrWhiteSpace(entityId) ? null : entityId,
                ChangeType = string.IsNullOrWhiteSpace(changeType) ? null : changeType
            };
            await db.AddAsync(trig, ct);
            await db.SaveChangesAsync(ct);
            return trig;
        }

        public async Task<List<AnalyticsTrigger>> GetTriggersAsync(Guid analyticsId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            return await db.Set<AnalyticsTrigger>()
                           .AsNoTracking()
                           .Where(t => t.AnalyticsId == analyticsId)
                           .ToListAsync(ct);
        }

        public async Task RemoveTriggerAsync(Guid triggerId, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var trig = await db.Set<AnalyticsTrigger>().FirstOrDefaultAsync(t => t.Id == triggerId, ct);
            if (trig == null) return;
            db.Remove(trig);
            await db.SaveChangesAsync(ct);
        }
    }
}
