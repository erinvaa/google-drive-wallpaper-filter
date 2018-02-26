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
        private static int runningThreads = 0;
        private static int maxConcurrentThreads = 20;
        private static Random random = new Random();


        static void Main(string[] args)
        {
            //Console.WriteLine("Please enter a png path.");
            //string path = Console.ReadLine();

            //FileStream fs = System.IO.File.OpenRead(path);

            //ImageHandler.ParsePngResolutionFromHeader(fs);

            //fs.Close();
            //Console.ReadKey();

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


            // Get ready for the intense multithreading.
            bigImageIdsLock.AcquireWriterLock(1000);
            bigImageIds = new HashSet<string>();
            bigImageIdsLock.ReleaseLock();

            resetEvent = new ManualResetEvent(false);
            runningTasks = new SemaphoreSlim(maxConcurrentThreads);

            Task rootTask = new Task(() => FindNestedImagesAboveResolution(service, wallpaperIds[0]));
            runningThreads++;
            rootTask.Start();

            // Wait for all the logic to finish.
            resetEvent.WaitOne();

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Size: " + bigImageIds.Count);
            Console.ReadKey();
        }


        private static void FindNestedImagesAboveResolution(DriveService service, string parentId, string pageToken = null)
        {
            runningTasks.Wait();
            Console.WriteLine("Started new thread; " + runningThreads + " threads queued and " + runningTasks.CurrentCount + " running");
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
            }
            IList<Google.Apis.Drive.v3.Data.File> files = requestResult.Files;

            if (requestResult.NextPageToken != null)
            {
                Task nextPageTask = new Task(() => FindNestedImagesAboveResolution(service, parentId, requestResult.NextPageToken));
                runningThreads++;
                nextPageTask.Start();
            }

            foreach (var file in files)
            {
                if (file.MimeType == "application/vnd.google-apps.folder")
                {
                    Task subDirectoryTask = new Task(() => FindNestedImagesAboveResolution(service, file.Id));
                    runningThreads++;
                    subDirectoryTask.Start();
                }
                else if (file.FileExtension == "png" || file.FileExtension == "jpeg" || file.FileExtension == "jpg")
                {
                    // TODO just adding all pictures for now... still need to calculate if file is bigger than threshold.
                    bigImageIdsLock.AcquireWriterLock(1000);
                    bigImageIds.Add(file.Id);
                    bigImageIdsLock.ReleaseLock();
                }
            }
            runningTasks.Release();
            if (--runningThreads == 0)
            {
                resetEvent.Set();
            }
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
