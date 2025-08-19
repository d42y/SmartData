using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace SmartData.Core.Queue
{
    public sealed class SmartRouter : IWorkRouter
    {
        private readonly Lane<ChangeLogItem> _cl;
        private readonly Lane<IntegrityItem> _ig;
        private readonly Lane<TimeseriesItem> _ts;
        private readonly Lane<EmbeddingItem> _em;

        public SmartRouter(
            Lane<ChangeLogItem> cl,
            Lane<IntegrityItem> ig,
            Lane<TimeseriesItem> ts,
            Lane<EmbeddingItem> em)
        { _cl = cl; _ig = ig; _ts = ts; _em = em; }

        public ValueTask EnqueueAsync(SmartItem item, CancellationToken ct) => item switch
        {
            ChangeLogItem x => _cl.EnqueueAsync(x, ct),
            IntegrityItem x => _ig.EnqueueAsync(x, ct),
            TimeseriesItem x => _ts.EnqueueAsync(x, ct),
            EmbeddingItem x => _em.EnqueueAsync(x, ct),
            _ => ValueTask.CompletedTask
        };

        public void Complete()
        {
            _cl.Complete(); _ig.Complete(); _ts.Complete(); _em.Complete();
        }

        public Task Completion => Task.WhenAll(_cl.Completion, _ig.Completion, _ts.Completion, _em.Completion);
    }

    public sealed class SmartDataBackgroundService : BackgroundService
    {
        private readonly IWorkRouter _router;
        private readonly ILogger<SmartDataBackgroundService> _log;

        public SmartDataBackgroundService(IWorkRouter router, ILogger<SmartDataBackgroundService> log)
        { _router = router; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await _router.Completion.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "SmartData background service terminated."); }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _router.Complete();
            return base.StopAsync(cancellationToken);
        }
    }
}
