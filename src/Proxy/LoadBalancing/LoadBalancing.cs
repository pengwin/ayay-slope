using System.Collections.Concurrent;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Proxy.Configuration;

namespace Proxy.LoadBalancing;

public interface ILoadBalancer
{
    DestinationConfig PickDestination(ClusterConfig cluster, HttpContext httpContext);
}

public sealed class RoundRobinLoadBalancer : ILoadBalancer
{
    private readonly ConcurrentDictionary<string, ClusterCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);

    public DestinationConfig PickDestination(ClusterConfig cluster, HttpContext httpContext)
    {
        if (cluster.Destinations.Count == 0)
        {
            throw new InvalidOperationException($"Cluster '{cluster.Id}' does not have any destinations configured.");
        }

        var cursor = _cursors.GetOrAdd(cluster.Id, _ => new ClusterCursor());
        var index = (int)(Interlocked.Increment(ref cursor.Value) % cluster.Destinations.Count);
        if (index < 0)
        {
            index += cluster.Destinations.Count;
        }

        return cluster.Destinations[index];
    }

    private sealed class ClusterCursor
    {
        public int Value = -1;
    }
}
