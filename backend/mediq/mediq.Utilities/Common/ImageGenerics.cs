namespace mediq.Utilities.Common;

public static class ImageGenerics
{
    public static async Task<string> UploadAsync(
        IFormFile file,
        string rootDir,
        string subDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        ArgumentException.ThrowIfNullOrEmpty(subDir);

        if (file.Length <= 0)
            throw new ArgumentException("Uploaded file is empty.", nameof(file));

        var uploadPath = Path.Combine(rootDir, subDir);
        Directory.CreateDirectory(uploadPath);

        var extension = Path.GetExtension(file.FileName);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(uploadPath, fileName);

        await using var stream = File.Create(filePath);
        await file.CopyToAsync(stream, cancellationToken);
        return filePath;
    }

    public static async Task<string?> GetImageAsBase64Async(
        string rootDir,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var imagePath = Path.Combine(rootDir, relativePath);
        if (!File.Exists(imagePath))
            return null;

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        return Convert.ToBase64String(imageBytes);
    }
}
