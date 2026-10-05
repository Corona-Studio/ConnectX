namespace ConnectX.Shared.Helpers;

public static class TaskHelper
{
    public static void Forget(this Task task) => new ValueTask(task).Forget();
    public static void Forget<T>(this Task<T> task) => new ValueTask<T>(task).Forget();

    // Pooled ValueTask sources must be consumed exactly once, even for callers
    // intentionally discarding the result. Reuse the observer and its delegate.
    public static void Forget(this ValueTask task)
    {
        if (task.IsCompleted)
        {
            try { task.GetAwaiter().GetResult(); }
            catch (Exception e) { System.Diagnostics.Trace.TraceError(e.ToString()); }
            return;
        }
        ForgetObserver.Rent(task);
    }

    public static void Forget<T>(this ValueTask<T> task)
    {
        if (task.IsCompleted)
        {
            try { task.GetAwaiter().GetResult(); }
            catch (Exception e) { System.Diagnostics.Trace.TraceError(e.ToString()); }
            return;
        }
        ForgetObserver<T>.Rent(task);
    }

    private sealed class ForgetObserver
    {
        private static readonly Hive.Common.Shared.Pooling.BoundedObjectPool<ForgetObserver> Pool = new(() => new ForgetObserver());
        private System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter _awaiter;
        private readonly Action _continuation;
        private ForgetObserver() { _continuation = Complete; }
        public static void Rent(ValueTask task)
        {
            var observer = Pool.Rent();
            observer._awaiter = task.ConfigureAwait(false).GetAwaiter();
            observer._awaiter.UnsafeOnCompleted(observer._continuation);
        }
        private void Complete()
        {
            try { _awaiter.GetResult(); }
            catch (Exception e) { System.Diagnostics.Trace.TraceError(e.ToString()); }
            finally { _awaiter = default; Pool.TryReturn(this); }
        }
    }

    private sealed class ForgetObserver<T>
    {
        private static readonly Hive.Common.Shared.Pooling.BoundedObjectPool<ForgetObserver<T>> Pool = new(() => new ForgetObserver<T>());
        private System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<T>.ConfiguredValueTaskAwaiter _awaiter;
        private readonly Action _continuation;
        private ForgetObserver() { _continuation = Complete; }
        public static void Rent(ValueTask<T> task)
        {
            var observer = Pool.Rent();
            observer._awaiter = task.ConfigureAwait(false).GetAwaiter();
            observer._awaiter.UnsafeOnCompleted(observer._continuation);
        }
        private void Complete()
        {
            try { _awaiter.GetResult(); }
            catch (Exception e) { System.Diagnostics.Trace.TraceError(e.ToString()); }
            finally { _awaiter = default; Pool.TryReturn(this); }
        }
    }

    public static async ValueTask WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken = default)
    {
        while (!predicate())
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
    }
}