// RadioService.cs - the one connection to the radio, shared by every page.
//
// The Radio page connects and disconnects; the Send page uses the same connection
// to key the transmitter (and connects with the saved settings if nobody has yet).
// Pages listen to Changed to keep their buttons up to date.
//
// Connecting and disconnecting take turns: if the Radio page's Connect and the Send page's
// own connecting happen at the same moment, the second waits for the first, so two
// connections are never opened at once (the second would find the port in use, and the
// first could be left open with nothing using it). The Send page reuses a connection
// that's already there rather than starting another.

namespace RFpix.Radio;

public static class RadioService
{
    /// <summary>The open connection, or null.</summary>
    public static RadioConnection? Current { get; private set; }

    /// <summary>Raised (on any thread) when the connection opens or closes.</summary>
    public static event Action? Changed;

    /// <summary>Raised (on the watcher thread) if a time limit un-keyed the radio.</summary>
    public static event Action? KeyedTooLong;

    // One connecting or disconnecting at a time.
    private static readonly SemaphoreSlim Turn = new(1, 1);

    /// <summary>Opens a connection (closing any open one first). Throws <see cref="RadioException"/>.</summary>
    public static async Task<RadioConnection> ConnectAsync(RadioSettings settings)
    {
        await Turn.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCurrentAsync().ConfigureAwait(false);
            return await OpenAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            Turn.Release();
        }
    }

    /// <summary>The open connection, or (if there isn't one) a new one with these settings: for
    /// sending, which only needs the radio connected. Throws <see cref="RadioException"/>.</summary>
    public static async Task<RadioConnection> ConnectIfNeededAsync(RadioSettings settings)
    {
        await Turn.WaitAsync().ConfigureAwait(false);
        try
        {
            return Current ?? await OpenAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            Turn.Release();
        }
    }

    /// <summary>Closes the connection: releases PTT, restores the mode, frees the port.</summary>
    public static async Task DisconnectAsync()
    {
        await Turn.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            Turn.Release();
        }
    }

    /// <summary>For closing RFpix: disconnect at once, on this thread (after a connection being
    /// made, if any, for up to 3 seconds).</summary>
    public static void Close()
    {
        bool turn = Turn.Wait(TimeSpan.FromSeconds(3));
        try
        {
            var radio = Current;
            Current = null;
            radio?.Dispose();
        }
        finally
        {
            if (turn) Turn.Release();
        }
    }

    // (Holding the turn.)
    private static async Task<RadioConnection> OpenAsync(RadioSettings settings)
    {
        var radio = await Task.Run(() => RadioConnection.Open(settings)).ConfigureAwait(false);
        radio.KeyedTooLong += () => KeyedTooLong?.Invoke();
        Current = radio;
        Changed?.Invoke();
        return radio;
    }

    // (Holding the turn.)
    private static async Task CloseCurrentAsync()
    {
        var radio = Current;
        if (radio is null) return;
        Current = null;
        await Task.Run(radio.Dispose).ConfigureAwait(false);
        Changed?.Invoke();
    }
}
