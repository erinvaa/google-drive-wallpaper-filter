using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter
{
    class DateTimeEncoder
    {
        public static string DateTimeNowEncoded()
        {
            return EncodeDateTimeAsString(DateTime.Now);
        }

        // Convert an object to a byte array
        public static string EncodeDateTimeAsString(DateTime dateTime)
        {
            long binary = dateTime.ToBinary();
            return binary.ToString("X");

        }

        public static DateTime DecodeStringAsDateTime(string inString)
        {
            long binary = Convert.ToInt64(inString, 16);
            return DateTime.FromBinary(binary);
        }
    }
}
