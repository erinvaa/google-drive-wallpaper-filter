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
            //byte readByte = 0x00;
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
                // TODO be unhappy; probably throw an error
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
    }
}
