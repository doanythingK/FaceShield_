using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages
{
    public partial class HomePageViewModel
    {
        // Window closing is still cancellable here. Freeze starts, cancel
        // Auto/manual work and await lifetime completion before capturing
        // the final resume index and mask snapshot. Failure reopens admission
        // and leaves all cached workspaces available for another save attempt.
        internal async Task PersistAllWorkspacesForWindowCloseAsync()
        {
            WorkspaceViewModel[] workspaces;
            lock (_workspaceCacheGate)
            {
                if (_shutdownRequested != 0)
                    throw new InvalidOperationException("Application shutdown is already in progress.");
                Volatile.Write(ref _closePreparationRequested, 1);
                workspaces = _workspaceCache.Values.Distinct().ToArray();
            }

            _autoRestartRequested = false;
            try { Volatile.Read(ref _autoCts)?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { Volatile.Read(ref _workspaceLoadCts)?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { Volatile.Read(ref _yoloDownloadCts)?.Cancel(); }
            catch (ObjectDisposedException) { }

            try
            {
                await Task.WhenAll(workspaces.Select(
                    workspace => workspace.PauseAndDrainForWindowCloseAsync()));
                PersistAllWorkspacesForShutdown();
            }
            catch
            {
                foreach (WorkspaceViewModel workspace in workspaces)
                    workspace.ResumeAfterFailedWindowClose();
                Volatile.Write(ref _closePreparationRequested, 0);
                throw;
            }
        }

        internal void PersistAllWorkspacesForShutdown()
        {
            WorkspaceViewModel[] workspaces;
            lock (_workspaceCacheGate)
                workspaces = _workspaceCache.Values.Distinct().ToArray();

            List<Exception>? failures = null;
            foreach (WorkspaceViewModel workspace in workspaces)
            {
                try
                {
                    workspace.PersistWorkspaceStateImmediate();
                }
                catch (Exception ex)
                {
                    failures ??= new List<Exception>();
                    failures.Add(new InvalidOperationException(
                        $"워크스페이스 종료 저장 실패: {workspace.FrameList.VideoPath} ({workspace.Mode})",
                        ex));
                }
            }

            try
            {
                _stateStore.SaveRecents(Recents);
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(new InvalidOperationException(
                    "최근 항목 종료 저장 실패.",
                    ex));
            }

            if (failures is { Count: > 0 })
            {
                throw new AggregateException(
                    "일부 종료 상태 저장에 실패했습니다.",
                    failures);
            }
        }
    }
}
