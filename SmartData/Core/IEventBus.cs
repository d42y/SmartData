using SmartData.Models;
using System;
using System.Threading.Tasks;

namespace SmartData.Core
{
    public interface IEventBus
    {
        void Publish(EntityChangeEvent changeEvent);
        IDisposable Subscribe(Func<EntityChangeEvent, Task> handler);
    }

    public class InMemoryEventBus : IEventBus
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<Func<EntityChangeEvent, Task>> _handlers = new();

        public void Publish(EntityChangeEvent changeEvent)
        {
            foreach (var handler in _handlers)
            {
                Task.Run(() => handler(changeEvent));
            }
        }

        public IDisposable Subscribe(Func<EntityChangeEvent, Task> handler)
        {
            _handlers.Add(handler);
            return new Unsubscriber(_handlers, handler);
        }

        private class Unsubscriber : IDisposable
        {
            private readonly System.Collections.Concurrent.ConcurrentBag<Func<EntityChangeEvent, Task>> _handlers;
            private readonly Func<EntityChangeEvent, Task> _handler;

            public Unsubscriber(System.Collections.Concurrent.ConcurrentBag<Func<EntityChangeEvent, Task>> handlers, Func<EntityChangeEvent, Task> handler)
            {
                _handlers = handlers;
                _handler = handler;
            }

            public void Dispose()
            {
                _handlers.TryTake(out _);
            }
        }
    }
}