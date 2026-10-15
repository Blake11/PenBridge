namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Test classes with zero-allocation assertions (<see cref="Alloc.AssertZero"/>). The collection runs alone, after
/// the parallel ones: runtime work done for other tests running in parallel (JIT, type loading, tiering) can land on
/// the measuring thread in every measured pass and make the assertion flaky.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationGroup
{
    public const string Name = "Allocation assertions (not parallel)";
}
