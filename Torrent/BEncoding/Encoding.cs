using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SimpleTorrentUWP.Torrent.Extensions;

namespace SimpleTorrentUWP.Torrent.BEncoding
{

    public static class Encoding
    {
        private static byte DictionaryStart         = System.Text.Encoding.UTF8.GetBytes("d")[0]; // 100
        private static byte DictionaryEnd           = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ListStart               = System.Text.Encoding.UTF8.GetBytes("l")[0]; // 108
        private static byte ListEnd                 = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte NumberStart             = System.Text.Encoding.UTF8.GetBytes("i")[0]; // 105
        private static byte NumberEnd               = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ByteArrayDivider        = System.Text.Encoding.UTF8.GetBytes(":")[0]; //  58

        public static byte[] Encode(object obj)
        {
            MemoryStream buffer = new MemoryStream();

            EncodeNextObject(buffer, obj);

            return buffer.ToArray();
        }

        public static void EncodeToFile(object obj, string path)
        {
            File.WriteAllBytes(path, Encode(obj));
        }

        private static void EncodeNextObject(MemoryStream buffer, object obj)
        {
            if (obj is byte[])
                EncodeByteArray(buffer, (byte[])obj);
            else if (obj is string)
                EncodeString(buffer, (string)obj);
            else if (obj is long)
                EncodeNumber(buffer, (long)obj);
            else if (obj.GetType() == typeof(List<object>))
                EncodeList(buffer, (List<object>)obj);
            else if (obj.GetType() == typeof(Dictionary<string, object>))
                EncodeDictionary(buffer, (Dictionary<string, object>)obj);
            else
                throw new Exception("unable to encode type " + obj.GetType());
        }

        private static void EncodeNumber(MemoryStream buffer, long input)
        {
            buffer.Append(NumberStart);
            buffer.Append(System.Text.Encoding.UTF8.GetBytes(Convert.ToString(input)));
            buffer.Append(NumberEnd);
        }

        private static void EncodeByteArray(MemoryStream buffer, byte[] body)
        {
            buffer.Append(System.Text.Encoding.UTF8.GetBytes(Convert.ToString(body.Length)));
            buffer.Append(ByteArrayDivider);
            buffer.Append(body);
        }

        private static  void EncodeString(MemoryStream buffer, string input)
        {
            EncodeByteArray(buffer, System.Text.Encoding.UTF8.GetBytes(input));
        }

        private static void EncodeList(MemoryStream buffer, List<object> input)
        {
            buffer.Append(ListStart);
            foreach (var item in input)
                EncodeNextObject(buffer, item);
            buffer.Append(ListEnd);
        }

        private static void EncodeDictionary(MemoryStream buffer, Dictionary<string,object> input)
        {
            buffer.Append(DictionaryStart);

            // make sure the dictionary is sorted
            var sortedKeys = input.Keys.ToList().OrderBy(x => BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(x)));

            foreach(var key in sortedKeys)
            {
                EncodeString(buffer, key);
                EncodeNextObject(buffer, input[key]);
            }
            buffer.Append(DictionaryEnd);
        }


    }
}
