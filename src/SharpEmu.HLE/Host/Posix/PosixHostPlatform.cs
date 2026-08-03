// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Android;
using SharpEmu.HLE.Host.Sdl;

namespace SharpEmu.HLE.Host.Posix;

internal sealed class PosixHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new PosixHostMemory();

    public IHostThreading Threading { get; } = new PosixHostThreading();

    public IHostSymbolResolver Symbols { get; } = new PosixHostSymbolResolver();

    // SDL everywhere it has an audio backend to open, which is everywhere but Android: none of
    // PipeWire, PulseAudio, JACK or ALSA is reachable there. See AndroidHostAudio.
    public IHostAudioOutput Audio { get; } =
        AndroidHostAudio.IsSelected() ? new AndroidHostAudio() : new SdlHostAudio();

    public IHostInput Input { get; } = new WindowHostInput();
}
