// AudioPump.cs - hands audio from the sound card's thread to a worker thread.
//
// Windows keeps only a short stretch of incoming sound (about 50 ms) waiting for a
// program to collect it. If the program is busy when the next chunk arrives,
// the oldest chunk is lost: a 10 ms hole in the signal. HamDRM's decoding comes
// in bursts (a whole frame's error correction at once), so doing it on the sound
// card's thread lost chunks now and then, found in Dan's off-air recordings as
// frame timing that jumped by exactly 10 ms.
//
// So the sound card's thread only copies each chunk into a queue and returns at
// once; a separate worker thread takes chunks off the queue, in order, and does
// the real work. The queue holds up to 30 seconds, far more than any burst needs.
// Other jobs that must happen in step with the audio (like "the recording has
// ended") go through the same queue, so they happen after the audio before them.

using System.Buffers;
using System.Collections.Concurrent;

namespace RFpix.Audio;

public sealed class AudioPump : IDisposable
{
    private readonly record struct Item(float[]? Samples, int Count, int SampleRate, object? Source, Action? Job);

    private readonly BlockingCollection<Item> _queue;
    private readonly Action<float[], int, int, object?> _process;
    private readonly Thread _worker;

    /// <summary>Chunks dropped because the queue was full (processing fell 30 s behind).</summary>
    public int Dropped => _dropped;
    private int _dropped;

    /// <summary>Chunks waiting to be processed.</summary>
    public int Waiting => _queue.Count;

    /// <summary>Raised (on the worker thread) if processing a chunk throws.</summary>
    public event Action<Exception>? Failed;

    /// <param name="process">Does the work for one chunk: samples (only the first count
    /// are valid), count, sample rate, and which input it came from.</param>
    /// <param name="capacity">How many chunks the queue holds (3000 × 10 ms = 30 s).</param>
    public AudioPump(Action<float[], int, int, object?> process, int capacity = 3000)
    {
        _process = process;
        _queue = new BlockingCollection<Item>(capacity);
        _worker = new Thread(Run) { IsBackground = true, Name = "RFpix audio processing" };
        _worker.Start();
    }

    /// <summary>Called on the sound card's thread: copies the chunk and returns at once.</summary>
    public void Add(float[] samples, int count, int sampleRate, object? source = null)
    {
        if (_queue.IsAddingCompleted) return;
        var copy = ArrayPool<float>.Shared.Rent(Math.Max(count, 1));
        Array.Copy(samples, copy, count);
        bool added;
        try { added = _queue.TryAdd(new Item(copy, count, sampleRate, source, null)); }
        catch (InvalidOperationException) { added = false; }   // closing down
        if (!added)
        {
            ArrayPool<float>.Shared.Return(copy);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Runs a job on the worker thread, after all the audio added before it.</summary>
    public void Post(Action job)
    {
        try { _queue.Add(new Item(null, 0, 0, null, job)); }
        catch (InvalidOperationException) { }   // closing down
    }

    /// <summary>Waits until everything added so far has been processed (for tests).</summary>
    public void Flush()
    {
        using var done = new ManualResetEventSlim();
        Post(done.Set);
        done.Wait(TimeSpan.FromSeconds(30));
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (item.Job is not null) item.Job();
                else _process(item.Samples!, item.Count, item.SampleRate, item.Source);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
            }
            finally
            {
                if (item.Samples is not null) ArrayPool<float>.Shared.Return(item.Samples);
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
    }
}
