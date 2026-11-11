using System.IO.Compression;
using CourseHub.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record BackupSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>Local hour (0–23) to run the daily backup.</summary>
    public int Hour { get; init; } = 3;
    public int Keep { get; init; } = 7;
    /// <summary>Transcripts take hours to recreate, so they're included by default.</summary>
    public bool IncludeTranscripts { get; init; } = true;
}

public sealed record BackupFile(string Name, long Size, DateTime CreatedAt);

/// <summary>
/// Daily zip of everything that can't be recreated: the database (a consistent copy via VACUUM INTO,
/// safe while CourseHub runs), the sign-in keys and, optionally, generated transcripts and translations.
/// Thumbnails, converted videos and speech models are left out; they're rebuilt or re-downloaded on demand.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<ProgressDb> dbFactory,
    AppSettingsService settings,
    MediaTools tools,
    CourseHubOptions options,
    IWebHostEnvironment env,
    ILogger<BackupService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Folder { get; private set; } = "";
    public string? LastError { get; private set; }
    public bool Running => _gate.CurrentCount == 0;

    private string DbFile()
    {
        using var db = dbFactory.CreateDbContext();
        return Path.GetFullPath(new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(DbFile())!, "backups")).FullName;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                var s = await settings.GetAsync<BackupSettings>();
                var now = DateTime.Now;
                if (s.Enabled && now.Hour == s.Hour && !List().Any(b => b.CreatedAt.ToLocalTime().Date == now.Date))
                    await RunAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled backup check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public List<BackupFile> List() =>
        Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).GetFiles("coursehub-backup-*.zip").OrderByDescending(f => f.CreationTimeUtc)
                .Select(f => new BackupFile(f.Name, f.Length, f.CreationTimeUtc)).ToList()
            : [];

    public string? PathOf(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || !name.StartsWith("coursehub-backup-") || !name.EndsWith(".zip")) return null;
        var p = Path.Combine(Folder, name);
        return File.Exists(p) ? p : null;
    }

    public void Delete(string name)
    {
        if (PathOf(name) is { } p) File.Delete(p);
    }

    /// <summary>Creates a backup now. Returns the file name, or null if one is already running or it failed (see <see cref="LastError"/>).</summary>
    public async Task<string?> RunAsync()
    {
        if (!await _gate.WaitAsync(0)) return null;
        var s = await settings.GetAsync<BackupSettings>();
        var name = $"coursehub-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var target = Path.Combine(Folder, name);
        var dbCopy = Path.Combine(Path.GetTempPath(), $"coursehub-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", dbCopy);

            await using (var zipStream = File.Create(target + ".part"))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dbCopy, "coursehub.db", CompressionLevel.Optimal);
                var keys = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.KeysFolder));
                AddFolder(zip, keys, "keys");
                if (s.IncludeTranscripts) AddFolder(zip, tools.TranscriptDir, "storage/transcripts");
                zip.CreateEntry("README.txt").Open().Write(System.Text.Encoding.UTF8.GetBytes(
                    "CourseHub backup.\nTo restore: stop CourseHub, put coursehub.db and keys/ (and storage/transcripts/) into your /config folder, start it again.\n"));
            }
            File.Move(target + ".part", target, overwrite: true);

            foreach (var old in List().Skip(Math.Max(1, s.Keep))) Delete(old.Name);
            LastError = null;
            logger.LogInformation("Backup written to {File} ({Size} MB)", target, new FileInfo(target).Length / 1024 / 1024);
            return name;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "Backup failed");
            try { File.Delete(target + ".part"); } catch { /* ignore */ }
            return null;
        }
        finally
        {
            try { File.Delete(dbCopy); } catch { /* ignore */ }
            _gate.Release();
        }
    }

    private static void AddFolder(ZipArchive zip, string folder, string prefix)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            zip.CreateEntryFromFile(f, $"{prefix}/{Path.GetRelativePath(folder, f).Replace('\\', '/')}", CompressionLevel.Fastest);
    }
}
