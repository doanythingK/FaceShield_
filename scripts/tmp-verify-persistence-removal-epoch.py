from pathlib import Path
import subprocess
import tempfile
import textwrap
import xml.sax.saxutils as xml

root = Path(__file__).resolve().parents[1]
store = (root / "Services" / "Workspace" / "WorkspaceStateStore.cs").read_text(encoding="utf-8-sig")
coord = (root / "Services" / "Workspace" / "WorkspacePersistenceCoordinator.cs").read_text(encoding="utf-8-sig")

checks = {
    "removal epoch registry": "WorkspaceRemovalEpochs" in store,
    "request epoch capture": "CaptureWorkspaceRemovalEpoch" in store,
    "commit epoch rejection": "GetWorkspaceRemovalEpochLocked(snapshot.VideoPath) != removalEpoch" in store,
    "rejected payload cleanup": "TryDeleteWorkspaceBaseDirectory(snapshot.VideoPath);" in store,
    "queue captures epoch": "long removalEpoch = _store.CaptureWorkspaceRemovalEpoch(snapshot.VideoPath);" in coord,
    "pending save carries epoch": "pending.RemovalEpoch" in coord,
}
missing = [name for name, ok in checks.items() if not ok]
if missing:
    raise SystemExit("missing structural checks: " + ", ".join(missing))

project_ref = xml.escape(str(root / "FaceShield.csproj"))
with tempfile.TemporaryDirectory(prefix="faceshield-removal-epoch-") as tmp:
    tmp_path = Path(tmp)
    (tmp_path / "Harness.csproj").write_text(textwrap.dedent(f"""\
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{project_ref}" />
          </ItemGroup>
        </Project>
        """), encoding="utf-8")

    (tmp_path / "Program.cs").write_text(textwrap.dedent("""\
        using FaceShield.Enums.Workspace;
        using FaceShield.Services.Video;
        using FaceShield.Services.Workspace;
        using System.Reflection;

        string videoPath = Path.Combine(
            Path.GetTempPath(),
            $"faceshield-removal-epoch-{Guid.NewGuid():N}.mp4");

        var store = new WorkspaceStateStore();
        using var provider = new FrameMaskProvider();
        using var coordinator = new WorkspacePersistenceCoordinator(store, provider);

        WorkspaceSnapshot MakeSnapshot(int selectedFrame) => new WorkspaceSnapshot(
            videoPath,
            WorkspaceMode.Manual,
            selectedFrame,
            0,
            10,
            DateTimeOffset.Now,
            0,
            false,
            null,
            false,
            false,
            null,
            false,
            false,
            null);

        var latestTaskField = typeof(WorkspacePersistenceCoordinator).GetField(
            "_latestTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_latestTask field not found");

        var blocker = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        latestTaskField.SetValue(coordinator, blocker.Task);

        Task stalePending = coordinator.QueueSaveAsync(MakeSnapshot(11));

        // Removal occurs after the request captured its epoch but before the worker
        // may enter WorkspaceStateStore.SaveWorkspaceCore().
        store.RemoveWorkspacesForPath(videoPath);
        blocker.SetResult(null);
        await stalePending;

        using (var loadProvider = new FrameMaskProvider())
        {
            if (store.TryLoadWorkspace(
                videoPath,
                WorkspaceMode.Manual,
                loadProvider,
                out _))
            {
                throw new InvalidOperationException(
                    "stale pending save recreated a removed workspace");
            }
        }

        // A genuinely new request after removal must still be allowed to recreate it.
        await coordinator.QueueSaveAsync(MakeSnapshot(22));
        using (var loadProvider = new FrameMaskProvider())
        {
            if (!store.TryLoadWorkspace(
                videoPath,
                WorkspaceMode.Manual,
                loadProvider,
                out WorkspaceSnapshot? restored) ||
                restored?.SelectedFrameIndex != 22)
            {
                throw new InvalidOperationException(
                    "post-removal save was incorrectly blocked");
            }
        }

        store.RemoveWorkspacesForPath(videoPath);
        using (var loadProvider = new FrameMaskProvider())
        {
            if (store.TryLoadWorkspace(
                videoPath,
                WorkspaceMode.Manual,
                loadProvider,
                out _))
            {
                throw new InvalidOperationException(
                    "final removal did not win");
            }
        }

        Console.WriteLine(
            "[PersistenceRemovalEpochHarness] PASS " +
            "staleSaveRejected=true postRemovalSaveAllowed=true finalRemovalWins=true");
        """), encoding="utf-8")

    subprocess.run(
        ["dotnet", "run", "--project", str(tmp_path / "Harness.csproj"), "-c", "Release"],
        cwd=root,
        check=True)

print("[PersistenceRemovalEpochVerify] PASS")
