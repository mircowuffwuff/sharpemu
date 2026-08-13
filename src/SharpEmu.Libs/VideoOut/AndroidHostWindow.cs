// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Android;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// A host window for Android, where there is no windowing system to open.
/// </summary>
/// <remarks>
/// SDL's Linux build has X11, Wayland and KMSDRM video drivers and Android has none of them, so
/// <see cref="SdlHostWindow"/> cannot be constructed there at all: it throws "No available video
/// device" before the presenter makes its first Vulkan call.
///
/// This asks for a surface through <c>VK_EXT_headless_surface</c> instead, which is a standard
/// extension meaning "a surface with no window". On Android the host supplies that extension and
/// owns the swapchain behind it, so what the presenter draws into is a real set of images with a
/// real present that blocks until the frame is done; where those images end up is the host's
/// business and not visible from here.
///
/// Selected by <c>SHARPEMU_HOST_WINDOW=android</c> rather than by a compile-time switch, because
/// the runtime identifier is still <c>linux-x64</c> and nothing about this build is Android.
/// </remarks>
internal sealed unsafe class AndroidHostWindow : IHostWindow
{
    public const string SelectorVariable = "SHARPEMU_HOST_WINDOW";

    /// <summary>
    /// <c>WIDTHxHEIGHT</c>, the size of the surface the host layer is presenting into.
    /// </summary>
    /// <remarks>
    /// The host is the only thing that knows this. It has an <c>ANativeWindow</c> and this process
    /// does not, and a window whose <see cref="PixelSize"/> disagrees with the surface makes
    /// <see cref="VulkanVideoPresenter"/> conclude the drawable was resized on every single frame,
    /// so it recreates its swapchain forever and never renders — silently, without any call
    /// returning an error. Until now the two agreed only because both were set to 1920x1080 by
    /// hand.
    /// </remarks>
    public const string SizeVariable = "SHARPEMU_HOST_WINDOW_SIZE";

    private const uint HeadlessSurfaceCreateInfoExt = 1000256000;

    private readonly HostVideoOptions _options;
    private readonly List<nint> _extensionStrings = new();
    private nint _extensionArray;
    private int _closeRequested;
    private bool _disposed;

    /// <summary>
    /// The pad, registered for as long as this window exists, or null where the host does not offer
    /// one.
    /// </summary>
    /// <remarks>
    /// The window owns this because the input seam is the window layer's to fill: on Linux
    /// <see cref="SdlHostWindow"/> connects SDL's, and the pad exports reach whichever is registered
    /// through <see cref="HostWindowInputSource"/> without knowing which platform answered. Android
    /// has no window to pump events from and no readable input device, so what stands in is a poll of
    /// the host — see <see cref="AndroidHostInput"/>.
    /// </remarks>
    private readonly AndroidHostInput? _input;

    public AndroidHostWindow(string title, HostVideoOptions options)
    {
        _options = ApplyHostSize(options).Normalize();
        // registered before anything renders, because a guest that reaches its pad initialisation
        // before its first frame — which several do — would otherwise find no source and cache the
        // absence.
        if (AndroidHostInput.IsSelected())
        {
            _input = new AndroidHostInput();
            HostWindowInputSource.Set(_input);
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] Android host window ready (headless surface): " +
            $"size={_options.Width}x{_options.Height} title=\"{title}\" " +
            $"pad={(_input is null ? "none" : "host")}");
    }

    public static bool IsSelected() =>
        string.Equals(
            Environment.GetEnvironmentVariable(SelectorVariable),
            "android",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the presentation size from the host layer when it has told us one, and leaves the
    /// configured size alone when it has not.
    /// </summary>
    /// <remarks>
    /// A malformed value is ignored rather than thrown on. This is read from the environment on a
    /// path that runs before anything can report an error usefully, and the configured size is a
    /// perfectly good answer — the failure it would cause is visible immediately, as a window that
    /// renders nothing.
    /// </remarks>
    private static HostVideoOptions ApplyHostSize(HostVideoOptions options)
    {
        var text = Environment.GetEnvironmentVariable(SizeVariable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return options;
        }

        var parts = text.Split('x', 'X');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var width) ||
            !int.TryParse(parts[1], out var height) ||
            width <= 0 || height <= 0)
        {
            Console.Error.WriteLine($"[LOADER][WARN] {SizeVariable}=\"{text}\" is not WIDTHxHEIGHT; ignoring it.");
            return options;
        }

        return options with { Width = width, Height = height };
    }

    public (int Width, int Height) PixelSize => (Math.Max(_options.Width, 1), Math.Max(_options.Height, 1));

    // There is no compositor to be minimised by, no window manager to restore a surface, and no
    // display to change HDR state. All three are constant.
    public bool IsMinimized => false;

    public SdlHdrState HdrState => new(false, 1f, 1f);

    public bool ConsumeSurfaceRestore() => false;

    public bool ConsumeHdrStateChange() => false;

    public byte** GetRequiredVulkanInstanceExtensions(out uint count)
    {
        if (_extensionArray == 0)
        {
            // VK_KHR_surface is genuinely present on the host loader; VK_EXT_headless_surface is
            // provided by the host layer itself. The presenter appends its own optional
            // extensions to whatever comes back from here, so this only has to carry the two
            // that a surface cannot be created without.
            _extensionStrings.Add(Marshal.StringToHGlobalAnsi("VK_KHR_surface"));
            _extensionStrings.Add(Marshal.StringToHGlobalAnsi("VK_EXT_headless_surface"));
            _extensionArray = Marshal.AllocHGlobal(nint.Size * _extensionStrings.Count);
            for (var i = 0; i < _extensionStrings.Count; i++)
            {
                Marshal.WriteIntPtr(_extensionArray, i * nint.Size, _extensionStrings[i]);
            }
        }

        count = (uint)_extensionStrings.Count;
        return (byte**)_extensionArray;
    }

    public SurfaceKHR CreateVulkanSurface(Instance instance)
    {
        // Resolved by name rather than through a Silk.NET extension binding, and the structure is
        // declared here rather than reused, so that this does not depend on the binding package
        // having chosen to cover VK_EXT_headless_surface.
        // The presenter builds its own Vk only in Initialize, after this window already exists,
        // so this asks the loader directly rather than holding a reference it could not have had
        // at construction time.
        var vk = Vk.GetApi();
        var entry = vk.GetInstanceProcAddr(instance, "vkCreateHeadlessSurfaceEXT");
        var create =
            (delegate* unmanaged<Instance, HeadlessSurfaceCreateInfo*, void*, SurfaceKHR*, Result>)entry.Handle;
        if (create is null)
        {
            throw new InvalidOperationException(
                "vkCreateHeadlessSurfaceEXT is unavailable; the host does not provide VK_EXT_headless_surface.");
        }

        var info = new HeadlessSurfaceCreateInfo
        {
            SType = HeadlessSurfaceCreateInfoExt,
            PNext = null,
            Flags = 0,
        };

        SurfaceKHR surface = default;
        var result = create(instance, &info, null, &surface);
        if (result != Result.Success || surface.Handle == 0)
        {
            throw new InvalidOperationException($"vkCreateHeadlessSurfaceEXT failed: {result}");
        }

        Console.Error.WriteLine($"[LOADER][INFO] Headless Vulkan surface created: {surface.Handle:X}");
        return surface;
    }

    public void SetTitle(string title)
    {
    }

    public void Close() => Volatile.Write(ref _closeRequested, 1);

    /// <remarks>
    /// The same shape as <see cref="SdlHostWindow.Run"/> with the event pump removed. There is no
    /// throttle here on purpose: the host's present blocks until the frame's own semaphores are
    /// signalled, so back-pressure comes from the GPU rather than from a sleep.
    /// </remarks>
    public void Run(Action initialize, Action<double> render, Action closing, Action? idle = null)
    {
        initialize();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var last = timer.Elapsed.TotalSeconds;
        try
        {
            while (Volatile.Read(ref _closeRequested) == 0)
            {
                var now = timer.Elapsed.TotalSeconds;
                render(now - last);
                last = now;
                idle?.Invoke();
            }
        }
        finally
        {
            closing();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_input is not null)
        {
            // Clear compares before it clears, so a source registered after this one is left alone.
            HostWindowInputSource.Clear(_input);
        }

        if (_extensionArray != 0)
        {
            Marshal.FreeHGlobal(_extensionArray);
            _extensionArray = 0;
        }

        foreach (var text in _extensionStrings)
        {
            Marshal.FreeHGlobal(text);
        }

        _extensionStrings.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeadlessSurfaceCreateInfo
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
    }
}
