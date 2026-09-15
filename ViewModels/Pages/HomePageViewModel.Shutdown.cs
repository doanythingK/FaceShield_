using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.ViewModels.Pages
{
    public partial class HomePageViewModel
    {
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
