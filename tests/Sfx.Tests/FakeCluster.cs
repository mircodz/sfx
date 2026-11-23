using System.Reflection;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Tests;

/// <summary>
/// An <see cref="IServiceFabricClient"/> for tests: every sub-client (Nodes, Applications, ImageStore…) answers the
/// methods registered by name with <see cref="On{T}"/> or <see cref="OnCall"/>, records every call, and throws for the rest.
/// </summary>
public sealed class FakeCluster
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = [];

    public FakeCluster() => Client = FakeProxy.Create<IServiceFabricClient>(this);

    public IServiceFabricClient Client { get; }

    public List<(string Method, object?[] Args)> Calls { get; } = [];

    /// <summary>Answers an async method with what <paramref name="answer"/> returns (or throws), given its arguments.</summary>
    public FakeCluster On(string method, Func<object?[], object?> answer)
    {
        _handlers[method] = answer;
        return this;
    }

    public FakeCluster On(string method, object? value) => On(method, _ => value);

    /// <summary>Answers an async method by failing with <paramref name="exception"/>.</summary>
    public FakeCluster Throws(string method, Exception exception) => On(method, _ => throw exception);

    /// <summary>Answers an async method returning a plain Task, doing <paramref name="action"/> first.</summary>
    public FakeCluster OnCall(string method, Action<object?[]>? action = null) => On(method, args =>
    {
        action?.Invoke(args);
        return null;
    });

    public FakeCluster Paged<T>(string method, params T[] items) => On(method, _ => new PagedData<T>(new ContinuationToken(""), items));

    /// <summary>The arguments of each call to <paramref name="method"/>, in order.</summary>
    public IReadOnlyList<object?[]> CallsTo(string method)
    {
        lock (Calls)
        {
            return Calls.Where(c => c.Method == method).Select(c => c.Args).ToList();
        }
    }

    internal object? Invoke(MethodInfo method, object?[] args)
    {
        lock (Calls)
        {
            Calls.Add((method.Name, args));
        }
        var result = method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : null;
        if (!_handlers.TryGetValue(method.Name, out var handler))
        {
            return Failed(result, new NotSupportedException($"The fake cluster has no answer for {method.DeclaringType?.Name}.{method.Name}."));
        }
        object? value;
        try
        {
            value = handler(args);
        }
        catch (Exception ex)
        {
            return Failed(result, ex);
        }
        return result is null
            ? Task.CompletedTask
            : typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
    }

    private static object Failed(Type? result, Exception exception) => result is null
        ? Task.FromException(exception)
        : typeof(Task).GetMethods().Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethod).MakeGenericMethod(result).Invoke(null, [exception])!;

    public class FakeProxy : DispatchProxy
    {
        private FakeCluster _cluster = null!;
        private readonly Dictionary<Type, object> _children = [];

        internal static T Create<T>(FakeCluster cluster) => (T)Create(typeof(T), cluster);

        private static object Create(Type type, FakeCluster cluster)
        {
            var proxy = DispatchProxy.Create(type, typeof(FakeProxy));
            ((FakeProxy)proxy)._cluster = cluster;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            // Sub-clients (client.Nodes, client.ImageStore…) are fakes too.
            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) && targetMethod.ReturnType.IsInterface)
            {
                lock (_children)
                {
                    if (!_children.TryGetValue(targetMethod.ReturnType, out var child))
                    {
                        _children[targetMethod.ReturnType] = child = Create(targetMethod.ReturnType, _cluster);
                    }
                    return child;
                }
            }
            return _cluster.Invoke(targetMethod, args ?? []);
        }
    }
}
