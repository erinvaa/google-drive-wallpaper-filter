using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter
{
    partial class ImageHandler
    {
        public class HeaderNotFoundException : Exception {
            public HeaderNotFoundException(string msg) : base(msg) { }
        }

        public enum FileType
        {
            UNKNOWN, PNG, JPG, GIF89, GIF87
        }

        private class HashNode<T>
        {
            private Dictionary<byte, HashNode<T>> children;
            private T _value;
            public bool HasValue { get; private set; }
            public T Value
            {
                get
                {
                    if (!HasValue) return default(T);
                    else return _value;
                }
                set
                {
                    _value = value;
                    HasValue = true;
                }
            }

            public HashNode()
            {
                children = new Dictionary<byte, HashNode<T>>();
                HasValue = false;
            }

            public HashNode<T> GetFirstKey(byte keyByte)
            {
                if (children.ContainsKey(keyByte))
                {
                    return children[keyByte];
                }
                else
                {
                    return null;
                }
            }

            public void Add(byte[] key, T value)
            {
                if (key.Length == 0)
                {
                    Value = value;
                    return;
                }

                if (!children.ContainsKey(key[0]))
                {
                    children[key[0]] = new HashNode<T>();
                }

                var subKey = new byte[key.Length - 1];
                Array.Copy(key, 1, subKey, 0, subKey.Length);

                children[key[0]].Add(subKey, value);
            }
        }

        private HashNode<FileType> _headerMap;
        private HashNode<FileType> HeaderMap
        {
            get
            {
                if (_headerMap == null)
                {
                    _headerMap = new HashNode<FileType>();
                    _headerMap.Add((new byte[] { 0xFF, 0xD8 }), FileType.JPG);
                    _headerMap.Add((new byte[] { 0x89, 0x50, 0x4E, 0X47, 0x0D, 0x0A, 0x1A, 0x0A }), FileType.PNG);
                    _headerMap.Add((new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }), FileType.GIF89);
                    _headerMap.Add((new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }), FileType.GIF87);
                }
                return _headerMap;
            }
        }

        public FileType InternalFileType { get; private set; }
        public long EndOfMetadataIndex { get; private set; }
                
        public ImageHandler()
        {
            InternalFileType = FileType.UNKNOWN;
        }

        public Dimensions ReadDimensions(Stream byteStream)
        {
            byteStream.Seek(0, SeekOrigin.Begin);

            List<HashNode<FileType>> relevantHashNodes = new List<HashNode<FileType>>();

            while (InternalFileType == FileType.UNKNOWN)
            {
                int readByte = byteStream.ReadByte();

                if (readByte == -1)
                {
                    throw new HeaderNotFoundException("Header not found in stream.");
                }

                HashNode<FileType> hashNode;
                for (int i = 0; i < relevantHashNodes.Count; i++)
                {
                    hashNode = relevantHashNodes[i].GetFirstKey((byte)readByte);
                    if (hashNode != null)
                    {
                        if (hashNode.HasValue)
                        {
                            InternalFileType = hashNode.Value;
                            break;
                        }
                        else
                        {
                            relevantHashNodes[i] = hashNode;
                        }
                    }
                    else
                    {
                        relevantHashNodes.RemoveAt(i);
                        i--;
                    }
                }
                hashNode = HeaderMap.GetFirstKey((byte)readByte);
                if (hashNode != null)
                {
                    relevantHashNodes.Add(hashNode);
                }
            }

            Dimensions dimensions = Dimensions.None;

            switch (InternalFileType)
            {
                case FileType.PNG:
                    dimensions = ReadPngDimensions(byteStream);
                    break;
                case FileType.JPG:
                    dimensions = ReadJpgDimensions(byteStream);
                    break;
                default:
                    Console.WriteLine("Found currently unsupported filetype");
                    break;
            }

            EndOfMetadataIndex = byteStream.Position;

            if (byteStream.ReadByte() == -1)
            {
                throw new HeaderNotFoundException("Header only partially present in stream.");
            }

            if (dimensions.Width < 0 || dimensions.Height < 0)
            {
                Console.WriteLine("Bad negative value found - Width: " + dimensions.Width + " Height: " + dimensions.Height);
            }

            return dimensions;
        }

        private static Dimensions ReadJpgDimensions(Stream byteStream)
        {
            int readByte = byteStream.ReadByte();
            while (readByte != -1)
            {
                if (readByte == 0xFF)
                {
                    readByte = byteStream.ReadByte();
                    if (readByte == 0xC0)
                    {
                        break;
                    }
                    else if (readByte == 0xC2)
                    {
                        break;
                    }
                    else
                    {
                        continue;
                    }
                }
                readByte = byteStream.ReadByte();
            }

            if (readByte == -1)
            {
                throw new HeaderNotFoundException("Got to end of stream without finding header information");
            }

            // Found correct header. Now skip ahead to height and width information.
            byteStream.Seek(3, SeekOrigin.Current);

            return new Dimensions
            {
                Height = byteStream.ReadByte() << 8 | byteStream.ReadByte(),
                Width = byteStream.ReadByte() << 8 | byteStream.ReadByte()
            };
        }

        private static Dimensions ReadPngDimensions(Stream byteStream)
        {
            int readByte = byteStream.ReadByte();
            char[] headerFlag = new char[] { 'I', 'H', 'D', 'R' };

            // Read until image header
            while (readByte != -1)
            {
                if (readByte == headerFlag[0])
                {
                    byte[] nextThreeByte = new byte[3];
                    int bytesRead = byteStream.Read(nextThreeByte, 0, 3);
                    if (bytesRead < 3)
                    {
                        throw new HeaderNotFoundException("Got to end of file without finding header information");
                    }
                    if (nextThreeByte[0] == headerFlag[1] &&
                        nextThreeByte[1] == headerFlag[2] &&
                        nextThreeByte[2] == headerFlag[3])
                    {
                        break;
                    }
                    else
                    {
                        byteStream.Seek(-3, SeekOrigin.Current);
                        
                    }
                }
                readByte = byteStream.ReadByte();
            }

            if (readByte == -1)
            {
                throw new HeaderNotFoundException("Got to end of file without finding header information");
            }

            // Found correct header. Now skip ahead to height and width information.
            return new Dimensions
            {
                Width = (byteStream.ReadByte() << 24) | (byteStream.ReadByte() << 16) | (byteStream.ReadByte() << 8) | byteStream.ReadByte(),
                Height = (byteStream.ReadByte() << 24) | (byteStream.ReadByte() << 16) | (byteStream.ReadByte() << 8) | byteStream.ReadByte()
            };
        }
    }
}
