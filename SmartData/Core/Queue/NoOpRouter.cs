// SmartData.Core/Queue/NoOpRouter.cs
namespace SmartData.Core.Queue
{
    /// <summary>
    /// No-op router for design-time tools / unit tests that build the DbContext without the full host.
    /// </summary>
    public sealed class NoOpRouter : IWorkRouter
    {
        public ValueTask EnqueueAsync(SmartItem item, CancellationToken ct) => ValueTask.CompletedTask;
        public void Complete() { }
        public Task Completion => Task.CompletedTask;
    }
}
