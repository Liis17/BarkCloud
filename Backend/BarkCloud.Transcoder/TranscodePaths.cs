namespace BarkCloud.Transcoder;

public sealed class TranscodePaths
{
    private readonly string _rootPath;
    private readonly StringComparison _pathComparison;

    public TranscodePaths(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        Directory.CreateDirectory(rootPath);
        var fullRootPath = Path.GetFullPath(rootPath);
        var rootInfo = new DirectoryInfo(fullRootPath);
        _rootPath = rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName is { } targetPath
            ? Path.GetFullPath(targetPath)
            : fullRootPath;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string ResolveInput(string relativePath)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Входной файл не найден.");
        }

        return path;
    }

    public string ResolveOutput(string relativePath)
    {
        var path = Resolve(relativePath);
        if (Directory.Exists(path))
        {
            throw new ArgumentException("Путь выходного файла указывает на каталог.", nameof(relativePath));
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Не удалось определить каталог выходного файла.", nameof(relativePath));
        Directory.CreateDirectory(directory);
        return Resolve(relativePath);
    }

    private string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.StartsWith('\\')
            || (relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':')
            || relativePath.Contains('\0'))
        {
            throw new ArgumentException("Путь должен быть относительным.", nameof(relativePath));
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new ArgumentException("Путь содержит недопустимый сегмент.", nameof(relativePath));
        }

        var currentPath = _rootPath;
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            var fileInfo = new FileInfo(currentPath);
            var directoryInfo = new DirectoryInfo(currentPath);
            FileSystemInfo? linkInfo = fileInfo.LinkTarget is not null
                ? fileInfo
                : directoryInfo.LinkTarget is not null
                    ? directoryInfo
                    : null;

            if (linkInfo is not null)
            {
                var linkTarget = linkInfo.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new ArgumentException("Не удалось разрешить символическую ссылку.", nameof(relativePath));
                currentPath = Path.GetFullPath(linkTarget.FullName);
                EnsureInsideRoot(currentPath, relativePath);
                continue;
            }

            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                continue;
            }

            var attributes = File.GetAttributes(currentPath);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo fileSystemInfo = (attributes & FileAttributes.Directory) != 0 ? directoryInfo : fileInfo;
            var target = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new ArgumentException("Не удалось разрешить символическую ссылку.", nameof(relativePath));
            currentPath = Path.GetFullPath(target.FullName);
            EnsureInsideRoot(currentPath, relativePath);
        }

        var fullPath = Path.GetFullPath(currentPath);
        EnsureInsideRoot(fullPath, relativePath);
        return fullPath;
    }

    private void EnsureInsideRoot(string path, string relativePath)
    {
        var relativeToRoot = Path.GetRelativePath(_rootPath, path);
        if (relativeToRoot == ".."
            || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", _pathComparison)
            || Path.IsPathRooted(relativeToRoot))
        {
            throw new ArgumentException("Путь выходит за пределы рабочей папки.", nameof(relativePath));
        }
    }
}
