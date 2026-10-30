using System.Text.Json;

namespace ZoneLab;

internal sealed class DatasetManifest
{
    public int Version { get; set; } = DatasetManifestStore.CurrentVersion;
    public List<DatasetCaptureEntry> Captures { get; set; } = new();
}

internal sealed class DatasetCaptureEntry
{
    public string Id { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Status { get; set; } = "todo";
    public List<DatasetManifestPin> Pins { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

internal sealed class DatasetManifestPin
{
    public int X { get; set; }
    public int Y { get; set; }
    public string? Zone { get; set; }
}

internal sealed record ManifestValidationResult(
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> OrphanImages);

internal sealed class DatasetManifestStore
{
    public const int CurrentVersion = 1;
    private const int BackupLimit = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly string _manifestPath;

    public DatasetManifest Manifest { get; private set; } = new();

    public DatasetManifestStore(string datasetRoot)
    {
        _root = Path.GetFullPath(datasetRoot);
        _manifestPath = Path.Combine(_root, "manifest.json");
    }

    public DatasetManifest LoadOrMigrate()
    {
        try
        {
            Directory.CreateDirectory(_root);
            if (File.Exists(_manifestPath))
            {
                Manifest = ReadManifest();
                return Manifest;
            }

            DatasetManifest migrated = MigrateExistingFolders();
            ValidateStructure(migrated);
            WriteManifest(migrated, createBackup: false);
            Manifest = migrated;
            return Manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException)
        {
            Log.ErrorAt(_root, "loading or migrating dataset manifest failed", ex);
            throw;
        }
    }

    public DatasetCaptureEntry? FindCapture(string imagePath)
    {
        string relativePath = GetRelativePath(imagePath);
        return Manifest.Captures.FirstOrDefault(entry =>
            string.Equals(entry.File, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    public string GetStatus(string imagePath) =>
        FindCapture(imagePath)?.Status
        ?? throw new InvalidDataException($"Capture is not present in manifest.json: {imagePath}");

    public string MoveToDone(string imagePath, string doneDirectory)
    {
        string destinationPath = CaptureFileOperations.MoveToDone(imagePath, doneDirectory);
        try
        {
            SetStatus(imagePath, "done", destinationPath);
            return destinationPath;
        }
        catch (Exception manifestException) when (manifestException is IOException
            or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            try
            {
                string destinationState = Path.ChangeExtension(destinationPath, ".json");
                string sourceState = Path.ChangeExtension(imagePath, ".json");
                if (File.Exists(destinationState))
                    File.Move(destinationState, sourceState);
                File.Move(destinationPath, imagePath);
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                Log.ErrorAt(_root, $"capture move rollback failed after manifest update error: {destinationPath}", rollbackException);
                throw new IOException(
                    $"Manifest update failed and the capture could not be restored to {imagePath}.",
                    new AggregateException(manifestException, rollbackException));
            }

            throw;
        }
    }

    public void AddCapture(string imagePath, string status, IEnumerable<DatasetManifestPin>? pins = null)
    {
        string relativePath = GetRelativePath(imagePath);
        if (Manifest.Captures.Any(entry =>
                string.Equals(entry.File, relativePath, StringComparison.OrdinalIgnoreCase)))
            return;

        DateTime now = DateTime.UtcNow;
        var entry = new DatasetCaptureEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            File = relativePath,
            Status = status,
            Pins = pins?.Select(ClonePin).ToList() ?? new List<DatasetManifestPin>(),
            CreatedUtc = File.Exists(imagePath) ? File.GetCreationTimeUtc(imagePath) : now,
            UpdatedUtc = now,
        };
        UpdateManifest(candidate => candidate.Captures.Add(entry));
    }

    public void UpdatePins(string imagePath, IEnumerable<DatasetManifestPin> pins)
    {
        string relativePath = GetRelativePath(imagePath);
        UpdateManifest(candidate =>
        {
            DatasetCaptureEntry entry = FindCapture(candidate, relativePath)
                ?? throw new InvalidDataException($"Capture is not registered in manifest: {relativePath}");
            entry.Pins = pins.Select(ClonePin).ToList();
            entry.UpdatedUtc = DateTime.UtcNow;
        });
    }

    public void SetStatus(string imagePath, string status, string? newImagePath = null)
    {
        ValidateStatus(status);
        string currentRelativePath = GetRelativePath(imagePath);
        string newRelativePath = newImagePath is null ? currentRelativePath : GetRelativePath(newImagePath);
        UpdateManifest(candidate =>
        {
            DatasetCaptureEntry entry = FindCapture(candidate, currentRelativePath)
                ?? throw new InvalidDataException($"Capture is not registered in manifest: {currentRelativePath}");
            if (!string.Equals(currentRelativePath, newRelativePath, StringComparison.OrdinalIgnoreCase)
                && candidate.Captures.Any(other =>
                    string.Equals(other.File, newRelativePath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"A capture already uses manifest path: {newRelativePath}");
            entry.File = newRelativePath;
            entry.Status = status;
            entry.UpdatedUtc = DateTime.UtcNow;
        });
    }

    public void RemoveCapture(string imagePath)
    {
        string relativePath = GetRelativePath(imagePath);
        if (FindCapture(Manifest, relativePath) is null) return;
        UpdateManifest(candidate => candidate.Captures.RemoveAll(entry =>
            string.Equals(entry.File, relativePath, StringComparison.OrdinalIgnoreCase)));
    }

    public string ResolveFile(string relativePath)
    {
        ValidateRelativePath(relativePath);
        string fullPath = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest file path escapes the dataset root: {relativePath}");
        return fullPath;
    }

    public ManifestValidationResult ValidateAgainstFiles()
    {
        ValidateStructure(Manifest);
        var entries = Manifest.Captures
            .Select(entry => entry.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string dataDirectory = Path.Combine(_root, "data");
        var diskImages = Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Where(CaptureFileOperations.IsSupportedImageFile)
                .Where(path => !IsWithinDirectory(path, dataDirectory))
                .Select(GetRelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] missing = entries.Where(file => !File.Exists(ResolveFile(file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] orphans = diskImages.Where(file => !entries.Contains(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ManifestValidationResult(missing, orphans);
    }

    public int PurgeDone(int retentionDays)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        DatasetCaptureEntry[] expired = Manifest.Captures.Where(entry =>
            entry.Status == "done"
            && File.Exists(ResolveFile(entry.File))
            && File.GetLastWriteTimeUtc(ResolveFile(entry.File)) < cutoff).ToArray();
        if (expired.Length == 0) return 0;

        var paths = expired.Select(entry => ResolveFile(entry.File)).ToArray();
        UpdateManifest(candidate =>
        {
            var expiredPaths = expired.Select(entry => entry.File)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            candidate.Captures.RemoveAll(entry => expiredPaths.Contains(entry.File));
        });

        int removed = 0;
        foreach (string imagePath in paths)
        {
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
                removed++;
            }
            string sidecar = Path.ChangeExtension(imagePath, ".json");
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
                removed++;
            }
        }
        return removed;
    }

    private DatasetManifest MigrateExistingFolders()
    {
        var manifest = new DatasetManifest();
        foreach ((string folderName, string status) in new[]
                 {
                     ("todo", "todo"),
                     ("done", "done"),
                     ("gallery", "todo"),
                 })
        {
            string folder = Path.Combine(_root, folderName);
            if (!Directory.Exists(folder)) continue;
            foreach (string imagePath in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                         .Where(CaptureFileOperations.IsSupportedImageFile)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string relativePath = GetRelativePath(imagePath);
                if (manifest.Captures.Any(entry =>
                        string.Equals(entry.File, relativePath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                GalleryCapture sidecar = ReadLegacySidecar(imagePath);
                DateTime created = File.GetCreationTimeUtc(imagePath);
                DateTime updated = File.GetLastWriteTimeUtc(imagePath);
                manifest.Captures.Add(new DatasetCaptureEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    File = relativePath,
                    Status = status,
                    Pins = sidecar.Pins.Select(pin => new DatasetManifestPin
                    {
                        X = pin.X,
                        Y = pin.Y,
                        Zone = pin.Zone,
                    }).ToList(),
                    CreatedUtc = created,
                    UpdatedUtc = updated,
                });
            }
        }
        return manifest;
    }

    private static GalleryCapture ReadLegacySidecar(string imagePath)
    {
        string sidecarPath = Path.ChangeExtension(imagePath, ".json");
        if (!File.Exists(sidecarPath)) return new GalleryCapture();
        return JsonSerializer.Deserialize<GalleryCapture>(
                   File.ReadAllText(sidecarPath),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException($"Capture sidecar is empty or invalid: {sidecarPath}");
    }

    private DatasetManifest ReadManifest()
    {
        DatasetManifest manifest = JsonSerializer.Deserialize<DatasetManifest>(
                File.ReadAllText(_manifestPath), JsonOptions)
            ?? throw new InvalidDataException("manifest.json is empty or invalid.");
        ValidateStructure(manifest);
        return manifest;
    }

    private void UpdateManifest(Action<DatasetManifest> update)
    {
        try
        {
            DatasetManifest candidate = CloneManifest(Manifest);
            update(candidate);
            ValidateStructure(candidate);
            WriteManifest(candidate, createBackup: true);
            Manifest = candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException)
        {
            Log.ErrorAt(_root, "updating dataset manifest failed", ex);
            throw;
        }
    }

    private void WriteManifest(DatasetManifest manifest, bool createBackup)
    {
        string temporaryPath = Path.Combine(_root, $".manifest.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(_manifestPath))
            {
                File.Move(temporaryPath, _manifestPath);
                return;
            }

            if (createBackup)
                CreateBackup();
            File.Replace(temporaryPath, _manifestPath, null, ignoreMetadataErrors: true);
            PruneBackups();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            Log.ErrorAt(_root, $"writing dataset manifest failed: {_manifestPath}", ex);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.ErrorAt(_root, $"removing temporary manifest failed: {temporaryPath}", ex);
            }
        }
    }

    private void CreateBackup()
    {
        string backupPath = Path.Combine(_root,
            $"manifest.json.bak.{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}");
        File.Copy(_manifestPath, backupPath, overwrite: false);
    }

    private void PruneBackups()
    {
        string[] backups = Directory.GetFiles(_root, "manifest.json.bak.*")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string backup in backups.Skip(BackupLimit))
        {
            try
            {
                File.Delete(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.ErrorAt(_root, $"pruning manifest backup failed: {backup}", ex);
            }
        }
    }

    private void ValidateStructure(DatasetManifest manifest)
    {
        if (manifest.Version != CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported manifest version {manifest.Version}; this application supports version {CurrentVersion}.");
        if (manifest.Captures is null)
            throw new InvalidDataException("manifest.json must contain a captures array.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DatasetCaptureEntry entry in manifest.Captures)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
                throw new InvalidDataException("Manifest capture IDs must be unique and non-empty.");
            ValidateRelativePath(entry.File);
            if (!files.Add(entry.File))
                throw new InvalidDataException($"Duplicate manifest file path: {entry.File}");
            ValidateStatus(entry.Status);
            if (entry.Pins is null)
                throw new InvalidDataException($"Manifest pins must be an array: {entry.File}");
        }
    }

    private static void ValidateStatus(string status)
    {
        if (status is not "todo" and not "done" and not "skipped")
            throw new InvalidDataException($"Unsupported manifest status: {status}");
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':')
            || relativePath.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Manifest file path must be relative to dataset: {relativePath}");
    }

    private string GetRelativePath(string imagePath)
    {
        string fullPath = Path.GetFullPath(imagePath);
        string rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Capture path is outside the dataset root: {imagePath}");
        return Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
    }

    private static DatasetCaptureEntry? FindCapture(DatasetManifest manifest, string relativePath) =>
        manifest.Captures.FirstOrDefault(entry =>
            string.Equals(entry.File, relativePath, StringComparison.OrdinalIgnoreCase));

    private bool IsWithinDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static DatasetManifest CloneManifest(DatasetManifest source) => new()
    {
        Version = source.Version,
        Captures = source.Captures.Select(entry => new DatasetCaptureEntry
        {
            Id = entry.Id,
            File = entry.File,
            Status = entry.Status,
            Pins = entry.Pins.Select(ClonePin).ToList(),
            CreatedUtc = entry.CreatedUtc,
            UpdatedUtc = entry.UpdatedUtc,
        }).ToList(),
    };

    private static DatasetManifestPin ClonePin(DatasetManifestPin pin) => new()
    {
        X = pin.X,
        Y = pin.Y,
        Zone = pin.Zone,
    };
}
