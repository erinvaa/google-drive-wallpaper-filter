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
        public static void ParseJpegResolutionFromHeader (Stream byteStream)
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
                    } else if (readByte == 0xC2)
                    {
                        // TODO - I want to do some logging or something here, as I want an example of a 
                        // file that uses this type of header
                    } else
                    {
                        continue;
                    }
                }
                readByte = byteStream.ReadByte();
            }

            if (readByte == -1)
            {
                // TODO be unhappy; probably throw an exception
                return;
            }

            // Found correct header. Now skip ahead to height and width information.
            byteStream.Seek(3, SeekOrigin.Current);

            int height = byteStream.ReadByte() << 8;
            height += byteStream.ReadByte();
            int width = byteStream.ReadByte() << 8;
            width += byteStream.ReadByte();

            Console.WriteLine("Width: " + width + " Height: " + height);
        }

        public static void ParsePngResolutionFromHeader(Stream byteStream)
        {
            int readByte = byteStream.ReadByte();
            char[] headerFlag = new char[] { 'I', 'H', 'D', 'R' };

            // Read until image header
            while (readByte != -1)
            {
                if (readByte == headerFlag[0])
                {
                    if (byteStream.ReadByte() == headerFlag[1] && 
                        byteStream.ReadByte() == headerFlag[2] &&
                        byteStream.ReadByte() == headerFlag[3])
                    {
                        break;
                    } else
                    {
                        byteStream.Seek(-3, SeekOrigin.Current);
                    }
                }
                readByte = byteStream.ReadByte();
            }

            if (readByte == -1)
            {
                // TODO be unhappy; probably throw an exception
                return;
            }

            // Found correct header. Now skip ahead to height and width information.
            int width = (byteStream.ReadByte() << 24) + (byteStream.ReadByte() << 16) + (byteStream.ReadByte() << 8) + byteStream.ReadByte();
            int height = (byteStream.ReadByte() << 24) + (byteStream.ReadByte() << 16) + (byteStream.ReadByte() << 8) + byteStream.ReadByte();
            Console.WriteLine("Width: " + width + " Height: " + height);
        }
    }
}
