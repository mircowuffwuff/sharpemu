// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Android;

/// <summary>
/// Host gamepad input for Android, read from the process that hosts this one.
/// </summary>
/// <remarks>
/// On every other platform <see cref="WindowHostInput"/> reads through a source the window layer
/// registers, and on Linux that is the SDL game window pumping SDL's own device events. Android has
/// no SDL window — <c>SharpEmu.Libs.VideoOut.AndroidHostWindow</c> stands in for one — and it also
/// has no reachable input device to read: <c>/dev/input/event*</c> nodes are owned by
/// <c>system:input</c> and an application's uid is not in that group. Nothing here can enumerate a
/// gamepad.
///
/// <para>What can is the Android application this process runs inside, which receives key and motion
/// events through the ordinary view dispatch. So input arrives the other way round from every other
/// backend: the host is told, and this asks the host. That inversion is the whole design, and it is
/// deliberately a poll rather than a callback — a host thread calling into this process would have to
/// enter guest code, which is the one direction the host's other seams all refuse.</para>
///
/// <para><b>The question is asked through a syscall with a magic number</b>, in the range the host
/// answers for pad state, reached by P/Invoking the C library's own <c>syscall</c> wrapper. There is
/// no shared library to load and nothing generated: <c>libc</c> is already resolved in this process
/// and is already how <c>PosixHostStubs</c> reaches a dozen other things.</para>
///
/// <para><b>The wire format is versioned and size-checked by the call itself.</b> This structure and
/// the host's are declared in different repositories that release independently, so a disagreement is
/// refused and reported rather than read as data. That check is the reason a call was chosen over a
/// page of shared memory, so <see cref="WireVersion"/> moves whenever a field's meaning does.</para>
///
/// <para>Selected by <c>SHARPEMU_HOST_INPUT=android</c> rather than by a compile-time switch, for the
/// same reason <see cref="AndroidHostAudio"/> is: the runtime identifier is still <c>linux-x64</c>,
/// and on a desktop Linux this magic number reaches nothing.</para>
/// </remarks>
/// <remarks>
/// Public where <see cref="AndroidHostAudio"/> is internal, and for a mechanical reason rather than a
/// stylistic one: audio is chosen inside this assembly by the platform, while an input source is
/// <i>registered</i> by whatever owns the window — which for Android is in a different assembly. It is
/// the same reason <c>SharpEmu.Libs.Pad.HostWindowInput</c> is public.
/// </remarks>
public sealed class AndroidHostInput : IHostWindowInputSource
{
    public const string SelectorVariable = "SHARPEMU_HOST_INPUT";

    /// <summary>"PD" in the top sixteen bits, with the command in the low sixteen.</summary>
    /// <remarks>
    /// Real Linux x86-64 syscall numbers are all below 1000, so the upper range cannot collide with
    /// one. The host recognises this range before it looks at its syscall table at all.
    /// </remarks>
    private const long Magic = 0x5044_0000;

    private const long CommandRead = Magic | 0;
    private const long CommandRumble = Magic | 1;

    /// <summary>The generation of <see cref="WireState"/> this understands.</summary>
    private const uint WireVersion = 1;

    /// <summary>
    /// One pad, exactly as the host holds it: sticks 0..255 with 128 centred and Y growing downward,
    /// triggers 0..255.
    /// </summary>
    /// <remarks>
    /// Those are already <see cref="HostGamepadState"/>'s conventions, so nothing between the
    /// Android input event and the guest's pad data rescales an axis.
    ///
    /// <para>The layout is stated explicitly and its size is checked against the host by every read.
    /// Sequential packing is what C# would have chosen anyway; saying so is what makes it a promise
    /// rather than a default that a later refactor could quietly change.</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WireState
    {
        public uint Buttons;
        public byte LeftX;
        public byte LeftY;
        public byte RightX;
        public byte RightY;
        public byte LeftTrigger;
        public byte RightTrigger;
        public byte Connected;
        public byte Reserved;
    }

    /// <summary>
    /// The C library's syscall wrapper, with six arguments whether or not they are all used.
    /// </summary>
    /// <remarks>
    /// Declared with a fixed arity against a variadic function, which is safe here and only here: the
    /// x86-64 implementation is hand-written assembly that shuffles its arguments into the kernel's
    /// registers and reads none of the variadic machinery, so there is no register-count byte for a
    /// non-variadic call site to have failed to set.
    /// </remarks>
    [DllImport("libc", EntryPoint = "syscall", SetLastError = false)]
    private static extern long Syscall(long number, long a, long b, long c, long d, long e);

    public static bool IsSelected() =>
        string.Equals(
            Environment.GetEnvironmentVariable(SelectorVariable),
            "android",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True once a read has been refused, after which nothing is asked again.
    /// </summary>
    /// <remarks>
    /// A refusal means the host layer and this payload disagree about the wire format, which is not a
    /// condition that repairs itself mid-run. Polling on regardless would be a thousand refusals a
    /// second, each one printing on the host's side.
    /// </remarks>
    private bool _givenUp;

    /// <summary>Android has no keyboard to fall back to, so the pad is the whole of the input.</summary>
    /// <remarks>
    /// False is what the pad exports want here rather than a limitation: it leaves their keyboard
    /// contribution at neutral — no buttons, sticks centred — and the gamepad snapshot is then merged
    /// over the top of it exactly as it is on a desktop with no key held.
    /// </remarks>
    public bool HasKeyboardFocus => false;

    public bool IsKeyDown(int virtualKey) => false;

    public int GetGamepadStates(Span<HostGamepadState> destination)
    {
        if (_givenUp || destination.IsEmpty)
        {
            return 0;
        }

        WireState wire = default;
        long written;
        unsafe
        {
            written = Syscall(
                CommandRead,
                WireVersion,
                (long)(nint)(&wire),
                sizeof(WireState),
                0,
                0);
        }

        if (written < 0)
        {
            // The host has already said why on its own side, and it says it once. Repeating it here
            // would be the same sentence from the other end of the same call.
            _givenUp = true;
            return 0;
        }

        if (written == 0 || wire.Connected == 0)
        {
            return 0;
        }

        destination[0] = new HostGamepadState(
            Connected: true,
            Buttons: (HostGamepadButtons)wire.Buttons,
            LeftX: wire.LeftX,
            LeftY: wire.LeftY,
            RightX: wire.RightX,
            RightY: wire.RightY,
            LeftTrigger: wire.LeftTrigger,
            RightTrigger: wire.RightTrigger,
            // Reported as generic rather than guessed at. The Android input framework names a device
            // but does not say what layout it presents, and claiming DualSense would have the pad
            // exports advertise a touchpad and adaptive triggers that nothing here can deliver.
            Type: HostGamepadType.Generic,
            Connection: HostGamepadConnection.Unknown);
        return 1;
    }

    public string? DescribeConnectedGamepad()
    {
        Span<HostGamepadState> one = stackalloc HostGamepadState[1];
        return GetGamepadStates(one) > 0 ? "Android gamepad" : null;
    }

    public void SetRumble(byte largeMotor, byte smallMotor)
    {
        if (_givenUp)
        {
            return;
        }

        // Returns as soon as the host has recorded it. The host delivers it from a thread of its own
        // precisely so that this does not wait for a platform call.
        Syscall(CommandRumble, largeMotor, smallMotor, 0, 0, 0);
    }

    /// <summary>
    /// Not forwarded. Android exposes one vibration API and no per-trigger actuator, so an
    /// approximation here would be indistinguishable from <see cref="SetRumble"/> having been called
    /// with the louder of the two.
    /// </summary>
    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
    {
    }

    /// <summary>Not forwarded. There is no DualSense trigger actuator behind this.</summary>
    public void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger)
    {
    }

    /// <summary>Not forwarded. There is no lightbar.</summary>
    public void SetLightbar(byte red, byte green, byte blue)
    {
    }

    public void ResetLightbar()
    {
    }
}
