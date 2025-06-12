using Microsoft.EntityFrameworkCore;
using SmartData.Attributes;
using SmartData.Configurations;
using SmartData.GPT.Embedder;
using SmartData.Parser;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Tables
{
    public class TableCollection<T> : ITableCollection<T> where T : class
    {
        private readonly DynamicDbContext _dbContext;
        private readonly DbSet<T> _dbSet;
        private readonly string _tableName;
        private readonly PropertyInfo _idProperty;
        private readonly List<(PropertyInfo Property, EmbeddableAttribute Attribute)> _embeddableProperties;
        private readonly EmbeddingExpressionParser _expressionParser;

        public TableCollection(DynamicDbContext dbContext, string tableName)
        {
            _dbContext = dbContext;
            _dbSet = dbContext.Set<T>();
            _tableName = tableName;
            _idProperty = typeof(T).GetProperties()
                .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                     p.GetCustomAttribute<KeyAttribute>() != null)
                ?? throw new InvalidOperationException("Entity must have an Id property.");
            _embeddableProperties = typeof(T).GetProperties()
                .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<EmbeddableAttribute>() })
                .Where(x => x.Attribute != null)
                .OrderBy(x => x.Attribute.Priority)
                .Select(x => (x.Property, x.Attribute))
                .ToList();
            _expressionParser = new EmbeddingExpressionParser(dbContext);
        }

        private string? GenerateParagraph(T entity)
        {
            if (!_embeddableProperties.Any()) return null;
            var sb = new StringBuilder();
            foreach (var (_, attr) in _embeddableProperties)
            {
                var formatted = _expressionParser.EvaluateExpression(entity, typeof(T), attr.Format);
                if (!string.IsNullOrEmpty(formatted))
                {
                    sb.Append(formatted + " ");
                }
            }
            return sb.ToString().Trim();
        }

        private async Task GenerateAndStoreEmbeddingAsync(T entity)
        {
            var paragraph = GenerateParagraph(entity);
            if (string.IsNullOrEmpty(paragraph)) return;

            var embedding = AllMiniLmL6V2Embedder.Instance.GenerateEmbedding(paragraph).ToArray();
            await AddOrUpdateEmbeddingAsync(entity, embedding);
        }

        public async Task<T> InsertAsync(T entity)
        {
            await _dbSet.AddAsync(entity);
            await _dbContext.SaveChangesAsync();
            await GenerateAndStoreEmbeddingAsync(entity);
            return entity;
        }

        public async Task InsertAsync(IEnumerable<T> entities)
        {
            await _dbSet.AddRangeAsync(entities);
            await _dbContext.SaveChangesAsync();
            foreach (var entity in entities)
            {
                await GenerateAndStoreEmbeddingAsync(entity);
            }
        }

        public async Task<bool> UpdateAsync(T entity)
        {
            var id = _idProperty.GetValue(entity);
            var existing = await _dbSet.FindAsync(id);
            if (existing == null) return false;

            _dbContext.Entry(existing).CurrentValues.SetValues(entity);
            await _dbContext.SaveChangesAsync();
            await GenerateAndStoreEmbeddingAsync(entity);
            return true;
        }

        public async Task<bool> DeleteAsync(object id)
        {
            var entity = await _dbSet.FindAsync(id);
            if (entity == null) return false;

            _dbSet.Remove(entity);
            await RemoveEmbeddingAsync(id);
            await _dbContext.SaveChangesAsync();
            return true;
        }

        public async Task<int> DeleteAllAsync()
        {
            var count = await _dbSet.CountAsync();
            await _dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM {_tableName}");
            await _dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM Embeddings WHERE TableName = @p0", _tableName);
            return count;
        }

        public async Task<T?> FindByIdAsync(object id)
        {
            return await _dbSet.FindAsync(id);
        }

        public async Task<List<T>> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue)
        {
            return await _dbSet.Where(predicate)
                .Skip(skip)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<List<T>> FindAllAsync()
        {
            return await _dbSet.ToListAsync();
        }

        public async Task<long> CountAsync(Expression<Func<T, bool>> predicate = null)
        {
            return predicate == null
                ? await _dbSet.LongCountAsync()
                : await _dbSet.LongCountAsync(predicate);
        }

        public async Task<List<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters)
        {
            return await _dbSet.FromSqlRaw(sql, parameters).ToListAsync();
        }

        public async Task AddOrUpdateEmbeddingAsync(T entity, float[] embedding)
        {
            var id = _idProperty.GetValue(entity);
            var embeddingRecord = await _dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && Equals(e.EntityId, id));

            if (embeddingRecord == null)
            {
                embeddingRecord = new EmbeddingRecord
                {
                    Id = Guid.NewGuid(),
                    EntityId = id,
                    Embedding = embedding,
                    TableName = _tableName
                };
                await _dbContext.Set<EmbeddingRecord>().AddAsync(embeddingRecord);
            }
            else
            {
                embeddingRecord.Embedding = embedding;
                _dbContext.Entry(embeddingRecord).State = EntityState.Modified;
            }

            await _dbContext.SaveChangesAsync();
        }

        public async Task<float[]?> GetEmbeddingAsync(object entityId)
        {
            var embeddingRecord = await _dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && Equals(e.EntityId, entityId));
            return embeddingRecord?.Embedding;
        }

        public async Task<bool> RemoveEmbeddingAsync(object entityId)
        {
            var embeddingRecord = await _dbContext.Set<EmbeddingRecord>()
                .FirstOrDefaultAsync(e => e.TableName == _tableName && Equals(e.EntityId, entityId));
            if (embeddingRecord == null) return false;

            _dbContext.Set<EmbeddingRecord>().Remove(embeddingRecord);
            await _dbContext.SaveChangesAsync();
            return true;
        }
    }
}
