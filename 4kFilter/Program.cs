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
        // TODO consider making this key shorter (possible just one character)
        private const string oldLastUpdatedKey = "LastUpdated";
        private static string lastUpdatedKey;

        // If modifying these scopes, delete your previously saved credentials
        // at ~/.credentials/drive-dotnet-quickstart.json
        static string[] Scopes = { DriveService.Scope.Drive };
        const string ApplicationName = "4K Image Filter";

        //private const string wallpaperFolderName = "Wallpapers";
        private const string wallpaperFolderName = "Wallpapers";
        private const string destination4kFolderName = "Resolution: 4K";
        private const string destinationHdFolderName = "Resolution: HD";
        private const string destinationWqhdFolderName = "Resolution: WQHD";

        private const string completedIdsFile = "completedFiles";


        private static SemaphoreSlim runningTasks;
        private static ReaderWriterLock foundImagesLock = new ReaderWriterLock();
        private static ISet<string> foundImages;
        private static ManualResetEvent resetEvent;
        private static int queuedThreads = 0;
        private static int maxConcurrentThreads = 20;
        private static Random random = new Random();
        private static ImageProcessingTaskManager imageProcessingTaskManager;
        private static int numberOfBytesToRead = 75;
        private static ReaderWriterLock completedIdsFileLock = new ReaderWriterLock();
        private static StreamWriter completedIdsFileWriterStream;


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

        static void Main(string[] args)
        {
            UserCredential credential;

            using (var stream = new FileStream("client_secret.json", FileMode.Open, FileAccess.Read))
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
            
            PopulateFileIdsFromLocalFile();

            var aboutRequest = service.About.Get();
            aboutRequest.Fields = "user";
            About about = aboutRequest.Execute();
            string user = about.User.EmailAddress;
            lastUpdatedKey = user.Split('@')[0];

            string wallpaperId = FindFileWithName(service, wallpaperFolderName);

            // Get ready for multithreading.
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

            completedIdsFileLock.AcquireWriterLock(1000);
            completedIdsFileWriterStream.Close();
            completedIdsFileLock.ReleaseWriterLock();

            Console.WriteLine();
            Console.WriteLine("Done moving files into new folder");
            Console.ReadKey();
        }

        static void OnProcessExit(object sender, EventArgs e)
        {
            completedIdsFileLock.AcquireWriterLock(1000);
            completedIdsFileWriterStream.Close();
            completedIdsFileLock.ReleaseWriterLock();
        }

        private static void PopulateFileIdsFromLocalFile()
        {
            foundImagesLock.AcquireWriterLock(1000);
            foundImages = new HashSet<string>();
            foundImagesLock.ReleaseLock();


            if (!System.IO.File.Exists(completedIdsFile))
            {
                completedIdsFileWriterStream = System.IO.File.CreateText(completedIdsFile);
                return;
            }

            completedIdsFileLock.AcquireReaderLock(1000);
            foundImagesLock.AcquireWriterLock(1000);
            using (var reader = new StreamReader(completedIdsFile))
            {
                string readLine = reader.ReadLine();
                while (readLine != null)
                {
                    foundImages.Add(readLine);
                    readLine = reader.ReadLine(); 
                }
            }

            completedIdsFileWriterStream = new StreamWriter(completedIdsFile, append:true);
            foundImagesLock.ReleaseWriterLock();
            completedIdsFileLock.ReleaseReaderLock();
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
                    int completedImages = totalImages - imageProcessingTaskManager.ImageCount;
                    double imagesPerSecond = (double)completedImages / (DateTime.Now - lastImageAcquired).TotalSeconds;
                    Console.WriteLine(completedImages + "/" + totalImages + " (" + ((double)completedImages * 100d / (double)(totalImages)).ToString("0.00") + "%) images processed at a rate of " +
                        imagesPerSecond.ToString("0.000") + " images per second. Threads:" + imageProcessingTaskManager.RunningThreads);
                }
                else
                {
                    Console.WriteLine(queuedThreads + " threads queued and " + (maxConcurrentThreads - runningTasks.CurrentCount) + " running and " +
                        imageProcessingTaskManager.ImageCount + " images found");
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
            listRequest.Fields = "nextPageToken, files(id, name, mimeType, fileExtension, appProperties, capabilities)";

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
                    // First check if this file is already sorted into the relevant directory.
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
                            // Shouldn't expect this error to go away after retries
                            throw ex;
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
                catch (Exception ex)
                {
                    Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                    Console.WriteLine("Unknown, unfixable problem. Skipping file: " + file.Name);
                    Console.WriteLine(ex.Message);
                    Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                }
            }
        }

        private static FilesResource.UpdateRequest GenerateUpdateRequest(DriveService service, Google.Apis.Drive.v3.Data.File originalFile, 
            List<byte> resolutions)
        {
            Google.Apis.Drive.v3.Data.File updateFile = new Google.Apis.Drive.v3.Data.File();
            if (originalFile.Capabilities.CanEdit == true)
            {
                updateFile.AppProperties = originalFile.AppProperties ?? new Dictionary<string, string>();
                updateFile.AppProperties.Add(lastUpdatedKey, DateTimeEncoder.DateTimeNowEncoded());
            }
            else
            {
                // As a backup to storing the found files in drive, store them locally.
                completedIdsFileLock.AcquireWriterLock(1000);
                completedIdsFileWriterStream.WriteLine(originalFile.Id);
                completedIdsFileWriterStream.Flush();
                completedIdsFileLock.ReleaseWriterLock();
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
