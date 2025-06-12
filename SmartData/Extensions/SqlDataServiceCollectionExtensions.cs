using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Configurations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Extensions
{
    public static class SqlDataServiceCollectionExtensions
    {
        public static IServiceCollection AddSqlDataManager<TContext>(
            this IServiceCollection services,
            Action<SqlDataBuilder> configure)
            where TContext : SqlDataContext
        {
            var builder = new SqlDataBuilder();
            configure(builder);

            services.AddDbContext<DynamicDbContext>(options =>
            {
                options.UseSqlServer(builder.ConnectionString, sqlOptions =>
                {
                    if (!string.IsNullOrEmpty(builder.MigrationsAssembly))
                        sqlOptions.MigrationsAssembly(builder.MigrationsAssembly);
                });
                builder.OptionsBuilder?.Invoke(options);
                if (builder.LoggerFactory != null)
                    options.UseLoggerFactory(builder.LoggerFactory);
            }, ServiceLifetime.Scoped);

            services.AddScoped<TContext>();
            services.AddScoped<SqlData>(sp =>
            {
                var context = sp.GetRequiredService<TContext>();
                return new SqlData(sp, context);
            });

            return services;
        }
    }
}
