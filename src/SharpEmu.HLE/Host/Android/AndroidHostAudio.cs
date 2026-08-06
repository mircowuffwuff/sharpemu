// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Android;

/// <summary>
/// Host audio output for Android, through AAudio.
/// </summary>
/// <remarks>
/// SDL's Linux build knows exactly four audio backends — PipeWire, PulseAudio, JACK and ALSA — and
/// Android runs none of the three sound servers and does not let an application open the kernel
/// device: <c>/dev/snd/pcmC0D0p</c> is <c>system:audio</c> and an app's uid is not in group
/// <c>audio</c>. So <see cref="Sdl.SdlHostAudio"/> cannot be constructed there at all, and
/// <see cref="Posix.PosixAlsaAudioStream"/> would fail at its first <c>open()</c> for the same
/// reason. Android routes all application audio through AudioFlinger, and AAudio is the C API for
/// it.
///
/// This implements <see cref="IHostPcmAudioOutput"/> rather than only the stereo PCM16 seam,
/// because the guest asks for 48000 Hz, 2 channels, float32 and
/// <c>AAUDIO_FORMAT_PCM_FLOAT</c> is exactly that — so guest float32 passes straight through and
/// a conversion the narrower path would have done never happens.
///
/// Selected by <c>SHARPEMU_HOST_AUDIO=android</c> rather than by a compile-time switch, for the
/// same reason <c>SharpEmu.Libs.VideoOut.AndroidHostWindow</c> is — it lives in a project this one
/// does not reference, so it is named rather than linked: the runtime identifier is still
/// <c>linux-x64</c>, and on a desktop Linux there is no <c>libaaudio.so</c> to P/Invoke, so this
/// must not be reachable by default.
/// </remarks>
internal sealed class AndroidHostAudio : IHostPcmAudioOutput
{
    public const string SelectorVariable = "SHARPEMU_HOST_AUDIO";

    /// <summary>
    /// How much audio the device is asked to hold, in milliseconds. The same variable
    /// <see cref="Sdl.SdlHostAudio"/> reads, and with the same default, so it stays one knob on
    /// every platform — and so a build's own <c>meta.json</c> can default it.
    /// </summary>
    /// <remarks>
    /// On AAudio this is not a soft cap the backend polices but the device buffer size itself, set
    /// with <c>AAudioStream_setBufferSizeInFrames</c>. That depth is what paces the guest, and it
    /// is what the seam's contract — "may block briefly while the device drains its queue (this is
    /// what paces the guest's audio loop)" — describes.
    ///
    /// <para><b>The waiting is ours, not the driver's.</b> Asking AAudio to block until the device has
    /// room is the obvious way to get that pacing and is the wrong shape for a guest thread, so
    /// <see cref="AndroidHostAudioStream"/> writes with a zero timeout and sleeps in a
    /// one-millisecond retry loop instead. The back-pressure is the same either way — this buffer
    /// size provides it — and only who does the waiting differs. See
    /// <c>AndroidHostAudioStream.WriteTimeoutNanoseconds</c> before changing that back.</para>
    /// </remarks>
    internal static int TargetLatencyMilliseconds =>
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_LATENCY_MS"),
            out var latencyMs) && latencyMs > 0
            ? latencyMs
            : 60;

    public string BackendName => "aaudio";

    public static bool IsSelected() =>
        string.Equals(
            Environment.GetEnvironmentVariable(SelectorVariable),
            "android",
            StringComparison.OrdinalIgnoreCase);

    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024)
    {
        // The caller's cap is in bytes of stereo PCM16, which is this backend's latency expressed
        // in the units the older seam speaks. Converted rather than ignored, because a caller that
        // asks for a deeper bed — AudioOut2, FMOD — is saying something about its own feeder.
        var bytesPerSecond = Math.Max(1L, (long)sampleRate * 2 * sizeof(short));
        var latencyMilliseconds = (int)Math.Clamp(
            Math.Max(maxQueuedPcmBytes, 4 * 1024) * 1_000L / bytesPerSecond,
            10L,
            2_000L);
        return new AndroidHostAudioStream(sampleRate, 2, HostPcmFormat.Signed16, latencyMilliseconds);
    }

    public IHostAudioStream OpenPcmStream(uint sampleRate, int channels, HostPcmFormat format) =>
        new AndroidHostAudioStream(sampleRate, channels, format, TargetLatencyMilliseconds);
}
