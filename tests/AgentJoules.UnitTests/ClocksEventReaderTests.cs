using AgentJoules.Nvml.Interop;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-003")]
[Trait("Spec", "EDGE-013")]
public sealed class ClocksEventReaderTests
{
    private const nint Device = 0x1000;

    private static ClocksEventReader Reader(Export current, Export deprecated) => new(current.Invoke, deprecated.Invoke);

    [Fact]
    public void The_current_export_wins_and_the_deprecated_one_is_never_called()
    {
        var current = new Export(() => 0, 0x4);
        var deprecated = new Export(() => 0, 0x99);
        ClocksEventReader reader = Reader(current, deprecated);

        NvmlReturn first = reader.Read(Device, out ulong firstReasons);
        NvmlReturn second = reader.Read(Device, out ulong secondReasons);

        Assert.Equal(NvmlReturn.Success, first);
        Assert.Equal(0x4UL, firstReasons);
        Assert.Equal(NvmlReturn.Success, second);
        Assert.Equal(0x4UL, secondReasons);
        Assert.Equal(2, current.Calls);
        Assert.Equal(0, deprecated.Calls);
    }

    [Fact]
    public void A_non_success_code_from_the_current_export_passes_through_without_a_fallback()
    {
        var current = new Export(() => 3, 0x99);
        var deprecated = new Export(() => 0, 0x99);
        ClocksEventReader reader = Reader(current, deprecated);

        NvmlReturn result = reader.Read(Device, out ulong reasons);
        NvmlReturn again = reader.Read(Device, out ulong againReasons);

        Assert.Equal(NvmlReturn.NotSupported, result);
        Assert.Equal(0UL, reasons);
        Assert.Equal(NvmlReturn.NotSupported, again);
        Assert.Equal(0UL, againReasons);
        Assert.Equal(0, deprecated.Calls);
        Assert.Equal(2, current.Calls);
    }

    [Fact]
    public void A_missing_current_export_falls_back_to_the_deprecated_one_and_remembers_it()
    {
        var current = new Export(() => throw new EntryPointNotFoundException("nvmlDeviceGetCurrentClocksEventReasons"));
        var deprecated = new Export(() => 0, 0x20);
        ClocksEventReader reader = Reader(current, deprecated);

        NvmlReturn first = reader.Read(Device, out ulong firstReasons);
        NvmlReturn second = reader.Read(Device, out ulong secondReasons);

        Assert.Equal(NvmlReturn.Success, first);
        Assert.Equal(0x20UL, firstReasons);
        Assert.Equal(NvmlReturn.Success, second);
        Assert.Equal(0x20UL, secondReasons);
        Assert.Equal(1, current.Calls);
        Assert.Equal(2, deprecated.Calls);
    }

    [Fact]
    public void Both_exports_missing_gives_NotSupported_and_later_reads_call_neither()
    {
        var current = new Export(() => throw new EntryPointNotFoundException("current"), 0x99);
        var deprecated = new Export(() => throw new EntryPointNotFoundException("deprecated"), 0x99);
        ClocksEventReader reader = Reader(current, deprecated);

        NvmlReturn first = reader.Read(Device, out ulong firstReasons);
        NvmlReturn second = reader.Read(Device, out ulong secondReasons);

        Assert.Equal(NvmlReturn.NotSupported, first);
        Assert.Equal(0UL, firstReasons);
        Assert.Equal(NvmlReturn.NotSupported, second);
        Assert.Equal(0UL, secondReasons);
        Assert.Equal(1, current.Calls);
        Assert.Equal(1, deprecated.Calls);
    }

    [Fact]
    public void A_DllNotFoundException_propagates_and_the_next_read_retries_from_the_start()
    {
        bool driverLoaded = false;
        var current = new Export(() => driverLoaded
            ? throw new EntryPointNotFoundException("current")
            : throw new DllNotFoundException("nvml"));
        var deprecated = new Export(() => 0, 0x8);
        ClocksEventReader reader = Reader(current, deprecated);

        Assert.Throws<DllNotFoundException>(() => reader.Read(Device, out _));
        Assert.Equal(1, current.Calls);
        Assert.Equal(0, deprecated.Calls);

        driverLoaded = true;
        NvmlReturn result = reader.Read(Device, out ulong reasons);

        Assert.Equal(NvmlReturn.Success, result);
        Assert.Equal(0x8UL, reasons);
        Assert.Equal(2, current.Calls);
        Assert.Equal(1, deprecated.Calls);
    }

    [Fact]
    public void Concurrent_first_reads_with_both_exports_missing_all_report_NotSupported()
    {
        var current = new Export(() => throw new EntryPointNotFoundException("current"));
        var deprecated = new Export(() => throw new EntryPointNotFoundException("deprecated"));
        ClocksEventReader reader = Reader(current, deprecated);
        var results = new NvmlReturn[16];

        Parallel.For(0, results.Length, i => results[i] = reader.Read(Device, out _));

        Assert.All(results, static result => Assert.Equal(NvmlReturn.NotSupported, result));
        int callsBefore = current.Calls + deprecated.Calls;
        Assert.Equal(NvmlReturn.NotSupported, reader.Read(Device, out _));
        Assert.Equal(callsBefore, current.Calls + deprecated.Calls);
    }

    /// <summary>A scripted export: runs <paramref name="behavior"/> (which may throw), records the call and reports <paramref name="reasons"/>.</summary>
    private sealed class Export(Func<int> behavior, ulong reasons = 0)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int Invoke(nint _, out ulong value)
        {
            Interlocked.Increment(ref _calls);

            // Assigned before the behavior runs so a throwing export leaves a stale value for the reader to discard.
            value = reasons;
            return behavior();
        }
    }
}