// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class VehTrampolineTests
{
    private static bool Supported => OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Handler(nint exceptionInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    [Fact]
    public async Task ConcurrentCallbacksDoNotSerializeAcrossManagedWaits()
    {
        if (!Supported) return;

        using var backend = new DirectExecutionBackend(new ModuleManager());
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = 0;
        Handler handler = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                secondEntered.Set();
            }
            return -1;
        };
        var thunk = backend.CreateExceptionHandlerTrampoline(Marshal.GetFunctionPointerForDelegate(handler));
        Assert.NotEqual(0, thunk);
        Task<int>? first = null;
        Task<int>? second = null;
        try
        {
            first = StartCallback(thunk);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)), "First callback did not enter.");
            second = StartCallback(thunk);
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5)),
                "A fault on another thread was blocked by the first managed callback.");
        }
        finally
        {
            releaseFirst.Set();
            if (first is not null) await first;
            if (second is not null) await second;
            VirtualFree(thunk, 0, 0x8000);
            GC.KeepAlive(handler);
        }
        Assert.Equal(-1, await first);
        Assert.Equal(-1, await second!);
    }

    [Theory]
    [InlineData(0xE0434352u)] // CLR exception must never re-enter managed code.
    [InlineData(0xE06D7363u)] // Native C++ exception.
    [InlineData(0xC00000FDu)] // Stack overflow.
    public void RuntimeExceptionsBypassManagedCallback(uint exceptionCode)
    {
        if (!Supported) return;
        using var backend = new DirectExecutionBackend(new ModuleManager());
        var called = false;
        Handler handler = _ => { called = true; return -1; };
        var thunk = backend.CreateExceptionHandlerTrampoline(Marshal.GetFunctionPointerForDelegate(handler));
        Assert.NotEqual(0, thunk);
        try
        {
            Assert.Equal(0, Invoke(thunk, exceptionCode));
            Assert.False(called);
        }
        finally
        {
            VirtualFree(thunk, 0, 0x8000);
            GC.KeepAlive(handler);
        }
    }

    private static Task<int> StartCallback(nint thunk) => Task.Factory.StartNew(
        () => Invoke(thunk, 0xC000001D), CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static unsafe int Invoke(nint thunk, uint code)
    {
        // Exercise the actual emitted Win64 entry on the host stack without
        // installing a process-wide VEH or raising a real hardware exception.
        byte* record = stackalloc byte[0xA0];
        byte* context = stackalloc byte[0x4D0];
        new Span<byte>(record, 0xA0).Clear();
        new Span<byte>(context, 0x4D0).Clear();
        *(uint*)record = code;
        nint* pointers = stackalloc nint[2];
        pointers[0] = (nint)record;
        pointers[1] = (nint)context;
        return ((delegate* unmanaged<nint, int>)thunk)((nint)pointers);
    }
}
