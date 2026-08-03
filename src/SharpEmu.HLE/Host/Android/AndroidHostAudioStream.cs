// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Android;

/// <summary>
/// One open AAudio playback stream.
/// </summary>
/// <remarks>
/// AAudio offers two ways to feed a stream: a data callback, where a driver-owned thread calls the
/// application every burst, and blocking writes, where the caller's own thread waits until the
/// device has room. This uses the second, and the first is not available to it — the guest runs
/// under an emulator whose host boundary is one-way, so there is no thread the device could call
/// back into.
///
/// That costs nothing, because blocking is what the seam already specifies: "may block briefly
/// while the device drains its queue (this is what paces the guest's audio loop)". The device
/// buffer is sized from <c>SHARPEMU_AUDIO_LATENCY_MS</c> and <c>AAudioStream_write</c> then paces
/// the guest at that depth with no sleeping loop of ours.
///
/// Disconnection — headphones plugged in, Bluetooth connected, a call arriving — is handled by
/// reopening underneath rather than by failing the port, so the guest never learns about it. Only
/// if the reopen fails does <see cref="Submit"/> return false and let the caller pace itself.
/// </remarks>
internal sealed unsafe class AndroidHostAudioStream : IHostAudioStream
{
    private const string Library = "libaaudio.so";

    private const int AAudioOk = 0;
    private const int AAudioErrorDisconnected = -899;
    private const int DirectionOutput = 0;
    private const int FormatPcm16 = 1;
    private const int FormatPcmFloat = 2;
    private const int SharingModeShared = 1;
    private const int PerformanceModeLowLatency = 12;
    private const int UsageGame = 14;

    /// <summary>
    /// How long one <c>AAudioStream_write</c> may park the calling thread.
    /// </summary>
    /// <remarks>
    /// Bounded rather than infinite, and this matters more here than it looks. The guest thread
    /// calling this is a managed thread, and the runtime suspends every thread to collect; one
    /// parked indefinitely inside a native write is one the collector waits for. A short write is
    /// a legal AAudio result that the loop below simply retries.
    /// </remarks>
    private const long WriteTimeoutNanoseconds = 20L * 1_000_000L;

    /// <summary>
    /// How long <see cref="Submit"/> keeps retrying before giving up on the buffer.
    /// </summary>
    /// <remarks>
    /// Matches the other backends' 250 ms. A device that has not taken a frame in a quarter of a
    /// second is not merely busy, and continuing to wait would turn a device problem into a hung
    /// guest audio thread.
    /// </remarks>
    private const int SubmitDeadlineMilliseconds = 250;

    private readonly object _gate = new();
    private readonly uint _requestedSampleRate;
    private readonly int _requestedChannels;
    private readonly HostPcmFormat _format;
    private readonly int _latencyMilliseconds;

    private nint _stream;
    private int _sampleRate;
    private int _channels;
    private int _bytesPerFrame;
    private long _totalSubmittedFrames;
    private bool _disposed;

    public AndroidHostAudioStream(uint sampleRate, int channels, HostPcmFormat format, int latencyMilliseconds)
    {
        if (sampleRate is < 8_000 or > 384_000 || channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                sampleRate is < 8_000 or > 384_000 ? nameof(sampleRate) : nameof(channels));
        }

        _requestedSampleRate = sampleRate;
        _requestedChannels = channels;
        _format = format;
        _latencyMilliseconds = latencyMilliseconds;
        Open();
    }

    /// <summary>Caller holds <see cref="_gate"/>, or is the constructor.</summary>
    private void Open()
    {
        nint builder = 0;
        var status = AAudio_createStreamBuilder(out builder);
        if (status != AAudioOk || builder == 0)
        {
            throw new InvalidOperationException(
                $"AAudio_createStreamBuilder failed: {DescribeError(status)}.");
        }

        try
        {
            AAudioStreamBuilder_setSampleRate(builder, checked((int)_requestedSampleRate));
            AAudioStreamBuilder_setChannelCount(builder, _requestedChannels);
            AAudioStreamBuilder_setFormat(
                builder,
                _format == HostPcmFormat.Float32 ? FormatPcmFloat : FormatPcm16);
            AAudioStreamBuilder_setDirection(builder, DirectionOutput);
            AAudioStreamBuilder_setSharingMode(builder, SharingModeShared);
            AAudioStreamBuilder_setPerformanceMode(builder, PerformanceModeLowLatency);
            AAudioStreamBuilder_setUsage(builder, UsageGame);

            status = AAudioStreamBuilder_openStream(builder, out var stream);
            if (status != AAudioOk || stream == 0)
            {
                throw new InvalidOperationException(
                    $"AAudioStreamBuilder_openStream failed: {DescribeError(status)}.");
            }

            // What the device gave us, which need not be what was asked for: AAudio negotiates,
            // and LOW_LATENCY in particular can change the burst size underneath. Everything below
            // is sized from these rather than from the request.
            _stream = stream;
            _sampleRate = AAudioStream_getSampleRate(stream);
            _channels = AAudioStream_getChannelCount(stream);
            var bytesPerSample = AAudioStream_getFormat(stream) == FormatPcmFloat
                ? sizeof(float)
                : sizeof(short);
            _bytesPerFrame = Math.Max(1, _channels * bytesPerSample);

            var wanted = (long)_sampleRate * _latencyMilliseconds / 1_000L;
            var capacity = AAudioStream_getBufferCapacityInFrames(stream);
            if (wanted > 0 && capacity > 0)
            {
                AAudioStream_setBufferSizeInFrames(stream, (int)Math.Min(wanted, capacity));
            }

            status = AAudioStream_requestStart(stream);
            if (status != AAudioOk)
            {
                AAudioStream_close(stream);
                _stream = 0;
                throw new InvalidOperationException(
                    $"AAudioStream_requestStart failed: {DescribeError(status)}.");
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] AAudio stream: {_sampleRate} Hz, {_channels} ch, " +
                $"{(bytesPerSample == sizeof(float) ? "float32" : "s16")}, " +
                $"burst {AAudioStream_getFramesPerBurst(stream)} frames, " +
                $"buffer {AAudioStream_getBufferSizeInFrames(stream)} of {capacity}");
        }
        finally
        {
            AAudioStreamBuilder_delete(builder);
        }
    }

    public int QueuedMilliseconds
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || _stream == 0 || _sampleRate <= 0)
                {
                    return -1;
                }

                // Frames handed to the device minus frames it has played is exactly the cushion
                // the seam asks about, and both are cheap non-blocking reads.
                var pending = AAudioStream_getFramesWritten(_stream) - AAudioStream_getFramesRead(_stream);
                return pending <= 0 ? 0 : (int)(pending * 1_000L / _sampleRate);
            }
        }
    }

    public bool Submit(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return true;
        }

        lock (_gate)
        {
            if (_disposed || _stream == 0 || _bytesPerFrame <= 0)
            {
                return false;
            }

            var frames = pcm.Length / _bytesPerFrame;
            if (frames <= 0)
            {
                return true;
            }

            var deadline = Environment.TickCount64 + SubmitDeadlineMilliseconds;
            var offset = 0;
            fixed (byte* data = pcm)
            {
                while (offset < frames)
                {
                    var written = AAudioStream_write(
                        _stream,
                        data + (long)offset * _bytesPerFrame,
                        frames - offset,
                        WriteTimeoutNanoseconds);

                    if (written < 0)
                    {
                        if (written != AAudioErrorDisconnected || !Reopen())
                        {
                            return false;
                        }

                        // The stream underneath is a new one; start this buffer again rather than
                        // resuming into it, since the device consumed nothing of what was lost.
                        offset = 0;
                        continue;
                    }

                    offset += written;
                    if (offset < frames && Environment.TickCount64 >= deadline)
                    {
                        // A gap is audible and so is an ever-growing delay. Give the rest of this
                        // buffer up and let the caller decide; it still gets a true, because what
                        // was accepted did play.
                        break;
                    }
                }
            }

            _totalSubmittedFrames += offset;

            // Everything handed over minus what the device still holds is what the player has
            // actually heard, which is the one clock that advances at the rate the listener
            // perceives.
            if (_sampleRate > 0)
            {
                GuestAudioClock.Report(Math.Max(0, AAudioStream_getFramesRead(_stream)) / (double)_sampleRate);
            }

            return true;
        }
    }

    /// <summary>
    /// Reopens after a disconnect. Caller holds <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// The guest is never told. A PS5 title has no notion of an audio device going away, and the
    /// honest translation of "the player unplugged their headphones" is that the same stream keeps
    /// playing somewhere else. If the reopen fails the port degrades to silence rather than
    /// failing the run, which is what the video path does with a lost surface.
    /// </remarks>
    private bool Reopen()
    {
        var previous = _stream;
        _stream = 0;
        if (previous != 0)
        {
            AAudioStream_close(previous);
        }

        try
        {
            Open();
            Console.Error.WriteLine("[LOADER][INFO] AAudio stream reopened after a device change.");
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] AAudio stream could not be reopened: {exception.Message}");
            _stream = 0;
            return false;
        }
    }

    private static string DescribeError(int status)
    {
        var text = AAudio_convertResultToText(status);
        return text == 0
            ? status.ToString()
            : $"{Marshal.PtrToStringUTF8(text) ?? status.ToString()} ({status})";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_stream != 0)
            {
                AAudioStream_requestStop(_stream);
                AAudioStream_close(_stream);
                _stream = 0;
            }
        }
    }

    [DllImport(Library)]
    private static extern int AAudio_createStreamBuilder(out nint builder);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setSampleRate(nint builder, int sampleRate);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setChannelCount(nint builder, int channelCount);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setFormat(nint builder, int format);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setDirection(nint builder, int direction);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setSharingMode(nint builder, int sharingMode);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setPerformanceMode(nint builder, int mode);

    [DllImport(Library)]
    private static extern void AAudioStreamBuilder_setUsage(nint builder, int usage);

    [DllImport(Library)]
    private static extern int AAudioStreamBuilder_openStream(nint builder, out nint stream);

    [DllImport(Library)]
    private static extern int AAudioStreamBuilder_delete(nint builder);

    [DllImport(Library)]
    private static extern int AAudioStream_requestStart(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_requestStop(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_close(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_write(nint stream, void* buffer, int numFrames, long timeoutNanoseconds);

    [DllImport(Library)]
    private static extern int AAudioStream_setBufferSizeInFrames(nint stream, int numFrames);

    [DllImport(Library)]
    private static extern int AAudioStream_getBufferSizeInFrames(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_getBufferCapacityInFrames(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_getFramesPerBurst(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_getSampleRate(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_getChannelCount(nint stream);

    [DllImport(Library)]
    private static extern int AAudioStream_getFormat(nint stream);

    [DllImport(Library)]
    private static extern long AAudioStream_getFramesRead(nint stream);

    [DllImport(Library)]
    private static extern long AAudioStream_getFramesWritten(nint stream);

    [DllImport(Library)]
    private static extern nint AAudio_convertResultToText(int result);
}
