using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Shared;

/// <summary>
/// A git repository in a temporary folder whose git processes ignore the machine's global and system configuration,
/// so settings such as commit.gpgsign or core.hooksPath cannot change the test. The folder is removed on dispose.
/// </summary>
internal sealed class GitTestRepository : IDisposable
{
    private readonly string _root;

    private GitTestRepository(string root)
    {
        _root = root;
        Path = System.IO.Path.Combine(root, "repo");
        string globalConfig = System.IO.Path.Combine(root, "global.gitconfig");
        Directory.CreateDirectory(Path);
        File.WriteAllText(globalConfig, string.Empty);
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = globalConfig,
            // A temporary folder inside a repository must not make the folders of the test part of that repository.
            ["GIT_CEILING_DIRECTORIES"] = root,
            // A protocol allowlist of the machine, as some CI systems set it, would override protocol.ext.allow.
            ["GIT_ALLOW_PROTOCOL"] = null
        };
    }

    /// <summary>Gets the temporary folder that holds the working tree, the remote and the clones.</summary>
    public string RootPath => _root;

    /// <summary>Gets the working tree root.</summary>
    public string Path { get; }

    /// <summary>Gets the environment variables every git process of the test uses; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>Gets git process options with <see cref="Environment"/> for the service under test.</summary>
    public GitProcessOptions ProcessOptions => new() { Environment = Environment };

    /// <summary>Gets the path of the bare remote repository, or null before <see cref="AddBareRemote"/>.</summary>
    public string? RemotePath { get; private set; }

    /// <summary>
    /// Creates a repository on branch <c>main</c> with a local user name and email address. With files, it contains
    /// one commit in which each file has its own name as content. Skips the test when git is not available.
    /// </summary>
    public static GitTestRepository Create(params string[] committedFiles)
    {
        GitTestRepository repository = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "xve-git-" + Guid.NewGuid().ToString("N")));
        try
        {
            // init -b needs git 2.28; the editor supports git 2.26.
            Assert.SkipUnless(repository.TryRunGit(out _, "init", "-q"), "git is not available.");
            repository.RunGit("symbolic-ref", "HEAD", "refs/heads/main");
            repository.RunGit("config", "user.name", "Test");
            repository.RunGit("config", "user.email", "test@example.invalid");
            repository.RunGit("config", "core.autocrlf", "false");
            if (committedFiles.Length > 0)
            {
                foreach (string file in committedFiles)
                {
                    repository.WriteFile(file, file + "\n");
                }

                List<string> addArgs = new() { "add", "--" };
                addArgs.AddRange(committedFiles);
                repository.RunGit(addArgs.ToArray());
                repository.RunGit("commit", "-q", "-m", "init");
            }

            return repository;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>Creates an empty bare repository next to the working tree and adds it as remote <c>origin</c>.</summary>
    public string AddBareRemote()
    {
        string remote = System.IO.Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(remote);
        RunGitIn(remote, "init", "-q", "--bare");
        RunGitIn(remote, "symbolic-ref", "HEAD", "refs/heads/main");
        RunGit("remote", "add", "origin", remote);
        RemotePath = remote;
        return remote;
    }

    /// <summary>
    /// Clones the bare remote into the folder <paramref name="name"/> next to the working tree, as a second user of
    /// the remote with its own user name, and returns the path of the clone.
    /// </summary>
    public string CloneRemote(string name)
    {
        string clone = System.IO.Path.Combine(_root, name);
        RunGitIn(_root, "clone", "-q", RemotePath ?? throw new InvalidOperationException("Add the bare remote first."), clone);
        RunGitIn(clone, "config", "user.name", "Other");
        RunGitIn(clone, "config", "user.email", "other@example.invalid");
        RunGitIn(clone, "config", "core.autocrlf", "false");
        return clone;
    }

    /// <summary>Writes a file in <paramref name="workingTree"/>, stages it and commits it with <paramref name="message"/>.</summary>
    public void CommitFileIn(string workingTree, string relativePath, string content, string message)
    {
        string fullPath = System.IO.Path.Combine(workingTree, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGitIn(workingTree, "add", "--", relativePath);
        RunGitIn(workingTree, "commit", "-q", "-m", message);
    }

    /// <summary>Writes a file below the working tree root, creating its folder.</summary>
    public string WriteFile(string relativePath, string content)
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>Runs git in the working tree, asserts exit code 0 and returns the output.</summary>
    public string RunGit(params string[] args) => RunGitIn(Path, args);

    /// <summary>Runs git in the working tree and returns whether it exited with code 0.</summary>
    public bool TryRunGit(out string output, params string[] args) => TryRunGitIn(Path, out output, args);

    public void Dispose()
    {
        try
        {
            // Git marks object files read-only.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs git in <paramref name="workingDirectory"/>, asserts exit code 0 and returns the output.</summary>
    public string RunGitIn(string workingDirectory, params string[] args)
    {
        Assert.True(
            TryRunGitIn(workingDirectory, out string output, args),
            "git " + string.Join(' ', args) + " failed: " + output);
        return output;
    }

    private bool TryRunGitIn(string workingDirectory, out string output, params string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (KeyValuePair<string, string?> variable in Environment)
        {
            if (variable.Value is null)
            {
                startInfo.Environment.Remove(variable.Key);
            }
            else
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                output += error.Result;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            output = string.Empty;
            return false;
        }
    }
}
