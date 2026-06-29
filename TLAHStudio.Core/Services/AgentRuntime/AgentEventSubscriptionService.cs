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

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
            {
                list = new List<Channel<AgentEvent>>();
                _subscribers[runId] = list;
            }
            list.Add(channel);
        }

        // Replay historical events in the background
        _ = ReplayHistoryAsync(runId, fromSequenceNumber, channel.Writer);

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

        foreach (var channel in list)
        {
            if (!channel.Writer.TryWrite(evt))
                break;
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

    private async Task ReplayHistoryAsync(Guid runId, int fromSequence, ChannelWriter<AgentEvent> writer)
    {
        try
        {
            var events = await _db.Set<AgentEvent>()
                .Where(e => e.AgentRunId == runId && e.SequenceNumber >= fromSequence)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();

            foreach (var evt in events)
            {
                if (!writer.TryWrite(evt))
                    return;
            }
        }
        catch
        {
            writer.TryComplete();
        }
    }
}
