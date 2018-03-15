using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter
{
    class ImageSizeFilter : ImageFilter
    {
        public Dimensions MinDimensions { get; private set; }
        public Dimensions MaxDimensions { get; private set; }

        public ImageSizeFilter(string targetDirectoryId, Dimensions minDimensions, Dimensions maxDimensions) : base (targetDirectoryId)
        {
            this.MinDimensions = minDimensions;
            this.MaxDimensions = maxDimensions;
        }

        public ImageSizeFilter(Google.Apis.Drive.v3.DriveService service, string filename,
            Dimensions minDimensions, Dimensions maxDimensions) : base(service, filename)
        {
            this.MinDimensions = minDimensions;
            this.MaxDimensions = maxDimensions;
        }

        public override bool MatchesCriteria(Dimensions dimensions)
        {
            return MinDimensions <= dimensions && dimensions < MaxDimensions;
        }
    }
}
