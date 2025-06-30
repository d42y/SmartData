using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Core;
using SmartData.Models;
using SmartData.Vectorizer;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartData.Data
{
    public class DataService<T> : IDisposable where T : class
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly DataOptions _options;
        private readonly ILogger<DataService<T>> _logger;
        private readonly IEmbeddingProvider _embedder;
        private readonly IFaissSearch _faissIndex;
        private readonly IEventBus _eventBus; 
        private readonly string _tableName;
        private readonly PropertyInfo _idProperty;
        private readonly List<(PropertyInfo Property, EmbeddableAttribute Attribute)> _embeddableProperties;
        private readonly List<(PropertyInfo Property, TrackChangeAttribute Attribute)> _trackChangeProperties;
        private readonly List<(PropertyInfo Property, EnsureIntegrityAttribute Attribute)> _integrityProperties;
        private readonly List<PropertyInfo> _timeseriesProperties;
        private readonly EmbeddableAttribute _classEmbeddableAttribute;
        private bool _disposed;

        public DataService(IServiceProvider serviceProvider, DataOptions options, string tableName, IEmbeddingProvider embedder = null, IFaissSearch faissIndex = null, IEventBus eventBus = null, ILogger<DataService<T>> logger = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _tableName = Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$")
                ? tableName : throw new ArgumentException("Invalid table name.", nameof(tableName));
            _embedder = embedder;
            _faissIndex = faissIndex;
            _eventBus = eventBus;
            _logger = logger;

            _idProperty = typeof(T).GetProperties()
                .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) || p.GetCustomAttribute<KeyAttribute>() != null)
                ?? throw new InvalidOperationException("Entity must have an Id property.");

            _embeddableProperties = _options.EnableEmbeddings ? typeof(T).GetProperties()
                .Select(p => (Property: p, Attribute: p.GetCustomAttribute<EmbeddableAttribute>()))
                .Where(x => x.Attribute != null)
                .OrderBy(x => x.Attribute.Priority)
                .ToList() : new();

            _classEmbeddableAttribute = _options.EnableEmbeddings ? typeof(T).GetCustomAttribute<EmbeddableAttribute>() : null;

            _trackChangeProperties = _options.EnableChangeTracking ? typeof(T).GetProperties()
                .Select(p => (Property: p, Attribute: p.GetCustomAttribute<TrackChangeAttribute>()))
                .Where(x => x.Attribute != null)
                .ToList() : new();

            _integrityProperties = _options.EnableIntegrityVerification ? typeof(T).GetProperties()
                .Select(p => (Property: p, Attribute: p.GetCustomAttribute<EnsureIntegrityAttribute>()))
                .Where(x => x.Attribute != null)
                .ToList() : new();

            _timeseriesProperties = _options.EnableTimeseries ? typeof(T).GetProperties()
                .Where(p => p.GetCustomAttribute<TimeseriesAttribute>() != null)
                .ToList() : new();

            InitializeAsync().GetAwaiter().GetResult();
        }

        private async Task InitializeAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            dbContext.RegisterEntity(typeof(T), _tableName);
            await dbContext.EnsureSchemaCreatedAsync();

            if (!_options.EnableEmbeddings) return;

            var embeddings = await dbContext.Set<EmbeddingRecord>()
                .Where(e => e.TableName == _tableName)
                .Select(e => new { e.Id, e.Embedding })
                .ToListAsync();

            foreach (var embedding in embeddings)
            {
                if (embedding.Id != Guid.Empty)
                    _faissIndex?.AddEmbedding(embedding.Id, embedding.Embedding);
            }
        }

        public async Task<T> InsertAsync(T entity)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            await dbContext.AddAsync(entity);
            await HandleFeaturesAsync(entity, null, "Insert", dbContext);
            await dbContext.SaveChangesAsync();
            _eventBus?.Publish(new EntityChangeEvent
            {
                TableName = _tableName,
                EntityId = _idProperty.GetValue(entity)?.ToString(),
                Operation = EntityOperation.Insert,
                ChangedProperties = new Dictionary<string, (object, object)>()
            });
            return entity;
        }

        public async Task InsertAsync(IEnumerable<T> entities)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            await dbContext.AddRangeAsync(entities);
            foreach (var entity in entities)
            {
                await HandleFeaturesAsync(entity, null, "Insert", dbContext);
                _eventBus?.Publish(new EntityChangeEvent
                {
                    TableName = _tableName,
                    EntityId = _idProperty.GetValue(entity)?.ToString(),
                    Operation = EntityOperation.Insert,
                    ChangedProperties = new Dictionary<string, (object, object)>()
                });
            }
            await dbContext.SaveChangesAsync();
        }

        public async Task<bool> UpdateAsync(T entity)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
                var id = _idProperty.GetValue(entity);
                var existing = await dbContext.Set<T>().FindAsync(id);
                if (existing == null) return false;

                var changedProperties = GetChangedProperties(existing, entity);
                // Create a copy of the existing entity to preserve original values
                var existingCopy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(existing));
                dbContext.Entry(existing).CurrentValues.SetValues(entity);
                await HandleFeaturesAsync(entity, existingCopy, "Update", dbContext);
                await dbContext.SaveChangesAsync();
                if (changedProperties.Any())
                {
                    _eventBus?.Publish(new EntityChangeEvent
                    {
                        TableName = _tableName,
                        EntityId = id?.ToString(),
                        Operation = EntityOperation.Update,
                        ChangedProperties = changedProperties
                    });
                }
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public async Task<T> UpsertAsync(T entity)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var id = _idProperty.GetValue(entity)?.ToString();
            var existing = await dbContext.Set<T>().FindAsync(id);

            if (existing != null)
            {
                var changedProperties = GetChangedProperties(existing, entity);
                // Create a copy of the existing entity to preserve original values
                var existingCopy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(existing));
                dbContext.Entry(existing).CurrentValues.SetValues(entity);
                await HandleFeaturesAsync(entity, existingCopy, "Update", dbContext);
                await dbContext.SaveChangesAsync();
                if (changedProperties.Any())
                {
                    _eventBus?.Publish(new EntityChangeEvent
                    {
                        TableName = _tableName,
                        EntityId = id,
                        Operation = EntityOperation.Update,
                        ChangedProperties = changedProperties
                    });
                }
            }
            else
            {
                await dbContext.AddAsync(entity);
                await HandleFeaturesAsync(entity, null, "Insert", dbContext);
                await dbContext.SaveChangesAsync();
                _eventBus?.Publish(new EntityChangeEvent
                {
                    TableName = _tableName,
                    EntityId = id,
                    Operation = EntityOperation.Insert,
                    ChangedProperties = new Dictionary<string, (object, object)>()
                });
            }

            return entity;
        }

        public async Task<List<T>> UpsertAsync(IEnumerable<T> entities)
        {
            var updatedEntities = new List<T>();
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entityIds = entities.Select(e => _idProperty.GetValue(e)?.ToString()).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var existingEntities = await dbContext.Set<T>()
                .Where(e => entityIds.Contains(EF.Property<string>(e, _idProperty.Name)))
                .ToDictionaryAsync(e => _idProperty.GetValue(e)?.ToString(), e => e);

            using var transaction = await dbContext.Database.BeginTransactionAsync();
            try
            {
                foreach (var entity in entities)
                {
                    var id = _idProperty.GetValue(entity)?.ToString();
                    if (existingEntities.TryGetValue(id, out var existing))
                    {
                        var changedProperties = GetChangedProperties(existing, entity);
                        // Create a copy of the existing entity to preserve original values
                        var existingCopy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(existing));
                        dbContext.Entry(existing).CurrentValues.SetValues(entity);
                        await HandleFeaturesAsync(entity, existingCopy, "Update", dbContext);
                        if (changedProperties.Any())
                        {
                            _eventBus?.Publish(new EntityChangeEvent
                            {
                                TableName = _tableName,
                                EntityId = id,
                                Operation = EntityOperation.Update,
                                ChangedProperties = changedProperties
                            });
                        }
                    }
                    else
                    {
                        await dbContext.AddAsync(entity);
                        await HandleFeaturesAsync(entity, null, "Insert", dbContext);
                        _eventBus?.Publish(new EntityChangeEvent
                        {
                            TableName = _tableName,
                            EntityId = id,
                            Operation = EntityOperation.Insert,
                            ChangedProperties = new Dictionary<string, (object, object)>()
                        });
                    }
                    updatedEntities.Add(entity);
                }
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
            return updatedEntities;
        }

        public async Task<bool> DeleteAsync(object id)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entity = await dbContext.Set<T>().FindAsync(id);
            if (entity == null) return false;

            await HandleFeaturesAsync(null, entity, "Delete", dbContext);
            dbContext.Remove(entity);
            await dbContext.SaveChangesAsync();
            _eventBus?.Publish(new EntityChangeEvent
            {
                TableName = _tableName,
                EntityId = id?.ToString(),
                Operation = EntityOperation.Delete,
                ChangedProperties = new Dictionary<string, (object, object)>()
            });
            return true;
        }

        public async Task<int> DeleteAllAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entities = await dbContext.Set<T>().ToListAsync();
            var count = entities.Count;

            foreach (var entity in entities)
            {
                await HandleFeaturesAsync(null, entity, "Delete", dbContext);
                _eventBus?.Publish(new EntityChangeEvent
                {
                    TableName = _tableName,
                    EntityId = _idProperty.GetValue(entity)?.ToString(),
                    Operation = EntityOperation.Delete,
                    ChangedProperties = new Dictionary<string, (object, object)>()
                });
            }

            await dbContext.SaveChangesAsync();

            await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM [{_tableName}]");
            if (_options.EnableEmbeddings)
                await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM sysEmbeddings WHERE TableName = {{0}}", _tableName);
            if (_options.EnableTimeseries)
            {
                await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM sysTimeseriesDeltas WHERE BaseValueId IN (SELECT Id FROM sysTimeseriesBaseValues WHERE TableName = {{0}})", _tableName);
                await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM sysTimeseriesBaseValues WHERE TableName = {{0}}", _tableName);
            }
            await dbContext.SaveChangesAsync();
            return count;
        }

        private Dictionary<string, (object OldValue, object NewValue)> GetChangedProperties(T oldEntity, T newEntity)
        {
            var changedProperties = new Dictionary<string, (object, object)>();
            foreach (var prop in typeof(T).GetProperties())
            {
                var oldValue = prop.GetValue(oldEntity);
                var newValue = prop.GetValue(newEntity);
                if (!Equals(oldValue, newValue))
                    changedProperties.Add(prop.Name, (oldValue, newValue));
            }
            return changedProperties;
        }

        public async Task<T?> FindByIdAsync(object id)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entity = await dbContext.Set<T>().FindAsync(id);
            if (_options.EnableIntegrityVerification && entity != null)
                await VerifyIntegrityAsync(new[] { entity }, dbContext);
            return entity;
        }

        public async Task<List<T>> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entities = await dbContext.Set<T>().Where(predicate).Skip(skip).Take(limit).ToListAsync();
            if (_options.EnableIntegrityVerification)
                await VerifyIntegrityAsync(entities, dbContext);
            return entities;
        }

        public async Task<List<T>> FindAllAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entities = await dbContext.Set<T>().ToListAsync();
            if (_options.EnableIntegrityVerification)
                await VerifyIntegrityAsync(entities, dbContext);
            return entities;
        }

        public async Task<long> CountAsync(Expression<Func<T, bool>> predicate = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            return predicate == null ? await dbContext.Set<T>().LongCountAsync() : await dbContext.Set<T>().LongCountAsync(predicate);
        }

        public async Task<List<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var entities = await dbContext.Set<T>().FromSqlRaw(sql, parameters).ToListAsync();
            if (_options.EnableIntegrityVerification)
                await VerifyIntegrityAsync(entities, dbContext);
            return entities;
        }

        public async Task<List<QueryResult>> SearchAsync(string query, int topK = 1)
        {
            if (!_options.EnableEmbeddings || string.IsNullOrEmpty(query) || topK < 1)
                return new List<QueryResult>();

            var queryEmbedding = _embedder.GenerateEmbedding(query).ToArray();
            var embeddingIds = _faissIndex.Search(queryEmbedding, topK);

            var results = new List<QueryResult>();
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();

            var records = await dbContext.Set<EmbeddingRecord>()
                .Where(e => embeddingIds.Contains(e.Id))
                .Select(e => new { e.Id, e.EntityId })
                .ToListAsync();

            foreach (var record in records)
            {
                var entity = await FindByIdAsync(record.EntityId);
                if (entity != null)
                {
                    var data = typeof(T).GetProperties()
                        .ToDictionary(p => p.Name, p => p.GetValue(entity));
                    data["Score"] = 1.0f; // Placeholder score
                    results.Add(new QueryResult(data));
                }
            }
            return results;
        }

        public async Task<List<TimeseriesResult>> GetTimeseriesAsync(string entityId, string propertyName, DateTime start, DateTime end)
        {
            if (!_options.EnableTimeseries) return new List<TimeseriesResult>();

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
            var baseValues = await dbContext.Set<TimeseriesBaseValue>()
                .Where(b => b.TableName == _tableName && b.EntityId == entityId && b.PropertyName == propertyName && b.Timestamp <= end)
                .OrderBy(b => b.Timestamp)
                .ToListAsync();

            var results = new List<TimeseriesResult>();
            foreach (var baseValue in baseValues)
            {
                var delta = await dbContext.Set<TimeseriesDelta>()
                    .FirstOrDefaultAsync(d => d.BaseValueId == baseValue.Id);
                if (delta == null) continue;

                var deltas = delta.GetDeltas();
                int currentTime = 0;
                foreach (var d in deltas)
                {
                    currentTime += d;
                    var timestamp = baseValue.Timestamp.AddMilliseconds(currentTime);
                    if (timestamp >= start && timestamp <= end)
                        results.Add(new TimeseriesResult { Timestamp = timestamp, Value = baseValue.Value });
                }
            }
            return results.OrderBy(r => r.Timestamp).ToList();
        }

        public async Task<List<TimeseriesResult>> GetInterpolatedTimeseriesAsync(string entityId, string propertyName, DateTime start, DateTime end, TimeSpan interval, InterpolationMethod method)
        {
            if (!_options.EnableTimeseries) return new List<TimeseriesResult>();

            var timeseries = await GetTimeseriesAsync(entityId, propertyName, start, end);
            var result = new List<TimeseriesResult>();

            if (!timeseries.Any()) return result;

            for (var currentTime = start; currentTime <= end; currentTime = currentTime.Add(interval))
            {
                if (method == InterpolationMethod.None)
                {
                    var exactMatch = timeseries.FirstOrDefault(t => t.Timestamp == currentTime);
                    if (exactMatch != null)
                        result.Add(new TimeseriesResult { Timestamp = currentTime, Value = exactMatch.Value });
                }
                else
                {
                    double? value = null;
                    var previous = timeseries.LastOrDefault(t => t.Timestamp <= currentTime);
                    var next = timeseries.FirstOrDefault(t => t.Timestamp >= currentTime);

                    if (previous == null && next == null) continue;

                    switch (method)
                    {
                        case InterpolationMethod.Linear:
                            if (previous != null && next != null)
                            {
                                if (double.TryParse(previous.Value, out var prevValue) && double.TryParse(next.Value, out var nextValue))
                                {
                                    var totalTime = (next.Timestamp - previous.Timestamp).TotalMilliseconds;
                                    if (totalTime > 0)
                                    {
                                        var elapsed = (currentTime - previous.Timestamp).TotalMilliseconds;
                                        var fraction = elapsed / totalTime;
                                        value = prevValue + (nextValue - prevValue) * fraction;
                                    }
                                }
                            }
                            else if (previous != null)
                            {
                                if (double.TryParse(previous.Value, out var prevValue))
                                    value = prevValue;
                            }
                            else if (next != null)
                            {
                                if (double.TryParse(next.Value, out var nextValue))
                                    value = nextValue;
                            }
                            break;
                        case InterpolationMethod.Nearest:
                            if (previous != null && next != null)
                            {
                                var prevDiff = Math.Abs((currentTime - previous.Timestamp).TotalMilliseconds);
                                var nextDiff = Math.Abs((currentTime - next.Timestamp).TotalMilliseconds);
                                value = prevDiff <= nextDiff ? double.Parse(previous.Value) : double.Parse(next.Value);
                            }
                            else if (previous != null)
                                value = double.Parse(previous.Value);
                            else if (next != null)
                                value = double.Parse(next.Value);
                            break;
                        case InterpolationMethod.Previous when previous != null:
                            value = double.Parse(previous.Value);
                            break;
                        case InterpolationMethod.Next when next != null:
                            value = double.Parse(next.Value);
                            break;
                    }

                    if (value.HasValue)
                        result.Add(new TimeseriesResult { Timestamp = currentTime, Value = value.Value.ToString("F2") });
                }
            }
            return result;
        }

        private async Task HandleFeaturesAsync(T newEntity, T oldEntity, string changeType, DataContext dbContext)
        {
            if (_options.EnableChangeTracking)
                await LogChangesAsync(newEntity, oldEntity, changeType, dbContext);
            if (_options.EnableIntegrityVerification)
                await LogIntegrityAsync(newEntity ?? oldEntity, dbContext);
            if (_options.EnableEmbeddings && newEntity != null)
                await StoreEmbeddingAsync(newEntity, dbContext);
            if (_options.EnableTimeseries && newEntity != null)
                await StoreTimeseriesAsync(newEntity, dbContext);
        }

        private async Task StoreEmbeddingAsync(T entity, DataContext dbContext)
        {
            if (!_embeddableProperties.Any() && _classEmbeddableAttribute == null) return;

            var entityId = _idProperty.GetValue(entity)?.ToString() ?? throw new InvalidOperationException("Entity ID cannot be null.");
            var parser = new EmbeddingExpressionParser(dbContext);
            var paragraph = await GenerateEmbeddingTextAsync(entity, parser);
            if (string.IsNullOrEmpty(paragraph)) return;

            var embedding = _embedder.GenerateEmbedding(paragraph).ToArray();
            var record = await dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && e.EntityId == entityId);

            var embeddingId = Guid.NewGuid();
            if (record == null)
            {
                record = new EmbeddingRecord { Id = embeddingId, EntityId = entityId, Embedding = embedding, TableName = _tableName };
                await dbContext.AddAsync(record);
            }
            else
            {
                record.Embedding = embedding;
                dbContext.Update(record);
                if (Guid.TryParse(entityId, out var guidId))
                    _faissIndex.UpdateEmbedding(guidId, embedding);
            }

            await dbContext.SaveChangesAsync();
            _faissIndex.AddEmbedding(embeddingId, embedding);
        }

        private async Task<string> GenerateEmbeddingTextAsync(T entity, EmbeddingExpressionParser parser)
        {
            var sb = new StringBuilder();
            var properties = typeof(T).GetProperties().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var (property, attr) in _embeddableProperties.OrderBy(x => x.Attribute.Priority))
            {
                try
                {
                    var formatted = await FormatWithNamedPlaceholders(attr.Format, entity, properties, parser);
                    if (!string.IsNullOrEmpty(formatted))
                        sb.Append(formatted + " ");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to format Embeddable attribute for {PropertyName}", property.Name);
                }
            }

            if (_classEmbeddableAttribute != null)
            {
                try
                {
                    var formatted = await FormatWithNamedPlaceholders(_classEmbeddableAttribute.Format, entity, properties, parser);
                    if (!string.IsNullOrEmpty(formatted))
                        sb.Append(formatted + " ");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to format class-level Embeddable attribute for {TypeName}", typeof(T).Name);
                }
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }

        private async Task<string> FormatWithNamedPlaceholders(string format, T entity, Dictionary<string, PropertyInfo> properties, EmbeddingExpressionParser parser)
        {
            if (string.IsNullOrEmpty(format)) return string.Empty;

            var result = format;
            var matches = Regex.Matches(format, @"\{([^{}]+)\}");
            foreach (Match match in matches)
            {
                var placeholder = match.Groups[1].Value;
                string value = string.Empty;

                if (placeholder.Contains(".") && placeholder.Contains("("))
                {
                    var parts = placeholder.Split(new[] { '.', '(' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && parts[2].EndsWith(")"))
                    {
                        var navPropertyName = parts[0];
                        var aggregateFunction = parts[1];
                        var targetProperty = parts[2].TrimEnd(')');
                        if (properties.TryGetValue(navPropertyName, out var navProperty))
                            value = await ComputeAggregateAsync(entity, navProperty, aggregateFunction, targetProperty);
                    }
                }
                else if (placeholder.Contains("."))
                {
                    var parts = placeholder.Split('.');
                    if (parts.Length == 2 && properties.TryGetValue(parts[0], out var navProperty))
                        value = parser.EvaluateExpression(entity, typeof(T), $"{{$.{parts[0]}.{parts[1]}}}");
                }
                else if (properties.TryGetValue(placeholder, out var property))
                {
                    value = property.GetValue(entity)?.ToString() ?? string.Empty;
                }
                result = result.Replace($"{{{placeholder}}}", value);
            }
            return result;
        }

        private async Task<string> ComputeAggregateAsync(T entity, PropertyInfo navProperty, string aggregateFunction, string targetProperty)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();

            var navType = navProperty.PropertyType;
            if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(navType) || navType == typeof(string)) return string.Empty;

            var elementType = navType.GetGenericArguments().FirstOrDefault() ?? navType.GetElementType();
            if (elementType == null) return string.Empty;

            var targetProp = elementType.GetProperty(targetProperty, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (targetProp == null) return string.Empty;

            var entityId = _idProperty.GetValue(entity)?.ToString();
            if (string.IsNullOrEmpty(entityId)) return string.Empty;

            var queryMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes).MakeGenericMethod(elementType);
            var query = (IQueryable)queryMethod.Invoke(dbContext, null);

            var foreignKeyProp = elementType.GetProperty($"{typeof(T).Name}Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (foreignKeyProp == null) return string.Empty;

            var parameter = Expression.Parameter(elementType, "e");
            var propertyAccess = Expression.Property(parameter, foreignKeyProp);
            var constant = Expression.Constant(entityId);
            var equal = Expression.Equal(propertyAccess, constant);
            var lambda = Expression.Lambda(equal, parameter);
            var whereMethod = typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.Where) && m.GetParameters().Length == 2)
                .MakeGenericMethod(elementType);
            query = (IQueryable)whereMethod.Invoke(null, new object[] { query, lambda });

            var propType = targetProp.PropertyType;
            object? result = aggregateFunction.ToUpper() switch
            {
                "MAX" => await ComputeMaxAsync(query, targetProp, propType),
                "MIN" => await ComputeMinAsync(query, targetProp, propType),
                "COUNT" => await query.Cast<object>().CountAsync(),
                _ => null
            };
            return result?.ToString() ?? string.Empty;
        }

        private async Task<object> ComputeMaxAsync(IQueryable query, PropertyInfo targetProp, Type propType)
        {
            var parameter = Expression.Parameter(query.ElementType, "e");
            var propertyAccess = Expression.Property(parameter, targetProp);
            var lambda = Expression.Lambda(propertyAccess, parameter);
            var maxMethod = typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.Max) && m.GetParameters().Length == 2)
                .MakeGenericMethod(query.ElementType, propType);
            var task = (Task)maxMethod.Invoke(null, new object[] { query, lambda });
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        private async Task<object> ComputeMinAsync(IQueryable query, PropertyInfo targetProp, Type propType)
        {
            var parameter = Expression.Parameter(query.ElementType, "e");
            var propertyAccess = Expression.Property(parameter, targetProp);
            var lambda = Expression.Lambda(propertyAccess, parameter);
            var minMethod = typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.Min) && m.GetParameters().Length == 2)
                .MakeGenericMethod(query.ElementType, propType);
            var task = (Task)minMethod.Invoke(null, new object[] { query, lambda });
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        private async Task LogChangesAsync(T newEntity, T oldEntity, string changeType, DataContext dbContext)
        {
            if (!_trackChangeProperties.Any()) return;
            var entity = newEntity ?? oldEntity;
            var entityId = entity != null ? _idProperty.GetValue(entity)?.ToString() : null;
            if (string.IsNullOrEmpty(entityId)) return;

            string changedBy = dbContext.Database.GetConnectionString().Contains("Integrated Security=True", StringComparison.OrdinalIgnoreCase)
                ? System.Security.Principal.WindowsIdentity.GetCurrent().Name
                : "System";

            foreach (var (property, _) in _trackChangeProperties)
            {
                var original = changeType != "Insert" && oldEntity != null ? property.GetValue(oldEntity) : null;
                var newValue = changeType != "Delete" && newEntity != null ? property.GetValue(newEntity) : null;

                if (changeType == "Insert" && newValue != null ||
                    changeType == "Delete" && original != null ||
                    changeType == "Update" && !Equals(original, newValue))
                {
                    await dbContext.AddAsync(new ChangeLogRecord
                    {
                        Id = Guid.NewGuid(),
                        TableName = _tableName,
                        EntityId = entityId,
                        ChangedBy = changedBy,
                        ChangedAt = DateTime.UtcNow,
                        OriginalValue = original != null ? JsonSerializer.Serialize(original) : null,
                        NewValue = newValue != null ? JsonSerializer.Serialize(newValue) : null,
                        ChangeType = changeType,
                        PropertyName = property.Name
                    });
                }
            }
        }

        private async Task LogIntegrityAsync(T entity, DataContext dbContext)
        {
            if (!_integrityProperties.Any()) return;
            var entityId = _idProperty.GetValue(entity)?.ToString() ?? throw new InvalidOperationException("Entity ID cannot be null.");

            foreach (var (property, _) in _integrityProperties)
            {
                var value = property.GetValue(entity)?.ToString() ?? string.Empty;
                var hash = ComputeSHA256Hash(value);
                var previousHash = await dbContext.Set<IntegrityLogRecord>()
                    .Where(l => l.TableName == _tableName && l.EntityId == entityId && l.PropertyName == property.Name)
                    .OrderByDescending(l => l.Timestamp)
                    .Select(l => l.Hash)
                    .FirstOrDefaultAsync() ?? string.Empty;

                await dbContext.AddAsync(new IntegrityLogRecord
                {
                    Id = Guid.NewGuid(),
                    TableName = _tableName,
                    EntityId = entityId,
                    PropertyName = property.Name,
                    Hash = hash,
                    PreviousHash = previousHash,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        private async Task VerifyIntegrityAsync(IEnumerable<T> entities, DataContext dbContext)
        {
            if (!_integrityProperties.Any()) return;
            var entityIds = entities.Select(e => _idProperty.GetValue(e)?.ToString()).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (!entityIds.Any()) return;

            var logs = await dbContext.Set<IntegrityLogRecord>()
                .Where(l => l.TableName == _tableName && entityIds.Contains(l.EntityId))
                .GroupBy(l => new { l.EntityId, l.PropertyName })
                .Select(g => g.OrderByDescending(l => l.Timestamp).FirstOrDefault())
                .ToListAsync();

            var logLookup = logs.ToLookup(l => l.EntityId + l.PropertyName);

            foreach (var entity in entities)
            {
                var entityId = _idProperty.GetValue(entity)?.ToString();
                foreach (var (property, _) in _integrityProperties)
                {
                    var currentValue = property.GetValue(entity)?.ToString() ?? string.Empty;
                    var currentHash = ComputeSHA256Hash(currentValue);
                    var log = logLookup[entityId + property.Name].FirstOrDefault();
                    if (log != null && log.Hash != currentHash)
                        throw new DataIntegrityException(_tableName, entityId, property.Name, log.Hash, currentHash,
                            $"Integrity check failed for {property.Name} in {_tableName} EntityId {entityId}");

                    var hashChainValid = await VerifyHashChainAsync(_tableName, entityId, property.Name, log, dbContext);
                    if (!hashChainValid)
                        throw new DataIntegrityException(_tableName, entityId, property.Name, log.Hash, currentHash,
                            $"Hash chain verification failed for {property.Name} in {_tableName} EntityId {entityId}");
                }
            }
        }

        private async Task<bool> VerifyHashChainAsync(string tableName, string entityId, string propertyName, IntegrityLogRecord latestLog, DataContext dbContext)
        {
            var logs = await dbContext.Set<IntegrityLogRecord>()
                .Where(l => l.TableName == tableName && l.EntityId == entityId && l.PropertyName == propertyName)
                .OrderByDescending(l => l.Timestamp)
                .ToListAsync();

            var currentLog = latestLog;
            foreach (var previousLog in logs.Skip(1))
            {
                if (currentLog.PreviousHash != previousLog.Hash)
                    return false;
                currentLog = previousLog;
            }
            return true;
        }

        private async Task StoreTimeseriesAsync(T entity, DataContext dbContext, int retryCount = 0, int maxRetries = 3)
        {
            if (!_timeseriesProperties.Any()) return;
            if (retryCount > maxRetries)
            {
                _logger?.LogError("Max retries exceeded for timeseries storage for entity {EntityId}.", _idProperty.GetValue(entity)?.ToString());
                throw new DbUpdateConcurrencyException("Max retries exceeded for timeseries storage.");
            }

            var entityId = _idProperty.GetValue(entity)?.ToString() ?? throw new InvalidOperationException("Entity ID cannot be null.");
            var timestamp = DateTime.UtcNow;

            IDbContextTransaction transaction = null;
            bool ownsTransaction = false;
            try
            {
                if (dbContext.Database.CurrentTransaction == null)
                {
                    transaction = await dbContext.Database.BeginTransactionAsync();
                    ownsTransaction = true;
                }

                foreach (var property in _timeseriesProperties)
                {
                    var value = property.GetValue(entity)?.ToString() ?? string.Empty;
                    _logger?.LogInformation("Processing timeseries for property {PropertyName} with value {Value} for entity {EntityId}.", property.Name, value, entityId);

                    var baseValue = await dbContext.Set<TimeseriesBaseValue>()
                        .FirstOrDefaultAsync(b => b.TableName == _tableName && b.EntityId == entityId && b.PropertyName == property.Name && b.Value == value);

                    if (baseValue == null)
                    {
                        baseValue = new TimeseriesBaseValue
                        {
                            Id = Guid.NewGuid(),
                            TableName = _tableName,
                            EntityId = entityId,
                            PropertyName = property.Name,
                            Value = value,
                            Timestamp = timestamp
                        };
                        await dbContext.AddAsync(baseValue);
                        _logger?.LogInformation("Added new TimeseriesBaseValue for {PropertyName} with ID {BaseValueId}.", property.Name, baseValue.Id);
                    }

                    var delta = await dbContext.Set<TimeseriesDelta>()
                        .FirstOrDefaultAsync(d => d.BaseValueId == baseValue.Id);

                    if (delta == null)
                    {
                        delta = new TimeseriesDelta
                        {
                            Id = Guid.NewGuid(),
                            BaseValueId = baseValue.Id,
                            Version = 1
                        };
                        delta.AddTimestamp((int)(timestamp - baseValue.Timestamp).TotalMilliseconds);
                        await dbContext.AddAsync(delta);
                        _logger?.LogInformation("Added new TimeseriesDelta for BaseValueId {BaseValueId}.", baseValue.Id);
                    }
                    else
                    {
                        delta.AddTimestamp((int)(timestamp - baseValue.Timestamp).TotalMilliseconds);
                        dbContext.Update(delta);
                        _logger?.LogInformation("Updated TimeseriesDelta for BaseValueId {BaseValueId} with Version {Version}.", baseValue.Id, delta.Version);
                    }
                }

                await dbContext.SaveChangesAsync();
                if (ownsTransaction)
                {
                    await transaction.CommitAsync();
                    _logger?.LogInformation("Successfully saved timeseries data for entity {EntityId}.", entityId);
                }
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (ownsTransaction)
                    await transaction.RollbackAsync();
                _logger?.LogWarning(ex, "Concurrency conflict on attempt {RetryCount} for timeseries storage of entity {EntityId}. Retrying...", retryCount + 1, entityId);
                await Task.Delay(200);
                await StoreTimeseriesAsync(entity, dbContext, retryCount + 1, maxRetries);
            }
            catch (Exception ex)
            {
                if (ownsTransaction)
                    await transaction.RollbackAsync();
                _logger?.LogError(ex, "Failed to save timeseries data for entity {EntityId}.", entityId);
                throw;
            }
            finally
            {
                if (ownsTransaction)
                    transaction?.Dispose();
            }
        }

        private string ComputeSHA256Hash(string input)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(input)));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _faissIndex?.Dispose();
                _disposed = true;
            }
        }
    }
}