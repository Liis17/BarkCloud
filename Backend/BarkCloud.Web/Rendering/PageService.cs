using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Rendering;

/// <summary>Читает файлы страниц из каталога Pages и прогоняет их через шаблонизатор.</summary>
public sealed class PageService
{
    private readonly string _root;
    private readonly TemplateRenderer _renderer;

    public PageService(IWebHostEnvironment env, TemplateRenderer renderer)
    {
        _root = Path.Combine(env.ContentRootPath, "Pages");
        _renderer = renderer;
    }

    public async Task<string> RenderAsync(string fileName, IReadOnlyDictionary<string, string?> vars)
    {
        var content = await File.ReadAllTextAsync(Path.Combine(_root, fileName));
        return _renderer.Render(content, vars);
    }

    public Task<string> ReadRawAsync(string fileName)
        => File.ReadAllTextAsync(Path.Combine(_root, fileName));
}
