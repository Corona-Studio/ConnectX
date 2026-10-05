using NUnit.Framework;

[TestFixture, NonParallelizable]
public class RegressionTests
{
    [Test] public Task BorrowedReceiveLifetimeAndCancellation() => RegressionSuite.ReceiveLifetime();
    [Test] public Task RejectMalformedAndTruncatedFrames() => RegressionSuite.InvalidFrames();
    [Test] public Task SegmentedPartialSendingAndCancellation() => RegressionSuite.Sending();
    [Test] public Task WritableFramesUseOriginalArrayAndHandlePartialSends() => RegressionSuite.WritableFrameSending();
    [Test] public void PacketMappingReadsRemainConsistentDuringRegistration() => RegressionSuite.PacketMappingConcurrency();
    [Test] public Task ConcurrentTcpStreamAndBorrowedSends() => RegressionSuite.TcpRoundTrip();
    [Test] public Task QueueOwnsPayloadUntilAsyncSendCompletes() => RegressionSuite.QueuedOwnerLifetime();
    [Test] public void WarmedBorrowedSendAndCallbackAllocations() => RegressionSuite.AllocationProbe();
    [Test] public void FragmentedSnappyCompatibilityAndMutations() => RegressionSuite.DecoderCompatibilityProbe();
    [Test] public void SharedBuffersAndRealMemoryPackWireCompatibility() => RegressionSuite.PoolAndCodecProbe();
    [Test] public void DiscardedValueTasksAreConsumedWithoutAllocations() => RegressionSuite.ForgetProbe();
    [Test] public void ConcurrentPoolOwnershipAndLimits() => RegressionSuite.PoolConcurrencyAndLimits();
    [Test] public Task BoundedQueueRetriesPreserveOrderingAndDrain() => RegressionSuite.BoundedQueueRetryAndDrain();
    [Test] public Task RealTcpAllocationMeasurement() => RegressionSuite.TcpAllocationProbe();
}
