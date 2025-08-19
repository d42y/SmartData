namespace SmartData.Core.Infrastructure
{
    public static class SmartDataAmbientFlags
    {
        private static readonly AsyncLocal<bool> _isSystem = new();
        public static bool IsSystem => _isSystem.Value;

        public static IDisposable SystemScope()
        {
            var prev = _isSystem.Value;
            _isSystem.Value = true;
            return new Scope(() => _isSystem.Value = prev);
        }
        private sealed class Scope : IDisposable
        {
            private readonly Action _end; public Scope(Action end) => _end = end;
            public void Dispose() => _end();
        }
    }
}
