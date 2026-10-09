using FluentAssertions;
using mRemoteNG.Core.Tools;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Tools;

public sealed class CommandLineTokenizerTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("-e \"ping host\"", new[] { "-e", "ping host" })]
    [InlineData("'single \"quoted\" \\n'", new[] { "single \"quoted\" \\n" })]
    [InlineData("\"a \\\"b\\\" c\"", new[] { "a \"b\" c" })]
    [InlineData("a\\ b", new[] { "a b" })]
    [InlineData("C:\\path\\file", new[] { "C:\\path\\file" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("pre\"fix suffix\"", new[] { "prefix suffix" })]
    [InlineData("\t-x\n-y ", new[] { "-x", "-y" })]
    public void Split_FollowsDocumentedRules(string input, string[] expected) =>
        CommandLineTokenizer.Split(input).Should().Equal(expected);

    [Fact]
    public void Split_UnterminatedQuote_TakesTheRest() =>
        CommandLineTokenizer.Split("a \"b c").Should().Equal("a", "b c");
}
