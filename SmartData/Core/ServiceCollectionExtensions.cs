using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.AnalyticsService;
using SmartData.Data;
using SmartData.Vectorizer;

namespace SmartData.Core
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSmartData<TContext>(this IServiceCollection services, Action<DataOptions> configure, Action<DbContextOptionsBuilder> dbOptions = null)
            where TContext : DataContext
        {
            var options = new DataOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.ConnectionString))
                throw new InvalidOperationException("Connection string is required.");

            if (dbOptions != null)
                options.WithDbOptions(dbOptions);

            services.AddDbContext<TContext>((sp, opt) =>
            {
                options.DbOptions?.Invoke(opt);
                if (options.LoggerFactory != null)
                    opt.UseLoggerFactory(options.LoggerFactory);
            }, ServiceLifetime.Scoped);

            services.AddScoped<DataContext>(sp =>
            {
                var context = sp.GetRequiredService<TContext>();
                return context;
            });

            services.AddScoped<ILogger<DataContext>>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger<DataContext>());
            services.AddSingleton(options);
            services.AddSingleton<IEventBus, InMemoryEventBus>(); // Register event bus

            if (options.EnableEmbeddings)
                services.AddSingleton<IEmbeddingProvider, AllMiniLmL6V2Embedder>();

            if (options.EnableCalculations)
                services.AddScoped<SmartAnalyticsService>();

            services.AddScoped(typeof(DataService<>));
            services.AddScoped<FaissSearch>();
            return services;
        }
    }
}