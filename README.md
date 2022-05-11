# SimpleTorrentUWP
An in-progress fork of Rebirth\SimpleTorrent lib with fixes for UWP  
  
### Current issues:  
- Timeout exception when connecting to TCPClient with *any* torrent

### Changes:  
- Added use of StorageFile to allow access to files instead of hard paths  
- Namespace change


### Example Usage:
 
 ```
using SimpleTorrentUWP.Torrent
public sealed partial class MainPage : Page
    {
        public static Client TorrentClient;

        public MainPage()
        {
            this.InitializeComponent();
		// Open torrent file
            FileOpenPicker torrentFile = new FileOpenPicker();
            torrentFile.ViewMode = PickerViewMode.List;
            torrentFile.SuggestedStartLocation = PickerLocationId.Downloads;
            torrentFile.FileTypeFilter.Add(".torrent");

            StorageFile torrent = await torrentFile.PickSingleFileAsync();
            if (torrent == null)
            {
                return;
            }
            else
            {
         //Select download folder
                FolderPicker saveFolder = new FolderPicker();
                saveFolder.SuggestedStartLocation = PickerLocationId.Downloads;
                saveFolder.FileTypeFilter.Add(".");
                StorageFolder DownloadFolder = await saveFolder.PickSingleFolderAsync();
                if (DownloadFolder == null)
                {
                    return;
                }
                else
                {
          // Start the client/download
                    TorrentClient = new Client(6881, torrent.Path, DownloadFolder.Path);
                    await TorrentClient.loadTorrent(torrent, DownloadFolder.Path);
                    TorrentClient.Start();
                }
            }
	 }
      }
