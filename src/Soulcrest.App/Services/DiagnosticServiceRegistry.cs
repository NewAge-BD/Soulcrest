using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Soulcrest.App.Services;

/// <summary>
/// Passive view of services already constructed by the host. A diagnostic lookup must never
/// instantiate a lazy service, subscribe a tracker or start a timer (user request 2026-11-09).
/// </summary>
public sealed class DiagnosticServiceRegistry : IServiceProvider
{
    private readonly ConcurrentDictionary<Type, object> _instances = new();

    public DiagnosticServiceRegistry(params object?[] instances)
    {
        foreach (var instance in instances)
            if (instance is not null) Add(instance);
    }

    public void Add(object instance) => Add(instance.GetType(), instance);

    public void Add(Type serviceType, object instance)
    {
        _instances[serviceType] = instance;
        _instances[instance.GetType()] = instance;
    }

    public object? GetService(Type serviceType) => _instances.GetValueOrDefault(serviceType);

    /// <summary>Observe normal singleton creation without changing when host services are resolved.</summary>
    public void ObserveSingletons(IServiceCollection services)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.IsKeyedService || descriptor.Lifetime != ServiceLifetime.Singleton ||
                descriptor.ServiceType.Namespace?.StartsWith("Soulcrest.", StringComparison.Ordinal) != true ||
                descriptor.ServiceType == typeof(DiagnosticServiceRegistry)) continue;
            if (descriptor.ImplementationInstance is { } instance)
            {
                Add(descriptor.ServiceType, instance);
                continue;
            }
            services[index] = ServiceDescriptor.Singleton(descriptor.ServiceType, provider =>
            {
                var created = descriptor.ImplementationFactory is { } factory
                    ? factory(provider)
                    : ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
                Add(descriptor.ServiceType, created);
                return created;
            });
        }
    }
}
