namespace AnswerCode.Services;

/// <summary>
/// Background service that periodically scans the source-code upload directory
/// and deletes folders that have not been accessed within the configured TTL.
/// This catches orphaned uploads left behind when users close their browser
/// without clicking REMOVE.
/// </summary>
public class UploadCleanupService : BackgroundService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<UploadCleanupService> _logger;
    private readonly TimeSpan _scanInterval;
    private readonly TimeSpan _maxAge;

    public UploadCleanupService(IWebHostEnvironment env,
                                IConfiguration configuration,
                                ILogger<UploadCleanupService> logger)
    {
        _env = env;
        _logger = logger;

        // Configurable via appsettings.json  "UploadCleanup": { "ScanIntervalMinutes": 10, "MaxAgeMinutes": 120 }
        var section = configuration.GetSection("UploadCleanup");
        _scanInterval = TimeSpan.FromMinutes(section.GetValue("ScanIntervalMinutes", 10));
        _maxAge = TimeSpan.FromMinutes(section.GetValue("MaxAgeMinutes", 120));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UploadCleanupService started. Scan every {Interval} min, TTL = {MaxAge} min", _scanInterval.TotalMinutes, _maxAge.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanupExpiredFolders();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during upload cleanup scan");
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }
    }

    private void CleanupExpiredFolders()
    {
        string root = Path.Combine(_env.WebRootPath, "source-code");
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _maxAge;
        var directories = Directory.GetDirectories(root);

        foreach (var dir in directories)
        {
            try
            {
                var info = new DirectoryInfo(dir);

                // Use the most recent write time of any file inside the folder,
                // so folders being actively used won't be deleted.
                var lastActivity = GetLastActivityUtc(info);

                if (lastActivity < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("Cleanup: deleted expired upload folder {Folder} (last activity: {LastActivity:u})", info.Name, lastActivity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup: failed to delete folder {Folder}", dir);
            }
        }
    }

    /// <summary>
    /// Returns the most recent LastWriteTimeUtc among all files in the directory,
    /// or the directory's own CreationTimeUtc if it is empty.
    /// </summary>
    private static DateTime GetLastActivityUtc(DirectoryInfo dir)
    {
        var files = dir.GetFiles("*", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            return dir.CreationTimeUtc;
        }

        return files.Max(f => f.LastWriteTimeUtc);
    }
}
