using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceShield.Models
{
    public sealed class RecentItem
    {
        public string Title { get; }
        public string Path { get; }
        public DateTimeOffset LastOpened { get; }

        public string LastOpenedText => $"Last opened: {LastOpened:yyyy-MM-dd HH:mm}";

        public RecentItem(string title, string path, DateTimeOffset lastOpened)
        {
            Title = title;
            Path = path;
            LastOpened = lastOpened;
        }
    }
}
