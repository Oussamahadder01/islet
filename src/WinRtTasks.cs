using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Islet
{
    // The in-box C# 5 compiler cannot bind System.Runtime.WindowsRuntime's generic
    // awaiters to the split System32 metadata, so async operations are adapted here.
    public static class WinRtTasks
    {
        public static Task<T> ToTask<T>(this IAsyncOperation<T> operation)
        {
            var completion = new TaskCompletionSource<T>();
            operation.Completed = (op, status) =>
            {
                try
                {
                    switch (status)
                    {
                        case AsyncStatus.Completed:
                            completion.TrySetResult(op.GetResults());
                            break;
                        case AsyncStatus.Canceled:
                            completion.TrySetCanceled();
                            break;
                        default:
                            completion.TrySetException(op.ErrorCode ?? new InvalidOperationException("WinRT operation failed: " + status));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            };
            return completion.Task;
        }
    }
}
