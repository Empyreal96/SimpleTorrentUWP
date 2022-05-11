using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace SimpleTorrentUWP.Lib
{
    class Settings
    {
        public static ApplicationData APP_DATA = ApplicationData.Current;
        public static ApplicationDataContainer LOCAL_SETTINGS = APP_DATA.LocalSettings;

        // use settings with static keys only
        public static string DOWNLOADS_PATH = "downloads_path";
        public static string IN_APP_PLAYER = "in_app_player";

        public static void SetString(string key, string value)
        {
            LOCAL_SETTINGS.Values[key] = value;
        }

        public static string GetString(string key)
        {
            string value = LOCAL_SETTINGS.Values[key] as string;
            return string.IsNullOrEmpty(value) ? null : value;
        }


        // downloads folder settings
        public static void setDownloadsPath(string path)
        {
            SetString(Settings.DOWNLOADS_PATH, path);
        }

        public static string getDownloadsPath()
        {
            return GetString(Settings.DOWNLOADS_PATH);
        }

        // in_app_player settings
        public static void setInAppPlayer(bool boolean)
        {
            var value = boolean ? "true" : "false";
            SetString(Settings.IN_APP_PLAYER, value);
        }

        public static bool getInAppPlayerStatus()
        {
            var value = GetString(Settings.IN_APP_PLAYER);
            if (value == null)
            {
                return false;
            }
            else
            {
                return value.Equals("true");
            }
        }

        public static async Task<StorageFolder> getDownloadsFolder()
        {
            string path = Settings.GetString(Settings.DOWNLOADS_PATH);
            return await StorageFolder.GetFolderFromPathAsync(path);
        }
    }
}
