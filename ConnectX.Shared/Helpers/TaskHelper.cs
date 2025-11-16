namespace ConnectX.Shared.Helpers;

public static class TaskHelper
{
    public static void Forget(this Task _)
    {
    }

    public static void Forget<T>(this Task<T> _)
    {
    }

    public static void Forget(this ValueTask _)
    {
    }

    public static void Forget<T>(this ValueTask<T> _)
    {
    }

    public static async ValueTask WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken = default)
    {
        while (!predicate())
        {
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
}