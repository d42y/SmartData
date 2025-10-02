// SmartData.Core/ServiceCollectionExtensions.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using SmartData.AnalyticsService;
using SmartData.Core.Queue;
using SmartData.Core.Queue.Strategies;
using SmartData.Core.Queue.Writers;
using SmartData.Data;
using SmartData.Vectorizer;
using SmartData.Core.Timeseries;

namespace SmartData.Core
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// One-stop SmartData registration:
        /// - Configures DataOptions
        /// - Registers writers/strategies/partitioned lanes/router + hosted worker
        /// - Adds EF interceptor and DbContext (pooled or not)
        /// - If a scoped IChangeUserProvider is present at registration time, it disables pooling automatically.
        ///   You can also force pooling choice via the usePooling parameter.
        /// </summary>
        public static IServiceCollection AddSmartData<TContext>(
            this IServiceCollection services,
            Action<DataOptions> configure,
            Action<DbContextOptionsBuilder>? dbOptions = null,
            bool? usePooling = null)
            where TContext : DataContext
        {
            // ---- Options ----
            var options = new DataOptions();
            configure(options);
            if (dbOptions != null) options.WithDbOptions(dbOptions);
            services.AddSingleton(options);

            // ---- Core deps ----
            services.TryAddSingleton<IEventBus, InMemoryEventBus>();
            if (options.EnableEmbeddings)
            {
                services.TryAddSingleton<IEmbeddingProvider, AllMiniLmL6V2Embedder>();
                services.TryAddSingleton<IFaissSearch, FaissSearch>();
            }
            if (options.EnableAnalytics)
                services.AddHostedService<SmartAnalyticsService>();

            // ASP.NET accessor (so a scoped provider can read the current user)
            services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // ---- Writers / Strategies ----


            

            
            

            // ---- Lanes (partitioned, bounded) ----
            int partitions = Math.Max(8, Environment.ProcessorCount);
            int capPerPartition = 4096;
            if (options.EnableChangeTracking)
            {
                services.AddSingleton<ChangeLogEfBatchWriter>();
                services.AddSingleton<ChangeLogStrategy>();
                services.AddSingleton(sp =>
                {
                    var life = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new Lane<ChangeLogItem>(
                        partitions: partitions,
                        capacityPerPartition: capPerPartition,
                        keySelector: i => i.Table + "|" + i.EntityKey,
                        partitioner: i => ((i.Table.GetHashCode() ^ (i.EntityKey.GetHashCode() * 16777619)) & 0x7fffffff) % partitions,
                        strategy: sp.GetRequiredService<ChangeLogStrategy>(),
                        appStop: life.ApplicationStopping);
                });
            }
            if (options.EnableIntegrityVerification)
            {
                services.AddSingleton<IntegrityEfBatchWriter>();
                services.AddSingleton<IntegrityStrategy>();
                services.AddSingleton(sp =>
                {
                    var life = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new Lane<IntegrityItem>(
                        partitions, capPerPartition,
                        i => i.Table + "|" + i.EntityKey,
                        i => ((i.Table.GetHashCode() ^ (i.EntityKey.GetHashCode() * 16777619)) & 0x7fffffff) % partitions,
                        sp.GetRequiredService<IntegrityStrategy>(),
                        life.ApplicationStopping);
                });
            }
            if (options.EnableTimeseries)
            {
                // Timeseries ingest pipeline (always registered; activated only if EnableTimeseries)
                services.AddSingleton(new TimeseriesIngestOptions());            // or bind from config
                services.AddSingleton<ITimeseriesIngestQueue, TimeseriesIngestQueue>();
                services.AddScoped<TimeseriesEfBatchWriter>();                   // uses DataContext inside scope
                services.AddHostedService<TimeseriesIngestWorker>();             // bulk flusher


                services.AddSingleton<TimeseriesStrategy>();
                services.AddSingleton(sp =>
                {
                    var life = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new Lane<TimeseriesItem>(
                        partitions, capPerPartition,
                        i => i.Table + "|" + i.EntityKey,
                        i => ((i.Table.GetHashCode() ^ (i.EntityKey.GetHashCode() * 16777619)) & 0x7fffffff) % partitions,
                        sp.GetRequiredService<TimeseriesStrategy>(),
                        life.ApplicationStopping);
                 });
            }
            if (options.EnableEmbeddings)
            {
                services.AddSingleton<EmbeddingStrategy>();
                services.AddSingleton<EmbeddingEfBatchWriter>(sp =>
                    new EmbeddingEfBatchWriter(
                        sp,
                        sp.GetRequiredService<ILogger<EmbeddingEfBatchWriter>>(),
                        sp.GetService<IEmbeddingProvider>(),
                        sp.GetService<IFaissSearch>()));
                services.AddSingleton(sp =>
                {
                    var life = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new Lane<EmbeddingItem>(
                        partitions, Math.Min(1024, capPerPartition), // smaller buffer for CPU lane
                        i => i.Table + "|" + i.EntityKey,
                        i => ((i.Table.GetHashCode() ^ (i.EntityKey.GetHashCode() * 16777619)) & 0x7fffffff) % partitions,
                        sp.GetRequiredService<EmbeddingStrategy>(),
                        life.ApplicationStopping);
                });
            }

            if (options.EnableAnalytics)
            {
                // Manager (control plane)
                services.AddSingleton<IAnalyticsEngineManager, AnalyticsEngineManager>();

                // Keep your existing strategy + lane (only change is it resolves IAnalyticsEngineManager now)
                services.AddSingleton<AnalyticsStrategy>();
                services.AddSingleton(sp =>
                {
                    var life = sp.GetRequiredService<IHostApplicationLifetime>();
                    var strat = sp.GetRequiredService<AnalyticsStrategy>();
                    var partitions = Math.Max(4, Environment.ProcessorCount / 2);
                    return new Lane<AnalyticsItem>(
                        partitions: partitions,
                        capacityPerPartition: 1024,
                        keySelector: i => (i.TenantId.ToString()) + "|" + i.Table + "|" + i.EntityKey,
                        partitioner: i => ((i.Table.GetHashCode() ^ (i.EntityKey.GetHashCode() * 16777619)) & 0x7fffffff) % partitions,
                        strategy: strat,
                        appStop: life.ApplicationStopping);
                });

                // OPTIONAL: start a few tenant engines at boot (if you have a tenant catalog you can loop here)
                // Otherwise, tenant engines will start on-demand via StartTenantAsync(...).
            }


            // Router + hosted worker
            services.AddSingleton<IWorkRouter, SmartRouter>();
            services.AddHostedService<SmartDataBackgroundService>();

            // ---- Interceptor + DbContext (pooling decision) ----
            // If a scoped IChangeUserProvider is already registered, we must avoid pooling.
            bool hasScopedUserProvider =
                services.Any(d => d.ServiceType == typeof(IChangeUserProvider) && d.Lifetime == ServiceLifetime.Scoped);

            bool pool = usePooling ?? !hasScopedUserProvider;

            services.RemoveAll<SmartDataWriteInterceptor>();
            if (pool)
                services.AddSingleton<SmartDataWriteInterceptor>();   // pooled requires singleton interceptor
            else
                services.AddScoped<SmartDataWriteInterceptor>();      // non-pooled supports scoped

            if (pool)
            {
                services.AddDbContextPool<TContext>((sp, opt) =>
                {
                    options.DbOptions?.Invoke(opt);
                    if (options.LoggerFactory != null) opt.UseLoggerFactory(options.LoggerFactory);
                    opt.AddInterceptors(sp.GetRequiredService<SmartDataWriteInterceptor>());
                });
            }
            else
            {
                services.AddDbContext<TContext>((sp, opt) =>
                {
                    options.DbOptions?.Invoke(opt);
                    if (options.LoggerFactory != null) opt.UseLoggerFactory(options.LoggerFactory);
                    
                    opt.AddInterceptors(sp.GetRequiredService<SmartDataWriteInterceptor>());
                });
            }

            // Convenience aliases
            services.AddScoped<DataContext>(sp => sp.GetRequiredService<TContext>());
            services.AddScoped<ILogger<DataContext>>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger<DataContext>());

            return services;
        }
    }
}
