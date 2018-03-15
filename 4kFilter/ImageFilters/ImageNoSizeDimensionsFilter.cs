using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter.ImageFilters
{
    class ImageNoSizeDimensionsFilter : ImageFilter
    {
        public ImageNoSizeDimensionsFilter(string targetDirectoryId) : base(targetDirectoryId) { }

        public ImageNoSizeDimensionsFilter(Google.Apis.Drive.v3.DriveService service, string filename) : base(service, filename) { }

        public override bool MatchesCriteria(Dimensions dimensions)
        {
            return dimensions == Dimensions.None;
        }
    }
}
