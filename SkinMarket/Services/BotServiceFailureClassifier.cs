namespace SkinMarket.Services;

internal static class BotServiceFailureClassifier
{
    public static bool IsConnectivityFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return true;
        }

        return exception.InnerException is not null && IsConnectivityFailure(exception.InnerException, cancellationToken);
    }
}
