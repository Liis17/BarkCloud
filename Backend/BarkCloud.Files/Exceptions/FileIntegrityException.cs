namespace BarkCloud.Files.Exceptions;

public sealed class FileIntegrityException : Exception
{
    public FileIntegrityException(string message) : base(message) { }
}
