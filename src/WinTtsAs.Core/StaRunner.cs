using System.Runtime.ExceptionServices;

namespace WinTtsAs.Core;

internal static class StaRunner
{
    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "winTTSas-sta"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }
}
