namespace OpenFreqAudio;

/// <summary>
/// A synchronized list of buffers that's drained in slices.
/// </summary>
/// <remarks>
/// Meant for moving large slices of elements at once.
/// All operations are synchronized on the list of buffers.
/// </remarks>
public class SyncRope<T>
{
    /// <returns>false if we're closed and nothing was appended.</returns>
    public bool Fill(ReadOnlyMemory<T> buf)
    {
        if (buf.Length == 0) return true;
        lock (buffers)
        {
            if (closed) return false;
            buffers.AddLast(buf);
            Monitor.Pulse(buffers);
            return true;
        }
    }

    public void Clear()
    {
        lock (buffers)
        {
            buffers.Clear();
            Monitor.Pulse(buffers);
        }
    }

    public void Close()
    {
        lock (buffers)
        {
            closed = true;
            Monitor.Pulse(buffers);
        }
    }

    /// <summary>Intended to immediately tell the receiver it's time to go on shutdown.</summary>
    public void ClearAndClose()
    {
        lock (buffers)
        {
            buffers.Clear();
            closed = true;
            Monitor.Pulse(buffers);
        }
    }

    /// <summary>
    /// Total number of available elements
    /// </summary>
    public int Available {
        get
        {
            lock (buffers)
            {
                return buffers.Select(b => b.Length).Sum();
            }
        }
    }

    /// <summary>
    /// Drains the rope into the given span,
    /// returning the number of elements copied.
    /// </summary>
    /// <returns>
    /// The number of elements copied,
    /// or null once the channel is closed and drained.
    /// Does NOT wait for enough elements to fill the span;
    /// see DrainExactly for that.
    /// </returns>
    public int? DrainTo(Span<T> to)
    {
        lock (buffers) {
            int copied = DrainLocked(to);
            if (copied == 0 && closed) return null;
            else return copied;
        }
    }

    /// <summary>
    /// Internal helper that assumes buffers is locked
    /// </summary>
    private int DrainLocked(Span<T> to)
    {
        int copied = 0;
        // We'll shrink `to` as we go.
        while (to.Length > 0)
        {
            // We're out of elements!
            if (buffers.Count == 0) return copied;

            // Grab the next buffer
            var head = buffers.First!.Value;
            // We can fit all of it in our dest
            if (head.Length <= to.Length)
            {
                head.Span.CopyTo(to);
                copied += head.Length;
                to = to[head.Length..];
                buffers.RemoveFirst();
            }
            // The next buffer exceeds what we need.
            else
            {
                head.Span[..to.Length].CopyTo(to);
                copied += to.Length;

                var newHead = head[to.Length..];
                buffers.RemoveFirst();
                buffers.AddFirst(newHead);
                return copied;
            }
        }
        return copied;
    }

    /// <summary>
    /// Extract exactly the given number of elements,
    /// blocking until at least that many have arrived.
    /// </summary>
    /// <returns>
    /// false once we're closed and fewer than to.Length elements remain.
    /// </returns>
    public bool DrainExactly(Span<T> to)
    {
        lock (buffers)
        {
            while (true)
            {
                int avail = buffers.Select(b => b.Length).Sum();
                if (avail >= to.Length)
                {
                    int drained = DrainLocked(to);
                    if (drained != to.Length)
                    {
                        throw new Exception($"Expected to drain {to.Length} bytes, got {drained}");
                    }
                    return true;
                }
                else if (closed)
                {
                    return false;
                }
                else
                {
                    Monitor.Wait(buffers);
                }
            }
        }
    }

    private LinkedList<ReadOnlyMemory<T>> buffers = [];
    private bool closed = false;
}
