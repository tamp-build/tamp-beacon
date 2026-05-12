using System;
using System.Threading.Tasks;
using Tamp.Beacon.Push;
using Xunit;

namespace Tamp.Beacon.Tests;

public sealed class FailureCoalescerTests
{
    [Fact]
    public void FirstEmit_AlwaysAllowed()
    {
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5));
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
    }

    [Fact]
    public void RepeatWithinWindow_Suppressed()
    {
        var now = DateTimeOffset.UtcNow;
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5), () => now);
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
        Assert.False(coalescer.ShouldEmit("HoldFast", "Test"));
    }

    [Fact]
    public void RepeatAfterWindow_AllowedAgain()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5), clock);
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
        now = now.AddMinutes(6);
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
    }

    [Fact]
    public void DifferentProjects_AreIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5), () => now);
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
        Assert.True(coalescer.ShouldEmit("DoTrack", "Test"));
    }

    [Fact]
    public void DifferentTargets_AreIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5), () => now);
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
        Assert.True(coalescer.ShouldEmit("HoldFast", "Pack"));
    }

    [Fact]
    public void NullTarget_Independent()
    {
        var now = DateTimeOffset.UtcNow;
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5), () => now);
        Assert.True(coalescer.ShouldEmit("HoldFast", null));
        Assert.False(coalescer.ShouldEmit("HoldFast", null));
        Assert.True(coalescer.ShouldEmit("HoldFast", "Test"));
    }

    [Fact]
    public async Task ConcurrentEmits_OnlyOneSucceedsPerWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var coalescer = new FailureCoalescer(TimeSpan.FromMinutes(5), () => now);

        var tasks = new Task<bool>[100];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() => coalescer.ShouldEmit("HoldFast", "Test"));

        var results = await Task.WhenAll(tasks);
        var emitted = 0;
        foreach (var r in results) if (r) emitted++;
        Assert.Equal(1, emitted);
    }

    [Fact]
    public void TrackedKeyCount_GrowsWithDistinctKeys()
    {
        var coalescer = new FailureCoalescer(TimeSpan.FromSeconds(60));
        coalescer.ShouldEmit("A", "x");
        coalescer.ShouldEmit("B", "x");
        coalescer.ShouldEmit("C", null);
        Assert.Equal(3, coalescer.TrackedKeyCount);
    }
}
