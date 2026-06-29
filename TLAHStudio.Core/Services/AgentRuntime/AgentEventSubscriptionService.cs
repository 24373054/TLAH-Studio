using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.AgentRuntime;

/// <summary>
/// M2.7.0: Event subscription service for live agent event replay and streaming.
/// Supports replaying events from a sequence number and then continuing with live events.
/// </summary>
public interface IAgentEventSubscriptionService
{
    /// <summary>
    /// Subscribe to events for a run. Replays historical events >= fromSequenceNumber,
    /// then streams live events as they are emitted.
    /// Returns a ChannelReader that the caller can read from.
    /// </summary>
    ChannelReader<AgentEvent> Subscribe(Guid runId, int fromSequenceNumber = 0);

    /// <summary>
    /// Publish a live event to all subscribers of the given run.
    /// Called by the engine after each event is persisted.
    /// </summary>
    void Publish(Guid runId, AgentEvent evt);

    /// <summary>
    /// Remove all subscribers for a run (called on run completion).
    /// </summary>
    void Complete(Guid runId);
}

public class AgentEventSubscriptionService : IAgentEventSubscriptionService
{
    private readonly DbContext _db;
    private readonly ConcurrentDictionary<Guid, List<Channel<AgentEvent>>> _subscribers = new();
    private readonly object _lock = new();

    public AgentEventSubscriptionService(DbContext db)
    {
        _db = db;
    }

    public ChannelReader<AgentEvent> Subscribe(Guid runId, int fromSequenceNumber = 0)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var historicalEvents = _db.Set<AgentEvent>()
            .AsNoTracking()
            .Where(e => e.AgentRunId == runId && e.SequenceNumber >= fromSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        foreach (var evt in historicalEvents)
        {
            if (!channel.Writer.TryWrite(evt))
                break;
        }

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
            {
                list = new List<Channel<AgentEvent>>();
                _subscribers[runId] = list;
            }
            list.Add(channel);
        }

        return channel.Reader;
    }

    public void Publish(Guid runId, AgentEvent evt)
    {
        List<Channel<AgentEvent>>? list;
        lock (_lock)
        {
            _subscribers.TryGetValue(runId, out list);
        }

        if (list == null) return;

        List<Channel<AgentEvent>>? stale = null;
        foreach (var channel in list)
        {
            if (channel.Writer.TryWrite(evt))
                continue;

            stale ??= [];
            stale.Add(channel);
        }

        if (stale == null)
            return;

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(runId, out var current))
                return;

            foreach (var channel in stale)
                current.Remove(channel);

            if (current.Count == 0)
                _subscribers.TryRemove(runId, out _);
        }
    }

    public void Complete(Guid runId)
    {
        List<Channel<AgentEvent>>? list;
        lock (_lock)
        {
            _subscribers.Remove(runId, out list);
        }

        if (list == null) return;

        foreach (var channel in list)
            channel.Writer.TryComplete();
    }
}
