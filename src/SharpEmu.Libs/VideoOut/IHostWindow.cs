// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// What the Vulkan presenter needs from the thing it draws into.
/// </summary>
/// <remarks>
/// This is exactly the surface <see cref="VulkanVideoPresenter"/> already used on
/// <see cref="SdlHostWindow"/>, extracted without changes so that a host with no SDL video
/// driver can supply its own. SDL remains the implementation everywhere it works;
/// <see cref="AndroidHostWindow"/> exists because Android has no X11, Wayland or KMSDRM and
/// SDL's Linux build therefore has no video device to open.
///
/// Unsafe because <see cref="GetRequiredVulkanInstanceExtensions"/> hands back the loader's own
/// array of UTF-8 strings rather than copying it.
/// </remarks>
internal unsafe interface IHostWindow : IDisposable
{
    (int Width, int Height) PixelSize { get; }

    bool IsMinimized { get; }

    SdlHdrState HdrState { get; }

    bool ConsumeSurfaceRestore();

    bool ConsumeHdrStateChange();

    byte** GetRequiredVulkanInstanceExtensions(out uint count);

    SurfaceKHR CreateVulkanSurface(Instance instance);

    void SetTitle(string title);

    void Close();

    void Run(Action initialize, Action<double> render, Action closing, Action? idle = null);
}
