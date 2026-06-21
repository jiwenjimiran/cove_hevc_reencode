using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleExtension;

public sealed class ExampleExtension : IExtension
{
    public string Id => "com.example.extension";
    public string Name => "Example Extension";
    public string Version => "0.1.0";
    public string? Description => "A minimal Cove extension template.";
    public string? Author => "Example Author";
    public string? Url => "https://github.com/example/com.example.extension";
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Tools];
    public string? MinCoveVersion => "1.0.0";

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }
}