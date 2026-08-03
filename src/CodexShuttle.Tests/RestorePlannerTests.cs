using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class RestorePlannerTests
{
    [TestMethod]
    public void CreatePlan_HistoryAndTools_IncludesSkillsButPreservesConfig()
    {
        var root = CreateTempPackage();
        try
        {
            var manifest = new BackupManifest { CodexHomeExists = true };
            var plan = new RestorePlanner().CreatePlan(root, manifest, new RestoreOptions
            {
                MigrationMode = MigrationMode.HistoryAndTools
            });

            Assert.IsTrue(plan.Any(item => item.Name == "Codex skills"));
            Assert.IsFalse(plan.Any(item => item.SourcePath.EndsWith("config.toml", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(plan.Any(item => item.SourcePath.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CreatePlan_FullProfile_UsesSensitiveExclusions()
    {
        var root = CreateTempPackage();
        try
        {
            var manifest = new BackupManifest { CodexHomeExists = true };
            var plan = new RestorePlanner().CreatePlan(root, manifest, new RestoreOptions
            {
                MigrationMode = MigrationMode.FullProfile
            });

            var profile = plan.Single(item => item.Name.StartsWith("Codex profile", StringComparison.Ordinal));
            CollectionAssert.Contains(profile.MirrorOptions.ExcludedFiles.ToList(), "auth.json");
            CollectionAssert.Contains(profile.MirrorOptions.ExcludedDirectories.ToList(), ".sandbox-secrets");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CreatePlan_DefaultOptions_RejectsWorkspacePathRemapping()
    {
        var root = CreateTempPackage();
        var sourceWorkspace = Path.Combine(Path.GetTempPath(), "CodexShuttleSource", Guid.NewGuid().ToString("N"));
        var differentTarget = Path.Combine(Path.GetTempPath(), "CodexShuttleTarget", Guid.NewGuid().ToString("N"));
        var packageWorkspace = Path.Combine(root, "workspaces", "workspace-001");
        Directory.CreateDirectory(packageWorkspace);

        try
        {
            var manifest = new BackupManifest
            {
                WorkspacePaths =
                [
                    new WorkspaceEntry
                    {
                        SourcePath = sourceWorkspace,
                        PackagePath = Path.GetRelativePath(root, packageWorkspace),
                        Enabled = true,
                        Exists = true
                    }
                ]
            };

            var action = () => new RestorePlanner().CreatePlan(root, manifest, new RestoreOptions
            {
                WorkspaceTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourceWorkspace] = differentTarget
                }
            });

            var exception = Assert.ThrowsException<InvalidOperationException>(action);
            StringAssert.Contains(exception.Message, "must remain unchanged");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var codex = Path.Combine(root, ".codex");
        Directory.CreateDirectory(Path.Combine(codex, "skills"));
        Directory.CreateDirectory(Path.Combine(codex, "sessions"));
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = 'test'");
        File.WriteAllText(Path.Combine(codex, "auth.json"), "secret");
        return root;
    }
}
