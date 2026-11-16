namespace RPM.Core.Cloud;

/// <summary>
/// Tracks durable realtime ordering and event-id deduplication. State is
/// committed only after the consumer has accepted the event.
/// </summary>
public sealed class RealtimeCursorState
{
    private readonly HashSet<long> _eventIds;
    private readonly LinkedList<long> _eventOrder;

    public RealtimeCursorState(long cursor = 0, IEnumerable<long>? eventIds = null)
    {
        Cursor = Math.Max(0, cursor);
        _eventOrder = new LinkedList<long>();
        _eventIds = new HashSet<long>();
        foreach (long eventId in eventIds?.Where(id => id > 0).Distinct().TakeLast(256) ?? Enumerable.Empty<long>())
        {
            _eventIds.Add(eventId);
            _eventOrder.AddLast(eventId);
        }
    }

    public long Cursor { get; private set; }

    public bool ShouldProcess(CloudRealtimeEvent realtimeEvent) =>
        realtimeEvent.Cursor > Cursor && !_eventIds.Contains(realtimeEvent.EventId);

    public bool AdvancePastDuplicate(CloudRealtimeEvent realtimeEvent)
    {
        if (realtimeEvent.Cursor <= Cursor || !_eventIds.Contains(realtimeEvent.EventId)) return false;
        Cursor = realtimeEvent.Cursor;
        return true;
    }

    public void Commit(CloudRealtimeEvent realtimeEvent)
    {
        Cursor = Math.Max(Cursor, realtimeEvent.Cursor);
        if (realtimeEvent.EventId > 0)
        {
            if (_eventIds.Add(realtimeEvent.EventId)) _eventOrder.AddLast(realtimeEvent.EventId);
            while (_eventOrder.Count > 256)
            {
                long oldest = _eventOrder.First!.Value;
                _eventOrder.RemoveFirst();
                _eventIds.Remove(oldest);
            }
        }
    }

    public IReadOnlyCollection<long> EventIds => _eventOrder.ToArray();
}
