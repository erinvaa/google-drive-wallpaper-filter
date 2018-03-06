using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace _4kFilter
{
    class Program
    {
        private const string lastUpdatedKey = "LastUpdated";

        // If modifying these scopes, delete your previously saved credentials
        // at ~/.credentials/drive-dotnet-quickstart.json
        static string[] Scopes = { DriveService.Scope.Drive };
        static string ApplicationName = "4K Image Filter";

        //private static string wallpaperFolderName = "Wallpapers";
        private static string wallpaperFolderName = "Resolution: 4K";
        private static string destination4kFolderName = "Resolution: 4K";
        private static string destinationHdFolderName = "Resolution: HD";
        private static string destinationWqhdFolderName = "Resolution: WQHD";


        private static SemaphoreSlim runningTasks;
        private static ReaderWriterLock foundImagesLock = new ReaderWriterLock();
        private static ISet<string> foundImages;
        private static ManualResetEvent resetEvent;
        private static int queuedThreads = 0;
        private static int maxConcurrentThreads = 20;
        private static Random random = new Random();
        private static ImageProcessingTaskManager imageProcessingTaskManager;
        private static int numberOfBytesToRead = 75;

        private static Dictionary<byte, Dimensions> dimensionsMap;
        private static Dictionary<byte, string> folderIdMap;

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

        //static void Main(string[] args)
        //{
        //    DateTime dateTime = DateTime.Now;

        //    string serializedDateTime = DateTimeEncoder.EncodeDateTimeAsString(dateTime);

        //    DateTime dateTime2 = DateTimeEncoder.DecodeStringAsDateTime(serializedDateTime);

        //    if (dateTime == dateTime2)
        //    {
        //        Console.WriteLine("Yay!");
        //    }
        //    else
        //    {
        //        Console.WriteLine("Aww...");
        //    }
        //}

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
            foundImagesLock.AcquireWriterLock(1000);
            foundImages = new HashSet<string>();
            foundImagesLock.ReleaseLock();
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
            Console.WriteLine("Done moving files into new folder");
            Console.ReadKey();
        }

        private static DateTime lastImageAcquired = DateTime.MinValue;
        private static bool loggerRunning = true;
        private static void AsyncLogger()
        {
            int totalImages = -1;
            while (loggerRunning)
            {
                if (lastImageAcquired != DateTime.MinValue)
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
            listRequest.Fields = "nextPageToken, files(id, name, mimeType, fileExtension, appProperties)";

            // List files
            FileList requestResult = ExecuteUntilSuccessful(listRequest);
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
                    //First check if this file is already sorted into the relevant directory.
                    bool alreadyUpdated = file.AppProperties != null && file.AppProperties.ContainsKey(lastUpdatedKey);
                    if (alreadyUpdated)
                    {
                        // This last updated time could be used in the future to recategorize older files every time some parameters are changed
                        // However for now, it's mere presence is sufficient for determining if a file has already been processed.
                        DateTime lastUpdatedTime = DateTimeEncoder.DecodeStringAsDateTime(file.AppProperties[lastUpdatedKey]);
                        continue;
                    }

                    // Add to queue if it's not already added
                    foundImagesLock.AcquireWriterLock(1000);
                    if (!foundImages.Contains(file.Id))
                    {
                        foundImages.Add(file.Id);
                        foundImagesLock.ReleaseWriterLock();

                        imageProcessingTaskManager.AddAction(() => CategorizeImage(service, file));

                    }
                    else
                    {
                        foundImagesLock.ReleaseWriterLock();
                    }
                }
            }
            runningTasks.Release();
            if (--queuedThreads == 0)
            {
                resetEvent.Set();
            }
        }

        private static T ExecuteUntilSuccessful<T>(DriveBaseServiceRequest<T> request)
        {
            T results = default(T);
            int failureCount = 0;
            bool success = false;
            while (!success)
            {
                try
                {
                    results = request.Execute();
                    success = true;
                }
                catch (Google.GoogleApiException ex)
                {
                    foreach (var singleError in ex.Error.Errors)
                    {
                        if (singleError.Reason == "insufficientFilePermissions")
                        {
                            // Can't avoid this error... log and avoid writing to file directly, and just move directories
                            throw new UserLacksPermissionsException("User cannot modify this file in the way requested.", ex);
                        }
                    }
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

            return results;
        }

        public class UserLacksPermissionsException : Exception
        {
            public UserLacksPermissionsException(Exception inner)
                : base("UserLacksPermissionsException", inner) { }

            public UserLacksPermissionsException(string message, Exception innerException) : base(message, innerException) { }
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
                List<byte> matchingResolutions = new List<byte>();

                foreach (KeyValuePair<byte, Dimensions> entry in dimensionsMap)
                {
                    if (dimensions >= entry.Value)
                    {
                        matchingResolutions.Add(entry.Key);
                    }
                }

                var request = GenerateUpdateRequest(service, file, matchingResolutions);

                try
                {
                    ExecuteUntilSuccessful(request);
                }
                catch (UserLacksPermissionsException ex)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine(ex.Message + "\n Filename:" + file.Name);
                    Console.WriteLine();
                    Console.WriteLine();

                    // Try again without modifying file information (just adjusting parents).
                    request = GenerateUpdateRequest(service, file, matchingResolutions, false);
                    // TODO I don't love nested try/catch... might see if I can improve this somehow.
                    try
                    {
                        ExecuteUntilSuccessful(request);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                        Console.WriteLine("Unknown, unfixable problem. Skipping file: " + file.Name);
                        Console.WriteLine(ex2.Message);
                        Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                    }
                }
            }
        }

        private static FilesResource.UpdateRequest GenerateUpdateRequest(DriveService service, Google.Apis.Drive.v3.Data.File originalFile, 
            List<byte> resolutions, bool writeToFile = true)
        {
            Google.Apis.Drive.v3.Data.File updateFile = new Google.Apis.Drive.v3.Data.File();
            if (writeToFile)
            {
                updateFile.AppProperties = originalFile.AppProperties ?? new Dictionary<string, string>();
                updateFile.AppProperties.Add(lastUpdatedKey, DateTimeEncoder.DateTimeNowEncoded());
            }


            var updateRequest = service.Files.Update(updateFile, originalFile.Id);
            List<string> newParents = new List<string>();
            foreach (var i in resolutions)
            {
                string parentId = folderIdMap[i];
                newParents.Add(parentId);
            }
            updateRequest.AddParents = String.Join(",", newParents);
            return updateRequest;
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
            int missedHeaderFailureCount = 0;
            int taskCancelledFailtureCount = 0;
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
                    missedHeaderFailureCount++;
                    int waitTime = CalculateWaitTime(missedHeaderFailureCount);
                    Console.WriteLine("Exception: " + ex.Message);
                    Console.WriteLine("This thread has failed " + missedHeaderFailureCount + " times");
                    Console.WriteLine("Sleeping for  " + waitTime + " milliseconds");
                    Thread.Sleep(waitTime);
                }
                catch (TaskCanceledException)
                {
                    taskCancelledFailtureCount++;
                    // Note: I'm a little worried that there might be an issue with deadlocks here.
                    // If there are still problems, I should investigate that
                    Console.WriteLine("Task cancelled... trying again?");
                    int waitTime = CalculateWaitTime(taskCancelledFailtureCount);
                    Thread.Sleep(waitTime);

                }
            }

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
