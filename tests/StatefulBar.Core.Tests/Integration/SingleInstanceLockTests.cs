using StatefulBar.Core.Integration;

namespace StatefulBar.Core.Tests.Integration;

public sealed class SingleInstanceLockTests
{
    private readonly string _name = @"Local\StatefulBar-test-" + Guid.NewGuid().ToString("N");

    // A mutex is owned per thread: "another instance" is simulated by acquiring on a dedicated thread.
    private static T OnOtherThread<T>(Func<T> action)
    {
        T result = default!;
        var thread = new Thread(() => result = action());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void NameForCurrentUser_IsLocalAndPerUser()
    {
        var name = SingleInstanceLock.NameForCurrentUser();
        Assert.StartsWith(@"Local\StatefulBar-S-1-", name);
    }

    [Fact]
    public void SecondAcquire_WhileHeld_Fails()
    {
        using var first = SingleInstanceLock.TryAcquire(_name, out var abandoned);
        Assert.NotNull(first);
        Assert.False(abandoned);

        var second = OnOtherThread(() => SingleInstanceLock.TryAcquire(_name, out _));

        Assert.Null(second);
    }

    [Fact]
    public void AfterDispose_CanBeAcquiredAgain()
    {
        SingleInstanceLock.TryAcquire(_name, out _)!.Dispose();

        var again = OnOtherThread(() =>
        {
            using var held = SingleInstanceLock.TryAcquire(_name, out _);
            return held is not null;
        });

        Assert.True(again);
    }

    [Fact]
    public void AbandonedMutex_CountsAsAcquired()
    {
        // The owning thread ends without releasing (like a crashed instance).
        var leaked = OnOtherThread(() => SingleInstanceLock.TryAcquire(_name, out _));
        Assert.NotNull(leaked);

        // Windows marks the mutex abandoned once the owning thread is gone at the OS level, which can be a moment after
        // Thread.Join returns; until then the mutex still reads as held.
        SingleInstanceLock? next = null;
        var abandoned = false;
        var acquired = SpinWait.SpinUntil(
            () => (next = SingleInstanceLock.TryAcquire(_name, out abandoned)) is not null, TimeSpan.FromSeconds(5));

        using (next)
        {
            Assert.True(acquired);
            Assert.True(abandoned);
        }
    }
}
