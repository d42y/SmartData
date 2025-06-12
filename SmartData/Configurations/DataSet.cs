using SmartData.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Configurations
{
    public class DataSet<T> where T : class
    {
        private readonly ITableCollection<T> _table;

        public DataSet(ITableCollection<T> table)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
        }

        public Task<T> InsertAsync(T entity) => _table.InsertAsync(entity);
        public Task InsertAsync(IEnumerable<T> entities) => _table.InsertAsync(entities);
        public Task<bool> UpdateAsync(T entity) => _table.UpdateAsync(entity);
        public Task<bool> DeleteAsync(object id) => _table.DeleteAsync(id);
        public Task<int> DeleteAllAsync() => _table.DeleteAllAsync();
        public Task<T?> FindByIdAsync(object id) => _table.FindByIdAsync(id);
        public Task<List<T>> FindAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue) => _table.FindAsync(predicate, skip, limit);
        public Task<List<T>> FindAllAsync() => _table.FindAllAsync();
        public Task<long> CountAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate = null) => _table.CountAsync(predicate);
        public Task<List<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters) => _table.ExecuteSqlQueryAsync(sql, parameters);
        public Task AddOrUpdateEmbeddingAsync(T entity, float[] embedding) => _table.AddOrUpdateEmbeddingAsync(entity, embedding);
        public Task<float[]?> GetEmbeddingAsync(object entityId) => _table.GetEmbeddingAsync(entityId);
        public Task<bool> RemoveEmbeddingAsync(object entityId) => _table.RemoveEmbeddingAsync(entityId);
    }
}
