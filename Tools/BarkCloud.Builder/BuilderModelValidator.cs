using System.IO;
using System.Text.RegularExpressions;

namespace BarkCloud.Builder;

public static partial class BuilderModelValidator
{
    private static readonly Regex DockerNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);
    private static readonly Regex NginxTokenPattern = new("^[A-Za-z0-9*._-]+$", RegexOptions.Compiled);
    private static readonly Regex PortPattern = new("^[0-9]+$", RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(BuilderModel model)
    {
        var errors = new List<string>();

        ValidateDockerName(model.DockerNetworkName, "Имя Docker-сети", errors);

        var allNames = model.ContainerNames;
        foreach (var container in allNames.Where(x => x.Enabled))
            ValidateDockerName(container.Name, $"Имя контейнера {container.ServiceAlias}", errors);

        foreach (var group in allNames.Where(x => x.Enabled)
                     .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
                errors.Add($"Имя контейнера «{group.Key}» используется несколькими сервисами.");
        }

        foreach (var container in allNames.Where(x => x.Enabled))
        {
            var conflictingAlias = allNames.FirstOrDefault(x =>
                !string.Equals(x.ServiceAlias, container.ServiceAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ServiceAlias, container.Name, StringComparison.OrdinalIgnoreCase));
            if (conflictingAlias is not null)
                errors.Add($"Имя контейнера {container.ServiceAlias} «{container.Name}» зарезервировано именем сервиса {conflictingAlias.ServiceAlias}.");
        }

        if (!model.UseNginx)
            return errors;

        if (model.NginxLocation is not ("Compose" or "External"))
            errors.Add("Выберите размещение nginx: в compose или внешний nginx.");

        if (string.IsNullOrWhiteSpace(model.NginxDomain)
            || !NginxTokenPattern.IsMatch(model.NginxDomain.Trim()))
            errors.Add("Укажите корректный домен nginx без пробелов и управляющих символов.");

        if (model.NginxLocation == "External")
        {
            ValidateExternalCertificatePath(model.ExternalNginxCertificatePath, "Путь к сертификату внешнего nginx", errors);
            ValidateExternalCertificatePath(model.ExternalNginxKeyPath, "Путь к ключу внешнего nginx", errors);
        }
        else if (model.NginxLocation == "Compose")
        {
            ValidateLocalCertificateName(model.CertCrtPath, "Файл сертификата", errors);
            ValidateLocalCertificateName(model.CertKeyPath, "Файл ключа", errors);
        }

        var ports = new List<(string Label, string Value)>
        {
            ("HTTPS nginx", "443"),
            ("Identity", model.IdentityPort),
            ("Users", model.UsersPort),
            ("Files", model.FilesPort),
        };

        ValidateInternalPort(model.FilesHttp1Port, "Files HTTP/1", errors);

        if (model.IncludeTorrent)
        {
            ports.Add(("Torrent", model.TorrentPort));
            ValidateInternalPort(model.TorrentHttp1Port, "Torrent HTTP/1", errors);
        }
        if (model.NginxSeqProxy && model.IncludeSeq)
            ports.Add(("Seq", model.NginxSeqPort));
        if (model.NginxMinioConsoleProxy && model.IncludeMinio)
            ports.Add(("MinIO Console", model.NginxMinioConsolePort));

        var parsedPorts = new List<(string Label, int Port)>();
        foreach (var (label, value) in ports)
        {
            if (!TryParsePort(value, out var port))
                errors.Add($"Порт {label} должен быть числом от 1 до 65535.");
            else
                parsedPorts.Add((label, port));
        }

        foreach (var group in parsedPorts.GroupBy(x => x.Port))
        {
            if (group.Count() > 1)
                errors.Add($"Порт {group.Key} указан для нескольких nginx-сервисов: {string.Join(", ", group.Select(x => x.Label))}.");
        }

        return errors;
    }

    private static void ValidateDockerName(string? value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !DockerNamePattern.IsMatch(value))
            errors.Add($"{label} должно начинаться с буквы или цифры и содержать только буквы, цифры, точку, дефис или подчёркивание.");
    }

    private static void ValidateExternalCertificatePath(string? value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.Any(char.IsWhiteSpace)
            || value.IndexOfAny([';', '{', '}', '$', '#', '\\', '"', '\'']) >= 0)
            errors.Add($"{label} должен быть абсолютным Linux-путём внутри nginx-контейнера.");
    }

    private static void ValidateInternalPort(string? value, string label, ICollection<string> errors)
    {
        if (!TryParsePort(value, out _))
            errors.Add($"Внутренний порт {label} должен быть числом от 1 до 65535.");
    }

    private static bool TryParsePort(string? value, out int port)
    {
        port = 0;
        return PortPattern.IsMatch(value ?? "")
            && int.TryParse(value, out port)
            && port is >= 1 and <= 65535;
    }

    private static void ValidateLocalCertificateName(string? path, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || !NginxTokenPattern.IsMatch(name))
            errors.Add($"Имя файла «{label}» содержит недопустимые символы для nginx-конфига.");
    }
}
