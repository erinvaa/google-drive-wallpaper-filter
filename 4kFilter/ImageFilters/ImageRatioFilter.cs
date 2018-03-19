using Google.Apis.Drive.v3;

namespace _4kFilter.ImageFilters
{
    class ImageRatioFilter : ImageFilter
    {
        public double? MinRatio { get; private set; }
        public double? MaxRatio { get; private set; }

        public ImageRatioFilter(string targetDirectoryId, double ratio, double tolerance=0) : base(targetDirectoryId)
        {
            MinRatio = ratio * (1 - tolerance);
            MaxRatio = ratio * (1 + tolerance);
        }

        public ImageRatioFilter(string targetDirectoryId, double? minRatio, double? maxRatio) : base(targetDirectoryId)
        {
            MinRatio = minRatio;
            MaxRatio = maxRatio;
        }

        public ImageRatioFilter(DriveService service, string filename, double ratio, double tolerance = 0) : base(service, filename)
        {
            MinRatio = ratio * (1 - tolerance);
            MaxRatio = ratio * (1 + tolerance);
        }

        public ImageRatioFilter(DriveService service, string filename, double? minRatio, double? maxRatio) : base(service, filename)
        {
            MinRatio = minRatio;
            MaxRatio = maxRatio;
        }

        public override bool MatchesCriteria(Dimensions dimensions)
        {
            return (MinRatio == null || MinRatio <= dimensions.Ratio) && (MaxRatio == null || dimensions.Ratio <= MaxRatio);
        }
    }
}
