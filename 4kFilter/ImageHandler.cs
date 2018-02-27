using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter
{
    class ImageHandler
    {
        static int minWidth = 3840;
        static int minHeight = 2160;

        public class HeaderNotFoundException : Exception {
            public HeaderNotFoundException(string msg) : base(msg) { }
        }

        public static bool IsBigJpegFromHeader(Stream byteStream)
        {
            byteStream.Seek(0, SeekOrigin.Begin);
            int readByte = byteStream.ReadByte();
            while (readByte != -1)
            {
                if (readByte == 0xFF)
                {
                    readByte = byteStream.ReadByte();
                    if (readByte == 0xC0)
                    {
                        break;
                    } else if (readByte == 0xC2)
                    {
                        break;
                    } else
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

            int height = byteStream.ReadByte() << 8;
            height += byteStream.ReadByte();
            int width = byteStream.ReadByte() << 8;
            width += byteStream.ReadByte();

            if (byteStream.ReadByte() == -1)
            {
                throw new HeaderNotFoundException("Header only partially present in stream.");
            }

            //Console.WriteLine("Width: " + width + " Height: " + height);

            if (width < 0 || height < 0)
            {
                Console.WriteLine("Bad negative value found - Width: " + width + " Height: " + height + " Flag:" + readByte.ToString("X"));
            }

            return width >= minWidth && height >= minHeight;
        }

        public static bool IsBigPngFromHeader(Stream byteStream)
        {
            byteStream.Seek(0, SeekOrigin.Begin);
            int readByte = byteStream.ReadByte();
            char[] headerFlag = new char[] { 'I', 'H', 'D', 'R' };

            // Read until image header
            while (readByte != -1)
            {
                if (readByte == headerFlag[0])
                {
                    if (byteStream.ReadByte() == headerFlag[1] & 
                        byteStream.ReadByte() == headerFlag[2] &
                        byteStream.ReadByte() == headerFlag[3])
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
            int width = (byteStream.ReadByte() << 24) + (byteStream.ReadByte() << 16) + (byteStream.ReadByte() << 8) + byteStream.ReadByte();
            int height = (byteStream.ReadByte() << 24) + (byteStream.ReadByte() << 16) + (byteStream.ReadByte() << 8) + byteStream.ReadByte();

            //Console.WriteLine("Width: " + width + " Height: " + height);

            return width >= minWidth && height >= minHeight;
        }
    }
}
