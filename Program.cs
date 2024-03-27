using System.Diagnostics;
using Meziantou.Framework.Win32;

namespace RedisService;

public static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:验证平台兼容性", Justification = "<挂起>")]
    public static void Main(string[] args)
    {
        // Create the Job object and assign it to the current process
        using var job = new JobObject();
        job.SetLimits(new JobObjectLimits
        {
            Flags = JobObjectLimitFlags.DieOnUnhandledException |
                    JobObjectLimitFlags.KillOnJobClose,
        });
        job.AssignProcess(Process.GetCurrentProcess());

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService();
        builder.Services.AddHostedService<RedisService>();

        var host = builder.Build();
        host.Run();
    }
}

public class ProcessExecutionException(string message) : Exception(message);

internal class RedisService(
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<RedisService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var basePath = Path.Combine(AppContext.BaseDirectory);
            var diskSymbol = basePath[..basePath.IndexOf(':')];
            var confPath = basePath.Replace(diskSymbol + ":", "/cygdrive/" + diskSymbol);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(basePath, "redis-server.exe").Replace("\\", "/"),
                Arguments = $"\"{Path.Combine(confPath, "redis.conf").Replace("\\", "/")}\"",
                WorkingDirectory = basePath,
            };

            if (process.Start())
            {
                await process.WaitForExitAsync(stoppingToken);

                if (process.ExitCode == 0)
                {
                    // When completed, the entire app host will stop.
                    hostApplicationLifetime.StopApplication();
                }
                else
                {
                    throw new ProcessExecutionException($"Redis exited abnormally, code: {process.ExitCode}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // When the stopping token is canceled, for example, a call made from services.msc,
            // we shouldn't exit with a non-zero exit code. In other words, this is expected...
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Message}", ex.Message);

            // Terminates this process and returns an exit code to the operating system.
            // This is required to avoid the 'BackgroundServiceExceptionBehavior', which
            // performs one of two scenarios:
            // 1. When set to "Ignore": will do nothing at all, errors cause zombie services.
            // 2. When set to "StopHost": will cleanly stop the host, and log errors.
            //
            // In order for the Windows Service Management system to leverage configured
            // recovery options, we need to terminate the process with a non-zero exit code.
            Environment.Exit(1);
        }
    }
}