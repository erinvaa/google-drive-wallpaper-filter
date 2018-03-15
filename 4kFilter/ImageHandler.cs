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

        public static Dimensions ReadJpgDimensions(Stream byteStream)
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

            Dimensions dimensions = new Dimensions();

            dimensions.Height = byteStream.ReadByte() << 8;
            dimensions.Height += byteStream.ReadByte();
            dimensions.Width = byteStream.ReadByte() << 8;
            dimensions.Width += byteStream.ReadByte();

            if (byteStream.ReadByte() == -1)
            {
                throw new HeaderNotFoundException("Header only partially present in stream.");
            }

            //Console.WriteLine("Width: " + width + " Height: " + height);

            if (dimensions.Width < 0 || dimensions.Height < 0)
            {
                Console.WriteLine("Bad negative value found - Width: " + dimensions.Width + " Height: " + 
                    dimensions.Height + " Flag:" + readByte.ToString("X"));
            }

            return dimensions;
        }

        public static Dimensions ReadPngDimensions(Stream byteStream)
        {
            byteStream.Seek(0, SeekOrigin.Begin);
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
            Dimensions dimensions = new Dimensions();

            dimensions.Width = (byteStream.ReadByte() << 24) + (byteStream.ReadByte() << 16) + (byteStream.ReadByte() << 8) + byteStream.ReadByte();
            dimensions.Height = (byteStream.ReadByte() << 24) + (byteStream.ReadByte() << 16) + (byteStream.ReadByte() << 8) + byteStream.ReadByte();

            if (dimensions.Width < 0 || dimensions.Height < 0)
            {
                Console.WriteLine("Bad negative value found - Width: " + dimensions.Width + " Height: " + dimensions.Height);
            }

            return dimensions;
        }
    }
}
