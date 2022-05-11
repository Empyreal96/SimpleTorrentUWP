using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SimpleTorrentUWP.Torrent.BEncoding
{
    public static class Decoding
    {
        private static byte DictionaryStart     = System.Text.Encoding.UTF8.GetBytes("d")[0]; // 100
        private static byte DictionaryEnd       = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ListStart           = System.Text.Encoding.UTF8.GetBytes("l")[0]; // 108
        private static byte ListEnd             = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte NumberStart         = System.Text.Encoding.UTF8.GetBytes("i")[0]; // 105
        private static byte NumberEnd           = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ByteArrayDivider    = System.Text.Encoding.UTF8.GetBytes(":")[0]; //  58

        public static object Decode(byte[] bytes)
        {
            IEnumerator<byte> enumerator = ((IEnumerable<byte>)bytes).GetEnumerator();
            enumerator.MoveNext();
            return DecodeNextObject(enumerator);
        }

        private static object DecodeNextObject(IEnumerator<byte> enumerator)
        {
            if (enumerator.Current == DictionaryStart)
                return DecodeDictionary(enumerator);

            if (enumerator.Current == ListStart)
                return DecodeList(enumerator);

            if (enumerator.Current == NumberStart)
                return DecodeNumber(enumerator);

            return DecodeByteArray(enumerator);
        }

        /**
         * decode a file with the given path
         **/
        public async static Task<object> DecodeFile(StorageFile path)
        {
            StorageFile file = path;
            byte[] bytes = null;

            using (IRandomAccessStreamWithContentType stream = await file.OpenReadAsync())
            {
                bytes = new byte[stream.Size];
                using (DataReader reader = new DataReader(stream))
                {
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(bytes);
                }
            }

            return Decoding.Decode(bytes);

            //if (!File.Exists(path))
            //    throw new FileNotFoundException("unable to find file: " + path);

            //byte[] bytes = File.ReadAllBytes(path);

            //return Decoding.Decode(bytes);
        }

        /**
         * decode a number and read it's value from the .torrent file
         **/
        private static long DecodeNumber(IEnumerator<byte> enumerator)
        {
            List<byte> bytes = new List<byte>();

            // loop through the enumerator until end flag 'e' is found
            while (enumerator.MoveNext())
            {
                if (enumerator.Current == NumberEnd)
                    break;

                bytes.Add(enumerator.Current);
            }

            string numberAsString = System.Text.Encoding.UTF8.GetString(bytes.ToArray());

            return Int64.Parse(numberAsString);
        }

        /**
         * decode an array from the .torrent file
         **/
        private static byte[] DecodeByteArray(IEnumerator<byte> enumerator)
        {
            List<byte> bytes = new List<byte>();

            do
            {
                if (enumerator.Current == ByteArrayDivider)
                    break;

                bytes.Add(enumerator.Current);
            }
            while (enumerator.MoveNext());

            string lengthString = System.Text.Encoding.UTF8.GetString(bytes.ToArray());

            int length;
            if (!Int32.TryParse(lengthString, out length))
                throw new Exception("unable to parse length of the byte array");

            byte[] readBytes = new byte[length];

            for (int i = 0; i < length; i++)
            {
                enumerator.MoveNext();
                readBytes[i] = enumerator.Current;
            }

            return readBytes;
        }

        private static List<object> DecodeList(IEnumerator<byte> enumerator)
        {
            List<object> list = new List<object>();

            while (enumerator.MoveNext())
            {
                if (enumerator.Current == ListEnd)
                    break;

                list.Add(DecodeNextObject(enumerator));
            }

            return list;
        }

        private static Dictionary<string, object> DecodeDictionary(IEnumerator<byte> enumerator)
        {
            Dictionary<string, object> dictionary = new Dictionary<string, object>();
            List<string> keys = new List<string>();

            while (enumerator.MoveNext())
            {
                if (enumerator.Current == DictionaryEnd)
                    break;

                string key = System.Text.Encoding.UTF8.GetString(DecodeByteArray(enumerator));
                enumerator.MoveNext();
                object val = DecodeNextObject(enumerator);

                keys.Add(key);
                dictionary.Add(key, val);
            }

            // verify that the incoming dictionary is sorted correctly
            // this is required to be able to create an identical encoding
            var sortedKeys = keys.OrderBy(x => BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(x)));

            if (!keys.SequenceEqual(sortedKeys))
                throw new Exception("error loading dictionary keys not sorted");

            return dictionary;   
        }

    }
}
