using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleTorrentUWP.Torrent
{
    public class FileItem
    {
        public string Path;
        public long Size;
        public long Offset;

        public string FormattedSize { get { return Torrent.BytesToString(Size); } }
    }
}
