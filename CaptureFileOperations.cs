using System.Text.Json;
using System.Globalization;

namespace ZoneLab;

internal static class CaptureFileOperations
{
    public static bool IsSupportedImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    public static DateTime GetCaptureTimeUtc(string imagePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(imagePath);
        const string timestampFormat = "yyyyMMdd_HHmmss_fff";
        if (fileName.Length >= timestampFormat.Length
            && DateTime.TryParseExact(fileName[..timestampFormat.Length], timestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime capturedAt))
            return capturedAt.ToUniversalTime();

        return File.GetLastWriteTimeUtc(imagePath);
    }

    public static bool IsReadyForCompletion(
        string imagePath,
        DatasetManifestStore manifestStore,
        out string reason)
    {
        DatasetCaptureEntry? manifestEntry = manifestStore.FindCapture(imagePath);
        if (manifestEntry is null)
            throw new InvalidDataException("This capture is not present in manifest.json.");

        string statePath = Path.ChangeExtension(imagePath, ".json");
        if (!File.Exists(statePath))
        {
            reason = "This capture has no saved pin data. Open it and add labeled pins before marking it done.";
            return false;
        }

        GalleryCapture capture = JsonSerializer.Deserialize<GalleryCapture>(
            File.ReadAllText(statePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The capture pin data is empty or invalid.");

        if (manifestEntry.Pins.Count == 0)
        {
            reason = "Add at least one labeled pin before marking this capture done.";
            return false;
        }

        var exportedPins = capture.Pins
            .Where(pin => pin.Exported)
            .Select(pin => (pin.X, pin.Y, pin.Zone))
            .ToHashSet();
        if (manifestEntry.Pins.Any(pin => string.IsNullOrWhiteSpace(pin.Zone)
                || !exportedPins.Contains((pin.X, pin.Y, pin.Zone))))
        {
            reason = "Finish labeling and save every pin before marking this capture done.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static string MoveToDone(string imagePath, string doneDirectory)
    {
        string sourceImage = Path.GetFullPath(imagePath);
        if (!File.Exists(sourceImage))
            throw new FileNotFoundException("The capture image no longer exists.", sourceImage);

        Directory.CreateDirectory(doneDirectory);
        string sourceState = Path.ChangeExtension(sourceImage, ".json");
        string fileStem = Path.GetFileNameWithoutExtension(sourceImage);
        string extension = Path.GetExtension(sourceImage);
        string destinationImage = Path.Combine(doneDirectory, Path.GetFileName(sourceImage));
        string destinationState = Path.ChangeExtension(destinationImage, ".json");
        int suffix = 1;
        while (File.Exists(destinationImage) || File.Exists(destinationState))
        {
            string candidateName = $"{fileStem}_{suffix++}{extension}";
            destinationImage = Path.Combine(doneDirectory, candidateName);
            destinationState = Path.ChangeExtension(destinationImage, ".json");
        }

        File.Move(sourceImage, destinationImage);
        try
        {
            if (File.Exists(sourceState))
                File.Move(sourceState, destinationState);
        }
        catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(destinationImage, sourceImage);
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                Log.Error($"capture move rollback failed: {destinationImage}", rollbackException);
                throw new IOException(
                    $"The pin data could not be moved, and the image could not be restored. The image remains at {destinationImage}.",
                    new AggregateException(moveException, rollbackException));
            }

            throw;
        }

        return destinationImage;
    }
}
