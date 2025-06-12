using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Configurations
{
    public abstract class SqlDataContext
    {
        internal void ConfigureTables(SqlData manager)
        {
            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DataSet<>));

            foreach (var prop in properties)
            {
                var entityType = prop.PropertyType.GetGenericArguments()[0];
                var tableName = prop.Name;
                // Use reflection to call generic RegisterTable<T>
                var registerMethod = typeof(SqlData)
                    .GetMethod(nameof(SqlData.RegisterTable))
                    .MakeGenericMethod(entityType);
                var table = registerMethod.Invoke(manager, new object[] { tableName });

                // Set the SqlSet<T> property
                var sqlSetType = typeof(DataSet<>).MakeGenericType(entityType);
                var sqlSet = Activator.CreateInstance(sqlSetType, table);
                prop.SetValue(this, sqlSet);
            }
        }
    }
}
