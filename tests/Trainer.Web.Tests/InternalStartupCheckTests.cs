using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Trainer.Web.Internal;

namespace Trainer.Web.Tests;

public sealed class InternalStartupCheckTests
{
    private const string ValidToken = "0123456789abcdef0123456789abcdef";

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Trainer.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Production", 8081, null)]
    [InlineData("Production", 0, ValidToken)]
    [InlineData("Testing", 8081, "short")]
    [InlineData("Testing", 8081, "                                ")]
    [InlineData("Testing", 8081, " 0123456789abcdef0123456789abcdef")]
    [InlineData("Testing", 8081, "0123456789abcdef0123456789abcdef\n")]
    [InlineData("Testing", 0, " 0123456789abcdef0123456789abcdef")]
    [InlineData("Production", 8081, "                                ")]
    public void StartupCheck_Throws(string env, int port, string? token) =>
        Assert.Throws<InvalidOperationException>(() => InternalStartupCheck.EnsureValid(new InternalOptions { Port = port }, token, new Environment(env)));

    [Theory]
    [InlineData("Production", 8081, ValidToken)]
    [InlineData("Development", 0, null)]
    [InlineData("Testing", 0, null)]
    [InlineData("Testing", 8081, ValidToken)]
    public void StartupCheck_AcceptsValidConfigurations(string env, int port, string? token) =>
        InternalStartupCheck.EnsureValid(new InternalOptions { Port = port }, token, new Environment(env));

    [Fact]
    public void StartupCheck_TokenOfExactlyMinimumLength_Passes() =>
        InternalStartupCheck.EnsureValid(new InternalOptions { Port = 8081 }, new string('x', InternalStartupCheck.MinimumTokenLength), new Environment("Testing"));

    [Fact]
    public void StartupCheck_TokenOneShorterThanMinimum_Throws() =>
        Assert.Throws<InvalidOperationException>(() => InternalStartupCheck.EnsureValid(
            new InternalOptions { Port = 8081 }, new string('x', InternalStartupCheck.MinimumTokenLength - 1), new Environment("Testing")));
}
