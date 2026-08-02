// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Where SharpEmu parks its own high bookkeeping: the import stub region, the
/// guest stack and TLS windows, the bootstrap stubs and the guest allocation
/// arena.
///
/// These have always been fixed literals near the top of a 47-bit user address
/// space, with a Windows arm and a POSIX arm one 16 TiB slot lower. That holds
/// on every desktop host, but not everywhere: an arm64 Linux kernel built for a
/// 39-bit VA - the common Android configuration - gives userspace 512 GiB, and
/// the classic layout sits 229x past that ceiling, so the very first import stub
/// mapping fails and nothing can load.
///
/// So the layout is chosen once, at startup, from the address space the host
/// actually has. Where the classic layout fits it is used unchanged, down to the
/// bit - which is every host SharpEmu has run on to date. Where it does not, the
/// whole family shifts down as one block, preserving every relative offset and
/// every downward probe window, into the middle of whatever space there is.
/// </summary>
public static class HostAddressSpace
{
    // The classic anchors. Nothing is mapped at the family top itself; it is the
    // exclusive ceiling every offset below is measured from.
    internal const ulong ClassicWindowsFamilyTop = 0x8000_0000_0000UL;
    internal const ulong ClassicPosixFamilyTop = 0x7000_0000_0000UL;

    // The import stub region is the one member that has never had a Windows arm:
    // 112 TiB on every host.
    internal const ulong ClassicImportStubBase = 0x7000_0000_0000UL;

    // ...and the guest allocation arena is at 96 TiB on every host, 16 TiB below
    // the family, which is why it cannot simply ride the same shift.
    internal const ulong ClassicGuestAllocationArenaBase = 0x6000_0000_0000UL;

    // Offsets below the family top. These reproduce the historical literals
    // exactly on both arms - 0x7FFF_F000_0000 / 0x6FFF_F000_0000 and friends.
    private const ulong StackOffset = 0x0000_1000_0000UL;
    private const ulong GuestThreadStackOffset = 0x0000_2000_0000UL;
    private const ulong TlsOffset = 0x0002_0000_0000UL;
    private const ulong BootstrapStubOffset = 0x0002_1000_0000UL;
    private const ulong BootstrapPayloadOffset = 0x0002_2000_0000UL;
    private const ulong DynlibFallbackStubOffset = 0x0002_3000_0000UL;
    private const ulong ReturnToHostStubOffset = 0x0002_4000_0000UL;

    // Largest region mapped at an offset base, used to work out what the classic
    // layout's highest touched address actually is.
    private const ulong MainStackSize = 0x0020_0000UL;
    private const ulong ImportStubRegionAllowance = 0x0010_0000UL;

    /// <summary>
    /// Stride between consecutive stack/TLS slot candidates. Every region in the
    /// family probes downward from its base at this stride.
    /// </summary>
    public const ulong GuestThreadRegionStride = 0x0100_0000UL;

    // The family's deepest downward reach is the per-thread TLS window: TlsOffset
    // plus 1023 slots, a little under 24 GiB. Reserve 32 GiB so the arena has
    // somewhere to sit below it in the compact layout.
    internal const ulong ReservationSize = 0x0008_0000_0000UL;
    private const ulong CompactGuestAllocationArenaOffset = 0x0007_0000_0000UL;

    // Compact layouts round to 16 GiB so the result does not move under ASLR: the
    // detected ceiling wobbles by a few pages per process and the layout should
    // not.
    private const ulong CompactAlignment = 0x0004_0000_0000UL;

    // The compact family has to land clear of everything else this process claims
    // lower down: the PS5 image and module search window (32-36 GiB, SelfLoader)
    // and the guest virtual address the kernel compat layer starts handing out
    // direct/flexible mappings from on POSIX (128 GiB, growing upward - see
    // KernelMemoryCompatExports.DefaultMapSearchBase). 160 GiB is the floor that
    // keeps both of those below us.
    internal const ulong CompactFloor = 0x0028_0000_0000UL;

    // Nothing above 2^48 is a user address anywhere; on x86-64 Linux
    // /proc/self/maps ends with [vsyscall] at 0xFFFFFFFFFF600000, which is the
    // kernel's, not ours.
    private const ulong MaximumPlausibleLimit = 0x0001_0000_0000_0000UL;

    // Classic ceilings, used when the host cannot be asked.
    internal const ulong ClassicWindowsLimit = 0x0000_7FFF_FFFF_0000UL;
    internal const ulong ClassicPosixLimit = 0x0000_7FFF_FFFF_F000UL;

    private static readonly Layout Current =
        Compute(DetectUserAddressSpaceLimit(), OperatingSystem.IsWindows());

    static HostAddressSpace()
    {
        if (Current.IsCompact)
        {
            // Only ever printed when the layout is not the historical one, so a
            // desktop run stays exactly as quiet as it was.
            Console.Error.WriteLine($"[LOADER] {Describe()}");
        }
    }

    /// <summary>
    /// The highest user address this process can expect to map, as measured
    /// rather than assumed.
    /// </summary>
    public static ulong UserAddressSpaceLimit => Current.UserAddressSpaceLimit;

    /// <summary>
    /// True when the classic 47-bit layout did not fit and the family was shifted
    /// down to suit a narrower address space.
    /// </summary>
    public static bool UsesCompactLayout => Current.IsCompact;

    /// <summary>
    /// Exclusive ceiling of SharpEmu's high bookkeeping. Every base below is an
    /// offset from here.
    /// </summary>
    public static ulong FamilyTop => Current.FamilyTop;

    /// <summary>Base of the downward import stub probe (SelfLoader).</summary>
    public static ulong ImportStubBaseAddress => Current.ImportStubBaseAddress;

    /// <summary>Base of the main guest stack probe (CpuDispatcher).</summary>
    public static ulong StackBaseAddress => Current.StackBaseAddress;

    /// <summary>Base of the main guest TLS probe (CpuDispatcher).</summary>
    public static ulong TlsBaseAddress => Current.TlsBaseAddress;

    /// <summary>Base of the bootstrap stub probe (CpuDispatcher).</summary>
    public static ulong BootstrapStubBaseAddress => Current.BootstrapStubBaseAddress;

    /// <summary>Base of the bootstrap payload probe (CpuDispatcher).</summary>
    public static ulong BootstrapPayloadBaseAddress => Current.BootstrapPayloadBaseAddress;

    /// <summary>Base of the dynlib fallback stub probe (CpuDispatcher).</summary>
    public static ulong DynlibFallbackStubBaseAddress => Current.DynlibFallbackStubBaseAddress;

    /// <summary>Base of the return-to-host stub probe (CpuDispatcher).</summary>
    public static ulong ReturnToHostStubBaseAddress => Current.ReturnToHostStubBaseAddress;

    /// <summary>Base of the per-thread guest stack probe (DirectExecutionBackend).</summary>
    public static ulong GuestThreadStackBaseAddress => Current.GuestThreadStackBaseAddress;

    /// <summary>Base of the per-thread guest TLS probe (DirectExecutionBackend).</summary>
    public static ulong GuestThreadTlsBaseAddress => Current.TlsBaseAddress;


    /// <summary>Base of the small guest allocation arena (PhysicalVirtualMemory).</summary>
    public static ulong GuestAllocationArenaAddress => Current.GuestAllocationArenaAddress;

    /// <summary>One line naming the chosen layout, for diagnostics.</summary>
    public static string Describe() =>
        $"host address space: limit=0x{UserAddressSpaceLimit:X} " +
        $"layout={(UsesCompactLayout ? "compact" : "classic")} " +
        $"top=0x{FamilyTop:X} stubs=0x{ImportStubBaseAddress:X} " +
        $"stack=0x{StackBaseAddress:X} tls=0x{TlsBaseAddress:X} " +
        $"threads=0x{GuestThreadStackBaseAddress:X} arena=0x{GuestAllocationArenaAddress:X}";

    /// <summary>
    /// The three values a layout is fully determined by. Split out from the
    /// properties above so the choice can be exercised for address space sizes
    /// the test machine does not have.
    /// </summary>
    internal readonly record struct Layout(
        ulong UserAddressSpaceLimit,
        bool IsCompact,
        ulong FamilyTop,
        ulong ImportStubBaseAddress,
        ulong GuestAllocationArenaAddress)
    {
        public ulong StackBaseAddress => FamilyTop - StackOffset;

        public ulong TlsBaseAddress => FamilyTop - TlsOffset;

        public ulong BootstrapStubBaseAddress => FamilyTop - BootstrapStubOffset;

        public ulong BootstrapPayloadBaseAddress => FamilyTop - BootstrapPayloadOffset;

        public ulong DynlibFallbackStubBaseAddress => FamilyTop - DynlibFallbackStubOffset;

        public ulong ReturnToHostStubBaseAddress => FamilyTop - ReturnToHostStubOffset;

        public ulong GuestThreadStackBaseAddress => FamilyTop - GuestThreadStackOffset;

        /// <summary>
        /// Lowest address any member of the family can probe down to: the
        /// per-thread TLS window's last slot.
        /// </summary>
        public ulong LowestProbedAddress => TlsBaseAddress - (1023 * GuestThreadRegionStride);
    }

    internal static Layout Compute(ulong userAddressSpaceLimit, bool windows)
    {
        var classicFamilyTop = windows ? ClassicWindowsFamilyTop : ClassicPosixFamilyTop;

        // The highest address the classic layout actually touches: the top of the
        // main guest stack, or the top of the import stub mapping, whichever is
        // higher. Not the family top, which is never mapped - on Windows that is
        // 0x8000_0000_0000, above the platform's own ceiling.
        var classicHighestMapped = Math.Max(
            classicFamilyTop - StackOffset + MainStackSize,
            ClassicImportStubBase + ImportStubRegionAllowance);

        if (classicHighestMapped <= userAddressSpaceLimit)
        {
            return new Layout(
                userAddressSpaceLimit,
                IsCompact: false,
                classicFamilyTop,
                ClassicImportStubBase,
                ClassicGuestAllocationArenaBase);
        }

        var compactFamilyTop = AlignDown(userAddressSpaceLimit / 2, CompactAlignment);
        if (compactFamilyTop < CompactFloor + ReservationSize)
        {
            throw new InvalidOperationException(
                $"The host user address space is too small for SharpEmu's layout: " +
                $"limit=0x{userAddressSpaceLimit:X} gives a reservation top of " +
                $"0x{compactFamilyTop:X}, which has to be at least " +
                $"0x{CompactFloor + ReservationSize:X}.");
        }

        // The import stub region rides the family in the compact layout. On a
        // 47-bit POSIX host it already shares the family top, so this is the same
        // arrangement, not a new one.
        return new Layout(
            userAddressSpaceLimit,
            IsCompact: true,
            compactFamilyTop,
            compactFamilyTop,
            compactFamilyTop - CompactGuestAllocationArenaOffset);
    }

    private static ulong DetectUserAddressSpaceLimit()
    {
        if (OperatingSystem.IsWindows())
        {
            // Every Windows x64 host gives the same ceiling and the classic
            // layout is validated there, so do not go looking.
            return ClassicWindowsLimit;
        }

        // Linux is the only host that narrows the address space, and it will say
        // so: the highest mapping in /proc/self/maps is the process stack, which
        // the kernel places just under the ceiling. macOS has no procfs and falls
        // through to the classic assumption, which is correct there.
        return TryReadHighestMappedAddress(out var highest) ? highest : ClassicPosixLimit;
    }

    private static bool TryReadHighestMappedAddress(out ulong highest)
    {
        highest = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var dash = line.IndexOf('-');
                if (dash <= 0)
                {
                    continue;
                }

                var space = line.IndexOf(' ', dash + 1);
                if (space < 0)
                {
                    continue;
                }

                if (!ulong.TryParse(
                        line.AsSpan(dash + 1, space - dash - 1),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var end))
                {
                    continue;
                }

                if (end <= MaximumPlausibleLimit && end > highest)
                {
                    highest = end;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        return highest != 0;
    }

    private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);
}
