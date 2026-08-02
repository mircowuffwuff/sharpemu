// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Guards the host address space layout. The classic arm has to keep producing
/// the literals it always did - those addresses are load bearing on Windows,
/// Linux and macOS alike - and the compact arm has to land somewhere a narrower
/// host can actually map, clear of the guest ranges below it.
/// </summary>
public sealed class HostAddressSpaceTests
{
    // 39-bit arm64 kernel, the Android configuration: the top of the process
    // stack as /proc/self/maps reports it on a Snapdragon 8 Elite.
    private const ulong NarrowLimit = 0x0000_007F_CFE9_F000UL;

    [Fact]
    public void WindowsKeepsTheHistoricalLayout()
    {
        var layout = HostAddressSpace.Compute(HostAddressSpace.ClassicWindowsLimit, windows: true);

        Assert.False(layout.IsCompact);
        Assert.Equal(0x7FFF_F000_0000UL, layout.StackBaseAddress);
        Assert.Equal(0x7FFE_0000_0000UL, layout.TlsBaseAddress);
        Assert.Equal(0x7FFD_F000_0000UL, layout.BootstrapStubBaseAddress);
        Assert.Equal(0x7FFD_E000_0000UL, layout.BootstrapPayloadBaseAddress);
        Assert.Equal(0x7FFD_D000_0000UL, layout.DynlibFallbackStubBaseAddress);
        Assert.Equal(0x7FFD_C000_0000UL, layout.ReturnToHostStubBaseAddress);
        Assert.Equal(0x7FFF_E000_0000UL, layout.GuestThreadStackBaseAddress);
        Assert.Equal(0x0000_7000_0000_0000UL, layout.ImportStubBaseAddress);
        Assert.Equal(0x0000_6000_0000_0000UL, layout.GuestAllocationArenaAddress);
    }

    [Fact]
    public void PosixWithA47BitAddressSpaceKeepsTheHistoricalLayout()
    {
        var layout = HostAddressSpace.Compute(HostAddressSpace.ClassicPosixLimit, windows: false);

        Assert.False(layout.IsCompact);
        Assert.Equal(0x6FFF_F000_0000UL, layout.StackBaseAddress);
        Assert.Equal(0x6FFE_0000_0000UL, layout.TlsBaseAddress);
        Assert.Equal(0x6FFD_F000_0000UL, layout.BootstrapStubBaseAddress);
        Assert.Equal(0x6FFD_E000_0000UL, layout.BootstrapPayloadBaseAddress);
        Assert.Equal(0x6FFD_D000_0000UL, layout.DynlibFallbackStubBaseAddress);
        Assert.Equal(0x6FFD_C000_0000UL, layout.ReturnToHostStubBaseAddress);
        Assert.Equal(0x6FFF_E000_0000UL, layout.GuestThreadStackBaseAddress);
        Assert.Equal(0x0000_7000_0000_0000UL, layout.ImportStubBaseAddress);
        Assert.Equal(0x0000_6000_0000_0000UL, layout.GuestAllocationArenaAddress);
    }

    [Fact]
    public void A39BitAddressSpaceGetsACompactLayoutThatFits()
    {
        var layout = HostAddressSpace.Compute(NarrowLimit, windows: false);

        Assert.True(layout.IsCompact);
        Assert.True(layout.FamilyTop < NarrowLimit);
        Assert.True(layout.ImportStubBaseAddress < NarrowLimit);

        // Every region, and every address the downward probes can reach, stays
        // inside the reservation.
        Assert.True(layout.LowestProbedAddress >= layout.FamilyTop - HostAddressSpace.ReservationSize);
        Assert.True(layout.GuestAllocationArenaAddress >= layout.FamilyTop - HostAddressSpace.ReservationSize);
        Assert.True(layout.GuestAllocationArenaAddress < layout.LowestProbedAddress);
    }

    [Fact]
    public void TheCompactLayoutStaysClearOfTheGuestRanges()
    {
        var layout = HostAddressSpace.Compute(NarrowLimit, windows: false);
        var lowest = layout.FamilyTop - HostAddressSpace.ReservationSize;

        // Ps5ModuleSearchEnd, 36 GiB (SelfLoader), and the POSIX arm of
        // KernelMemoryCompatExports.DefaultMapSearchBase, 128 GiB, which grows
        // upward as the guest asks for direct and flexible memory.
        Assert.True(lowest > 0x0000_0009_0000_0000UL);
        Assert.True(lowest > 0x0000_0020_0000_0000UL);
        Assert.True(lowest >= HostAddressSpace.CompactFloor);
    }

    [Fact]
    public void TheCompactLayoutDoesNotMoveWithAslr()
    {
        // The detected ceiling is the top of the process stack, which the kernel
        // jitters by a few pages per launch. The layout must not follow it.
        var low = HostAddressSpace.Compute(NarrowLimit - 0x21_0000UL, windows: false);
        var high = HostAddressSpace.Compute(NarrowLimit + 0x33_0000UL, windows: false);

        Assert.Equal(low.FamilyTop, high.FamilyTop);
    }

    [Fact]
    public void AnAddressSpaceTooSmallForTheLayoutSaysSoRatherThanOverlapping()
    {
        // 34 bits. Nothing we target, but silently producing a layout on top of
        // the guest's own ranges would be far worse than refusing.
        Assert.Throws<InvalidOperationException>(
            () => HostAddressSpace.Compute(0x0000_0003_FFFF_F000UL, windows: false));
    }
}
