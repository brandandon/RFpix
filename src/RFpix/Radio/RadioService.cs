// RadioService.cs - the one connection to the radio, shared by every page.
//
// The Radio page connects and disconnects; the Send page uses the same connection
// to key the transmitter (and connects with the saved settings if nobody has yet).
// Pages listen to Changed to keep their buttons up to date.

namespace RFpix.Radio;

public static class RadioService
{
    /// <summary>The open connection, or null.</summary>
    public static RadioConnection? Current { get; private set; }

    /// <summary>Raised (on any thread) when the connection opens or closes.</summary>
    public static event Action? Changed;

    /// <summary>Raised (on the watcher thread) if a time limit un-keyed the radio.</summary>
    public static event Action? KeyedTooLong;

    /// <summary>Opens a connection (closing any open one first). Throws <see cref="RadioException"/>.</summary>
    public static async Task<RadioConnection> ConnectAsync(RadioSettings settings)
    {
        await DisconnectAsync();
        var radio = await Task.Run(() => RadioConnection.Open(settings));
        radio.KeyedTooLong += () => KeyedTooLong?.Invoke();
        Current = radio;
        Changed?.Invoke();
        return radio;
    }

    /// <summary>Closes the connection: releases PTT, restores the mode, frees the port.</summary>
    public static async Task DisconnectAsync()
    {
        var radio = Current;
        if (radio is null) return;
        Current = null;
        await Task.Run(radio.Dispose);
        Changed?.Invoke();
    }

    /// <summary>For closing RFpix: disconnect at once, on this thread.</summary>
    public static void Close()
    {
        var radio = Current;
        Current = null;
        radio?.Dispose();
    }
}
