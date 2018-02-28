using Google.Apis.Auth.OAuth2;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace _4kFilter
{
    class Program
    {
        // If modifying these scopes, delete your previously saved credentials
        // at ~/.credentials/drive-dotnet-quickstart.json
        static string[] Scopes = { DriveService.Scope.Drive };
        static string ApplicationName = "4K Image Filter";

        private static string wallpaperFolderName = "Wallpapers";
        private static string destination4kFolderName = "Resolution: 4K";
        private static string destinationHdFolderName = "Resolution: HD";
        private static string destinationWqhdFolderName = "Resolution: WQHD";


        private static SemaphoreSlim runningTasks;
        private static ReaderWriterLock imageInformationLock = new ReaderWriterLock();
        private static ISet<ImageInformation> imageInformation;
        private static ManualResetEvent resetEvent;
        private static int queuedThreads = 0;
        private static int maxConcurrentThreads = 20;
        private static Random random = new Random();
        private static ImageProcessingTaskManager imageProcessingTaskManager;
        private static int numberOfBytesToRead = 75;

        private static Dictionary<byte, Dimensions> dimensionsMap;
        private static Dictionary<byte, string> folderIdMap;

        class ImageInformation
        {
            public string Id { get; set; }
            public string Name { get; set; }

            public List<byte> MatchingResolutions { get; set; }

            public ImageInformation()
            {
                MatchingResolutions = new List<byte>();
            }

            public override bool Equals(object obj)
            {
                var information = obj as ImageInformation;
                return information != null &&
                       Id == information.Id;
            }

            public override int GetHashCode()
            {
                return 2108858624 + EqualityComparer<string>.Default.GetHashCode(Id);
            }
        }

        class ImageInformationEqualityComparer : EqualityComparer<ImageInformation>
        {
            public override bool Equals(ImageInformation x, ImageInformation y)
            {
                return x.Id == y.Id;
            }

            public override int GetHashCode(ImageInformation obj)
            {
                return 2108858624 + EqualityComparer<string>.Default.GetHashCode(obj.Id);
            }
        }

        // This is writen so it should be possible to replace with user input (with some work)
        private static void PopulateDimenionInformation(DriveService service)
        {
            dimensionsMap = new Dictionary<byte, Dimensions>();
            folderIdMap = new Dictionary<byte, string>();

            dimensionsMap.Add(0, new Dimensions(3840, 2160));
            folderIdMap.Add(0, FindFileWithName(service, destination4kFolderName));

            dimensionsMap.Add(1, new Dimensions(2560, 1440));
            folderIdMap.Add(1, FindFileWithName(service, destinationWqhdFolderName));

            dimensionsMap.Add(2, new Dimensions(1920, 1200));
            folderIdMap.Add(2, FindFileWithName(service, destinationHdFolderName));
        }

        static void Main(string[] args)
        {
            UserCredential credential;

            using (var stream =
                new FileStream("client_secret.json", FileMode.Open, FileAccess.Read))
            {
                string credPath = System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.Personal);
                credPath = Path.Combine(credPath, ".credentials/drive-4k-filter.json");

                credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    Scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                Console.WriteLine("Credential file saved to: " + credPath);
            }

            // Create Drive API service.
            var service = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            PopulateDimenionInformation(service);


            string wallpaperId = FindFileWithName(service, wallpaperFolderName);

            // Get ready for multithreading.
            imageInformationLock.AcquireWriterLock(1000);
            imageInformation = new HashSet<ImageInformation>(new ImageInformationEqualityComparer());
            imageInformationLock.ReleaseLock();
            imageProcessingTaskManager = new ImageProcessingTaskManager();

            resetEvent = new ManualResetEvent(false);
            runningTasks = new SemaphoreSlim(maxConcurrentThreads, maxConcurrentThreads);

            Task rootTask = new Task(() => FindNestedImagesAboveResolution(service, wallpaperId));
            queuedThreads++;
            rootTask.Start();

            Task loggerHelper = new Task(AsyncLogger);
            loggerHelper.Start();

            // Wait for all the logic to finish.
            resetEvent.WaitOne();

            lastImageAcquired = DateTime.Now;
            Console.WriteLine("Done scanning for files; starting to analyse images.");
            imageProcessingTaskManager.Start();
            imageProcessingTaskManager.StopWhenTasksCompleted = true; // Tell the processor no new tasks are coming.
            imageProcessingTaskManager.StoppedEvent.WaitOne();
            loggerRunning = false;

            Console.WriteLine();
            Console.WriteLine("Size: " + imageInformation.Count);
            Console.WriteLine();

            // There SHOULDN'T be anything else using this lock by now... but may as well be safe about it.
            imageInformationLock.AcquireReaderLock(1000);
            // We'll try to be smarter about this later... for now just seeing if it works
            foreach (var information in imageInformation)
            {
                var updateRequest = service.Files.Update(new Google.Apis.Drive.v3.Data.File(), information.Id);
                //updateRequest.Fields = "id, parents";
                List<string> newParents = new List<string>();
                foreach (var i in information.MatchingResolutions)
                {
                    string parentId = folderIdMap[i];
                    newParents.Add(parentId);
                }
                updateRequest.AddParents = String.Join(",", newParents);
                imagesMoved++;
                // TODO collect these up into batches and send a batch request instead.
                updateRequest.ExecuteAsync();
            }
            imageInformationLock.ReleaseReaderLock();

            loggerRunning = false;

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Done moving files into new folder");
            Console.ReadKey();
        }

        private static DateTime lastImageAcquired = DateTime.MinValue;
        private static bool loggerRunning = true;
        private static int imagesMoved = 0;
        private static void AsyncLogger()
        {
            int totalImages = -1;
            int totalFoundImages = -1;
            while (loggerRunning)
            {
                if (imagesMoved > 0)
                {
                    if (totalFoundImages < 0)
                    {
                        imageInformationLock.AcquireReaderLock(1000);
                        totalFoundImages = imageInformation.Count;
                        imageInformationLock.ReleaseReaderLock();
                    }
                    Console.WriteLine("Moved " + imagesMoved + "/" + totalFoundImages + " images.");
                }
                else if (lastImageAcquired != DateTime.MinValue)
                {
                    if (totalImages < 0)
                    {
                        totalImages = imageProcessingTaskManager.ImageCount;
                    }
                    double imagesPerSecond = (double)(totalImages - imageProcessingTaskManager.ImageCount) / (DateTime.Now - lastImageAcquired).TotalSeconds;
                    Console.WriteLine(imageProcessingTaskManager.ImageCount + " images to process at a rate of " +
                        imagesPerSecond.ToString("0.000") + " images per second. Threads:" + imageProcessingTaskManager.RunningThreads);
                }
                else
                {
                    Console.WriteLine(queuedThreads + " threads queued and " + (maxConcurrentThreads - runningTasks.CurrentCount) + " running and " +
                        imageProcessingTaskManager.ImageCount + " images to process");
                }
                Thread.Sleep(500);
            }
        }

        private static void FindNestedImagesAboveResolution(DriveService service, string parentId, string pageToken = null)
        {
            runningTasks.Wait();
            FilesResource.ListRequest listRequest = service.Files.List();
            listRequest.PageSize = 1000;
            listRequest.Q = "'" + parentId + "' in parents";
            listRequest.PageToken = pageToken;
            listRequest.Fields = "nextPageToken, files(id, name, mimeType, fileExtension)";

            // List files
            FileList requestResult = null;
            int failureCount = 0;
            bool success = false;
            while (!success)
            {
                try
                {
                    requestResult = listRequest.Execute();
                    success = true;
                }
                catch (Google.GoogleApiException ex)
                {
                    failureCount++;
                    int waitTime = CalculateWaitTime(failureCount);
                    Console.WriteLine("Exception: " + ex.Message);
                    Console.WriteLine("This thread has failed " + failureCount + " times");
                    Console.WriteLine("Sleeping for  " + waitTime + " milliseconds");
                    Thread.Sleep(waitTime);
                }
                catch (TaskCanceledException)
                {
                    // Note: I'm a little worried that there might be an issue with deadlocks here.
                    // If there are still problems, I should investigate that
                    Console.WriteLine("Task cancelled... trying again?");
                    failureCount++;
                    int waitTime = CalculateWaitTime(failureCount);
                    Thread.Sleep(waitTime);

                }
            }
            IList<Google.Apis.Drive.v3.Data.File> files = requestResult.Files;

            if (requestResult.NextPageToken != null)
            {
                Task nextPageTask = new Task(() => FindNestedImagesAboveResolution(service, parentId, requestResult.NextPageToken));
                queuedThreads++;
                nextPageTask.Start();
            }

            foreach (var file in files)
            {
                if (file.MimeType == "application/vnd.google-apps.folder")
                {
                    Task subDirectoryTask = new Task(() => FindNestedImagesAboveResolution(service, file.Id));
                    queuedThreads++;
                    subDirectoryTask.Start();
                }
                else if (file.FileExtension == "png" || file.FileExtension == "jpeg" || file.FileExtension == "jpg")
                {
                    imageProcessingTaskManager.AddAction(() => CategorizeImage(service, file));
                }
            }
            runningTasks.Release();
            if (--queuedThreads == 0)
            {
                resetEvent.Set();
            }
        }

        private static void CategorizeImage(DriveService service, Google.Apis.Drive.v3.Data.File file)
        {
            int failureCount = 0;
            bool success = false;
            Dimensions dimensions = Dimensions.None;
            while (!success)
            {
                MemoryStream stream = getFileHeader(service, file.Id, failureCount);
                if (stream.Length == 0)
                {
                    Console.WriteLine("Header not present in file.");
                    break;
                }
                try
                {
                    switch (file.FileExtension)
                    {
                        case "png":
                            dimensions = ImageHandler.ReadPngDimensions(stream);
                            break;
                        case "jpeg":
                        case "jpg":
                            dimensions = ImageHandler.ReadJpgDimensions(stream);
                            break;
                        default:
                            break;
                    }
                    success = true;
                }
                catch (ImageHandler.HeaderNotFoundException)
                {
                    failureCount++;
                }
            }

            if (dimensions != Dimensions.None)
            {
                ImageInformation information = new ImageInformation
                {
                    Id = file.Id,
                    Name = file.Name
                };

                foreach (KeyValuePair<byte, Dimensions> entry in dimensionsMap)
                {
                    if (dimensions >= entry.Value)
                    {
                        information.MatchingResolutions.Add(entry.Key);
                    }
                }

                imageInformationLock.AcquireWriterLock(1000);
                imageInformation.Add(information);
                imageInformationLock.ReleaseLock();
            }
        }

        private static MemoryStream getFileHeader(DriveService service, string fileId, int attemptNumber = 0)
        {
            var request = service.Files.Get(fileId);
            // TODO investigate using download async
            var stream = new MemoryStream();

            long lowerByte = attemptNumber == 0 ? 0 : (long)numberOfBytesToRead * ((long)1 << (attemptNumber - 1));
            // The 10 byte overlap between requests is so that the header isn't split between two requests.
            long upperByte = (long)numberOfBytesToRead * ((long)1 << attemptNumber) + 10;

            // TODO refactor this out (and combine with other version)
            int failureCount = 0;
            bool success = false;
            while (!success)
            {
                try
                {
                    request.DownloadRange(stream, new System.Net.Http.Headers.RangeHeaderValue(lowerByte, upperByte));
                    success = true;
                }
                catch (Google.GoogleApiException ex)
                {
                    failureCount++;
                    int waitTime = CalculateWaitTime(failureCount);
                    Console.WriteLine("Exception: " + ex.Message);
                    Console.WriteLine("This thread has failed " + failureCount + " times");
                    Console.WriteLine("Sleeping for  " + waitTime + " milliseconds");
                    Thread.Sleep(waitTime);
                }
            }

            //Thread.Sleep(10000);

            return stream;
        }

        private static int slotTime = 50;
        private static int CalculateWaitTime(int failureCount)
        {
            // 2 ^ failure count
            return (int)random.Next(1 << failureCount) * slotTime;
        }

        private static string FindFileWithName(DriveService service, string filename)
        {
            FilesResource.ListRequest findFilenameRequest = service.Files.List();
            findFilenameRequest.Fields = "files(id)";
            findFilenameRequest.Q = "name = '" + filename + "'";
            IList<Google.Apis.Drive.v3.Data.File> filesList = findFilenameRequest.Execute()
                .Files;

            return filesList[0].Id;
        }
    }
}
