using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _4kFilter
{
    abstract class ImageFilter
    {
        public string TargetDirectoryId { get; private set; }

        protected ImageFilter(string targetDirectoryId)
        {
            TargetDirectoryId = targetDirectoryId;
        }

        protected ImageFilter(Google.Apis.Drive.v3.DriveService service, string filename)
        {
            Google.Apis.Drive.v3.FilesResource.ListRequest findFilenameRequest = service.Files.List();
            findFilenameRequest.Fields = "files(id)";
            findFilenameRequest.Q = "name = '" + filename + "'";
            IList<Google.Apis.Drive.v3.Data.File> filesList = findFilenameRequest.Execute().Files;

            TargetDirectoryId = filesList[0].Id;
        }

        public abstract bool MatchesCriteria(Dimensions dimensions);
    }
}
