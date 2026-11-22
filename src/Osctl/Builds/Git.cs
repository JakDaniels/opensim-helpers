// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Processes;

namespace Osctl.Builds;

/// <summary>The git operations builds need, run through <see cref="IProcessRunner"/>.</summary>
public sealed class Git(IProcessRunner runner)
{
    public static bool IsClone(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    /// <summary>Clones the repository into <paramref name="dir"/>, or brings an existing clone up to date with it.</summary>
    public async Task CloneOrFetchAsync(string repository, string dir, CancellationToken ct)
    {
        if (!IsClone(dir))
        {
            // git creates the missing parent directories itself (and under --dry-run nothing is created).
            await RunAsync(["clone", "--no-checkout", repository, dir], change: true, ct);
            return;
        }

        await RunAsync(["-C", dir, "remote", "set-url", "origin", repository], change: true, ct);
        await RunAsync(["-C", dir, "fetch", "--prune", "--tags", "--force", "origin"], change: true, ct);
        // The remote's default branch may have changed (Ref = HEAD follows it).
        await RunAsync(["-C", dir, "remote", "set-head", "origin", "--auto"], change: true, ct);
    }

    /// <summary>
    /// The commit a ref names: <c>HEAD</c> is the remote's default branch; otherwise a remote branch, a tag, or a
    /// commit, in that order.
    /// </summary>
    public async Task<string> ResolveAsync(string dir, string gitRef, CancellationToken ct)
    {
        string[] candidates = gitRef == "HEAD"
            ? ["refs/remotes/origin/HEAD"]
            : [$"refs/remotes/origin/{gitRef}", $"refs/tags/{gitRef}", gitRef];

        foreach (var candidate in candidates)
        {
            var r = await runner.RunAsync("git", ["-C", dir, "rev-parse", "--verify", "--quiet", candidate + "^{commit}"],
                cancellationToken: ct);
            if (r.Succeeded)
                return r.StandardOutput.Trim();
        }
        throw new OsctlException($"'{gitRef}' is not a branch, tag or commit in {dir}");
    }

    public async Task<string> SubjectAsync(string dir, string commit, CancellationToken ct) =>
        (await RunAsync(["-C", dir, "log", "-1", "--format=%s", commit], change: false, ct)).StandardOutput.Trim();

    /// <summary>Checks out a commit and removes everything git does not track (osctl owns these clones).</summary>
    public async Task CheckoutCleanAsync(string dir, string commit, CancellationToken ct)
    {
        await RunAsync(["-C", dir, "checkout", "--force", "--detach", commit], change: true, ct);
        await RunAsync(["-C", dir, "clean", "-fdxq"], change: true, ct);
    }

    /// <summary>Applies a patch, checking first that all of it applies.</summary>
    public async Task ApplyAsync(string dir, string patch, CancellationToken ct)
    {
        var check = await runner.RunAsync("git", ["-C", dir, "apply", "--check", patch], cancellationToken: ct);
        if (!check.Succeeded)
            throw new OsctlException($"Patch {patch} does not apply: {check.StandardError.Trim()}");
        await RunAsync(["-C", dir, "apply", patch], change: true, ct);
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, bool change, CancellationToken ct)
    {
        var r = await runner.RunAsync("git", args, change ? ProcessRunOptions.Change : ProcessRunOptions.Query, ct);
        if (r.NotFound)
            throw new OsctlException("git is not installed (sudo apt install git)");
        if (!r.Succeeded)
            throw new OsctlException($"{ShellQuote.Describe("git", args)} failed: {r.StandardError.Trim()}");
        return r;
    }
}
