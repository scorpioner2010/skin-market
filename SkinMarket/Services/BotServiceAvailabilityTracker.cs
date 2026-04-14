namespace SkinMarket.Services;

public sealed class BotServiceAvailabilityTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, OutageState> _states = new(StringComparer.OrdinalIgnoreCase);

    public FailureRegistration RegisterFailure(string serviceUrl)
    {
        var key = Normalize(serviceUrl);

        lock (_sync)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                state = new OutageState();
                _states[key] = state;
            }

            state.FailureCount += 1;
            if (state.IsActive)
            {
                return new FailureRegistration(false, state.FailureCount);
            }

            state.IsActive = true;
            return new FailureRegistration(true, state.FailureCount);
        }
    }

    public RecoveryRegistration RegisterSuccess(string serviceUrl)
    {
        var key = Normalize(serviceUrl);

        lock (_sync)
        {
            if (!_states.Remove(key, out var state) || !state.IsActive)
            {
                return new RecoveryRegistration(false, 0);
            }

            return new RecoveryRegistration(true, state.FailureCount);
        }
    }

    private static string Normalize(string serviceUrl)
    {
        return string.IsNullOrWhiteSpace(serviceUrl)
            ? string.Empty
            : serviceUrl.Trim().TrimEnd('/');
    }

    private sealed class OutageState
    {
        public bool IsActive { get; set; }
        public int FailureCount { get; set; }
    }

    public readonly record struct FailureRegistration(bool ShouldLog, int FailureCount);
    public readonly record struct RecoveryRegistration(bool Recovered, int FailureCount);
}
