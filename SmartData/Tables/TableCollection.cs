using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Attributes;
using SmartData.Configurations;
using SmartData.Exceptions;
using SmartData.GPT.Embedder;
using SmartData.GPT.Search;
using SmartData.Tables.Models;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartData.Tables
{
    public class TableCollection<T> : ITableCollection<T> where T : class
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly string _tableName;
        private readonly PropertyInfo _idProperty;
        private readonly List<(PropertyInfo Property, EmbeddableAttribute Attribute)> _embeddableProperties;
        private readonly List<(PropertyInfo Property, TrackChangeAttribute Attribute)> _trackChangeProperties;
        private readonly List<(PropertyInfo Property, EnsureIntegrityAttribute Attribute)> _ensureIntegrityProperties; // CHANGED
        private readonly List<PropertyInfo> _timeseriesProperties;
        private readonly ILogger _logger;
        private readonly IEmbedder _embedder;
        private readonly bool _embeddingEnabled;
        private readonly bool _timeseriesEnabled;
        private readonly bool _changeTrackingEnabled; // NEW
        private readonly bool _integrityVerificationEnabled; // NEW
        private readonly FaissNetSearch _faissIndex;

        private readonly EmbeddableAttribute _classEmbeddableAttribute; // NEW: Store class-level attribute

        public TableCollection(IServiceProvider serviceProvider, string tableName, bool embeddingEnabled, bool timeseriesEnabled, IEmbedder embedder = null, FaissNetSearch faissIndex = null, ILogger logger = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            _embeddingEnabled = embeddingEnabled;
            _timeseriesEnabled = timeseriesEnabled;
            _changeTrackingEnabled = serviceProvider.GetRequiredService<SmartDataOptions>().ChangeTrackingEnabled;
            _integrityVerificationEnabled = serviceProvider.GetRequiredService<SmartDataOptions>().IntegrityVerificationEnabled;
            _embedder = embeddingEnabled ? embedder ?? throw new ArgumentNullException(nameof(embedder)) : null;
            _faissIndex = embeddingEnabled ? faissIndex ?? throw new ArgumentNullException(nameof(faissIndex)) : null;
            _logger = logger;
            _idProperty = typeof(T).GetProperties()
                .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                     p.GetCustomAttribute<KeyAttribute>() != null)
                ?? throw new InvalidOperationException("Entity must have an Id property.");
            _embeddableProperties = embeddingEnabled ? typeof(T).GetProperties()
                .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<EmbeddableAttribute>() })
                .Where(x => x.Attribute != null)
                .OrderBy(x => x.Attribute.Priority)
                .Select(x => (x.Property, x.Attribute))
                .ToList() : new List<(PropertyInfo, EmbeddableAttribute)>();
            _classEmbeddableAttribute = embeddingEnabled ? typeof(T).GetCustomAttribute<EmbeddableAttribute>() : null; // NEW: Get class-level attribute
            _trackChangeProperties = _changeTrackingEnabled ? typeof(T).GetProperties()
                .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<TrackChangeAttribute>() })
                .Where(x => x.Attribute != null)
                .Select(x => (x.Property, x.Attribute))
                .ToList() : new List<(PropertyInfo, TrackChangeAttribute)>();
            _ensureIntegrityProperties = _integrityVerificationEnabled ? typeof(T).GetProperties()
                .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<EnsureIntegrityAttribute>() })
                .Where(x => x.Attribute != null)
                .Select(x => (x.Property, x.Attribute))
                .ToList() : new List<(PropertyInfo, EnsureIntegrityAttribute)>();
            _timeseriesProperties = timeseriesEnabled ? typeof(T).GetProperties()
                .Where(p => p.GetCustomAttribute<TimeseriesAttribute>() != null)
                .ToList() : new List<PropertyInfo>();
        }

        public async Task<T> InsertAsync(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            await dbSet.AddAsync(entity);
            await dbContext.SaveChangesAsync();

            // NEW: Log changes for TrackChange properties
            if (_changeTrackingEnabled)
                await LogChangesAsync(entity, null, "Insert", dbContext);

            // NEW: Log integrity for EnsureIntegrity properties
            if (_integrityVerificationEnabled)
                await LogIntegrityAsync(entity, dbContext);

            if (_embeddingEnabled) await GenerateAndStoreEmbeddingAsync(entity);
            if (_timeseriesEnabled) await StoreTimeseriesAsync(entity);
            await dbContext.SaveChangesAsync();
            return entity;
        }

        public async Task InsertAsync(IEnumerable<T> entities)
        {
            if (entities == null || !entities.Any()) throw new ArgumentException("Entities cannot be null or empty.", nameof(entities));
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            await dbSet.AddRangeAsync(entities);
            await dbContext.SaveChangesAsync();

            foreach (var entity in entities)
            {
                // NEW: Log changes for TrackChange properties
                if (_changeTrackingEnabled)
                    await LogChangesAsync(entity, null, "Insert", dbContext);

                // NEW: Log integrity for EnsureIntegrity properties
                if (_integrityVerificationEnabled)
                    await LogIntegrityAsync(entity, dbContext);

                if (_embeddingEnabled) await GenerateAndStoreEmbeddingAsync(entity);
                if (_timeseriesEnabled) await StoreTimeseriesAsync(entity);
            }
            await dbContext.SaveChangesAsync();
        }

        public async Task<bool> UpdateAsync(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                var dbSet = dbContext.Set<T>();
                var id = _idProperty.GetValue(entity);
                var existing = await dbSet.FindAsync(id);
                if (existing == null) return false;

                // NEW: Capture original values for TrackChange
                var originalEntity = existing;

                dbContext.Entry(existing).CurrentValues.SetValues(entity);

                // NEW: Log changes for TrackChange properties
                if (_changeTrackingEnabled)
                    await LogChangesAsync(entity, originalEntity, "Update", dbContext);

                // NEW: Verify and log integrity for EnsureIntegrity properties
                if (_integrityVerificationEnabled) await EnsureIntegrityAsync(new[] { originalEntity }, dbContext);

                await dbContext.SaveChangesAsync();
                if (_embeddingEnabled) await GenerateAndStoreEmbeddingAsync(entity);
                if (_timeseriesEnabled) await StoreTimeseriesAsync(entity);
                await dbContext.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger?.LogWarning(ex, "Concurrency conflict updating entity in table {TableName}", _tableName);
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update entity in table {TableName}", _tableName);
                throw new InvalidOperationException($"Failed to update entity in {_tableName}: {ex.Message}", ex);
            }
        }

        public async Task<bool> ExistsAsync(object id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            return await dbSet.AnyAsync(e => EF.Property<object>(e, _idProperty.Name).Equals(id));
        }

        public async Task<T> UpsertAsync(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();

            var id = _idProperty.GetValue(entity)?.ToString()
                ?? throw new InvalidOperationException("Entity ID cannot be null.");

            var existing = await dbSet.FindAsync(id);
            if (existing != null)
            {
                // NEW: Capture original values for TrackChange
                var originalEntity = existing;

                dbContext.Entry(existing).CurrentValues.SetValues(entity);

                // NEW: Log changes for TrackChange properties
                if (_changeTrackingEnabled)
                    await LogChangesAsync(entity, originalEntity, "Update", dbContext);

                if (_integrityVerificationEnabled)
                    await EnsureIntegrityAsync(new[] { originalEntity }, dbContext);
            }
            else
            {
                await dbSet.AddAsync(entity);

                // NEW: Log changes for TrackChange properties
                if (_changeTrackingEnabled)
                    await LogChangesAsync(entity, null, "Insert", dbContext);

                // NEW: Log integrity for EnsureIntegrity properties
                if (_integrityVerificationEnabled)
                    await LogIntegrityAsync(entity, dbContext);
            }

            await dbContext.SaveChangesAsync();
            if (_embeddingEnabled) await GenerateAndStoreEmbeddingAsync(entity);
            if (_timeseriesEnabled) await StoreTimeseriesAsync(entity);
            return entity;
        }

        public async Task<List<T>> UpsertAsync(IEnumerable<T> entities)
        {
            if (entities == null || !entities.Any()) throw new ArgumentException("Entities cannot be null or empty.", nameof(entities));

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var updatedEntities = new List<T>();

            // Batch fetch existing entities
            var entityIds = entities.Select(e => _idProperty.GetValue(e)?.ToString())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
            if (!entityIds.Any()) throw new InvalidOperationException("All entities must have non-null IDs.");

            var existingEntities = await dbSet
                .Where(e => entityIds.Contains(EF.Property<string>(e, _idProperty.Name)))
                .ToDictionaryAsync(e => _idProperty.GetValue(e)?.ToString(), e => e);

            using var transaction = await dbContext.Database.BeginTransactionAsync();

            try
            {
                foreach (var entity in entities)
                {
                    var id = _idProperty.GetValue(entity)?.ToString()
                        ?? throw new InvalidOperationException($"Entity ID cannot be null for entity in {_tableName}");

                    if (existingEntities.TryGetValue(id, out var existing))
                    {
                        var originalEntity = existing;
                        dbContext.Entry(existing).CurrentValues.SetValues(entity);

                        if (_changeTrackingEnabled)
                            await LogChangesAsync(entity, originalEntity, "Update", dbContext);

                        if (_integrityVerificationEnabled)
                            await EnsureIntegrityAsync(new[] { originalEntity }, dbContext);

                        updatedEntities.Add(entity);
                    }
                    else
                    {
                        await dbSet.AddAsync(entity);

                        if (_changeTrackingEnabled)
                            await LogChangesAsync(entity, null, "Insert", dbContext);

                        if (_integrityVerificationEnabled)
                            await LogIntegrityAsync(entity, dbContext);

                        updatedEntities.Add(entity);
                    }

                    if (_embeddingEnabled) await GenerateAndStoreEmbeddingAsync(entity);
                    if (_timeseriesEnabled) await StoreTimeseriesAsync(entity);
                }

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger?.LogError(ex, "Failed to upsert entities in table {TableName}", _tableName);
                throw new InvalidOperationException($"Failed to upsert entities in {_tableName}: {ex.Message}", ex);
            }

            return updatedEntities;
        }

        public async Task<bool> DeleteAsync(object id)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var entity = await dbSet.FindAsync(id);
            if (entity == null) return false;

            // NEW: Log changes for TrackChange properties
            if (_changeTrackingEnabled)
                await LogChangesAsync(null, entity, "Delete", dbContext);

            if (_integrityVerificationEnabled)
                await EnsureIntegrityAsync(new[] { entity }, dbContext);

            dbSet.Remove(entity);
            if (_embeddingEnabled) await RemoveEmbeddingAsync(id);
            if (_timeseriesEnabled)
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"DELETE FROM sysTsDeltaT WHERE BaseValueId IN (SELECT Id FROM sysTsBaseValues WHERE TableName = {{0}} AND EntityId = {{1}})",
                    _tableName, id.ToString());
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"DELETE FROM sysTsBaseValues WHERE TableName = {{0}} AND EntityId = {{1}}",
                    _tableName, id.ToString());
            }
            await dbContext.SaveChangesAsync();
            return true;
        }

        public async Task<int> DeleteAllAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var entities = await dbSet.ToListAsync();
            var count = entities.Count;

            foreach (var entity in entities)
            {
                // NEW: Log changes for TrackChange properties
                if (_changeTrackingEnabled)
                    await LogChangesAsync(null, entity, "Delete", dbContext);
            }
            if (_integrityVerificationEnabled)
                await EnsureIntegrityAsync(entities, dbContext);

            await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM [{_tableName}]");
            if (_embeddingEnabled)
                await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM sysEmbeddings WHERE TableName = {{0}}", _tableName);
            if (_timeseriesEnabled)
            {
                await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM sysTsDeltaT WHERE BaseValueId IN (SELECT Id FROM sysTsBaseValues WHERE TableName = {{0}})", _tableName);
                await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM sysTsBaseValues WHERE TableName = {{0}}", _tableName);
            }

            return count;
        }

        public async Task<T?> FindByIdAsync(object id)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var entity = await dbSet.FindAsync(id);

            if (_integrityVerificationEnabled && entity != null)
                await EnsureIntegrityAsync(new[] { entity }, dbContext);

            return entity;
        }

        public async Task<List<T>> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var entities = await dbSet.Where(predicate)
                .Skip(skip)
                .Take(limit)
                .ToListAsync();

            if (_integrityVerificationEnabled)
                await EnsureIntegrityAsync(entities, dbContext);

            return entities;
        }

        public async Task<List<T>> FindAllAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var entities = await dbSet.ToListAsync();

            if (_integrityVerificationEnabled)
                await EnsureIntegrityAsync(entities, dbContext);

            return entities;
        }

        public async Task<long> CountAsync(Expression<Func<T, bool>> predicate = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            return predicate == null
                ? await dbSet.LongCountAsync()
                : await dbContext.Set<T>().LongCountAsync(predicate);
        }

        public async Task<List<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var dbSet = dbContext.Set<T>();
            var entities = await dbSet.FromSqlRaw(sql, parameters).ToListAsync();

            if (_integrityVerificationEnabled)
                await EnsureIntegrityAsync(entities, dbContext);

            return entities;
        }

        #region Embedding
        public async Task GenerateAndStoreEmbeddingAsync(T entity)
        {
            if (!_embeddingEnabled) return;

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var entityId = _idProperty.GetValue(entity)?.ToString()
                ?? throw new InvalidOperationException("Entity ID cannot be null.");
            var paragraph = await GenerateParagraph(entity, dbContext);
            if (string.IsNullOrEmpty(paragraph)) return;

            var embedding = _embedder.GenerateEmbedding(paragraph).ToArray();
            var embeddingId = await AddOrUpdateEmbeddingAsync(entity, embedding);
            if (embeddingId != Guid.Empty)
            {
                _faissIndex.AddEmbedding(embeddingId, embedding);
                _logger?.LogDebug("Added embedding to FaissNetSearch for EmbeddingId {EmbeddingId}", embeddingId);
            }
            _logger?.LogDebug("Generated embedding for entity {EntityId} with length {Length}", entityId, embedding.Length);
        }

        private async Task<string?> GenerateParagraph(T entity, SmartDataContext dbContext)
        {
            if (!_embeddingEnabled || (!_embeddableProperties.Any() && _classEmbeddableAttribute == null)) return null;

            var sb = new StringBuilder();
            var properties = typeof(T).GetProperties().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            // Process property-level embeddings
            foreach (var (property, attr) in _embeddableProperties.OrderBy(x => x.Attribute.Priority))
            {
                try
                {
                    var formatted = await FormatWithNamedPlaceholders(attr.Format, entity, properties, dbContext);
                    if (!string.IsNullOrEmpty(formatted))
                    {
                        sb.Append(formatted + " ");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to format Embeddable attribute for property {PropertyName} with format {Format}", property.Name, attr.Format);
                }
            }

            // Process class-level embedding
            if (_classEmbeddableAttribute != null)
            {
                try
                {
                    var formatted = await FormatWithNamedPlaceholders(_classEmbeddableAttribute.Format, entity, properties, dbContext);
                    if (!string.IsNullOrEmpty(formatted))
                    {
                        sb.Append(formatted + " ");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to format class-level Embeddable attribute for type {TypeName} with format {Format}", typeof(T).Name, _classEmbeddableAttribute.Format);
                }
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }

        private async Task<string> FormatWithNamedPlaceholders(string format, T entity, Dictionary<string, PropertyInfo> properties, SmartDataContext dbContext)
        {
            if (string.IsNullOrEmpty(format)) return string.Empty;

            var result = format;
            var matches = System.Text.RegularExpressions.Regex.Matches(format, @"\{([^{}]+)\}");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var placeholder = match.Groups[1].Value;
                string value = string.Empty;

                // Handle aggregate functions (e.g., {Products.MAX(Price)})
                if (placeholder.Contains(".") && placeholder.Contains("("))
                {
                    var parts = placeholder.Split(new[] { '.', '(' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && parts[2].EndsWith(")"))
                    {
                        var navPropertyName = parts[0];
                        var aggregateFunction = parts[1];
                        var targetProperty = parts[2].TrimEnd(')');

                        if (properties.TryGetValue(navPropertyName, out var navProperty))
                        {
                            value = await ComputeAggregateAsync(entity, navProperty, aggregateFunction, targetProperty, dbContext);
                        }
                    }
                }
                // Handle navigation properties (e.g., {Customer.Name})
                else if (placeholder.Contains("."))
                {
                    var parts = placeholder.Split('.');
                    if (parts.Length == 2 && properties.TryGetValue(parts[0], out var navProperty))
                    {
                        value = await GetNavigationPropertyValueAsync(entity, navProperty, parts[1], dbContext);
                    }
                }
                // Handle direct properties
                else if (properties.TryGetValue(placeholder, out var property))
                {
                    value = property.GetValue(entity)?.ToString() ?? string.Empty;
                }
                else
                {
                    _logger?.LogWarning("Property or navigation {Placeholder} not found in type {TypeName} for format string {Format}", placeholder, typeof(T).Name, format);
                }

                result = result.Replace($"{{{placeholder}}}", value);
            }

            return result;
        }

        private async Task<string> GetNavigationPropertyValueAsync(T entity, PropertyInfo navProperty, string targetProperty, SmartDataContext dbContext)
        {
            var navValue = navProperty.GetValue(entity);
            if (navValue == null) return string.Empty;

            // Handle single navigation property (e.g., Customer.Name)
            var navType = navProperty.PropertyType;
            var targetProp = navType.GetProperty(targetProperty, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (targetProp != null)
            {
                return targetProp.GetValue(navValue)?.ToString() ?? string.Empty;
            }

            // Handle collection navigation property (e.g., Customer.Products)
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(navType) && navType != typeof(string))
            {
                var elementType = navType.GetGenericArguments().FirstOrDefault() ?? navType.GetElementType();
                if (elementType != null)
                {
                    targetProp = elementType.GetProperty(targetProperty, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (targetProp != null)
                    {
                        var values = ((System.Collections.IEnumerable)navValue).Cast<object>()
                            .Select(item => targetProp.GetValue(item)?.ToString() ?? string.Empty)
                            .Where(s => !string.IsNullOrEmpty(s));
                        return string.Join(", ", values);
                    }
                }
            }

            return string.Empty;
        }

        private async Task<string> ComputeAggregateAsync(T entity, PropertyInfo navProperty, string aggregateFunction, string targetProperty, SmartDataContext dbContext)
        {
            var navType = navProperty.PropertyType;
            if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(navType) || navType == typeof(string)) return string.Empty;

            var elementType = navType.GetGenericArguments().FirstOrDefault() ?? navType.GetElementType();
            if (elementType == null) return string.Empty;

            var targetProp = elementType.GetProperty(targetProperty, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (targetProp == null) return string.Empty;

            var entityId = _idProperty.GetValue(entity)?.ToString();
            if (string.IsNullOrEmpty(entityId)) return string.Empty;

            // Dynamically query the related collection
            var queryMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes).MakeGenericMethod(elementType);
            var query = (IQueryable)queryMethod.Invoke(dbContext, null);

            // Filter by foreign key (assume <Entity>Id convention, e.g., CustomerId)
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

            // Compute aggregate
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

        public async Task<Guid> AddOrUpdateEmbeddingAsync(T entity, float[] embedding)
        {
            if (!_embeddingEnabled) return Guid.Empty;

            var id = _idProperty.GetValue(entity)?.ToString()
                ?? throw new InvalidOperationException("Entity ID cannot be null.");
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var embeddingRecord = await dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && e.EntityId == id);

            if (embeddingRecord == null)
            {
                embeddingRecord = new EmbeddingRecord
                {
                    Id = Guid.NewGuid(),
                    EntityId = id,
                    Embedding = embedding,
                    TableName = _tableName
                };
                await dbContext.Set<EmbeddingRecord>().AddAsync(embeddingRecord);
            }
            else
            {
                embeddingRecord.Embedding = embedding;
                dbContext.Entry(embeddingRecord).State = EntityState.Modified;
                if (Guid.TryParse(id, out var guidId))
                {
                    _faissIndex.UpdateEmbedding(guidId, embedding);
                    _logger?.LogDebug("Updated embedding in FaissNetSearch for EntityId {EntityId}", guidId);
                }
            }

            await dbContext.SaveChangesAsync();
            return embeddingRecord.Id;
        }

        public async Task<float[]?> GetEmbeddingAsync(object entityId)
        {
            if (!_embeddingEnabled) return null;

            var id = entityId?.ToString()
                ?? throw new ArgumentNullException(nameof(entityId));
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var embeddingRecord = await dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && e.EntityId == id);
            return embeddingRecord?.Embedding;
        }

        public async Task<bool> RemoveEmbeddingAsync(object entityId)
        {
            if (!_embeddingEnabled) return false;

            var id = entityId?.ToString()
                ?? throw new ArgumentNullException(nameof(entityId));
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var embeddingRecord = await dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && e.EntityId == id);
            if (embeddingRecord == null) return false;

            dbContext.Set<EmbeddingRecord>().Remove(embeddingRecord);
            if (Guid.TryParse(id, out var guidId))
            {
                _faissIndex.RemoveEmbedding(guidId);
                _logger?.LogDebug("Removed embedding from FaissNetSearch for EntityId {EntityId}", guidId);
            }
            await dbContext.SaveChangesAsync();
            return true;
        }
        #endregion

        #region Timeseries
        private async Task StoreTimeseriesAsync(T entity, int retryCount = 0, int maxRetries = 3)
        {
            if (!_timeseriesEnabled) return;

            if (retryCount > maxRetries)
                throw new DbUpdateConcurrencyException("Max retries exceeded for StoreTimeseriesAsync");

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var entityId = _idProperty.GetValue(entity)?.ToString()
                ?? throw new InvalidOperationException("Entity ID cannot be null.");
            var timestamp = DateTime.UtcNow;

            using var transaction = await dbContext.Database.BeginTransactionAsync();

            try
            {
                foreach (var property in _timeseriesProperties)
                {
                    var value = property.GetValue(entity)?.ToString() ?? string.Empty;
                    var baseValue = await dbContext.Set<TsBaseValue<string>>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(b => b.TableName == _tableName &&
                                                 b.EntityId == entityId &&
                                                 b.PropertyName == property.Name &&
                                                 b.Value == value);

                    if (baseValue == null)
                    {
                        baseValue = new TsBaseValue<string>(value, timestamp)
                        {
                            TableName = _tableName,
                            EntityId = entityId,
                            PropertyName = property.Name
                        };
                        await dbContext.Set<TsBaseValue<string>>().AddAsync(baseValue);
                    }

                    var deltaT = await dbContext.Set<TsDeltaT>()
                        .FirstOrDefaultAsync(d => d.BaseValueId == baseValue.Id);

                    if (deltaT == null)
                    {
                        deltaT = new TsDeltaT { Id = Guid.NewGuid(), BaseValueId = baseValue.Id };
                        var milliseconds = (int)(timestamp - baseValue.StartTime).TotalMilliseconds;
                        deltaT.AddTimestamp(milliseconds);
                        await dbContext.Set<TsDeltaT>().AddAsync(deltaT);
                    }
                    else
                    {
                        var milliseconds = (int)(timestamp - baseValue.StartTime).TotalMilliseconds;
                        deltaT.AddTimestamp(milliseconds);
                        dbContext.Entry(deltaT).Property(d => d.CompressedDeltas).IsModified = true;
                        dbContext.Entry(deltaT).Property(d => d.LastTimestamp).IsModified = true;
                        dbContext.Entry(deltaT).Property(d => d.Version).IsModified = true;
                    }

                    _logger?.LogDebug("Stored TsDeltaT for BaseValueId {BaseValueId} with CompressedDeltas length {Length}",
                        deltaT.BaseValueId, deltaT.CompressedDeltas.Length);
                }

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger?.LogWarning(ex, "Concurrency conflict in StoreTimeseriesAsync for entity {EntityId}, table {TableName}", entityId, _tableName);
                await transaction.RollbackAsync();
                await Task.Delay(100);
                await StoreTimeseriesAsync(entity, retryCount + 1, maxRetries);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in StoreTimeseriesAsync for entity {EntityId}, table {TableName}", entityId, _tableName);
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<List<TimeseriesResult>> GetTimeseriesAsync(string entityId, string propertyName, DateTime startTime, DateTime endTime)
        {
            if (!_timeseriesEnabled) return new List<TimeseriesResult>();

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
            var baseValues = await dbContext.Set<TsBaseValue<string>>()
                .Where(b => b.TableName == _tableName &&
                            b.EntityId == entityId &&
                            b.PropertyName == propertyName &&
                            b.StartTime <= endTime)
                .OrderBy(b => b.StartTime)
                .ToListAsync();

            var timeseriesData = new List<TimeseriesResult>();
            foreach (var baseValue in baseValues)
            {
                var deltaT = await dbContext.Set<TsDeltaT>()
                    .FirstOrDefaultAsync(d => d.BaseValueId == baseValue.Id);
                if (deltaT == null) continue;

                var deltas = deltaT.GetTimeDeltas();
                int currentTime = 0;
                foreach (var delta in deltas)
                {
                    currentTime += delta;
                    var timestamp = baseValue.StartTime.AddMilliseconds(currentTime);
                    if (timestamp >= startTime && timestamp <= endTime)
                    {
                        timeseriesData.Add(new TimeseriesResult
                        {
                            Timestamp = timestamp,
                            Value = baseValue.Value
                        });
                    }
                }
            }

            return timeseriesData.OrderBy(d => d.Timestamp).ToList();
        }

        public async Task<List<TimeseriesResult>> GetInterpolatedTimeseriesAsync(string entityId, string propertyName,
            DateTime startTime, DateTime endTime, TimeSpan interval, InterpolationMethod method)
        {
            if (!_timeseriesEnabled) return new List<TimeseriesResult>();

            var timeseries = await GetTimeseriesAsync(entityId, propertyName, startTime, endTime);
            var result = new List<TimeseriesResult>();

            if (!timeseries.Any()) return result;

            for (var currentTime = startTime; currentTime <= endTime; currentTime = currentTime.Add(interval))
            {
                if (method == InterpolationMethod.None)
                {
                    var exactMatch = timeseries.FirstOrDefault(t => t.Timestamp == currentTime);
                    if (exactMatch != null)
                    {
                        result.Add(new TimeseriesResult { Timestamp = currentTime, Value = exactMatch.Value });
                    }
                }
                else
                {
                    double? value = null;

                    var previous = timeseries.LastOrDefault(t => t.Timestamp <= currentTime);
                    var next = timeseries.FirstOrDefault(t => t.Timestamp >= currentTime);

                    if (previous == null && next == null)
                    {
                        continue;
                    }

                    switch (method)
                    {
                        case InterpolationMethod.Linear when previous != null && next != null:
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
                            break;

                        case InterpolationMethod.Nearest:
                            if (previous != null && next != null)
                            {
                                var prevDiff = Math.Abs((currentTime - previous.Timestamp).TotalMilliseconds);
                                var nextDiff = Math.Abs((currentTime - next.Timestamp).TotalMilliseconds);
                                value = prevDiff <= nextDiff ? double.Parse(previous.Value) : double.Parse(next.Value);
                            }
                            else if (previous != null)
                            {
                                value = double.Parse(previous.Value);
                            }
                            else if (next != null)
                            {
                                value = double.Parse(next.Value);
                            }
                            break;

                        case InterpolationMethod.Previous when previous != null:
                            value = double.Parse(previous.Value);
                            break;

                        case InterpolationMethod.Next when next != null:
                            value = double.Parse(next.Value);
                            break;
                    }

                    if (value.HasValue)
                    {
                        result.Add(new TimeseriesResult
                        {
                            Timestamp = currentTime,
                            Value = value.Value.ToString("F2")
                        });
                    }
                }
            }

            return result;
        }
        #endregion

        #region Change Tracking
        private async Task LogChangesAsync(T newEntity, T oldEntity, string changeType, SmartDataContext dbContext)
        {
            if (!_changeTrackingEnabled || !_trackChangeProperties.Any()) return;

            var entityId = newEntity != null ? _idProperty.GetValue(newEntity)?.ToString() :
                          oldEntity != null ? _idProperty.GetValue(oldEntity)?.ToString() : null;
            if (string.IsNullOrEmpty(entityId)) return;

            string changeBy;
            var connectionString = dbContext.Database.GetConnectionString();
            if (connectionString.Contains("Integrated Security=True", StringComparison.OrdinalIgnoreCase))
            {
                changeBy = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            }
            else
            {
                try
                {
                    changeBy = await dbContext.Database.ExecuteSqlRawAsync("SELECT CURRENT_USER") switch
                    {
                        var result when result > 0 => await dbContext.Set<object>().FromSqlRaw("SELECT CURRENT_USER").Select(x => x.ToString()).FirstOrDefaultAsync() ?? "Unknown",
                        _ => "Unknown"
                    };
                }
                catch
                {
                    changeBy = connectionString.Split(';')
                        .FirstOrDefault(x => x.StartsWith("User ID=", StringComparison.OrdinalIgnoreCase))?
                        .Split('=')[1] ?? "System";
                }
            }

            var changeDate = DateTime.UtcNow;

            foreach (var (property, _) in _trackChangeProperties)
            {
                var originalValue = oldEntity != null ? property.GetValue(oldEntity) : null;
                var newValue = newEntity != null ? property.GetValue(newEntity) : null;

                if (changeType == "Insert" && newValue != null ||
                    changeType == "Delete" && originalValue != null ||
                    changeType == "Update" && !Equals(originalValue, newValue))
                {
                    var changeLog = new ChangeLogRecord
                    {
                        Id = Guid.NewGuid(),
                        TableName = _tableName,
                        EntityId = entityId,
                        ChangeBy = changeBy,
                        ChangeDate = changeDate,
                        OriginalData = originalValue != null ? JsonSerializer.Serialize(originalValue) : null,
                        NewData = newValue != null ? JsonSerializer.Serialize(newValue) : null,
                        ChangeType = changeType,
                        PropertyName = property.Name
                    };

                    await dbContext.Set<ChangeLogRecord>().AddAsync(changeLog);
                    _logger?.LogDebug("Logged change for {PropertyName} in {TableName} EntityId {EntityId}: {ChangeType}", property.Name, _tableName, entityId, changeType);
                }
            }
        }
        #endregion

        #region Data Integrity
        private async Task LogIntegrityAsync(T entity, SmartDataContext dbContext)
        {
            if (!_integrityVerificationEnabled || !_ensureIntegrityProperties.Any()) return;

            var entityId = _idProperty.GetValue(entity)?.ToString()
                ?? throw new InvalidOperationException("Entity ID cannot be null.");

            foreach (var (property, _) in _ensureIntegrityProperties)
            {
                var value = property.GetValue(entity)?.ToString() ?? string.Empty;
                var dataHash = ComputeSHA256Hash(value);
                var previousHash = await GetLatestIntegrityHashAsync(_tableName, entityId, property.Name, dbContext);

                var integrityLog = new IntegrityLogRecord
                {
                    Id = Guid.NewGuid(),
                    TableName = _tableName,
                    EntityId = entityId,
                    PropertyName = property.Name,
                    DataHash = dataHash,
                    PreviousHash = previousHash,
                    Timestamp = DateTime.UtcNow
                };

                await dbContext.Set<IntegrityLogRecord>().AddAsync(integrityLog);
                _logger?.LogDebug("Logged integrity for {PropertyName} in {TableName} EntityId {EntityId}: Hash={DataHash}", property.Name, _tableName, entityId, dataHash);
            }
        }

        private async Task EnsureIntegrityAsync(IEnumerable<T> entities, SmartDataContext dbContext)
        {
            if (!_integrityVerificationEnabled || !_ensureIntegrityProperties.Any()) return;

            // Batch fetch latest integrity logs for all entities
            var entityIds = entities.Select(e => _idProperty.GetValue(e)?.ToString()).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (!entityIds.Any()) return;

            var latestLogs = await dbContext.Set<IntegrityLogRecord>()
                .Where(l => l.TableName == _tableName && entityIds.Contains(l.EntityId))
                .GroupBy(l => new { l.EntityId, l.PropertyName })
                .Select(g => g.OrderByDescending(l => l.Timestamp).FirstOrDefault())
                .ToListAsync();

            var logLookup = latestLogs.ToLookup(l => l.EntityId + l.PropertyName);

            await Parallel.ForEachAsync(entities, async (entity, ct) =>
            {
                var entityId = _idProperty.GetValue(entity)?.ToString();
                if (string.IsNullOrEmpty(entityId)) throw new DataIntegrityException(_tableName, entityId, "", "", "", "Entity ID cannot be null.");

                foreach (var (property, _) in _ensureIntegrityProperties)
                {
                    var currentValue = property.GetValue(entity)?.ToString() ?? string.Empty;
                    var currentHash = ComputeSHA256Hash(currentValue);
                    var key = entityId + property.Name;
                    var latestLog = logLookup[key].FirstOrDefault();

                    if (latestLog == null) continue;

                    if (latestLog.DataHash != currentHash)
                    {
                        var message = $"Integrity check failed for {property.Name} in {_tableName} EntityId {entityId}: Expected hash {latestLog.DataHash}, got {currentHash}";
                        _logger?.LogError(message);
                        throw new DataIntegrityException(_tableName, entityId, property.Name, latestLog.DataHash, currentHash, message);
                    }

                    var hashChainValid = await VerifyHashChainAsync(_tableName, entityId, property.Name, latestLog, dbContext);
                    if (!hashChainValid)
                    {
                        var message = $"Hash chain verification failed for {property.Name} in {_tableName} EntityId {entityId}";
                        _logger?.LogError(message);
                        throw new DataIntegrityException(_tableName, entityId, property.Name, latestLog.DataHash, currentHash, message);
                    }
                }
            });
        }

        private async Task<string> GetLatestIntegrityHashAsync(string tableName, string entityId, string propertyName, SmartDataContext dbContext)
        {
            var latestLog = await dbContext.Set<IntegrityLogRecord>()
                .Where(l => l.TableName == tableName && l.EntityId == entityId && l.PropertyName == propertyName)
                .OrderByDescending(l => l.Timestamp)
                .FirstOrDefaultAsync();

            return latestLog?.DataHash ?? string.Empty;
        }

        private async Task<bool> VerifyHashChainAsync(string tableName, string entityId, string propertyName, IntegrityLogRecord latestLog, SmartDataContext dbContext)
        {
            var logs = await dbContext.Set<IntegrityLogRecord>()
                .Where(l => l.TableName == tableName && l.EntityId == entityId && l.PropertyName == propertyName)
                .OrderByDescending(l => l.Timestamp)
                .ToListAsync();

            var currentLog = latestLog;
            foreach (var previousLog in logs.Skip(1))
            {
                if (currentLog.PreviousHash != previousLog.DataHash)
                {
                    return false;
                }
                currentLog = previousLog;
            }

            return true;
        }

        private string ComputeSHA256Hash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }
        #endregion
    }
}