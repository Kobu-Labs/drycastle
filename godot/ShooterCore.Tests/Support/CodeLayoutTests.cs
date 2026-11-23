using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Support;

/// <summary>Keeps the folder structure honest: a file's namespace is its folder, and nothing new lands loose in a project root.</summary>
public class CodeLayoutTests
{
    static readonly (string Dir, string Namespace)[] Projects =
    {
        ("godot/shooter", "Drycastle.Shooter"),
        ("godot/ShooterCore", "Drycastle.ShooterCore"),
        ("godot/viewer", "Drycastle.Viewer"),
        ("godot/netspike", "Drycastle.Netspike"),
    };

    static readonly string[] SkippedDirs = { "bin", "obj", ".godot", "addons" };

    [Fact]
    public void NamespaceFollowsFolder()
    {
        var wrong = Projects.SelectMany(p => Sources(p.Dir).Select(f => (File: f, Want: Expected(p, f), Text: File.ReadAllText(f))))
            // Parts of a partial class live in the namespace of the class, wherever the file sits.
            .Where(f => !Regex.IsMatch(f.Text, @"\bpartial\s+(class|struct|record)\b"))
            .Where(f => Regex.Match(f.Text, @"^namespace\s+([\w.]+)", RegexOptions.Multiline).Groups[1].Value != f.Want)
            .Select(f => $"{Path.GetRelativePath(RepoRoot(), f.File)}: expected namespace {f.Want}");
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("godot/shooter", "GroundCheck.cs", "Shooter.cs")]
    [InlineData("godot/ShooterCore")]
    public void NoLooseFilesInTheProjectRoot(string dir, params string[] allowed)
    {
        var loose = Directory.GetFiles(Path.Combine(RepoRoot(), dir), "*.cs").Select(Path.GetFileName).Except(allowed);
        Assert.Empty(loose);
    }

    static string Expected((string Dir, string Namespace) project, string file)
    {
        var folder = Path.GetRelativePath(Path.Combine(RepoRoot(), project.Dir), Path.GetDirectoryName(file)!);
        return folder == "." ? project.Namespace : project.Namespace + "." + folder.Replace(Path.DirectorySeparatorChar, '.');
    }

    static string[] Sources(string dir) => Directory.GetFiles(Path.Combine(RepoRoot(), dir), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(SkippedDirs.Contains)).ToArray();

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
