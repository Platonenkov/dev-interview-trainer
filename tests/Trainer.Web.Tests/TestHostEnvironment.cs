using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Trainer.Web.Tests;

internal sealed class TestHostEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "Trainer.Web";

    public string ContentRootPath { get; set; } = ".";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
