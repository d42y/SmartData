using System.Linq.Expressions;

namespace SmartData.Tables
{
    // Non-generic base interface for dictionary storage
    public interface ITableCollection
    {
    }

    public interface ITableCollection<T> : ITableCollection where T : class
    {
        Task<T> InsertAsync(T entity);
        Task InsertAsync(IEnumerable<T> entities);
        Task<bool> UpdateAsync(T entity);
        Task<bool> DeleteAsync(object id);
        Task<int> DeleteAllAsync();
        Task<T?> FindByIdAsync(object id);
        Task<List<T>> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue);
        Task<List<T>> FindAllAsync();
        Task<long> CountAsync(Expression<Func<T, bool>> predicate = null);
        Task<List<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters);
        Task AddOrUpdateEmbeddingAsync(T entity, float[] embedding);
        Task<float[]?> GetEmbeddingAsync(object entityId);
        Task<bool> RemoveEmbeddingAsync(object entityId);
    }
}
