// FILE: D:\WorkSpace\FaceShield\ViewModels\Pages\WorkspaceViewModel.cs
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using FaceShield.ViewModels.Workspace;
using System;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages
{
    public partial class WorkspaceViewModel : ViewModelBase
    {
        public ToolPanelViewModel ToolPanel { get; } = new();
        public FramePreviewViewModel FramePreview { get; }
        public FrameListViewModel FrameList { get; }

        // ✅ 프레임별 최종 마스크 저장소
        private readonly FrameMaskProvider _maskProvider = new();

        public WorkspaceViewModel(string videoPath)
        {
            FrameList = new FrameListViewModel(videoPath);
            FramePreview = new FramePreviewViewModel(ToolPanel);

            // 🔹 VideoSession 생성 (타임라인 썸네일 + 정확 프레임)
            var session = new VideoSession(videoPath);
            FramePreview.InitializeSession(session);

            // 🔹 타임라인 선택 / 재생 / 키 이동 → 프리뷰 갱신
            FrameList.SelectedFrameIndexChanged += index =>
            {
                FramePreview.OnFrameIndexChanged(index);
            };

            // 🔹 ToolPanel 명령 연결
            ToolPanel.UndoRequested += () => FramePreview.Undo();

            ToolPanel.SaveRequested += async () =>
            {
                // 현재 프레임 마스크를 provider에 저장(최소 동작)
                if (FrameList.SelectedFrameIndex >= 0 && FramePreview.MaskBitmap != null)
                    _maskProvider.SetMask(FrameList.SelectedFrameIndex, FramePreview.MaskBitmap);

                // TODO: 실제 앱에서는 "편집된 모든 프레임"을 provider에 넣어둬야 합니다.
                // 지금 구조상 최소로는 "사용자가 편집한 프레임"이 선택될 때마다 저장하면 됩니다.

                await SaveVideoAsync();
            };
        }

        private Task SaveVideoAsync()
        {
            // 출력 경로는 일단 입력 옆에 _blur.mp4 (실제 저장)
            string input = FrameList.VideoPath;
            string output = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(input)!,
                System.IO.Path.GetFileNameWithoutExtension(input) + "_blur.mp4");

            var exporter = new VideoExportService(_maskProvider);

            return Task.Run(() =>
            {
                // blurRadius는 일단 6 (추후 UI 연동 가능)
                exporter.Export(input, output, blurRadius: 6);
            });
        }
    }
}
