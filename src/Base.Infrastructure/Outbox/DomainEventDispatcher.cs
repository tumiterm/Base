using System.Collections.Concurrent;
using System.Text.Json;
using Base.Application.Messaging;
using Base.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Infrastructure.Outbox;

/// <summary>Deserialises an outbox payload and invokes every registered <see cref="IDomainEventHandler{TEvent}"/>.</summary>
internal sealed class DomainEventDispatcher(IServiceProvider services)
{
    private static readonly ConcurrentDictionary<string, Type?> Types = new(StringComparer.Ordinal);

    public async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // Only IDomainEvent types can be materialised: the type name comes from the database, so never trust it blindly.
        var type = Types.GetOrAdd(message.Type, static name =>
        {
            var resolved = Type.GetType(name, throwOnError: false);
            return resolved is not null && typeof(IDomainEvent).IsAssignableFrom(resolved) ? resolved : null;
        }) ?? throw new InvalidOperationException($"'{message.Type}' is not a known domain event type.");

        var domainEvent = JsonSerializer.Deserialize(message.Payload, type, OutboxMessage.JsonOptions)
            ?? throw new InvalidOperationException($"Payload of message {message.Id} deserialised to null.");

        var invoker = (IInvoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(type))!;
        await invoker.InvokeAsync(services, domainEvent, cancellationToken);
    }

    private interface IInvoker
    {
        Task InvokeAsync(IServiceProvider services, object domainEvent, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TEvent> : IInvoker
        where TEvent : IDomainEvent
    {
        public async Task InvokeAsync(IServiceProvider services, object domainEvent, CancellationToken cancellationToken)
        {
            foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
            {
                await handler.HandleAsync((TEvent)domainEvent, cancellationToken);
            }
        }
    }
}
