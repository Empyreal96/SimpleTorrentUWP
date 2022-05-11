using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading;
using MiscUtil.Conversion;
using System.IO;
using Windows.Networking.Sockets;
using Windows.Networking;
using System.Threading.Tasks;

namespace SimpleTorrentUWP.Torrent
{
    public class DataRequest
    {
        public Peer Peer;
        public int Piece;
        public int Begin;
        public int Length;
        public bool IsCancelled;
    }

    public class DataPackage
    {
        public Peer Peer;
        public int Piece;
        public int Block;
        public byte[] Data;
    }

    public enum MessageType : int
    {
        Unknown = -3,
        Handshake = -2,
        KeepAlive = -1,
        Choke = 0,
        Unchoke = 1,
        Interested = 2,
        NotInterested = 3,
        Have = 4,
        Bitfield = 5,
        Request = 6,
        Piece = 7,
        Cancel = 8,
        Port = 9,
    }

    public class Peer
    {
        #region Events

        public event EventHandler Disconnected;
        public event EventHandler StateChanged;
        public event EventHandler<DataRequest> BlockRequested;
        public event EventHandler<DataRequest> BlockCancelled;
        public event EventHandler<DataPackage> BlockReceived;

        #endregion

        #region Properties

        public string LocalId { get; set; }
        public string Id { get; set; }

        public Torrent Torrent { get; private set; }

        public IPEndPoint IPEndPoint { get; private set; }
        public string Key { get { return IPEndPoint.ToString(); } }

        private Socket TcpSocket { get; set; }
        private static ManualResetEvent ClientDone = new ManualResetEvent(false);
        private const int TIMEOUT_MILLISECONDS = 5000;

        private TcpClient TcpClient { get; set; }
        private NetworkStream stream { get; set; }
        private const int bufferSize = 256;
        private byte[] streamBuffer = new byte[bufferSize];
        private List<byte> data = new List<byte>();

        public bool[] IsPieceDownloaded = new bool[0];
        public string PiecesDownloaded { get { return String.Join("", IsPieceDownloaded.Select(x => Convert.ToInt32(x))); } }
        public int PiecesRequiredAvailable { get { return IsPieceDownloaded.Select((x, i) => x && !Torrent.isPieceVerified[i]).Count(x => x); } }
        public int PiecesDownloadedCount { get { return IsPieceDownloaded.Count(x => x); } }
        public bool IsCompleted { get { return PiecesDownloadedCount == Torrent.pieceCount; } }

        public bool IsDisconnected;

        public bool IsHandshakeSent;
        public bool IsPositionSent;
        public bool IsChokeSent = true;
        public bool IsInterestedSent = false;

        public bool IsHandshakeReceived;
        public bool IsChokeReceived = true;
        public bool IsInterestedReceived = false;

        public bool[][] IsBlockRequested = new bool[0][];
        public int BlocksRequested { get { return IsBlockRequested.Sum(x => x.Count(y => y)); } }

        public DateTime LastActive;
        public DateTime LastKeepAlive = DateTime.MinValue;

        public long Uploaded;
        public long Downloaded;

        #endregion

        #region Constructors

        public Peer(Torrent torrent, string localId, TcpClient client) : this(torrent, localId)
        {
            TcpClient = client;
            IPEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
        }

        public Peer(Torrent torrent, string localId, IPEndPoint endPoint) : this(torrent, localId)
        {
            IPEndPoint = endPoint;
        }

        private Peer(Torrent torrent, string localId)
        {
            LocalId = localId;
            Torrent = torrent;

            LastActive = DateTime.UtcNow;
            IsPieceDownloaded = new bool[Torrent.pieceCount];
            IsBlockRequested = new bool[Torrent.pieceCount][];
            for (int i = 0; i < Torrent.pieceCount; i++)
                IsBlockRequested[i] = new bool[Torrent.GetBlockCount(i)];
        }

        #endregion

        #region Tcp

        public async void Connect()
        {
            await Task.Delay(10);
            if (TcpClient == null)
            {
                TcpClient = new TcpClient();
                try
                {
                    TcpClient.Client.Connect(IPEndPoint);
                    //await TcpClient.ConnectAsync(IPEndPoint.Address, IPEndPoint.Port);
                }
                catch (Exception e)
                {
                    Debug.WriteLine("disconnected at Connect() while trying to connect to: " + IPEndPoint.ToString());
                    Debug.WriteLine(e.StackTrace);
                    Disconnect();
                    return;
                }
            }

            Debug.WriteLine(this, "connected");
            stream = TcpClient.GetStream();

            SendHandshake();
            if (IsHandshakeReceived)
                SendBitfield(Torrent.isPieceVerified);

            readFromStream(stream);
            //stream.Read(streamBuffer, 0, Peer.bufferSize);
            //await stream.ReadAsync(streamBuffer, 0, Peer.bufferSize);
            //HandleRead(streamBuffer);

        }

        public void Disconnect()
        {
            if (!IsDisconnected)
            {
                IsDisconnected = true;
                Debug.WriteLine(this, "disconnected, down " + Downloaded + ", up " + Uploaded);
            }

            if (TcpClient != null)
                TcpClient.Dispose();

            if (Disconnected != null)
                Disconnected(this, new EventArgs());
        }

        private void SendBytes(byte[] bytes)
        {
            try
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Debug.WriteLine("Disconnected at SendBytes()");
                Debug.WriteLine(e.StackTrace);
                Disconnect();
            }
        }

        private async void readFromStream(NetworkStream stream)
        {
            await Task.Delay(5);
            while(!IsDisconnected)
            {
                int read = await stream.ReadAsync(streamBuffer, 0, Peer.bufferSize);
                HandleRead(streamBuffer, read);
            }
        }

        private void HandleRead(byte[] bytes, int readLength)
        {
            data.AddRange(streamBuffer.Take(readLength));
            int messageLength;
            while (
                data.Count > 0 && 
                (messageLength = GetMessageLength(data)) > 0 &&
                (messageLength = GetMessageLength(data)) < int.MaxValue)
            {
                if (data.Count < messageLength)
                    break;

                HandleMessage(data.Take(messageLength).ToArray());
                data = data.Skip(messageLength).ToList();

                messageLength = GetMessageLength(data);
            }

            //while (data.Count >= messageLength)
            //{
            //    HandleMessage(data.Take(messageLength).ToArray());
            //    data = data.Skip(messageLength).ToList();

            //    messageLength = GetMessageLength(data);
            //}
        }

        private void HandleRead(byte[] bytes)
        {
            int messageLength = GetMessageLength(data);
            Debug.WriteLine(" == message length: " + messageLength);
            data.AddRange(streamBuffer.Take(messageLength));

            
            while (data.Count >= messageLength)
            {
                HandleMessage(data.Take(messageLength).ToArray());
                data = data.Skip(messageLength).ToList();

                messageLength = GetMessageLength(data);
            }

            //try
            //{
            //    stream = TcpClient.GetStream();

            //    stream.Read(streamBuffer, 0, Peer.bufferSize);
            //    //await stream.ReadAsync(streamBuffer, 0, Peer.bufferSize);
            //    HandleRead(streamBuffer);
            //}
            //catch (Exception e)
            //{
            //    Debug.WriteLine("Disconnected at HandleRead");
            //    Debug.WriteLine(e.StackTrace);
            //    Disconnect();
            //}
        }

        private int GetMessageLength(List<byte> data)
        {
            if (!IsHandshakeReceived)
                return 68;

            if (data.Count < 4)
                return int.MaxValue;

            return EndianBitConverter.Big.ToInt32(data.ToArray(), 0) + 4;
        }

        #endregion

        #region Incoming Messages

        private MessageType GetMessageType(byte[] bytes)
        {
            if (!IsHandshakeReceived)
                return MessageType.Handshake;

            if (bytes.Length == 4 && EndianBitConverter.Big.ToInt32(bytes, 0) == 0)
                return MessageType.KeepAlive;

            if (bytes.Length > 4 && Enum.IsDefined(typeof(MessageType), (int)bytes[4]))
                return (MessageType)bytes[4];

            return MessageType.Unknown;
        }

        private void HandleMessage(byte[] bytes)
        {
            LastActive = DateTime.UtcNow;

            MessageType type = GetMessageType(bytes);

            if (type == MessageType.KeepAlive)
            {
                return;
            }

            if (type == MessageType.Unknown)
            {
                return;
            }
            else if (type == MessageType.Handshake)
            {
                byte[] hash;
                string id;
                if (DecodeHandshake(bytes, out hash, out id))
                {
                    HandleHandshake(hash, id);
                    return;
                }
            }
            else if (type == MessageType.KeepAlive && DecodeKeepAlive(bytes))
            {
                //HandleKeepAlive();
                return;
            }
            else if (type == MessageType.Choke && DecodeChoke(bytes))
            {
                HandleChoke();
                return;
            }
            else if (type == MessageType.Unchoke && DecodeUnchoke(bytes))
            {
                HandleUnchoke();
                return;
            }
            else if (type == MessageType.Interested && DecodeInterested(bytes))
            {
                HandleInterested();
                return;
            }
            else if (type == MessageType.NotInterested && DecodeNotInterested(bytes))
            {
                HandleNotInterested();
                return;
            }
            else if (type == MessageType.Have)
            {
                int index;
                if (DecodeHave(bytes, out index))
                {
                    HandleHave(index);
                    return;
                }
            }
            else if (type == MessageType.Bitfield)
            {
                bool[] isPieceDownloaded;
                if (DecodeBitfield(bytes, IsPieceDownloaded.Length, out isPieceDownloaded))
                {
                    HandleBitfield(isPieceDownloaded);
                    return;
                }
            }
            else if (type == MessageType.Request)
            {
                int index;
                int begin;
                int length;
                if (DecodeRequest(bytes, out index, out begin, out length))
                {
                    HandleRequest(index, begin, length);
                    return;
                }
            }
            else if (type == MessageType.Piece)
            {
                int index;
                int begin;
                byte[] data;
                if (DecodePiece(bytes, out index, out begin, out data))
                {
                    HandlePiece(index, begin, data);
                    return;
                }
            }
            else if (type == MessageType.Cancel)
            {
                int index;
                int begin;
                int length;
                if (DecodeCancel(bytes, out index, out begin, out length))
                {
                    HandleCancel(index, begin, length);
                    return;
                }
            }
            else if (type == MessageType.Port)
            {
                Debug.WriteLine(this, " <- port: " + String.Join("", bytes.Select(x => x.ToString("x2"))));
                return;
            }

            Debug.WriteLine(this, " Unhandled incoming message " + String.Join("", bytes.Select(x => x.ToString("x2"))));
            Disconnect();
        }

        private void HandleHandshake(byte[] hash, string id)
        {
            Debug.WriteLine(this, "<- handshake");

            if (!Torrent.infohash.SequenceEqual(hash))
            {
                Debug.WriteLine(this, "invalid handshake, incorrect torrent hash: expecting=" + Torrent.hexStringInfohash + ", received =" + String.Join("", hash.Select(x => x.ToString("x2"))));
                Disconnect();
                return;
            }

            Id = id;

            IsHandshakeReceived = true;
            SendBitfield(Torrent.isPieceVerified);
        }

        private async void HandleKeepAlive()
        {
            Debug.WriteLine(this, "<- keep alive");
            await Task.Delay(1);
        }

        private void HandleChoke()
        {
            Debug.WriteLine(this, "<- choke");
            IsChokeReceived = true;

            var handler = StateChanged;
            if (handler != null)
                handler(this, new EventArgs());
        }

        private void HandleUnchoke()
        {
            Debug.WriteLine(this, "<- unchoke");
            IsChokeReceived = false;

            var handler = StateChanged;
            if (handler != null)
                handler(this, new EventArgs());
        }

        private void HandleInterested()
        {
            Debug.WriteLine(this, "<- interested");
            IsInterestedReceived = true;

            var handler = StateChanged;
            if (handler != null)
                handler(this, new EventArgs());
        }

        private void HandleNotInterested()
        {
            Debug.WriteLine(this, "<- not interested");
            IsInterestedReceived = false;

            var handler = StateChanged;
            if (handler != null)
                handler(this, new EventArgs());
        }

        private void HandleHave(int index)
        {
            IsPieceDownloaded[index] = true;
            Debug.WriteLine(this, "<- have " + index + " - " + PiecesDownloadedCount + " available (" + PiecesDownloaded + ")");

            var handler = StateChanged;
            if (handler != null)
                handler(this, new EventArgs());
        }

        private void HandleBitfield(bool[] isPieceDownloaded)
        {
            for (int i = 0; i < Torrent.pieceCount; i++)
                IsPieceDownloaded[i] = IsPieceDownloaded[i] || isPieceDownloaded[i];

            Debug.WriteLine(this, "<- bitfield " + PiecesDownloadedCount + " available (" + PiecesDownloaded + ")");

            var handler = StateChanged;
            if (handler != null)
                handler(this, new EventArgs());
        }

        private void HandleRequest(int index, int begin, int length)
        {
            Debug.WriteLine(this, "<- request " + index + ", " + begin + ", " + length);

            var handler = BlockRequested;
            if (handler != null)
            {
                handler(this, new DataRequest()
                {
                    Peer = this,
                    Piece = index,
                    Begin = begin,
                    Length = length
                });
            }
        }

        private void HandlePiece(int index, int begin, byte[] data)
        {
            Debug.WriteLine(this, "<- piece " + index + ", " + begin + ", " + data.Length);
            Downloaded += data.Length;

            var handler = BlockReceived;
            if (handler != null)
            {
                handler(this, new DataPackage()
                {
                    Peer = this,
                    Piece = index,
                    Block = begin / Torrent.blockSize,
                    Data = data
                });
            }
        }

        private void HandleCancel(int index, int begin, int length)
        {
            Debug.WriteLine(this, " <- cancel");

            var handler = BlockCancelled;
            if (handler != null)
            {
                handler(this, new DataRequest()
                {
                    Peer = this,
                    Piece = index,
                    Begin = begin,
                    Length = length
                });
            }
        }

        private void HandlePort(int port)
        {
            Debug.WriteLine(this, "<- port");
        }

        #endregion

        #region Outgoing Messages

        private void SendHandshake()
        {
            if (IsHandshakeSent)
                return;

            Debug.WriteLine(this, "-> handshake");
            SendBytes(EncodeHandshake(Torrent.infohash, LocalId));
            IsHandshakeSent = true;
        }

        public void SendKeepAlive()
        {
            if (LastKeepAlive > DateTime.UtcNow.AddSeconds(-30))
                return;

            Debug.WriteLine(this, "-> keep alive");
            SendBytes(EncodeKeepAlive());
            LastKeepAlive = DateTime.UtcNow;
        }

        public void SendChoke()
        {
            if (IsChokeSent)
                return;

            Debug.WriteLine(this, "-> choke");
            SendBytes(EncodeChoke());
            IsChokeSent = true;
        }

        public void SendUnchoke()
        {
            if (!IsChokeSent)
                return;

            Debug.WriteLine(this, "-> unchoke");
            SendBytes(EncodeUnchoke());
            IsChokeSent = false;
        }

        public void SendInterested()
        {
            if (IsInterestedSent)
                return;

            Debug.WriteLine(this, "-> interested");
            SendBytes(EncodeInterested());
            IsInterestedSent = true;
        }

        public void SendNotInterested()
        {
            if (!IsInterestedSent)
                return;

            Debug.WriteLine(this, "-> not interested");
            SendBytes(EncodeNotInterested());
            IsInterestedSent = false;
        }

        public void SendHave(int index)
        {
            Debug.WriteLine(this, "-> have " + index);
            SendBytes(EncodeHave(index));
        }

        public void SendBitfield(bool[] isPieceDownloaded)
        {
            Debug.WriteLine(this, "-> bitfield " + String.Join("", isPieceDownloaded.Select(x => x ? 1 : 0)));
            SendBytes(EncodeBitfield(isPieceDownloaded));
        }

        public void SendRequest(int index, int begin, int length)
        {
            Debug.WriteLine(this, "-> request " + index + ", " + begin + ", " + length);
            SendBytes(EncodeRequest(index, begin, length));
        }

        public void SendPiece(int index, int begin, byte[] data)
        {
            Debug.WriteLine(this, "-> piece " + index + ", " + begin + ", " + data.Length);
            SendBytes(EncodePiece(index, begin, data));
            Uploaded += data.Length;
        }

        public void SendCancel(int index, int begin, int length)
        {
            Debug.WriteLine(this, "-> cancel");
            SendBytes(EncodeCancel(index, begin, length));
        }

        #endregion

        #region Encoding

        public static byte[] EncodeHandshake(byte[] hash, string id)
        {
            byte[] message = new byte[68];
            message[0] = 19;
            Buffer.BlockCopy(Encoding.UTF8.GetBytes("BitTorrent protocol"), 0, message, 1, 19);
            Buffer.BlockCopy(hash, 0, message, 28, 20);
            Buffer.BlockCopy(Encoding.UTF8.GetBytes(id), 0, message, 48, 20);

            return message;
        }

        public static byte[] EncodeKeepAlive()
        {
            return EndianBitConverter.Big.GetBytes(0);
        }

        public static byte[] EncodeChoke()
        {
            return EncodeState(MessageType.Choke);
        }

        public static byte[] EncodeUnchoke()
        {
            return EncodeState(MessageType.Unchoke);
        }

        public static byte[] EncodeInterested()
        {
            return EncodeState(MessageType.Interested);
        }

        public static byte[] EncodeNotInterested()
        {
            return EncodeState(MessageType.NotInterested);
        }

        public static byte[] EncodeState(MessageType type)
        {
            byte[] message = new byte[5];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(1), 0, message, 0, 4);
            message[4] = (byte)type;
            return message;
        }

        public static byte[] EncodeHave(int index)
        {
            byte[] message = new byte[9];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(5), 0, message, 0, 4);
            message[4] = (byte)MessageType.Have;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);

            return message;
        }

        public static byte[] EncodeBitfield(bool[] isPieceDownloaded)
        {
            int numPieces = isPieceDownloaded.Length;
            int numBytes = Convert.ToInt32(Math.Ceiling(numPieces / 8.0));
            int numBits = numBytes * 8;

            int length = numBytes + 1;

            byte[] message = new byte[length + 4];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 0, 4);
            message[4] = (byte)MessageType.Bitfield;

            bool[] downloaded = new bool[numBits];
            for (int i = 0; i < numPieces; i++)
                downloaded[i] = isPieceDownloaded[i];

           

            bool[] bitfield = new bool[downloaded.Length];
            downloaded.CopyTo(bitfield, 0);

            bool[] reversed = new bool[numBits];
            
            for (int i = 0; i < numBits; i++)
                reversed[i] = bitfield[numBits - i - 1];

            byte[] compact = new byte[numBytes];
            int bitIndex = 0, byteIndex = 0;
            for (int i = 0; i < numBytes; i++)
            {
                if (reversed[i])
                {
                    compact[byteIndex] |= (byte)(((byte)1) << bitIndex);
                }
                bitIndex++;
                if (bitIndex == 8)
                {
                    bitIndex = 0;
                    byteIndex++;
                }
            }

            compact.CopyTo(message, 5);

            return message;
        }

        public static byte[] EncodeRequest(int index, int begin, int length)
        {
            byte[] message = new byte[17];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(13), 0, message, 0, 4);
            message[4] = (byte)MessageType.Request;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(begin), 0, message, 9, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 13, 4);

            return message;
        }

        public static byte[] EncodePiece(int index, int begin, byte[] data)
        {
            int length = data.Length + 9;

            byte[] message = new byte[length + 4];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 0, 4);
            message[4] = (byte)MessageType.Piece;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(begin), 0, message, 9, 4);
            Buffer.BlockCopy(data, 0, message, 13, data.Length);

            return message;
        }

        public static byte[] EncodeCancel(int index, int begin, int length)
        {
            byte[] message = new byte[17];
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(13), 0, message, 0, 4);
            message[4] = (byte)MessageType.Cancel;
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(index), 0, message, 5, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(begin), 0, message, 9, 4);
            Buffer.BlockCopy(EndianBitConverter.Big.GetBytes(length), 0, message, 13, 4);

            return message;
        }

        #endregion

        #region Decoding

        public static bool DecodeHandshake(byte[] bytes, out byte[] hash, out string id)
        {
            hash = new byte[20];
            id = "";

            if (bytes.Length != 68 || bytes[0] != 19)
            {
                Debug.WriteLine("invalid handshake, must be of length 68 and first byte must equal 19");
                return false;
            }

            if (Encoding.UTF8.GetString(bytes.Skip(1).Take(19).ToArray()) != "BitTorrent protocol")
            {
                Debug.WriteLine("invalid handshake, protocol must equal \"BitTorrent protocol\"");
                return false;
            }

            // flags
            //byte[] flags = bytes.Skip(20).Take(8).ToArray();

            hash = bytes.Skip(28).Take(20).ToArray();

            id = Encoding.UTF8.GetString(bytes.Skip(48).Take(20).ToArray());

            return true;
        }

        public static bool DecodeKeepAlive(byte[] bytes)
        {
            if (bytes.Length != 4 || EndianBitConverter.Big.ToInt32(bytes, 0) != 0)
            {
                Debug.WriteLine("invalid keep alive");
                return false;
            }
            return true;
        }

        public static bool DecodeChoke(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.Choke);
        }

        public static bool DecodeUnchoke(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.Unchoke);
        }

        public static bool DecodeInterested(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.Interested);
        }

        public static bool DecodeNotInterested(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.NotInterested);
        }

        public static bool DecodeState(byte[] bytes, MessageType type)
        {
            if (bytes.Length != 5 || EndianBitConverter.Big.ToInt32(bytes, 0) != 1 || bytes[4] != (byte)type)
            {
                Debug.WriteLine("invalid " + Enum.GetName(typeof(MessageType), type));
                return false;
            }
            return true;
        }

        public static bool DecodeHave(byte[] bytes, out int index)
        {
            index = -1;

            if (bytes.Length != 9 || EndianBitConverter.Big.ToInt32(bytes, 0) != 5)
            {
                Debug.WriteLine("invalid have, first byte must equal 0x2");
                return false;
            }

            index = EndianBitConverter.Big.ToInt32(bytes, 5);

            return true;
        }

        public static bool DecodeBitfield(byte[] bytes, int pieces, out bool[] isPieceDownloaded)
        {
            isPieceDownloaded = new bool[pieces];

            int expectedLength = Convert.ToInt32(Math.Ceiling(pieces / 8.0)) + 1;

            if (bytes.Length != expectedLength + 4 || EndianBitConverter.Big.ToInt32(bytes, 0) != expectedLength)
            {
                Debug.WriteLine("invalid bitfield, first byte must equal " + expectedLength);
                return false;
            }

            BitArray bitfield = new BitArray(bytes.Skip(5).ToArray());

            for (int i = 0; i < pieces; i++)
                isPieceDownloaded[i] = bitfield[bitfield.Length - 1 - i];

            return true;
        }

        public static bool DecodeRequest(byte[] bytes, out int index, out int begin, out int length)
        {
            index = -1;
            begin = -1;
            length = -1;

            if (bytes.Length != 17 || EndianBitConverter.Big.ToInt32(bytes, 0) != 13)
            {
                Debug.WriteLine("invalid request message, must be of length 17");
                return false;
            }

            index = EndianBitConverter.Big.ToInt32(bytes, 5);
            begin = EndianBitConverter.Big.ToInt32(bytes, 9);
            length = EndianBitConverter.Big.ToInt32(bytes, 13);

            return true;
        }

        public static bool DecodePiece(byte[] bytes, out int index, out int begin, out byte[] data)
        {
            index = -1;
            begin = -1;
            data = new byte[0];

            if (bytes.Length < 13)
            {
                Debug.WriteLine("invalid piece message");
                return false;
            }

            int length = EndianBitConverter.Big.ToInt32(bytes, 0) - 9;
            index = EndianBitConverter.Big.ToInt32(bytes, 5);
            begin = EndianBitConverter.Big.ToInt32(bytes, 9);

            data = new byte[length];
            Buffer.BlockCopy(bytes, 13, data, 0, length);

            return true;
        }

        public static bool DecodeCancel(byte[] bytes, out int index, out int begin, out int length)
        {
            index = -1;
            begin = -1;
            length = -1;

            if (bytes.Length != 17 || EndianBitConverter.Big.ToInt32(bytes, 0) != 13)
            {
                Debug.WriteLine("invalid cancel message, must be of length 17");
                return false;
            }

            index = EndianBitConverter.Big.ToInt32(bytes, 5);
            begin = EndianBitConverter.Big.ToInt32(bytes, 9);
            length = EndianBitConverter.Big.ToInt32(bytes, 13);

            return true;
        }

        #endregion

        #region Helper

        public override string ToString()
        {
            return string.Format("[{0} ({1})]", IPEndPoint.ToString() , Id);
        }

        #endregion
    }
}
