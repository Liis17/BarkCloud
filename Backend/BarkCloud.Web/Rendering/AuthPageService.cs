using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BarkCloud.Web.Rendering;

public sealed class AuthPageService(IWebHostEnvironment environment, IConfiguration configuration)
{
    private const string ShellFile = "Login Page Full.html";
    private const string StateMarker = "<!-- AUTH_PAGE_STATE -->";
    private const string StylesMarker = "<!-- AUTH_STYLES -->";
    private const string EntryMarker = "<!-- AUTH_ENTRY -->";
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> RenderAsync(BarkCloud.Web.Auth.AuthPageState state, CancellationToken cancellationToken = default)
    {
        var shellPath = Path.Combine(environment.ContentRootPath, "Pages", ShellFile);
        var shell = await File.ReadAllTextAsync(shellPath, cancellationToken);

        var devServer = GetDevelopmentServerUrl();
        string styles;
        string entry;
        if (devServer is not null)
        {
            styles = string.Empty;
            entry = DevelopmentEntry(devServer);
        }
        else
        {
            var manifest = await File.ReadAllTextAsync(GetManifestPath(), cancellationToken);
            var assets = ParseAssets(manifest);
            styles = string.Join("\n", assets.Css.Select(path =>
                $"<link rel=\"stylesheet\" href=\"{HtmlEncoder.Default.Encode(path)}\" />"));
            entry = string.Join("\n", assets.Preloads.Select(path =>
                    $"<link rel=\"modulepreload\" href=\"{HtmlEncoder.Default.Encode(path)}\" />"))
                + $"\n<script type=\"module\" src=\"{HtmlEncoder.Default.Encode(assets.Entry)}\"></script>";
        }

        shell = shell.Replace(StylesMarker, styles, StringComparison.Ordinal)
            .Replace(EntryMarker, entry, StringComparison.Ordinal);
        if (!shell.Contains(StateMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("Auth page shell is missing its state marker.");

        // Keep this as the final insertion step. The auth state must never pass through TemplateRenderer.
        var json = JsonSerializer.Serialize(state, StateJsonOptions);
        var stateScript = $"<script type=\"application/json\" id=\"auth-page-state\">{json}</script>";
        return shell.Replace(StateMarker, stateScript, StringComparison.Ordinal);
    }

    private string? GetDevelopmentServerUrl()
    {
        var configured = configuration["Auth:ViteDevServerUrl"];
        if (!environment.IsDevelopment() || string.IsNullOrWhiteSpace(configured))
            return null;

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Auth:ViteDevServerUrl must be a loopback http(s) URL without credentials, query, or fragment.");

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + uri.AbsolutePath.TrimEnd('/');
    }

    private string GetManifestPath()
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        return Path.Combine(webRoot, "asset-manifest.json");
    }

    private static string DevelopmentEntry(string baseUrl)
    {
        var attr = HtmlEncoder.Default.Encode(baseUrl);
        return $$"""
            <script type="module">
              import RefreshRuntime from {{JsonSerializer.Serialize(baseUrl + "/@react-refresh")}};
              RefreshRuntime.injectIntoGlobalHook(window);
              window.$RefreshReg$ = () => {};
              window.$RefreshSig$ = () => type => type;
              window.__vite_plugin_react_preamble_installed__ = true;
            </script>
            <script type="module" src="{{attr}}/@vite/client"></script>
            <script type="module" src="{{attr}}/src/auth/main.tsx"></script>
            """;
    }

    private static ManifestAssets ParseAssets(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var entryKey = root.EnumerateObject()
            .FirstOrDefault(p => p.Name == "src/auth/main.tsx" || p.Value.TryGetProperty("src", out var src) && src.GetString() == "src/auth/main.tsx")
            .Name;
        if (string.IsNullOrEmpty(entryKey) || !root.TryGetProperty(entryKey, out var entry))
            throw new InvalidOperationException("Vite manifest does not contain the auth entry src/auth/main.tsx.");

        var css = new List<string>();
        var preloads = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(entryKey, root, visited, css, preloads, includeEntryJs: false);

        var entryFile = GetRequiredString(entry, "file");
        return new ManifestAssets(ToPublicPath(entryFile), css.Select(ToPublicPath).ToArray(), preloads.Select(ToPublicPath).ToArray());
    }

    private static void Visit(string key, JsonElement root, HashSet<string> visited,
        List<string> css, List<string> preloads, bool includeEntryJs)
    {
        if (!visited.Add(key) || !root.TryGetProperty(key, out var item))
            return;

        if (item.TryGetProperty("imports", out var imports) && imports.ValueKind == JsonValueKind.Array)
        {
            foreach (var imported in imports.EnumerateArray())
            {
                var importedKey = imported.GetString();
                if (!string.IsNullOrEmpty(importedKey))
                    Visit(importedKey, root, visited, css, preloads, includeEntryJs: true);
            }
        }

        if (item.TryGetProperty("css", out var styles) && styles.ValueKind == JsonValueKind.Array)
            foreach (var style in styles.EnumerateArray())
            {
                var file = style.GetString();
                if (!string.IsNullOrEmpty(file)) css.Add(file);
            }

        if (includeEntryJs && item.TryGetProperty("file", out var jsFile))
        {
            var file = jsFile.GetString();
            if (!string.IsNullOrEmpty(file) && file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                preloads.Add(file);
        }
    }

    private static string GetRequiredString(JsonElement value, string property)
        => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw new InvalidOperationException($"Vite auth entry is missing '{property}'.");

    private static string ToPublicPath(string path)
    {
        if (path.StartsWith('/') || path.Split('/').Any(segment => segment == ".."))
            throw new InvalidOperationException("Vite manifest contains an invalid public asset path.");
        return "/" + path;
    }

    private sealed record ManifestAssets(string Entry, IReadOnlyList<string> Css, IReadOnlyList<string> Preloads);
}
