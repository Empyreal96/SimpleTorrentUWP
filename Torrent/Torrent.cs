using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography.Core;
using Windows.Storage;

namespace SimpleTorrentUWP.Torrent
{
    public class Torrent
    {
        // .torrent variables
        // delegating to tracker to keep track of the addresses of each tracker and to assign listeners to them
        public List<Tracker> trackers { get; } = new List<Tracker>();
        public string comment { get; set; }
        public string createdBy { get; set; }
        public DateTime creationDate { get; set; }
        public System.Text.Encoding encoding { get; set; }

        // .torrent info block
        // delegating to fileitem to keep track wether the torrent has a single file or multiple files (also including length)
        public List<FileItem> files { get; private set; } = new List<FileItem>();
        public string name { get; private set; }
        public int pieceSize { get; private set; }
        public int blockSize { get; private set; }
        public byte[][] pieceHashes { get; private set; }
        public bool? isPrivate { get; private set; }

        // assign a file directory and a download directory
        public string fileDirectory { get { return (files.Count > 1 ? name + Path.DirectorySeparatorChar : ""); } }
        public string downloadDirectory { get; private set; }

        // info hashing
        public byte[] infohash { get; private set; } = new byte[20]; // raw hash of info dictionary of .torrent
        public string hexStringInfohash { get { return String.Join("", this.infohash.Select(x => x.ToString("x2"))); } }
        public string urlSafeStringInfohash { get { return Encoding.UTF8.GetString(WebUtility.UrlEncodeToBytes(this.infohash, 0, 20)); } }

        public long totalSize { get { return files.Sum(x => x.Size); } }

        public string formattedPieceSize { get { return BytesToString(pieceSize); } }
        public string formattedTotalSize { get { return BytesToString(totalSize); } }

        public int pieceCount { get { return pieceHashes.Length; } }
        public bool[] isPieceVerified { get; private set; }
        public bool[][] isBlockAcquired { get; private set; }

        public string verifiedPiecesString
        {
            get { return String.Join("", isPieceVerified.Select(x => x ? 1 : 0)); }

        }
        public int verifiedPieceCount
        {
            get { return isPieceVerified.Count(x => x); }
        }

        public double verifiedRatio
        {
            get { return verifiedPieceCount / (double)pieceCount; }
        }

        public bool isCompleted
        {
            get { return verifiedPieceCount == pieceCount; }
        }

        public bool isStarted
        {
            get { return verifiedPieceCount > 0; }
        }

        public long uploaded { get; set; } = 0;
        public long downloaded
        {
            get { return pieceSize * verifiedPieceCount; } // TODO this is an approximation, may need to go into further detail to retrieve exact downloaded size
        }

        public long left
        {
            get { return totalSize - downloaded; }
        }

        public EventHandler<List<IPEndPoint>> PeerListUpdated;
        public object[] fileWriteLocks;
        private static HashAlgorithmProvider sha1 = HashAlgorithmProvider.OpenAlgorithm("SHA1");

// piece and block sizes calculations
        public int GetPieceSize(int piece)
        {
            // piece can never be more than the count of the actually present pieces
            if (piece > pieceCount)
                throw new Exception("requested piece is bigger than the current pieceCount!");

            // if the last piece is requested check how much is stored in this last piece
            if (piece == pieceCount - 1)
            {
                int remainder = Convert.ToInt32(totalSize % pieceSize);
                if (remainder != 0)
                    return remainder;
            }

            return pieceSize;
        }

        public int GetBlockSize(int piece, int block)
        {
            if (piece > pieceCount)
                throw new Exception("requested piece is bigger than the current pieceCount!");

            // if this is the last block it may have a smaller size
            if (block == GetBlockCount(piece) - 1)
            {
                int remainder = Convert.ToInt32(GetPieceSize(piece) % blockSize);
                if (remainder != 0)
                    return remainder;
            }
            return blockSize;
        }

        public int GetBlockCount(int piece)
        {
            return Convert.ToInt32(
                Math.Ceiling(GetPieceSize(piece) / (double)blockSize)
            );
        }

// bytes to string conversion
        public static string BytesToString(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            if (bytes == 0)
                return "0" + units[0];
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return num + units[place];
        }

        // constructor
        public Torrent(string name, string location, List<FileItem> files, List<string> trackers, int pieceSize, byte[] pieceHashes = null, int blockSize = 16484, bool? isPrivate = false)
        {
            this.name = name;
            this.downloadDirectory = location;
            this.files = files;
            this.fileWriteLocks = new object[files.Count];
            for (int i = 0; i < this.files.Count; i++)
                fileWriteLocks[i] = new object();

            if (this.trackers != null)
            {
                foreach(string url in trackers)
                {
                    Tracker tracker = new SimpleTorrentUWP.Torrent.Tracker(url);
                    this.trackers.Add(tracker);
                    tracker.PeerListUpdated += HandlePeerListUpdated;
                }
            }

            this.pieceSize = pieceSize;
            this.blockSize = blockSize;
            this.isPrivate = isPrivate;

            int count = Convert.ToInt32(Math.Ceiling(totalSize / Convert.ToDouble(pieceSize)));

            this.pieceHashes = new byte[count][];
            this.isPieceVerified = new bool[count];
            this.isBlockAcquired = new bool[count][];

            for (int i = 0; i < pieceCount; i++)
            {
                this.isBlockAcquired[i] = new bool[GetBlockCount(i)];
            }

            if (this.pieceHashes == null)
            {
                // new torrent, create hashes from files
                for (int i = 0; i < this.pieceCount; i++)
                    this.pieceHashes[i] = GetHash(i);
            } else
            {
                for (int i = 0; i < this.pieceCount; i++)
                {
                    this.pieceHashes[i] = new byte[20];
                    Buffer.BlockCopy(pieceHashes, i * 20, this.pieceHashes[i], 0, 20);
                }
            }

            object info = TorrentInfoToBEncodingObject(this);
            byte[] bytes = BEncoding.Encoding.Encode(info);
            this.infohash = HashAlgorithmProvider.OpenAlgorithm("SHA1").HashData(bytes.AsBuffer()).ToArray();

            for (int i = 0; i < this.pieceCount; i++)
            {
                Verify(i);
            }
        }

// Read
        public byte[] Read(long start, int length)
        {
            long end = start + length;
            byte[] buffer = new byte[length];

            for (int i = 0; i < files.Count; i++)
            {
                if ((start < files[i].Offset && end < files[i].Offset) || (start > files[i].Offset + files[i].Size && end > files[i].Offset + files[i].Size))
                    continue;

                string filePath = downloadDirectory + Path.AltDirectorySeparatorChar + fileDirectory + files[i].Path;

                if (!File.Exists(filePath))
                    return null;

                long fstart = Math.Max(0, start - files[i].Offset);
                long fend = Math.Min(end - files[i].Offset, files[i].Size);
                int flength = Convert.ToInt32(fend - fstart);
                int bstart = Math.Max(0, Convert.ToInt32(files[i].Offset - start));

                using (Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(fstart, SeekOrigin.Begin);
                    stream.Read(buffer, bstart, flength);
                }
            }
            return buffer;
        }

// Write
        public void Write(long start, byte[] bytes)
        {
            long end = start + bytes.Length;

            for (int i = 0; i < files.Count; i++)
            {
                if ((start < files[i].Offset && end < files[i].Offset) || (start > files[i].Offset + files[i].Size && end > files[i].Offset + files[i].Size))
                    continue;

                string filePath = downloadDirectory + Path.AltDirectorySeparatorChar + fileDirectory + files[i].Path;

                string dir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                lock (fileWriteLocks[i])
                {
                    using (Stream stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                    {
                        long fstart = Math.Max(0, start - files[i].Offset);
                        long fend = Math.Min(end - files[i].Offset, files[i].Size);
                        int flength = Convert.ToInt32(fend - fstart);
                        int bstart = Math.Max(0, Convert.ToInt32(files[i].Offset - start));

                        stream.Seek(fstart, SeekOrigin.Begin);
                        stream.Write(bytes, bstart, flength);
                    }
                }
            }
        }

// read piece and read/write block
        
        public byte[] ReadPiece(int piece)
        {
            return Read(piece * pieceSize, GetPieceSize(piece));
        }

        public byte[] ReadBlock(int piece, int offset, int length)
        {
            return Read(piece * pieceSize + offset, length);
        }

        public void WriteBlock(int piece, int block, byte[] bytes)
        {
            Write(piece * pieceSize + block * blockSize, bytes);
            isBlockAcquired[piece][block] = true;
            Verify(piece);
        }

        // verification
        public event EventHandler<int> pieceVerified;

        public void Verify(int piece)
        {
            byte[] hash = GetHash(piece);

            bool isVerified = (hash != null && hash.SequenceEqual(pieceHashes[piece]));

            if (isVerified)
            {
                isPieceVerified[piece] = true;

                for (int i = 0; i < isBlockAcquired[piece].Length; i++)
                    isBlockAcquired[piece][i] = true;

                var handler = pieceVerified;
                if (handler != null)
                    handler(this, piece);

                return;
            }

            isPieceVerified[piece] = false;

            // reload piece
            if (isBlockAcquired[piece].All(x => x))
            {
                for (int i = 0; i < isBlockAcquired[piece].Length; i++)
                    isBlockAcquired[piece][i] = false;
            }
        }

        public byte[] GetHash(int piece)
        {
            byte[] data = ReadPiece(piece);

            if (data == null)
                return null;

            return sha1.HashData(data.AsBuffer()).ToArray();
        }

// importing/exporting
        public async static Task<Torrent> LoadFromFile(StorageFile filePath, string downloadPath)
        {
            object obj = await BEncoding.Decoding.DecodeFile(filePath);
            string name = Path.GetFileNameWithoutExtension(filePath.Path);

            return BEncodingObjectToTorrent(obj, name, downloadPath);
        }

        public static void SaveToFile(Torrent torrent)
        {
            object obj = TorrentToBEncodingObject(torrent);
            BEncoding.Encoding.EncodeToFile(obj, torrent.name + ".torrent");
        }

// torrent to bencoding object

        public static long DateTimeToUnixTimeStamp(DateTime time)
        {
            return Convert.ToInt64((DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds);
        } 

        public static object TorrentToBEncodingObject(Torrent torrent)
        {
            Dictionary<string, object> dictionary = new Dictionary<string, object>();

            if (torrent.trackers.Count == 1)
                dictionary["announce"] = System.Text.Encoding.UTF8.GetBytes(torrent.trackers[0].Address);
            else
                dictionary["announce"] = torrent.trackers.Select(x => (object)System.Text.Encoding.UTF8.GetBytes(x.Address)).ToList();

            dictionary["comment"] = System.Text.Encoding.UTF8.GetBytes(torrent.comment);
            dictionary["created by"] = System.Text.Encoding.UTF8.GetBytes(torrent.createdBy);
            dictionary["creation date"] = DateTimeToUnixTimeStamp(torrent.creationDate);
            dictionary["encoding"] = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.WebName.ToUpper());
            dictionary["info"] = TorrentInfoToBEncodingObject(torrent);

            return dictionary;
        }

        public static object TorrentInfoToBEncodingObject(Torrent torrent)
        {
            Dictionary<string, object> dictionary = new Dictionary<string, object>();

            dictionary["piece length"] = (long)torrent.pieceSize;
            byte[] pieces = new byte[20 * torrent.pieceCount];
            for (int i = 0; i < torrent.pieceCount; i++)
                Buffer.BlockCopy(torrent.pieceHashes[i], 0, pieces, i * 20, 20);
            dictionary["pieces"] = pieces;

            if (torrent.isPrivate.HasValue)
                dictionary["private"] = torrent.isPrivate.Value ? 1L : 0L;

            if (torrent.files.Count == 1)
            {
                dictionary["name"] = System.Text.Encoding.UTF8.GetBytes(torrent.files[0].Path);
                dictionary["length"] = torrent.files[0].Size;
            } else
            {
                List<object> files = new List<object>();

                foreach (var f in torrent.files)
                {
                    Dictionary<string, object> fileDictionary = new Dictionary<string, object>();
                    fileDictionary["path"] = f.Path.Split(Path.DirectorySeparatorChar).Select(x => (object)System.Text.Encoding.UTF8.GetBytes(x)).ToList();
                    fileDictionary["length"] = f.Size;
                    files.Add(fileDictionary);
                }

                dictionary["files"] = files;
                dictionary["name"] = System.Text.Encoding.UTF8.GetBytes(torrent.fileDirectory.Substring(0, torrent.fileDirectory.Length - 1));
            }

            return dictionary;
        }


// bencoding object to torrent
        public static string DecodeUTF8String(object obj)
        {
            byte[] bytes = obj as byte[];

            if (bytes == null)
                throw new Exception("unable to decode utf-8 string, object is not a byte array, it's null");

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            System.DateTime time = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            return time.AddSeconds(unixTimeStamp).ToLocalTime();
        }

        private static Torrent BEncodingObjectToTorrent(object bencoding, string name, string downloadPath)
        {
            Dictionary<string, object> obj = (Dictionary<string, object>)bencoding;

            if (obj == null)
                throw new Exception("not a torrent file");

            if (!obj.ContainsKey("info"))
                throw new Exception("missing info section!");

            List<string> trackers = new List<string>();

            if (obj.ContainsKey("announce"))
                trackers.Add(DecodeUTF8String(obj["announce"]));

            Dictionary<string, object> info = (Dictionary<string, object>)obj["info"];

            if (info == null)
                throw new Exception("info dictionay was null");

            List<FileItem> files = new List<FileItem>();

            if (info.ContainsKey("name") && info.ContainsKey("length"))
            {
                files.Add(new FileItem()
                {
                    Path = DecodeUTF8String(info["name"]),
                    Size = (long)info["length"]
                });
            } else if (info.ContainsKey("files"))
            {
                long running = 0;

                foreach (object item in (List<object>)info["files"])
                {
                    var dictionary = item as Dictionary<string, object>;

                    if (dictionary == null || !dictionary.ContainsKey("path") || !dictionary.ContainsKey("length"))
                        throw new Exception("error: incorrect file specification");

                    string path = String.Join(Path.DirectorySeparatorChar.ToString(), ((List<object>)dictionary["path"]).Select(x => DecodeUTF8String(x)));

                    long size = (long)dictionary["length"];

                    files.Add(new FileItem()
                    {
                        Path = path,
                        Size = size,
                        Offset = running
                    });

                    running += size;
                }
            } else
            {
                throw new Exception("error: no files specified in torrent");
            }

            if (!info.ContainsKey("piece length"))
                throw new Exception("error: piece length not found");

            int pieceSize = Convert.ToInt32(info["piece length"]);

            if (!info.ContainsKey("pieces"))
                throw new Exception("error: pieces not found");

            byte[] pieceHashes = (byte[])info["pieces"];

            bool? isPrivate = null;
            if (info.ContainsKey("private"))
                isPrivate = ((long)info["private"]) == 1L;

            Torrent torrent = new Torrent(name, downloadPath, files, trackers, pieceSize, pieceHashes, 16384, isPrivate);

            if (obj.ContainsKey("comment"))
                torrent.comment = DecodeUTF8String(obj["comment"]);

            if (obj.ContainsKey("created by"))
                torrent.createdBy = DecodeUTF8String(obj["created by"]);

            if (obj.ContainsKey("creation date"))
                torrent.creationDate = UnixTimeStampToDateTime(Convert.ToDouble(obj["creation date"]));

            if (obj.ContainsKey("encoding"))
                torrent.encoding = System.Text.Encoding.GetEncoding(DecodeUTF8String(obj["encoding"]));

            return torrent;
        }

// create a torrent
        public static Torrent Create(string path, List<string> trackers = null, int pieceSize = 32768, string comment = "")
        {
            string name = "";
            List<FileItem> files = new List<FileItem>();

            if (File.Exists(path))
            {
                name = Path.GetFileName(path);

                long size = new FileInfo(path).Length;
                files.Add(new FileItem()
                {
                    Path = Path.GetFileName(path),
                    Size = size
                });
            } else
            {
                name = path;
                string directory = path + Path.DirectorySeparatorChar;

                long running = 0;
                foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    string f = file.Substring(directory.Length);

                    if (f.StartsWith("."))
                        continue;

                    long size = new FileInfo(file).Length;

                    files.Add(new FileItem()
                    {
                        Path = f,
                        Size = size,
                        Offset = running
                    });

                    running += size;
                }
            }

            Torrent torrent = new Torrent(name, "", files, trackers, pieceSize);
            torrent.comment = comment;
            torrent.createdBy = "simpleTorrent";
            torrent.creationDate = DateTime.Now;
            torrent.encoding = Encoding.UTF8;

            return torrent;
        }

// update trackers
        public void UpdateTrackers(TrackerEvent ev, string id, int port)
        {
            foreach (var tracker in trackers)
                tracker.Update(this, ev, id, port);
        }

        public void ResetTrackersLastRequest()
        {
            foreach (var tracker in trackers)
                tracker.ResetLastRequest();
        }

        private void HandlePeerListUpdated(object sender, List<IPEndPoint> endPoints)
        {
            var handler = PeerListUpdated;
            if (handler != null)
                handler(sender, endPoints);
        }


    }
}
