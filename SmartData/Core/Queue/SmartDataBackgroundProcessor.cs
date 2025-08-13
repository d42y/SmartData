using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;
using SmartData.Vectorizer;

namespace SmartData.Core.Queue
{
    public sealed class SmartDataProcessingOptions
    {
        public int DegreeOfParallelism { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
    }

    public sealed class SmartDataBackgroundProcessor : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ISmartDataQueue _queue;
        private readonly IEmbeddingProvider? _embedder;
        private readonly IFaissSearch? _faiss;
        private readonly DataOptions _options;
        private readonly ILogger<SmartDataBackgroundProcessor> _logger;
        private readonly SmartDataProcessingOptions _procOptions;

        // Per-entity key serialization (keeps order per row)
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

        public SmartDataBackgroundProcessor(
            IServiceProvider sp,
            ISmartDataQueue queue,
            DataOptions options,
            ILogger<SmartDataBackgroundProcessor> logger,
            SmartDataProcessingOptions? procOptions = null,
            IEmbeddingProvider? embedder = null,
            IFaissSearch? faiss = null)
        {
            _sp = sp;
            _queue = queue;
            _options = options;
            _logger = logger;
            _procOptions = procOptions ?? new SmartDataProcessingOptions();
            _embedder = embedder;
            _faiss = faiss;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var workers = Enumerable.Range(0, _procOptions.DegreeOfParallelism)
                .Select(_ => Task.Run(() => WorkerLoop(stoppingToken), stoppingToken))
                .ToArray();

            await Task.WhenAll(workers);
        }

        private async Task WorkerLoop(CancellationToken ct)
        {
            await foreach (var item in _queue.DequeueAllAsync(ct))
            {
                var gate = _keyLocks.GetOrAdd($"{item.Table}|{item.EntityKey}", _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    using var scope = _sp.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<DataContext>();

                    // CHANGE LOG
                    if (_options.EnableChangeTracking && item.Changes.Count > 0)
                    {
                        var rows = item.Changes.Select(c => new ChangeLogRecord
                        {
                            Id = Guid.NewGuid(),
                            TableName = item.Table,
                            EntityId = item.EntityKey,
                            ChangedBy = item.ChangedBy,
                            ChangedAt = item.TimestampUtc,
                            OriginalValue = c.Original,
                            NewValue = c.New,
                            ChangeType = c.ChangeType,
                            PropertyName = c.Property
                        });
                        await db.AddRangeAsync(rows, ct);
                    }

                    // INTEGRITY
                    if (_options.EnableIntegrityVerification && item.Integrity.Count > 0)
                    {
                        var props = item.Integrity.Select(i => i.Property).ToList();
                        var last = await db.Set<IntegrityLogRecord>()
                            .Where(l => l.TableName == item.Table && l.EntityId == item.EntityKey && props.Contains(l.PropertyName))
                            .GroupBy(l => l.PropertyName)
                            .Select(g => g.OrderByDescending(x => x.Timestamp).FirstOrDefault())
                            .ToListAsync(ct);

                        var lastByProp = last.Where(x => x != null).ToDictionary(x => x.PropertyName, x => x.Hash);
                        var now = item.TimestampUtc;

                        var rows = item.Integrity.Select(i => new IntegrityLogRecord
                        {
                            Id = Guid.NewGuid(),
                            TableName = item.Table,
                            EntityId = item.EntityKey,
                            PropertyName = i.Property,
                            Hash = ComputeSha256(i.CurrentValue),
                            PreviousHash = lastByProp.TryGetValue(i.Property, out var prev) ? prev : string.Empty,
                            Timestamp = now
                        });
                        await db.AddRangeAsync(rows, ct);
                    }

                    // TIMESERIES
                    if (_options.EnableTimeseries && item.Timeseries.Count > 0)
                    {
                        foreach (var ts in item.Timeseries)
                        {
                            var baseValue = await db.Set<TimeseriesBaseValue>()
                                .FirstOrDefaultAsync(b => b.TableName == item.Table && b.EntityId == item.EntityKey &&
                                                          b.PropertyName == ts.Property && b.Value == ts.Value, ct);
                            if (baseValue == null)
                            {
                                baseValue = new TimeseriesBaseValue
                                {
                                    Id = Guid.NewGuid(),
                                    TableName = item.Table,
                                    EntityId = item.EntityKey,
                                    PropertyName = ts.Property,
                                    Value = ts.Value,
                                    Timestamp = item.TimestampUtc
                                };
                                await db.AddAsync(baseValue, ct);
                            }

                            var delta = new TimeseriesDelta
                            {
                                Id = Guid.NewGuid(),
                                BaseValueId = baseValue.Id,
                                Version = 1
                            };
                            delta.AddTimestamp(0);
                            await db.AddAsync(delta, ct);
                        }
                    }

                    // EMBEDDINGS (load fresh entity and compute)
                    if (_options.EnableEmbeddings && item.NeedEmbedding && _embedder != null && _faiss != null)
                    {
                        var entity = await db.FindAsync(item.EntityType, item.KeyValues, ct);
                        if (entity != null)
                        {
                            var parser = new Vectorizer.EmbeddingExpressionParser(db);
                            var meta = item.EntityType.GetCustomAttributes(typeof(EmbeddableAttribute), true)
                                        .Cast<EmbeddableAttribute>().FirstOrDefault();
                            var props = item.EntityType.GetProperties()
                                        .Select(p => (Prop: p, Attr: p.GetCustomAttribute<EmbeddableAttribute>()))
                                        .Where(x => x.Attr != null).OrderBy(x => x.Attr!.Priority).ToList();

                            var sb = new System.Text.StringBuilder();
                            foreach (var (p, a) in props)
                            {
                                var text = parser.EvaluateExpression(entity, item.EntityType, a!.Format);
                                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append(' ');
                            }
                            if (meta != null)
                            {
                                var text = parser.EvaluateExpression(entity, item.EntityType, meta.Format);
                                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append(' ');
                            }

                            var paragraph = sb.ToString().Trim();
                            if (paragraph.Length > 0)
                            {
                                var vec = _embedder.GenerateEmbedding(paragraph);
                                var rec = await db.Set<EmbeddingRecord>()
                                    .FirstOrDefaultAsync(r => r.TableName == item.Table && r.EntityId == item.EntityKey, ct);

                                var embeddingId = rec?.Id ?? Guid.NewGuid();
                                if (rec == null)
                                {
                                    await db.AddAsync(new EmbeddingRecord
                                    {
                                        Id = embeddingId,
                                        TableName = item.Table,
                                        EntityId = item.EntityKey,
                                        Embedding = vec
                                    }, ct);
                                    _faiss.AddEmbedding(embeddingId, vec);
                                }
                                else
                                {
                                    rec.Embedding = vec;
                                    db.Update(rec);
                                    _faiss.UpdateEmbedding(embeddingId, vec);
                                }
                            }
                        }
                    }

                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SmartData background processing failed for {Table}/{Key}", item.Table, item.EntityKey);
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        private static string ComputeSha256(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
        }
    }
}
