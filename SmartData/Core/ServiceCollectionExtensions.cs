using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.AnalyticsService;
using SmartData.Core;
using SmartData.Core.Queue;
using SmartData.Data;
using SmartData.Vectorizer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSmartData<TContext>(
        this IServiceCollection services,
        Action<DataOptions> configure,
        Action<DbContextOptionsBuilder>? dbOptions = null)
        where TContext : DataContext
    {
        var options = new DataOptions();
        configure(options);
        if (string.IsNullOrEmpty(options.ConnectionString))
            throw new InvalidOperationException("Connection string is required.");

        if (dbOptions != null)
            options.WithDbOptions(dbOptions);

        services.AddSingleton(options);

        

        // Core deps
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        if (options.EnableEmbeddings)
        {
            services.AddSingleton<IEmbeddingProvider, AllMiniLmL6V2Embedder>();
            services.AddSingleton<IFaissSearch, FaissSearch>(); // share index
        }

        if (options.EnableAnalytics)
            services.AddHostedService<SmartAnalyticsService>();

        services.AddScoped<SmartDataWriteInterceptor>();
        services.AddSingleton<ISmartDataQueue, SmartDataQueue>();
        services.AddSingleton(new SmartDataProcessingOptions { DegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) });
        // Hosted background processor (runs integrity/timeseries/embeddings/change logs)
        services.AddHostedService<SmartDataBackgroundProcessor>();
        

        // Register DbContext (do this here OR in Program.cs, not both)
        services.AddDbContext<TContext>((sp, opt) =>
        {
            options.DbOptions?.Invoke(opt); // e.g., UseSqlServer(options.ConnectionString)
            if (options.LoggerFactory != null)
                opt.UseLoggerFactory(options.LoggerFactory);

            var interceptor = sp.GetRequiredService<SmartDataWriteInterceptor>();
            opt.AddInterceptors(interceptor);
        });

        // Alias base type to concrete
        services.AddScoped<DataContext>(sp => sp.GetRequiredService<TContext>());

        // Optional logger for DataContext
        services.AddScoped<ILogger<DataContext>>(sp =>
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DataContext>());

        // Remove: services.AddScoped(typeof(DataService<>)); // needs a factory (tableName)

        return services;
    }
}
