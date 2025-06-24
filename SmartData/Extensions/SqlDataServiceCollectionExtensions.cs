using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Configurations;
using SmartData.Vectorizer.Embedder;
using SmartData.Vectorizer.Search;

namespace SmartData.Extensions
{
    public static class SqlDataServiceCollectionExtensions
    {
        public static IServiceCollection AddSqlData<TContext>(
            this IServiceCollection services,
            Action<SqlDataBuilder> configure,
            Action<DbContextOptionsBuilder> providerConfiguration)
            where TContext : SqlDataContext
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));
            if (providerConfiguration == null)
                throw new ArgumentNullException(nameof(providerConfiguration));

            var builder = new SqlDataBuilder();
            configure(builder);

            if (string.IsNullOrEmpty(builder.ConnectionString))
                throw new InvalidOperationException("Connection string is required.");

            services.AddDbContext<SmartDataContext>(options =>
            {
                providerConfiguration(options);
                builder.OptionsBuilder?.Invoke(options);
                if (builder.LoggerFactory != null)
                    options.UseLoggerFactory(builder.LoggerFactory);
            }, ServiceLifetime.Scoped);

            if (builder.EmbeddingEnabled && !services.Any(d => d.ServiceType == typeof(IEmbedder)))
            {
                services.AddSingleton<IEmbedder, AllMiniLmL6V2Embedder>();
            }

            services.AddSingleton<SmartDataOptions>(sp => new SmartDataOptions
            {
                EmbeddingEnabled = builder.EmbeddingEnabled,
                TimeseriesEnabled = builder.TimeseriesEnabled,
                ChangeTrackingEnabled = builder.ChangeTrackingEnabled,
                IntegrityVerificationEnabled = builder.IntegrityVerificationEnabled,
                SmartCalcEnabled = builder.SmartCalcEnabled
            });

            services.AddScoped<FaissNetSearch>(sp =>
                new FaissNetSearch(dimension: 384, sp.GetService<ILogger<FaissNetSearch>>()));

            services.AddScoped<TContext>();
            services.AddScoped<SmartDataContext>(sp =>
            {
                var context = sp.GetRequiredService<TContext>();
                var options = sp.GetRequiredService<DbContextOptions<SmartDataContext>>();
                var smartDataOptions = sp.GetService<SmartDataOptions>();
                return new SmartDataContext(options, smartDataOptions, builder.MigrationsAssembly, context);
            });
            services.AddScoped<SqlData>(sp =>
            {
                var context = sp.GetRequiredService<TContext>();
                var embedder = builder.EmbeddingEnabled ? sp.GetRequiredService<IEmbedder>() : null;
                var faissIndex = builder.EmbeddingEnabled ? sp.GetRequiredService<FaissNetSearch>() : null;
                return new SqlData(sp, embedder, faissIndex, context, sp.GetService<ILogger<SqlData>>(), builder.MigrationsAssembly);
            });

            return services;
        }
    }
}