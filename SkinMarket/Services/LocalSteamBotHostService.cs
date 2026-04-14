using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;

namespace SkinMarket.Services;

public sealed class LocalSteamBotHostService : BackgroundService, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(5);

    private readonly IHostEnvironment _hostEnvironment;
    private readonly IOptions<SteamBotOptions> _options;
    private readonly BotServiceAvailabilityTracker _availabilityTracker;
    private readonly ILogger<LocalSteamBotHostService> _logger;
    private readonly IAppLogService _appLogService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _stopping;
    private bool _missingScriptLogged;
    private bool _missingDependenciesLogged;
    private DateTime _lastStartAttemptUtc = DateTime.MinValue;

    public LocalSteamBotHostService(
        IHostEnvironment hostEnvironment,
        IOptions<SteamBotOptions> options,
        BotServiceAvailabilityTracker availabilityTracker,
        IAppLogService appLogService,
        ILogger<LocalSteamBotHostService> logger)
    {
        _hostEnvironment = hostEnvironment;
        _options = options;
        _availabilityTracker = availabilityTracker;
        _appLogService = appLogService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!ShouldStartLocalService(options, out var skipReason))
        {
            _logger.LogDebug("Skipping local Steam bot watchdog. Reason: {Reason}", skipReason);
            return;
        }

        _logger.LogInformation("Local Steam bot watchdog started. ServiceUrl={ServiceUrl}", options.ServiceUrl);

        using var timer = new PeriodicTimer(MonitorInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureBotRunningAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Local Steam bot watchdog iteration failed.");
                await _appLogService.WriteAsync(
                    "Error",
                    "Local Steam bot watchdog iteration failed.",
                    nameof(LocalSteamBotHostService),
                    exception,
                    stoppingToken);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Local Steam bot watchdog stopped.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        await base.StopAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopManagedProcessCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        DisposeProcess();
        _gate.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task EnsureBotRunningAsync(SteamBotOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var health = await GetHealthAsync(options.ServiceUrl, cancellationToken);
            if (health.Reachable)
            {
                _availabilityTracker.RegisterSuccess(options.ServiceUrl);
                if (_process is not null && _process.HasExited)
                {
                    DisposeProcess();
                }

                return;
            }

            if (_process is not null && _process.HasExited)
            {
                DisposeProcess();
            }

            var botDirectory = Path.Combine(_hostEnvironment.ContentRootPath, "bot-service");
            var entryScriptPath = Path.Combine(botDirectory, "src", "server.js");
            if (!File.Exists(entryScriptPath))
            {
                if (!_missingScriptLogged)
                {
                    _missingScriptLogged = true;
                    _logger.LogWarning(
                        "Cannot autostart local Steam bot because the entry script was not found at {EntryScriptPath}.",
                        entryScriptPath);
                    await _appLogService.WriteAsync(
                        "Warning",
                        $"Local Steam bot entry script was not found: {entryScriptPath}",
                        nameof(LocalSteamBotHostService),
                        cancellationToken: cancellationToken);
                }

                return;
            }

            _missingScriptLogged = false;

            var nodeModulesPath = Path.Combine(botDirectory, "node_modules");
            if (!Directory.Exists(nodeModulesPath))
            {
                if (!_missingDependenciesLogged)
                {
                    _missingDependenciesLogged = true;
                    _logger.LogWarning(
                        "Local Steam bot dependencies are missing at {NodeModulesPath}. Run `npm ci` inside bot-service if startup fails.",
                        nodeModulesPath);
                }
            }
            else
            {
                _missingDependenciesLogged = false;
            }

            if (_process is not null && !_process.HasExited)
            {
                if (DateTime.UtcNow - _lastStartAttemptUtc < StartupTimeout)
                {
                    return;
                }

                _logger.LogWarning(
                    "Local Steam bot process is running but service is unreachable. Restarting. Pid={Pid} LastError={LastError}",
                    _process.Id,
                    health.LastError ?? "<none>");
                await _appLogService.WriteAsync(
                    "Warning",
                    $"Local Steam bot process was running but unreachable. Restarting. Pid={_process.Id}; ServiceUrl={options.ServiceUrl}; LastError={health.LastError ?? "<none>"}",
                    nameof(LocalSteamBotHostService),
                    cancellationToken: cancellationToken);

                await StopManagedProcessCoreAsync(cancellationToken);
            }

            if (DateTime.UtcNow - _lastStartAttemptUtc < RestartCooldown)
            {
                return;
            }

            await StartManagedProcessAsync(options.ServiceUrl, botDirectory, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartManagedProcessAsync(string serviceUrl, string botDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "src/server.js",
                WorkingDirectory = botDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            _process.OutputDataReceived += HandleOutput;
            _process.ErrorDataReceived += HandleError;
            _process.Exited += HandleExit;

            if (!_process.Start())
            {
                _logger.LogError("Failed to start the local Steam bot process.");
                await _appLogService.WriteAsync(
                    "Error",
                    "Failed to start the local Steam bot process.",
                    nameof(LocalSteamBotHostService),
                    cancellationToken: cancellationToken);
                DisposeProcess();
                return;
            }

            _lastStartAttemptUtc = DateTime.UtcNow;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _logger.LogInformation(
                "Started local Steam bot process. Pid={Pid} ServiceUrl={ServiceUrl}",
                _process.Id,
                serviceUrl);

            var deadline = DateTime.UtcNow.Add(StartupTimeout);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (_process is null || _process.HasExited)
                {
                    DisposeProcess();
                    return;
                }

                var health = await GetHealthAsync(serviceUrl, cancellationToken);
                if (health.Reachable)
                {
                    _availabilityTracker.RegisterSuccess(serviceUrl);
                    _logger.LogInformation(
                        "Local Steam bot service is reachable. Ready={Ready} LastError={LastError}",
                        health.Ready,
                        health.LastError ?? "<none>");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            _logger.LogWarning(
                "Local Steam bot process did not become reachable within {TimeoutSeconds} seconds.",
                StartupTimeout.TotalSeconds);
            await _appLogService.WriteAsync(
                "Warning",
                $"Local Steam bot process did not become reachable within {(int)StartupTimeout.TotalSeconds} seconds.",
                nameof(LocalSteamBotHostService),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(
                exception,
                "Failed to autostart the local Steam bot process. Ensure Node.js is installed and available in PATH.");
            await _appLogService.WriteAsync(
                "Error",
                "Failed to autostart the local Steam bot process. Ensure Node.js is installed and available in PATH.",
                nameof(LocalSteamBotHostService),
                exception,
                cancellationToken);
            DisposeProcess();
        }
    }

    private async Task StopManagedProcessCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _logger.LogInformation("Stopping local Steam bot process. Pid={Pid}", _process.Id);
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop the local Steam bot process cleanly.");
        }
        finally
        {
            DisposeProcess();
        }
    }

    private bool ShouldStartLocalService(SteamBotOptions options, out string reason)
    {
        if (!_hostEnvironment.IsDevelopment())
        {
            reason = "Application is not running in Development.";
            return false;
        }

        if (!options.Enabled)
        {
            reason = "Steam bot integration is disabled.";
            return false;
        }

        if (!options.AutoStartLocalService)
        {
            reason = "AutoStartLocalService is disabled.";
            return false;
        }

        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUri))
        {
            reason = "Steam bot service URL is invalid.";
            return false;
        }

        if (!serviceUri.IsLoopback)
        {
            reason = "Steam bot service URL is not loopback.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void HandleOutput(object? sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            _logger.LogInformation("bot-service: {Message}", eventArgs.Data);
        }
    }

    private void HandleError(object? sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            _logger.LogWarning("bot-service stderr: {Message}", eventArgs.Data);
        }
    }

    private void HandleExit(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process)
        {
            return;
        }

        var exitCode = "<unknown>";
        try
        {
            exitCode = process.ExitCode.ToString();
        }
        catch
        {
        }

        _logger.LogWarning("Local Steam bot process exited. ExitCode={ExitCode}", exitCode);
        if (!_stopping)
        {
            _ = _appLogService.WriteAsync(
                "Warning",
                $"Local Steam bot process exited unexpectedly. ExitCode={exitCode}",
                nameof(LocalSteamBotHostService));
        }
    }

    private static async Task<BotHealthSnapshot> GetHealthAsync(string serviceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var baseUri))
        {
            return new BotHealthSnapshot(false, false, "Service URL is invalid.");
        }

        var healthUri = new Uri(baseUri, "/healthz");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HealthCheckTimeout);

            using var client = new HttpClient();
            using var response = await client.GetAsync(healthUri, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new BotHealthSnapshot(false, false, $"HTTP {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new BotHealthSnapshot(true, false, "Health payload is empty.");
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var ready = false;
            string? lastError = null;
            if (root.TryGetProperty("bot", out var botElement) && botElement.ValueKind == JsonValueKind.Object)
            {
                if (botElement.TryGetProperty("ready", out var readyElement) &&
                    readyElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    ready = readyElement.GetBoolean();
                }

                if (botElement.TryGetProperty("lastError", out var lastErrorElement) &&
                    lastErrorElement.ValueKind == JsonValueKind.String)
                {
                    lastError = lastErrorElement.GetString();
                }
            }

            return new BotHealthSnapshot(true, ready, lastError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BotHealthSnapshot(false, false, "Health check timed out.");
        }
        catch (Exception exception)
        {
            return new BotHealthSnapshot(false, false, exception.Message);
        }
    }

    private void DisposeProcess()
    {
        if (_process is null)
        {
            return;
        }

        _process.OutputDataReceived -= HandleOutput;
        _process.ErrorDataReceived -= HandleError;
        _process.Exited -= HandleExit;
        _process.Dispose();
        _process = null;
    }

    private sealed record BotHealthSnapshot(bool Reachable, bool Ready, string? LastError);
}
