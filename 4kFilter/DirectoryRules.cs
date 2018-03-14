using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter
{
    class DirectoryRules
    {
        public string TargetDirectoryId { get; private set; }
        public Dimensions MinDimensions { get; private set; }
        public Dimensions MaxDimensions { get; private set; }

        public DirectoryRules(string targetDirectoryId, Dimensions minDimensions, Dimensions maxDimensions)
        {
            this.TargetDirectoryId = targetDirectoryId;
            this.MinDimensions = minDimensions;
            this.MaxDimensions = maxDimensions;
        }

        public DirectoryRules(Google.Apis.Drive.v3.DriveService service, string filename, Dimensions minDimensions, Dimensions maxDimensions)
        {
            // TODO consider doing this elsewhere.
            Google.Apis.Drive.v3.FilesResource.ListRequest findFilenameRequest = service.Files.List();
            findFilenameRequest.Fields = "files(id)";
            findFilenameRequest.Q = "name = '" + filename + "'";
            IList<Google.Apis.Drive.v3.Data.File> filesList = findFilenameRequest.Execute().Files;

            TargetDirectoryId = filesList[0].Id;
            this.MinDimensions = minDimensions;
            this.MaxDimensions = maxDimensions;
        }

        public bool MatchesCriteria(Dimensions dimensions)
        {
            if (dimensions == Dimensions.None) return MinDimensions == Dimensions.None && MaxDimensions == Dimensions.None;
            return MinDimensions <= dimensions && dimensions < MaxDimensions;
        }
    }
}
