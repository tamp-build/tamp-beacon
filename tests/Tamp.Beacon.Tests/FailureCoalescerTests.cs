using System;
using Tamp.Beacon.Push;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Unit tests for the (project_id, target_name) coalescer. Clock is
/// injected so we can fast-forward through windows without sleeping.
/// </summary>
public sealed class FailureCoalescerTests
{
    [Fact]
    public void First_Emit_Returns_True()
    {
        var clock = new ManualClock();
        var c = new FailureCoalescer(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.True(c.ShouldEmit(projectId: 1, targetName: "Compile"));
    }

    [Fact]
    public void Repeat_Within_Window_Is_Suppressed()
    {
        var clock = new ManualClock();
        var c = new FailureCoalescer(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.True(c.ShouldEmit(1, "Compile"));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(c.ShouldEmit(1, "Compile"));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(c.ShouldEmit(1, "Compile"));
    }

    [Fact]
    public void Emit_Reopens_After_Window_Lapses()
    {
        var clock = new ManualClock();
        var c = new FailureCoalescer(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.True(c.ShouldEmit(1, "Compile"));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(c.ShouldEmit(1, "Compile"));
    }

    [Fact]
    public void Different_Project_Same_Target_Independent()
    {
        var clock = new ManualClock();
        var c = new FailureCoalescer(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.True(c.ShouldEmit(1, "Compile"));
        Assert.True(c.ShouldEmit(2, "Compile"));
    }

    [Fact]
    public void Same_Project_Different_Target_Independent()
    {
        var clock = new ManualClock();
        var c = new FailureCoalescer(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.True(c.ShouldEmit(1, "Compile"));
        Assert.True(c.ShouldEmit(1, "Test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_Or_Empty_Target_Is_Distinct_Bucket(string? target)
    {
        var clock = new ManualClock();
        var c = new FailureCoalescer(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.True(c.ShouldEmit(1, target));
        // a different (project, target) — "Compile" — still emits because
        // it's a different key, even with the same projectId.
        Assert.True(c.ShouldEmit(1, "Compile"));
        // re-emit with the SAME null/empty bucket → suppressed.
        Assert.False(c.ShouldEmit(1, target));
    }

    private sealed class ManualClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan dt) => Now += dt;
    }
}
