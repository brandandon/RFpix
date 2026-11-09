// RadioConnection.Keying.cs - part of RadioConnection (RadioConnection.cs): keying, un-keying, and the watcher that
// un-keys the radio if a transmission runs past its time limit (or un-keying failed).

using RFpix.Diagnostics;

namespace RFpix.Radio;

public sealed partial class RadioConnection
{
    /// <summary>Keys the transmitter for at most <paramref name="limit"/>. With data mode on, switches
    /// the radio into data mode first (and <see cref="Unkey"/> switches it back). With VOX, sends no
    /// key command (the radio keys itself from the audio) but still switches data mode.</summary>
    public void Key(TimeSpan limit)
    {
        lock (_lock)
        {
            if (IsKeyed) return;

            LastDataModeReport = "";
            if (DataModeForTransmit) WithHamlibDetail(DataModeOn);

            if (_settings.Ptt != PttMethod.Vox)
            {
                try
                {
                    if (_hrd is { } hrd) hrd.SetPtt(true);
                    else Check(HamlibNative.rig_set_ptt(Rig, HamlibNative.VfoCurrent, HamlibNative.PttOn), "Couldn't key the transmitter");
                }
                catch (RadioException)
                {
                    // Not keyed: put the radio's mode back as it was (if data mode was switched on).
                    if (_modeBeforeTransmit is not null)
                        try { DataModeOff(); } catch (RadioException) { /* closing tries again */ }
                    throw;
                }
            }
            IsKeyed = true;
            AppLog.Info("Radio", _settings.Ptt == PttMethod.Vox
                ? $"Transmitting by VOX (no key command), time limit {limit.TotalSeconds:0} s."
                : $"Keyed the transmitter, time limit {limit.TotalSeconds:0} s.");

            var guard = new KeyGuard();
            _guard = guard;
            new Thread(() => Watch(guard, limit)) { IsBackground = true, Name = "RFpix transmit time limit" }.Start();
        }
    }

    /// <summary>How often un-keying is tried again once it has failed.</summary>
    public static TimeSpan UnkeyRetryEvery { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The time limit's watcher: <see cref="Released"/> once un-keyed (nothing more to do);
    /// <see cref="Retry"/> when un-keying failed (try again now, and every few seconds).</summary>
    private sealed class KeyGuard
    {
        public readonly ManualResetEventSlim Released = new();
        public readonly ManualResetEventSlim Retry = new();
        public bool Failing;   // un-keying has failed (said once in the log)
    }

    // On its own thread, so a busy computer can't delay it. Waits for the time limit (or for un-keying
    // to fail); then un-keys, and if that fails keeps trying every few seconds until it works or
    // the connection is closed. The radio is never left transmitting because one try failed.
    private void Watch(KeyGuard guard, TimeSpan limit)
    {
        int woken = WaitHandle.WaitAny(new[] { guard.Released.WaitHandle, guard.Retry.WaitHandle }, limit);
        if (woken == 0) return;   // un-keyed in time
        bool atLimit = woken == WaitHandle.WaitTimeout;
        while (true)
        {
            switch (TryUnkey())
            {
                case UnkeyTry.Released:
                    if (atLimit)
                    {
                        AppLog.Error("Radio", $"The transmit time limit ({limit.TotalSeconds:0} s) was reached, so RFpix released the transmitter.");
                        KeyedTooLong?.Invoke();
                    }
                    else AppLog.Info("Radio", "Un-keyed the transmitter on a later try.");
                    return;
                case UnkeyTry.NothingToDo:
                    return;
            }
            if (guard.Released.Wait(UnkeyRetryEvery)) return;   // un-keyed meanwhile (or closed)
        }
    }

    private enum UnkeyTry { Released, StillKeyed, NothingToDo }

    private UnkeyTry TryUnkey()
    {
        try
        {
            lock (_lock)
            {
                if (!IsKeyed || _rig == IntPtr.Zero && _hrd is null) return UnkeyTry.NothingToDo;
                Unkey();
                return UnkeyTry.Released;
            }
        }
        catch (RadioException)
        {
            // Un-keyed, but the radio's mode couldn't be put back (closing tries again); or still keyed.
            return IsKeyed ? UnkeyTry.StillKeyed : UnkeyTry.Released;
        }
    }

    /// <summary>Un-keys the transmitter and, if data mode was switched on for transmitting, switches back.
    /// If un-keying fails, the radio stays marked as keyed and the time limit's watcher keeps trying
    /// every few seconds (closing the connection tries once more). If switching back fails, that's
    /// tried again next time (closing too).</summary>
    public void Unkey()
    {
        lock (_lock)
        {
            if (IsKeyed)
            {
                // Un-key (try twice before giving up). With VOX there's nothing to un-key.
                if (_settings.Ptt != PttMethod.Vox && _hrd is { } hrd)
                {
                    try { hrd.SetPtt(false); }
                    catch (RadioException)
                    {
                        try { hrd.SetPtt(false); }
                        catch (RadioException ex)
                        {
                            StillKeyed();
                            throw new RadioException("Couldn't un-key the transmitter through Ham Radio Deluxe. Un-key the radio by hand. " + ex.Message);
                        }
                    }
                }
                else if (_settings.Ptt != PttMethod.Vox)
                {
                    int result = HamlibNative.rig_set_ptt(Rig, HamlibNative.VfoCurrent, HamlibNative.PttOff);
                    if (result != 0) result = HamlibNative.rig_set_ptt(Rig, HamlibNative.VfoCurrent, HamlibNative.PttOff);
                    if (result != 0)
                    {
                        StillKeyed();
                        Check(result, "Couldn't un-key the transmitter. Un-key the radio by hand");
                    }
                }
                IsKeyed = false;
                _guard?.Released.Set();   // only now: the watcher has nothing left to do
                _guard = null;
                AppLog.Info("Radio", _settings.Ptt == PttMethod.Vox ? "Transmission over (VOX)." : "Released the transmitter.");
            }

            if (_modeBeforeTransmit is not null) WithHamlibDetail(DataModeOff);
        }
    }

    // Un-keying failed: the watcher tries again now, and every few seconds.
    private void StillKeyed()
    {
        if (_guard is not { } guard) return;
        if (!guard.Failing)
        {
            guard.Failing = true;
            AppLog.Error("Radio", $"Un-keying failed: RFpix will keep trying every {UnkeyRetryEvery.TotalSeconds:0} seconds until it works.");
        }
        guard.Retry.Set();
    }

    /// <summary>Data mode is switched with commands RFpix checks itself: if it goes wrong, the
    /// log gets Hamlib's detail of what was sent and what came back too.</summary>
    private static void WithHamlibDetail(Action action)
    {
        try { action(); }
        catch (RadioException) { HamlibLog.WriteRecent(); throw; }
    }

    /// <summary>Is the radio transmitting, by its own report?</summary>
    public bool ReadPtt()
    {
        lock (_lock)
        {
            if (_hrd is { } hrd) return hrd.ReadPtt();
            Check(HamlibNative.rig_get_ptt(Rig, HamlibNative.VfoCurrent, out int ptt), "Couldn't read the transmit state");
            return ptt != HamlibNative.PttOff;
        }
    }

    /// <summary>For closing: un-keys (and puts the mode back), trying again a few times over about
    /// 2 seconds if the radio doesn't answer. Returns whether the radio is un-keyed. If it isn't,
    /// the log says so (RFpix's window has already warned, if it could: MainWindow.OnClosing).</summary>
    public bool UnkeyForClosing(int tries = 4)
    {
        lock (_lock)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Unkey();
                    return true;
                }
                catch (RadioException ex)
                {
                    if (attempt >= tries)
                    {
                        AppLog.Error("Radio", $"Closing: couldn't un-key the radio after {tries} tries. Un-key it by hand. ({ex.Message})");
                        return false;
                    }
                    Thread.Sleep(500);
                }
            }
        }
    }

    // (Closing.) The watcher stops; it has nothing to talk to any more.
    private void StopWatching()
    {
        _guard?.Released.Set();
        _guard = null;
    }
}
