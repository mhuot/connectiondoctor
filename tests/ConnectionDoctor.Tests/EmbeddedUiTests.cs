using System.Text;

namespace ConnectionDoctor.Tests;

/// <summary>
/// These assert the routing rules the dashboard bundle relies on, and are
/// skipped when no bundle is staged so a plain source build stays green.
/// Run scripts/build-ui.ps1 to exercise them.
/// </summary>
public sealed class EmbeddedUiTests
{
    private static bool Staged => EmbeddedUi.IsPresent;

    [SkippableFact]
    public void RootServesTheAppShell()
    {
        Skip.IfNot(Staged);

        var asset = EmbeddedUi.Find("/");

        Assert.NotNull(asset);
        Assert.StartsWith("text/html", asset!.ContentType, StringComparison.Ordinal);
        Assert.Contains("<div id=\"root\">", Encoding.UTF8.GetString(asset.Bytes), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void IndexIsNotCachedButFingerprintedAssetsAre()
    {
        Skip.IfNot(Staged);

        Assert.False(EmbeddedUi.Find("/")!.Immutable);
        Assert.False(EmbeddedUi.Find("/index.html")!.Immutable);

        var script = EmbeddedUi.Find("/" + ScriptPath());
        Assert.NotNull(script);
        Assert.True(script!.Immutable);
        Assert.StartsWith("text/javascript", script.ContentType, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void BundleReferencesResolveToRealAssets()
    {
        Skip.IfNot(Staged);

        // Every relative href/src the shell asks for must actually be servable,
        // which is the failure that would otherwise only show up in a browser.
        var shell = Encoding.UTF8.GetString(EmbeddedUi.Find("/")!.Bytes);
        var references = System.Text.RegularExpressions.Regex
            .Matches(shell, "(?:src|href)=\"\\./([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(references);
        foreach (var reference in references)
        {
            Assert.True(EmbeddedUi.Find("/" + reference) is not null, $"bundle references missing asset: {reference}");
        }
    }

    [SkippableFact]
    public void UnknownPathsAreNotFoundRatherThanTheAppShell()
    {
        Skip.IfNot(Staged);

        Assert.Null(EmbeddedUi.Find("/assets/does-not-exist.js"));
        Assert.Null(EmbeddedUi.Find("/nope"));
    }

    [Fact]
    public void TraversalAndAbsolutePathsAreRefused()
    {
        Assert.Null(EmbeddedUi.Find("/../Program.cs"));
        Assert.Null(EmbeddedUi.Find("/assets/../../secrets"));
        Assert.Null(EmbeddedUi.Find("/%2e%2e/Program.cs"));
        Assert.Null(EmbeddedUi.Find("/C:/Windows/win.ini"));
    }

    private static string ScriptPath()
    {
        var shell = Encoding.UTF8.GetString(EmbeddedUi.Find("/")!.Bytes);
        var match = System.Text.RegularExpressions.Regex.Match(shell, "src=\"\\./(assets/[^\"]+\\.js)\"");
        Assert.True(match.Success, "app shell has no fingerprinted script reference");
        return match.Groups[1].Value;
    }
}
