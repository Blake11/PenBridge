namespace PenBridge.Core.Tests.Input;

[Collection(AllocationGroup.Name)]
public class AllocTests
{
    [Fact]
    public void Helper_detects_a_per_iteration_allocation()
    {
        object? keep = null;
        long n = 0;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Alloc.AssertZero(100, i =>
        {
            keep = new byte[(i & 7) + 1];
            n++;
        }, () => n));
        Assert.NotNull(keep);
    }

    [Fact]
    public void Helper_fails_when_the_hot_path_did_not_run()
    {
        long n = 0;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Alloc.AssertZero(100, _ => { }, () => n));
    }
}
