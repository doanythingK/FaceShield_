using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace
{
    public sealed class FrameItemViewModel : ViewModelBase
    {
        public int Index { get; }
        public bool HasFace { get; }

        public TimeSpan Time { get; }

        public FrameItemViewModel(int index, bool hasFace, TimeSpan time)
        {
            Index = index;
            HasFace = hasFace;
            Time = time;
        }
    }
}
