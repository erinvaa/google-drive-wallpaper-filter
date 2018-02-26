using Google.Apis.Auth.OAuth2;
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
        static string ApplicationName = "4K Filter";


        private static SemaphoreSlim runningTasks;
        private static ReaderWriterLock bigImageIdsLock = new ReaderWriterLock();
        private static ISet<string> bigImageIds;
        private static ManualResetEvent resetEvent;
        private static int queuedThreads = 0;
        private static int maxConcurrentThreads = 20;
        private static Random random = new Random();
        private static ImageProcessingTaskManager imageProcessingTaskManager;


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

            IList<string> wallpaperIds = FindWallpaperId(service);


            // Get ready for multithreading.
            bigImageIdsLock.AcquireWriterLock(1000);
            bigImageIds = new HashSet<string>();
            bigImageIdsLock.ReleaseLock();
            imageProcessingTaskManager = new ImageProcessingTaskManager();

            resetEvent = new ManualResetEvent(false);
            runningTasks = new SemaphoreSlim(maxConcurrentThreads, maxConcurrentThreads);

            Task rootTask = new Task(() => FindNestedImagesAboveResolution(service, wallpaperIds[0]));
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
            Console.WriteLine();
            Console.WriteLine("Size: " + bigImageIds.Count);
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
                        imagesPerSecond.ToString("#.000") + " images per second. Threads:" + imageProcessingTaskManager.RunningThreads);
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

            // List files.
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
                catch (TaskCanceledException ex)
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
                    imageProcessingTaskManager.AddAction(() => AddFileIfBig(service, file));
                }
            }
            runningTasks.Release();
            if (--queuedThreads == 0)
            {
                resetEvent.Set();
            }
        }

        private static void AddFileIfBig(DriveService service, Google.Apis.Drive.v3.Data.File file)
        {
            runningTasks.Wait();
            MemoryStream stream = getFileHeader(service, file.Id);
            bool isBigImage;
            switch (file.FileExtension)
            {
                case "png":
                    isBigImage = ImageHandler.IsBigPngFromHeader(stream);
                    break;
                case "jpeg":
                case "jpg":
                    isBigImage = ImageHandler.IsBigJpegFromHeader(stream);
                    break;
                default:
                    isBigImage = false;
                    break;
            }

            if (isBigImage)
            {
                bigImageIdsLock.AcquireWriterLock(1000);
                bigImageIds.Add(file.Id);
                bigImageIdsLock.ReleaseLock();
            }

            runningTasks.Release();
        }

        private static MemoryStream getFileHeader(DriveService service, string fileId)
        {
            var request = service.Files.Get(fileId);
            // TODO investigate using download async
            var stream = new MemoryStream();

            // TODO refactor this out (and combine with other version)
            int failureCount = 0;
            bool success = false;
            while (!success)
            {
                try
                {
                    request.DownloadRange(stream, new System.Net.Http.Headers.RangeHeaderValue(0, 75));
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

            return stream;
        }

        private static int slotTime = 50;
        private static int CalculateWaitTime(int failureCount)
        {
            // 2 ^ failure count
            return (int)random.Next(1 << failureCount) * slotTime;
        }

        private static IList<string> FindWallpaperId(DriveService service)
        {
            FilesResource.ListRequest wallpaperFolderRequest = service.Files.List();
            wallpaperFolderRequest.Fields = "files(id)";
            wallpaperFolderRequest.Q = "name = 'Wallpapers'";
            IList<Google.Apis.Drive.v3.Data.File> wallpaperFolderList = wallpaperFolderRequest.Execute()
                .Files;

            IList<string> ids = new List<string>();
            foreach (var file in wallpaperFolderList)
            {
                ids.Add(file.Id);
            }

            return ids;
        }
    }
}
