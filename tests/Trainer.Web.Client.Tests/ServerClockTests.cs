using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class ServerClockTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ServerClock_CalibratesFromDateHeader()
    {
        FakeTimeProvider device = new(DeviceStart);
        ServerClock clock = new(device, new FakeBrowserStore());
        Assert.Equal(DeviceStart, clock.Now());

        // Sent at T, received at T+2 s: the midpoint is T+1 s. The server said T+1 h; Date drops the fraction, so the
        // best estimate is the middle of that second.
        DateTimeOffset serverDate = DeviceStart.AddHours(1);
        clock.Observe(serverDate, DeviceStart, DeviceStart.AddSeconds(2));

        TimeSpan expected = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(0.5) - TimeSpan.FromSeconds(1);
        Assert.Equal(expected, clock.Offset);
        Assert.Equal(DeviceStart + expected, clock.Now());

        // The latest accepted sample wins, also when it moves the offset back.
        device.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset at = device.GetUtcNow();
        clock.Observe(at.AddMinutes(-5), at, at);
        Assert.Equal(TimeSpan.FromMinutes(-5) + TimeSpan.FromSeconds(0.5), clock.Offset);
    }

    [Fact]
    public void ServerClock_IgnoresSlowRoundTripAndAbsurdOffset()
    {
        FakeTimeProvider device = new(DeviceStart);
        ServerClock clock = new(device, new FakeBrowserStore());
        clock.Observe(DeviceStart.AddMinutes(2), DeviceStart, DeviceStart.AddSeconds(1));
        TimeSpan calibrated = clock.Offset;

        clock.Observe(DeviceStart.AddHours(3), DeviceStart, DeviceStart.AddSeconds(10.5));
        clock.Observe(DeviceStart.AddDays(400), DeviceStart, DeviceStart.AddSeconds(1));
        clock.Observe(DeviceStart.AddDays(-400), DeviceStart, DeviceStart.AddSeconds(1));
        clock.Observe(DeviceStart.AddHours(3), DeviceStart.AddSeconds(1), DeviceStart);

        Assert.Equal(calibrated, clock.Offset);

        // Exactly 10 s and just under a year still count.
        clock.Observe(DeviceStart.AddDays(364), DeviceStart, DeviceStart.AddSeconds(10));
        Assert.Equal(TimeSpan.FromDays(364) + TimeSpan.FromSeconds(0.5) - TimeSpan.FromSeconds(5), clock.Offset);
    }

    [Fact]
    public async Task ServerClock_PersistsAndReloadsOffset()
    {
        FakeTimeProvider device = new(DeviceStart);
        FakeBrowserStore store = new();
        ServerClock first = new(device, store);
        first.Observe(DeviceStart.AddMinutes(-7), DeviceStart, DeviceStart);

        using (JsonDocument saved = JsonDocument.Parse(store.Raw(StorageKeys.ServerClock)!))
        {
            Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
            Assert.Equal((long)first.Offset.TotalMilliseconds, saved.RootElement.GetProperty("offsetMs").GetInt64());
            Assert.Equal(DeviceStart, saved.RootElement.GetProperty("observedAt").GetDateTimeOffset());
        }

        ServerClock reloaded = new(device, store);
        Assert.Equal(TimeSpan.Zero, reloaded.Offset);
        await reloaded.LoadAsync();

        Assert.Equal(first.Offset, reloaded.Offset);
        Assert.Equal(DeviceStart + first.Offset, reloaded.Now());
    }

    [Fact]
    public async Task ServerClock_SlowEarlierWrite_NeverOverwritesTheNewestSample()
    {
        FakeTimeProvider device = new(DeviceStart);
        FakeBrowserStore inner = new();
        HookStore store = new(inner);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        // The first write stalls (a busy storage lock in another tab); the second sample arrives meanwhile.
        store.Intercept = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
            }

            return null;
        };
        ServerClock clock = new(device, store);

        clock.Observe(DeviceStart.AddMinutes(-7), DeviceStart, DeviceStart);
        clock.Observe(DeviceStart.AddMinutes(-3), DeviceStart, DeviceStart);
        release.SetResult();
        for (int i = 0; i < 100 && inner.Writes < 2; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        using JsonDocument saved = JsonDocument.Parse(inner.Raw(StorageKeys.ServerClock)!);
        Assert.Equal((long)clock.Offset.TotalMilliseconds, saved.RootElement.GetProperty("offsetMs").GetInt64());
    }

    [Fact]
    public void ServerClock_DeviceClockJumpAfterCalibration_DoesNotChangeStamps()
    {
        FakeTimeProvider monotonic = new(DeviceStart);
        JumpingTimeProvider device = new(monotonic);
        ServerClock clock = new(device, new FakeBrowserStore());
        clock.Observe(DeviceStart.AddMinutes(2), DeviceStart, DeviceStart);
        DateTimeOffset calibrated = DeviceStart.AddMinutes(2).AddSeconds(0.5);
        Assert.Equal(calibrated, clock.Now());

        monotonic.Advance(TimeSpan.FromSeconds(30));
        device.Jump = TimeSpan.FromHours(3);
        Assert.Equal(calibrated.AddSeconds(30), clock.Now());

        device.Jump = TimeSpan.FromHours(-5);
        monotonic.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(calibrated.AddSeconds(40), clock.Now());

        // The next response re-calibrates from the server, whatever the device says.
        DateTimeOffset at = device.GetUtcNow();
        clock.Observe(DeviceStart.AddMinutes(3), at, at);
        Assert.Equal(DeviceStart.AddMinutes(3).AddSeconds(0.5), clock.Now());
    }

    [Fact]
    public async Task ServerClock_PersistedOffset_JumpAfterLoad_DoesNotChangeStamps()
    {
        FakeTimeProvider monotonic = new(DeviceStart);
        JumpingTimeProvider device = new(monotonic);
        FakeBrowserStore store = new();
        store.SetRaw(StorageKeys.ServerClock, "{\"version\":1,\"offsetMs\":60000,\"observedAt\":\"2026-11-12T09:30:00+00:00\"}");
        ServerClock clock = new(device, store);

        await clock.LoadAsync();
        device.Jump = TimeSpan.FromHours(2);

        Assert.Equal(DeviceStart.AddMinutes(1), clock.Now());
    }

    [Theory]
    [InlineData("2026-11-12T08:59:59+00:00")]
    [InlineData("2026-11-12T10:00:01+00:00")]
    public async Task ServerClock_StalePersistedOffset_IsIgnored(string observedAt)
    {
        FakeBrowserStore store = new();
        store.SetRaw(StorageKeys.ServerClock, $"{{\"version\":1,\"offsetMs\":60000,\"observedAt\":\"{observedAt}\"}}");
        ServerClock clock = new(new FakeTimeProvider(DeviceStart), store);

        await clock.LoadAsync();

        Assert.Equal(TimeSpan.Zero, clock.Offset);
        Assert.Equal(DeviceStart, clock.Now());
    }

    [Fact]
    public void ServerClock_WritesOncePerCalibrationNeverPerStamp()
    {
        FakeTimeProvider device = new(DeviceStart);
        FakeBrowserStore store = new();
        ServerClock clock = new(device, store);

        clock.Observe(DeviceStart.AddMinutes(1), DeviceStart, DeviceStart);
        for (int i = 0; i < 10; i++)
        {
            _ = clock.Now();
        }

        clock.Observe(DeviceStart.AddSeconds(10.5), DeviceStart, DeviceStart.AddSeconds(11));
        clock.Observe(DeviceStart.AddMinutes(1), DeviceStart, DeviceStart);

        Assert.Equal(2, store.Ops.Count(o => o.Op == "put" && o.Key == StorageKeys.ServerClock));
    }

    /// <summary>A device whose wall clock the user can move while the monotonic timestamp keeps ticking.</summary>
    private sealed class JumpingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        public TimeSpan Jump { get; set; }

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Jump;

        public override long GetTimestamp() => inner.GetTimestamp();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"offsetMs\":\"x\",\"observedAt\":\"2026-11-12T10:00:00+00:00\"}")]
    [InlineData("{\"version\":1,\"offsetMs\":99999999999999,\"observedAt\":\"2026-11-12T10:00:00+00:00\"}")]
    [InlineData("{\"version\":1,\"offsetMs\":5000}")]
    [InlineData("{\"version\":1,\"offsetMs\":-9223372036854775808,\"observedAt\":\"2026-11-12T10:00:00+00:00\"}")]
    [InlineData("{\"version\":1,\"offsetMs\":5000,\"observedAt\":\"x\"}")]
    [InlineData("{\"version\":2,\"offsetMs\":5000,\"observedAt\":\"2026-11-12T10:00:00+00:00\"}")]
    public async Task ServerClock_UnreadableStoredOffset_StartsAtZero(string raw)
    {
        FakeBrowserStore store = new();
        store.SetRaw(StorageKeys.ServerClock, raw);
        ServerClock clock = new(new FakeTimeProvider(DeviceStart), store);

        await clock.LoadAsync();

        Assert.Equal(TimeSpan.Zero, clock.Offset);
    }
}
