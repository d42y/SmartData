using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartData.Configurations;
using SmartData.GPT.Embedder;
using SmartData.GPT.Search;
using SmartData.SmartCalc.Models;
using SmartData.Tables.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartData.SmartCalc
{
    public class SmartCalcService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, DateTime> _tableChangeTriggers;
        private readonly IEmbedder _embedder;
        private readonly FaissNetSearch _faissIndex;

        public SmartCalcService(IServiceProvider serviceProvider, ILogger logger, IEmbedder embedder = null, FaissNetSearch faissIndex = null)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _tableChangeTriggers = new ConcurrentDictionary<string, DateTime>();
            _embedder = embedder;
            _faissIndex = faissIndex;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                    var calculations = await dbContext.Set<Calculation>().ToListAsync(stoppingToken);

                    foreach (var calculation in calculations)
                    {
                        if (await ShouldRunCalculationAsync(calculation, dbContext, stoppingToken))
                        {
                            var oldValue = calculation.Value;
                            await ExecuteCalculationAsync(calculation, dbContext, stoppingToken);
                            if (calculation.Embeddable && calculation.Value != oldValue && _embedder != null && _faissIndex != null)
                            {
                                await GenerateAndStoreEmbeddingAsync(calculation, dbContext);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in SmartCalcService execution loop");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Poll every 30 seconds
            }
        }

        private async Task<bool> ShouldRunCalculationAsync(Calculation calculation, SmartDataContext dbContext, CancellationToken cancellationToken)
        {
            if (calculation.Interval > 0) // Timer
            {
                if (!calculation.LastRun.HasValue || DateTime.UtcNow - calculation.LastRun.Value >= TimeSpan.FromSeconds(calculation.Interval))
                {
                    return true;
                }
            }
            else if (calculation.Interval < 0) // OnChange
            {
                var tables = await GetReferencedTablesAsync(calculation.Id, dbContext, cancellationToken);
                foreach (var table in tables)
                {
                    var lastChange = await dbContext.Set<ChangeLogRecord>()
                        .Where(c => c.TableName == table.Trim())
                        .MaxAsync(c => (DateTime?)c.ChangeDate, cancellationToken) ?? DateTime.MinValue;
                    if (_tableChangeTriggers.TryGetValue(table.Trim(), out var lastTriggered) && lastChange > lastTriggered)
                    {
                        _tableChangeTriggers[table.Trim()] = lastChange;
                        return true;
                    }
                    else if (!lastTriggered.Equals(DateTime.MinValue))
                    {
                        _tableChangeTriggers[table.Trim()] = lastChange;
                    }
                }
            }

            return false; // Manual (Interval == 0) only runs via ExecuteCalculationAsync
        }

        private async Task ExecuteCalculationAsync(Calculation calculation, SmartDataContext dbContext, CancellationToken cancellationToken)
        {
            var steps = await dbContext.Set<CalculationStep>()
                .Where(s => s.CalculationId == calculation.Id)
                .OrderBy(s => s.StepOrder)
                .ToListAsync(cancellationToken);

            var tables = await GetReferencedTablesAsync(calculation.Id, dbContext, cancellationToken);
            var variables = new Dictionary<string, string>();
            foreach (var step in steps)
            {
                try
                {
                    var result = await ExecuteStepAsync(step, dbContext, variables, tables, cancellationToken);
                    if (!string.IsNullOrEmpty(step.ResultVariable))
                    {
                        variables[step.ResultVariable] = result;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to execute step {StepOrder} for calculation {CalculationName}", step.StepOrder, calculation.Name);
                }
            }

            calculation.Value = variables.LastOrDefault().Value ?? string.Empty;
            calculation.LastRun = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Executed calculation {CalculationName} with result {Value}", calculation.Name, calculation.Value);
        }

        private async Task<string> ExecuteStepAsync(CalculationStep step, SmartDataContext dbContext, Dictionary<string, string> variables, HashSet<string> referenceTables, CancellationToken cancellationToken)
        {
            var expression = step.Expression;
            var matches = Regex.Matches(expression, @"\{([^{}]+)\}");
            foreach (Match match in matches)
            {
                var placeholder = match.Groups[1].Value;
                string value = string.Empty;

                if (variables.ContainsKey(placeholder))
                {
                    value = variables[placeholder];
                }
                else if (placeholder.Contains("."))
                {
                    var parts = placeholder.Split('.');
                    if (parts.Length == 2)
                    {
                        var tableName = parts[0];
                        var columnName = parts[1];
                        if (referenceTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                        {
                            var query = dbContext.Set<object>().FromSqlRaw($"SELECT TOP 1 [{columnName}] FROM [{tableName}] ORDER BY Id DESC");
                            value = (await query.FirstOrDefaultAsync(cancellationToken))?.ToString() ?? string.Empty;
                        }
                    }
                }

                expression = expression.Replace($"{{{placeholder}}}", value);
            }

            return step.OperationType.ToUpper() switch
            {
                "MATH" => EvaluateMathExpression(expression),
                "STRING" => await EvaluateStringExpressionAsync(expression, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported operation type: {step.OperationType}")
            };
        }

        private string EvaluateMathExpression(string expression)
        {
            try
            {
                var result = new System.Data.DataTable().Compute(expression, null);
                return result?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to evaluate math expression: {expression}", ex);
            }
        }

        private async Task<string> EvaluateStringExpressionAsync(string expression, CancellationToken cancellationToken)
        {
            if (expression.StartsWith("SEARCH(", StringComparison.OrdinalIgnoreCase))
            {
                var parts = expression.Substring(7, expression.Length - 8).Split(',');
                if (parts.Length == 2)
                {
                    return parts[0].Contains(parts[1], StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                }
            }
            else if (expression.StartsWith("SUBSTRING(", StringComparison.OrdinalIgnoreCase))
            {
                var parts = expression.Substring(10, expression.Length - 11).Split(',');
                if (parts.Length == 3 && int.TryParse(parts[1], out var start) && int.TryParse(parts[2], out var length))
                {
                    return parts[0].Length > start ? parts[0].Substring(start, Math.Min(length, parts[0].Length - start)) : string.Empty;
                }
            }
            else if (expression.StartsWith("CONCAT(", StringComparison.OrdinalIgnoreCase))
            {
                var parts = expression.Substring(7, expression.Length - 8).Split(',');
                return string.Join("", parts.Select(p => p.Trim()));
            }
            else if (expression.StartsWith("REPLACE(", StringComparison.OrdinalIgnoreCase))
            {
                var parts = expression.Substring(8, expression.Length - 9).Split(',');
                if (parts.Length == 3)
                {
                    return parts[0].Replace(parts[1], parts[2], StringComparison.OrdinalIgnoreCase);
                }
            }

            throw new InvalidOperationException($"Unsupported string expression: {expression}");
        }

        private async Task GenerateAndStoreEmbeddingAsync(Calculation calculation, SmartDataContext dbContext)
        {
            var paragraph = $"Calculation {calculation.Name} has value {calculation.Value}";
            var embedding = _embedder.GenerateEmbedding(paragraph).ToArray();
            var embeddingRecord = await dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == "sysCalculations" && e.EntityId == calculation.Id.ToString());

            if (embeddingRecord == null)
            {
                embeddingRecord = new EmbeddingRecord
                {
                    Id = Guid.NewGuid(),
                    EntityId = calculation.Id.ToString(),
                    Embedding = embedding,
                    TableName = "sysCalculations"
                };
                await dbContext.Set<EmbeddingRecord>().AddAsync(embeddingRecord);
            }
            else
            {
                embeddingRecord.Embedding = embedding;
                dbContext.Entry(embeddingRecord).State = EntityState.Modified;
                _faissIndex.UpdateEmbedding(embeddingRecord.Id, embedding);
            }

            await dbContext.SaveChangesAsync();
            _logger?.LogDebug("Generated embedding for calculation {CalculationName} with length {Length}", calculation.Name, embedding.Length);
        }

        private async Task<HashSet<string>> GetReferencedTablesAsync(Guid calculationId, SmartDataContext dbContext, CancellationToken cancellationToken)
        {
            var steps = await dbContext.Set<CalculationStep>()
                .Where(s => s.CalculationId == calculationId)
                .ToListAsync(cancellationToken);
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in steps)
            {
                var matches = Regex.Matches(step.Expression, @"\{([^{}]+)\}");
                foreach (Match match in matches)
                {
                    var placeholder = match.Groups[1].Value;
                    if (placeholder.Contains("."))
                    {
                        var parts = placeholder.Split('.');
                        if (parts.Length == 2)
                        {
                            tables.Add(parts[0]);
                        }
                    }
                }
            }

            return tables;
        }

        public async Task AddCalculationAsync(string name, int intervalSeconds, bool embeddable, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            if (await dbContext.Set<Calculation>().AnyAsync(c => c.Name == name, cancellationToken))
                throw new InvalidOperationException($"Calculation with name {name} already exists.");

            var calculation = new Calculation
            {
                Id = Guid.NewGuid(),
                Name = name,
                Embeddable = embeddable
            };
            calculation.SetInterval(intervalSeconds);

            await dbContext.Set<Calculation>().AddAsync(calculation, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Added calculation {CalculationName} with Interval={Interval} Embeddable={Embeddable}", name, intervalSeconds, embeddable);
        }

        public async Task AddCalculationStepAsync(Guid calculationId, int stepOrder, string operationType, string expression, string resultVariable, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            if (!await dbContext.Set<Calculation>().AnyAsync(c => c.Id == calculationId, cancellationToken))
                throw new InvalidOperationException($"Calculation with ID {calculationId} does not exist.");

            var step = new CalculationStep
            {
                Id = Guid.NewGuid(),
                CalculationId = calculationId,
                StepOrder = stepOrder,
                OperationType = operationType,
                Expression = expression,
                ResultVariable = resultVariable
            };

            await dbContext.Set<CalculationStep>().AddAsync(step, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Added step {StepOrder} to calculation {CalculationId}", stepOrder, calculationId);
        }

        public async Task DeleteCalculationAsync(Guid calculationId, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            var calculation = await dbContext.Set<Calculation>().FirstOrDefaultAsync(c => c.Id == calculationId, cancellationToken);
            if (calculation == null)
                throw new InvalidOperationException($"Calculation with ID {calculationId} does not exist.");

            if (calculation.Embeddable)
            {
                var embedding = await dbContext.Set<EmbeddingRecord>()
                    .FirstOrDefaultAsync(e => e.TableName == "sysCalculations" && e.EntityId == calculationId.ToString(), cancellationToken);
                if (embedding != null)
                {
                    dbContext.Set<EmbeddingRecord>().Remove(embedding);
                    _faissIndex?.RemoveEmbedding(embedding.Id);
                }
            }

            dbContext.Set<Calculation>().Remove(calculation);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Deleted calculation {CalculationName} with ID {CalculationId}", calculation.Name, calculationId);
        }

        public async Task DeleteCalculationStepAsync(Guid stepId, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            var step = await dbContext.Set<CalculationStep>().FirstOrDefaultAsync(s => s.Id == stepId, cancellationToken);
            if (step == null)
                throw new InvalidOperationException($"Step with ID {stepId} does not exist.");

            dbContext.Set<CalculationStep>().Remove(step);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Deleted step {StepId} from calculation {CalculationId}", stepId, step.CalculationId);
        }

        public async Task ExecuteCalculationAsync(Guid calculationId, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            var calculation = await dbContext.Set<Calculation>().FirstOrDefaultAsync(c => c.Id == calculationId, cancellationToken);
            if (calculation == null)
                throw new InvalidOperationException($"Calculation with ID {calculationId} does not exist.");

            var oldValue = calculation.Value;
            await ExecuteCalculationAsync(calculation, dbContext, cancellationToken);
            if (calculation.Embeddable && calculation.Value != oldValue && _embedder != null && _faissIndex != null)
            {
                await GenerateAndStoreEmbeddingAsync(calculation, dbContext);
            }

            _logger?.LogInformation("Manually executed calculation {CalculationName} with result {Value}", calculation.Name, calculation.Value);
        }

        public async Task<(bool IsValid, List<string> Errors)> VerifyCalculationStepsAsync(Guid calculationId, CancellationToken cancellationToken = default)
        {
            var errors = new List<string>();
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            var calculation = await dbContext.Set<Calculation>().FirstOrDefaultAsync(c => c.Id == calculationId, cancellationToken);
            if (calculation == null)
            {
                errors.Add($"Calculation with ID {calculationId} does not exist.");
                return (false, errors);
            }

            var steps = await dbContext.Set<CalculationStep>()
                .Where(s => s.CalculationId == calculationId)
                .OrderBy(s => s.StepOrder)
                .ToListAsync(cancellationToken);

            var variables = new HashSet<string>();
            var referenceTables = await GetReferencedTablesAsync(calculationId, dbContext, cancellationToken);
            var tableNames = dbContext.Model.GetEntityTypes().Select(t => t.GetTableName()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step.StepOrder != i + 1)
                    errors.Add($"Step {i + 1} has incorrect order {step.StepOrder}.");

                if (!new[] { "Math", "String" }.Contains(step.OperationType, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"Step {step.StepOrder}: Invalid operation type {step.OperationType}.");

                var matches = Regex.Matches(step.Expression, @"\{([^{}]+)\}");
                foreach (Match match in matches)
                {
                    var placeholder = match.Groups[1].Value;
                    if (variables.Contains(placeholder)) continue;

                    if (placeholder.Contains("."))
                    {
                        var parts = placeholder.Split('.');
                        if (parts.Length == 2)
                        {
                            var tableName = parts[0];
                            if (!referenceTables.Contains(tableName, StringComparer.OrdinalIgnoreCase) || !tableNames.Contains(tableName))
                                errors.Add($"Step {step.StepOrder}: Invalid table reference {tableName} in {placeholder}.");
                        }
                        else
                        {
                            errors.Add($"Step {step.StepOrder}: Invalid reference format {placeholder}.");
                        }
                    }
                    else if (i > 0 && !variables.Contains(placeholder))
                    {
                        errors.Add($"Step {step.StepOrder}: Unknown variable {placeholder}.");
                    }
                }

                if (step.OperationType.Equals("Math", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var testExpression = step.Expression;
                        foreach (Match match in matches)
                        {
                            testExpression = testExpression.Replace(match.Value, "1");
                        }
                        new System.Data.DataTable().Compute(testExpression, null);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Step {step.StepOrder}: Invalid math expression {step.Expression}: {ex.Message}");
                    }
                }
                else if (step.OperationType.Equals("String", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Regex.IsMatch(step.Expression, @"^(SEARCH|SUBSTRING|CONCAT|REPLACE)\(.+\)$", RegexOptions.IgnoreCase))
                        errors.Add($"Step {step.StepOrder}: Invalid string expression {step.Expression}.");
                }

                if (!string.IsNullOrEmpty(step.ResultVariable))
                    variables.Add(step.ResultVariable);
            }

            return (errors.Count == 0, errors);
        }

        public async Task<string> ExportCalculationAsync(Guid calculationId, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            var calculation = await dbContext.Set<Calculation>()
                .FirstOrDefaultAsync(c => c.Id == calculationId, cancellationToken);
            if (calculation == null)
                throw new InvalidOperationException($"Calculation with ID {calculationId} does not exist.");

            var steps = await dbContext.Set<CalculationStep>()
                .Where(s => s.CalculationId == calculationId)
                .OrderBy(s => s.StepOrder)
                .ToListAsync(cancellationToken);

            var export = new
            {
                calculation.Id,
                calculation.Name,
                calculation.Interval,
                calculation.Embeddable,
                Steps = steps.Select(s => new
                {
                    s.Id,
                    s.StepOrder,
                    s.OperationType,
                    s.Expression,
                    s.ResultVariable
                }).ToList()
            };

            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }

        public async Task ImportCalculationAsync(string json, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();

            var import = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (import == null || !import.ContainsKey("Id") || !import.ContainsKey("Name"))
                throw new InvalidOperationException("Invalid JSON format for calculation import.");

            var calcId = Guid.Parse(import["Id"].ToString());
            if (await dbContext.Set<Calculation>().AnyAsync(c => c.Id == calcId, cancellationToken))
                throw new InvalidOperationException($"Calculation with ID {calcId} already exists.");

            var calculation = new Calculation
            {
                Id = calcId,
                Name = import["Name"].ToString(),
                Embeddable = import.ContainsKey("Embeddable") && bool.Parse(import["Embeddable"].ToString())
            };
            calculation.SetInterval(int.Parse(import["Interval"].ToString()));

            await dbContext.Set<Calculation>().AddAsync(calculation, cancellationToken);

            if (import.ContainsKey("Steps"))
            {
                var stepsJson = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(import["Steps"].ToString());
                foreach (var stepJson in stepsJson)
                {
                    var step = new CalculationStep
                    {
                        Id = Guid.Parse(stepJson["Id"].ToString()),
                        CalculationId = calcId,
                        StepOrder = int.Parse(stepJson["StepOrder"].ToString()),
                        OperationType = stepJson["OperationType"].ToString(),
                        Expression = stepJson["Expression"].ToString(),
                        ResultVariable = stepJson.ContainsKey("ResultVariable") ? stepJson["ResultVariable"].ToString() : null
                    };
                    await dbContext.Set<CalculationStep>().AddAsync(step, cancellationToken);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Imported calculation {CalculationName}", calculation.Name);
        }
    }
}