using Microsoft.EntityFrameworkCore;

namespace SmartData.Extensions
{
    public static class DbContextExtensions
    {
        public static async Task<List<QueryResult>> ExecuteSqlQueryAsync(this DbContext dbContext, string sqlQuery, params object[] parameters)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (string.IsNullOrWhiteSpace(sqlQuery)) throw new ArgumentException("SQL query cannot be empty.", nameof(sqlQuery));

            var results = new List<QueryResult>();
            using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = sqlQuery;
            command.Parameters.AddRange(parameters);

            await dbContext.Database.OpenConnectionAsync();
            try
            {
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var columnName = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader[i];
                        row[columnName] = value;
                    }
                    results.Add(new QueryResult(row));
                }
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }

            return results;
        }
    }
}