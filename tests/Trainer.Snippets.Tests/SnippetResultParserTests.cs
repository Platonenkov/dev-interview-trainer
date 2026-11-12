namespace Trainer.Snippets.Tests;

public class SnippetResultParserTests
{
    private static (string, string?) Kv(SnippetResult r) => (r.Kind, r.Value);

    [Fact]
    public void CompilerError_IsCompileError() =>
        Assert.Equal("compile-error", SnippetResultParser.ParseBuild(1, "Program.cs(1,1): error CS0103: The name does not exist").Kind);

    [Fact]
    public void CompilerError_CarriesFirstErrorCode() =>
        Assert.Equal("CS0103", SnippetResultParser.ParseBuild(1, "Program.cs(1,1): error CS0103: x\nProgram.cs(2,1): error CS1002: y").Value);

    [Fact]
    public void RestoreFailure_IsSandboxError() =>
        Assert.Equal("sandbox-error", SnippetResultParser.ParseBuild(1, "error NU1301: Unable to load the service index").Kind);

    [Fact]
    public void UnhandledException_YieldsTypeName() =>
        Assert.Equal(
            ("exception", "System.InvalidOperationException"),
            Kv(SnippetResultParser.ParseRun(134, "", "Unhandled exception. System.InvalidOperationException: boom\n   at ...", false, false)));

    [Fact]
    public void OomKill_IsSandboxError() =>
        Assert.Equal("sandbox-error", SnippetResultParser.ParseRun(137, "", "", false, false).Kind);

    [Fact]
    public void Deadline_IsTimeout() =>
        Assert.Equal("timeout", SnippetResultParser.ParseRun(137, "", "", true, false).Kind);

    [Fact]
    public void Output_IsTrimmedAndLf() =>
        Assert.Equal("1\n2", SnippetResultParser.ParseRun(0, "1\r\n2\r\n", "", false, false).Value);

    [Fact]
    public void Truncated_IsSandboxError_EvenWithExceptionText() =>
        Assert.Equal("sandbox-error", SnippetResultParser.ParseRun(134, "x", "Unhandled exception. System.Exception: x", false, true).Kind);

    [Fact]
    public void NonZeroExitWithoutException_IsSandboxError() =>
        Assert.Equal("sandbox-error", SnippetResultParser.ParseRun(1, "partial", "", false, false).Kind);

    [Fact]
    public void Deadline_WinsOverTruncation() =>
        Assert.Equal("timeout", SnippetResultParser.ParseRun(137, "", "", true, true).Kind);
}
