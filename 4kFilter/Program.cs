using _4kFilter.ImageFilters;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace _4kFilter
{
    class Program
    {
        private static string lastUpdatedKey;

        static string[] Scopes = { DriveService.Scope.Drive };
        const string ApplicationName = "4K Image Filter";

        // ------------------------------------------------------------
        //                         Settings
        // ------------------------------------------------------------
        private const string WallpaperFolderName = "Wallpapers";
        //private const string wallpaperFolderName = noDimensionsFoundFolderName;
        private const string DefaultFolderName = "Wallpapers";
        private const string NoDimensionsFoundFolderName = "No Dimensions Found";
        private const string Destination4kFolderName = "Resolution: 4K";
        private const string DestinationHdFolderName = "Resolution: HD";
        private const string DestinationWqhdFolderName = "Resolution: WQHD";
        private const string DestinationVerticalFolderName = "Vertical WQHD Screen";
        private const string DestinationPhoneRatioFolderName = "Phone Ratio";
        private const string DestinationWidescreenRatioFolderName = "Widescreen";

        private static DateTime NewestVersionTimestamp = new DateTime(2018, 5, 10, 0, 0, 0);
        //private static DateTime NewestVersionTimestamp = DateTime.Now;
        //private static DateTime NewestVersionTimestamp = DateTime.MinValue;
        private static bool ShouldFilterExistingFolders = false;
        private static bool ShouldRenameIncorrectExtensions = true;

        // Null to not report metadata.
        private static string MetadataFilename = null;

        private const string CompletedIdsFilename = "completedFiles";
        // ------------------------------------------------------------

        private static bool ShouldRecordMetadata => MetadataFilename != null;
        private static SemaphoreSlim runningTasks;
        private static ReaderWriterLock foundImagesLock = new ReaderWriterLock();
        private static ISet<string> foundImages;
        private static ManualResetEvent resetEvent;
        private static int queuedThreads = 0;
        private static int maxConcurrentThreads = 10;
        private static Random random = new Random();
        private static ImageProcessingTaskDispatcher imageProcessingTaskManager;
        private static ReaderWriterLock completedIdsFileLock = new ReaderWriterLock();
        private static StreamWriter completedIdsFileWriterStream;
        private static StreamWriter metadataFileWriterStream;

        private static IList<ImageFilter> directoryRules;
        private static IList<string> categoryDirectories;
        private static string defaultFolderId;

        private class AlreadyAttemptedEntireFileException : Exception { }

        private static long[] byteChunks = new long[] { 167, 525, 20_000 };

        // This is writen so it should be possible to replace with user input (with some work)
        private static void PopulateDimensionInformation(DriveService service)
        {
            defaultFolderId = FindFileWithName(service, DefaultFolderName);

            directoryRules = new List<ImageFilter>
            {
                new ImageSizeFilter(service, Destination4kFolderName, new Dimensions(3840, 2160), Dimensions.MaxDimension),
                new ImageSizeFilter(service, DestinationWqhdFolderName, new Dimensions(2560, 1440), Dimensions.MaxDimension),
                new ImageSizeFilter(service, DestinationVerticalFolderName, new Dimensions(1440, 2560), Dimensions.MaxDimension),
                new ImageSizeFilter(service, DestinationHdFolderName, new Dimensions(1920, 1200), Dimensions.MaxDimension),
                new ImageRatioFilter(service, DestinationWidescreenRatioFolderName, 16d/10d, 0.01),
                new ImageRatioFilter(service, DestinationPhoneRatioFolderName, null, 9d/10d),
                new ImageNoSizeDimensionsFilter(service, NoDimensionsFoundFolderName)
            };

            categoryDirectories = new List<string>();
            foreach (var rule in directoryRules)
            {
                if (rule.TargetDirectoryId != defaultFolderId)
                {
                    categoryDirectories.Add(rule.TargetDirectoryId);
                }
            }
        }

        static void Main(string[] args)
        {
            UserCredential credential;

            using (var stream = new FileStream("client_secret.json", FileMode.Open, FileAccess.Read))
            {
                string credPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
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

            PopulateDimensionInformation(service);

            PopulateFileIdsFromLocalFile();

            SetupMetadataFile();

            var aboutRequest = service.About.Get();
            aboutRequest.Fields = "user";
            About about = aboutRequest.Execute();
            string user = about.User.EmailAddress;
            lastUpdatedKey = user.Split('@')[0];

            string wallpaperId = FindFileWithName(service, WallpaperFolderName);

            // Get ready for multithreading.
            imageProcessingTaskManager = new ImageProcessingTaskDispatcher();

            resetEvent = new ManualResetEvent(false);
            runningTasks = new SemaphoreSlim(maxConcurrentThreads, maxConcurrentThreads);

            Thread rootTask = new Thread(() => FindAllImages(service, wallpaperId));
            queuedThreads++;
            rootTask.Start();

            Thread loggerHelper = new Thread(AsyncLogger);
            loggerHelper.Start();

            // Wait for all the logic to finish.
            resetEvent.WaitOne();

            if (imageProcessingTaskManager.ImageCount > 0)
            {
                lastImageAcquired = DateTime.Now;
                Console.WriteLine("Done scanning for files; starting to analyse images.");
                imageProcessingTaskManager.Start();
                imageProcessingTaskManager.StopWhenTasksCompleted = true; // Tell the processor no new tasks are coming.
                imageProcessingTaskManager.StoppedEvent.WaitOne();
            }
            else
            {
                Console.WriteLine("No new images found");
            }
            loggerRunning = false;
            loggerHelper.Interrupt();

            completedIdsFileLock.AcquireWriterLock(1000);
            completedIdsFileWriterStream.Close();
            completedIdsFileLock.ReleaseWriterLock();
            if (metadataFileWriterStream != null)
            {
                lock (metadataFileWriterStream)
                {
                    metadataFileWriterStream.Close();
                }
            }
            // Make sure logger is done before finishing up here.
            loggerHelper.Join();
            Console.WriteLine();
            Console.WriteLine("Done moving files into new folder");
            Console.ReadKey();
        }

        static void OnProcessExit(object sender, EventArgs e)
        {
            // TODO this could be a closeOpenFiles method or something
            completedIdsFileLock.AcquireWriterLock(1000);
            completedIdsFileWriterStream.Close();
            completedIdsFileLock.ReleaseWriterLock();
            if (metadataFileWriterStream != null)
            {
                lock (metadataFileWriterStream)
                {
                    metadataFileWriterStream.Close();
                }
            }
        }

        private static void SetupMetadataFile()
        {
            if (ShouldRecordMetadata)
            {
                if (!System.IO.File.Exists(MetadataFilename))
                {
                    metadataFileWriterStream = System.IO.File.CreateText(MetadataFilename);
                    return;
                }
                else if (new FileInfo(CompletedIdsFilename).LastWriteTimeUtc < NewestVersionTimestamp)
                {
                    // File is outdated; rewrite information.
                    System.IO.File.WriteAllText(MetadataFilename, string.Empty);
                }

                metadataFileWriterStream = new StreamWriter(MetadataFilename, append: true);

            }
        }

        private static void PopulateFileIdsFromLocalFile()
        {
            foundImagesLock.AcquireWriterLock(1000);
            foundImages = new HashSet<string>();
            foundImagesLock.ReleaseLock();


            if (!System.IO.File.Exists(CompletedIdsFilename))
            {
                completedIdsFileWriterStream = System.IO.File.CreateText(CompletedIdsFilename);
                return;
            }
            else if (new FileInfo(CompletedIdsFilename).LastWriteTimeUtc < NewestVersionTimestamp)
            {
                // File is outdated; rewrite information.
                completedIdsFileLock.AcquireReaderLock(1000);
                System.IO.File.WriteAllText(CompletedIdsFilename, string.Empty);
            }
            else
            {
                completedIdsFileLock.AcquireReaderLock(1000);
            }

            foundImagesLock.AcquireWriterLock(1000);
            using (var reader = new StreamReader(CompletedIdsFilename))
            {
                string readLine = reader.ReadLine();
                while (readLine != null)
                {
                    foundImages.Add(readLine);
                    readLine = reader.ReadLine();
                }
            }

            completedIdsFileWriterStream = new StreamWriter(CompletedIdsFilename, append: true);
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
                        totalImages = imageProcessingTaskManager.ImageCount + imageProcessingTaskManager.RunningThreads;
                    }
                    int completedImages = totalImages - imageProcessingTaskManager.IncompleteTasks;
                    double imagesPerSecond = (double)completedImages / (DateTime.Now - lastImageAcquired).TotalSeconds;
                    Console.WriteLine(completedImages + "/" + totalImages + " (" + ((double)completedImages * 100d / (double)(totalImages)).ToString("0.00") + "%) images processed at a rate of " +
                        imagesPerSecond.ToString("0.000") + " images per second. Threads:" + imageProcessingTaskManager.RunningThreads);
                }
                else
                {
                    Console.WriteLine(queuedThreads + " threads queued and " + (maxConcurrentThreads - runningTasks.CurrentCount) + " running and " +
                        imageProcessingTaskManager.ImageCount + " images found");
                }
                try
                {
                    Thread.Sleep(500);
                }
                catch (ThreadInterruptedException) {
                    // Print one final log, if appropriate.
                    if (totalImages > 0)
                    {
                        int completedImages = totalImages;
                        double imagesPerSecond = (double)completedImages / (DateTime.Now - lastImageAcquired).TotalSeconds;
                        Console.WriteLine(completedImages + "/" + totalImages + " (" + ((double)completedImages * 100d / (double)(totalImages)).ToString("0.00") + "%) images processed at a rate of " +
                            imagesPerSecond.ToString("0.000") + " images per second. Threads:" + imageProcessingTaskManager.RunningThreads);
                    }
                }
            }
        }

        private static void FindAllImages(DriveService service, string parentId, bool isInKeyDirectory = false, string pageToken = null)
        {
            runningTasks.Wait();
            FilesResource.ListRequest listRequest = service.Files.List();
            listRequest.PageSize = 1000;
            listRequest.Q = "'" + parentId + "' in parents";
            listRequest.PageToken = pageToken;
            listRequest.Fields = "nextPageToken, files(id, name, mimeType, fileExtension, appProperties, capabilities, parents)";

            // List files
            FileList requestResult = ExecuteUntilSuccessful(listRequest);
            IList<Google.Apis.Drive.v3.Data.File> files = requestResult.Files;

            if (requestResult.NextPageToken != null)
            {
                Task nextPageTask = new Task(() => FindAllImages(service, parentId, isInKeyDirectory, requestResult.NextPageToken));
                queuedThreads++;
                nextPageTask.Start();
            }

            foreach (var file in files)
            {
                if (file.MimeType == "application/vnd.google-apps.folder")
                {
                    foundImagesLock.AcquireWriterLock(1000);
                    if (!foundImages.Contains(file.Id))
                    {
                        foundImages.Add(file.Id);
                        foundImagesLock.ReleaseWriterLock();

                        isInKeyDirectory = ShouldFilterExistingFolders && (isInKeyDirectory || categoryDirectories.Contains(file.Id));
                        Task subDirectoryTask = new Task(() => FindAllImages(service, file.Id, isInKeyDirectory));
                        queuedThreads++;
                        subDirectoryTask.Start();
                    }
                    else
                    {
                        foundImagesLock.ReleaseWriterLock();
                    }
                }
                else if (file.FileExtension == "png" || file.FileExtension == "jpeg" || file.FileExtension == "jpg")
                {
                    // First check if this file is already sorted into the relevant directory.
                    bool alreadyUpdated = file.AppProperties != null && file.AppProperties.ContainsKey(lastUpdatedKey);
                    if (alreadyUpdated && !isInKeyDirectory)
                    {
                        // This last updated time could be used in the future to recategorize older files every time some parameters are changed
                        // However for now, it's mere presence is sufficient for determining if a file has already been processed.
                        DateTime lastUpdatedTime = DateTimeEncoder.DecodeStringAsDateTime(file.AppProperties[lastUpdatedKey]);
                        if (lastUpdatedTime >= NewestVersionTimestamp) continue;
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
            int missCount = 0;
            bool success = false;
            string newName = null;
            Dimensions dimensions = Dimensions.None;
            ImageHandler handler = new ImageHandler();
            while (!success)
            {
                MemoryStream stream;
                try
                {
                    stream = GetFileHeader(service, file.Id, missCount);
                }
                catch (AlreadyAttemptedEntireFileException)
                {
                    Console.WriteLine("Header not present in file.");
                    break;
                }
                try
                {
                    dimensions = handler.ReadDimensions(stream);
                    success = true;
                }
                catch (ImageHandler.HeaderNotFoundException)
                {
                    missCount++;
                }
            }

            if (ShouldRenameIncorrectExtensions)
            {
                if (handler.InternalFileType == ImageHandler.FileType.PNG && file.FileExtension != "png")
                {
                    Console.WriteLine("PNG file found with different extension '" + file.FileExtension + "'... renaming.");
                    newName = ReplaceExtension(file.Name, "png");
                }
                else if (handler.InternalFileType == ImageHandler.FileType.JPG && file.FileExtension != "jpg" && file.FileExtension != "jpeg")
                {
                    Console.WriteLine("JPG file found with different extension '" + file.FileExtension + "'... renaming.");
                    newName = ReplaceExtension(file.Name, "jpg");
                }
            }

            if (ShouldRecordMetadata && dimensions != null)
            {
                long headerPositionInFile = GetLowerByte(missCount) + handler.EndOfMetadataIndex;
                lock (metadataFileWriterStream)
                {
                    metadataFileWriterStream.Write(file.Name);
                    metadataFileWriterStream.Write(',');
                    metadataFileWriterStream.WriteLine(headerPositionInFile);
                    metadataFileWriterStream.Flush();
                }
            }

            HashSet<string> newParentIds = new HashSet<string>();
            HashSet<string> removeParentsId = new HashSet<string>();

            foreach (ImageFilter entry in directoryRules)
            {
                // This might be starting to get too complicate; look into streamlining it potentially
                if (entry.MatchesCriteria(dimensions))
                {
                    if (!removeParentsId.Contains(entry.TargetDirectoryId))
                    {
                        newParentIds.Add(entry.TargetDirectoryId);
                    }
                }
                else if (categoryDirectories.Contains(entry.TargetDirectoryId))
                {
                    newParentIds.Remove(entry.TargetDirectoryId);
                    if (file.Parents.Contains(entry.TargetDirectoryId))
                    {
                        removeParentsId.Add(entry.TargetDirectoryId);
                    }
                }
            }

            // Make sure we aren't stranding files with no standard parents.
            HashSet<string> standardParents = new HashSet<string>();
            standardParents.UnionWith(file.Parents);
            standardParents.UnionWith(newParentIds);
            standardParents.RemoveWhere((x) => categoryDirectories.Contains(x) || removeParentsId.Contains(x));

            if (standardParents.Count == 0)
            {
                newParentIds.Add(defaultFolderId);
            }

            var request = GenerateUpdateRequest(service, file, newParentIds, removeParentsId, newName);

            try
            {
                ExecuteUntilSuccessful(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Console.WriteLine("Unknown problem occurred. Skipping file: " + file.Name);
                Console.WriteLine(ex.Message);
                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");

            }
        }

        private static string ReplaceExtension(string name, string newExt)
        {
            string newName;
            int indexPosition = name.LastIndexOf('.');
            newName = name.Remove(indexPosition + 1) + newExt;
            return newName;
        }

        private static FilesResource.UpdateRequest GenerateUpdateRequest(DriveService service, Google.Apis.Drive.v3.Data.File originalFile,
            IEnumerable<string> newParentsIds, IEnumerable<string> removeParentsId = null, string newName = null)
        {
            Google.Apis.Drive.v3.Data.File updateFile = new Google.Apis.Drive.v3.Data.File();
            if (originalFile.Capabilities.CanEdit == true)
            {
                updateFile.AppProperties = originalFile.AppProperties ?? new Dictionary<string, string>();
                updateFile.AppProperties[lastUpdatedKey] = DateTimeEncoder.DateTimeNowEncoded();

                if (newName != null)
                {
                    updateFile.Name = newName;
                }
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
            updateRequest.AddParents = String.Join(",", newParentsIds);
            if (removeParentsId != null)
            {
                updateRequest.RemoveParents = String.Join(",", removeParentsId);
            }
            return updateRequest;
        }

        private static MemoryStream GetFileHeader(DriveService service, string fileId, int attemptNumber = 0)
        {
            var request = service.Files.Get(fileId);
            var stream = new MemoryStream();

            long? lowerByte = GetLowerByte(attemptNumber);
            // The 10 byte overlap between requests is so that the header isn't split between two requests.
            long? upperByte = GetUpperByte(attemptNumber);

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


        private static long? GetUpperByte(int attemptNumber)
        {
            if (attemptNumber == byteChunks.Length)
            {
                return null;
            }
            else if (attemptNumber > byteChunks.Length)
            {
                throw new AlreadyAttemptedEntireFileException();
            }
            return byteChunks[attemptNumber];
        }

        private static long GetLowerByte(int attemptNumber)
        {
            if (attemptNumber == 0)
            {
                return 0;
            }
            else if (attemptNumber > byteChunks.Length)
            {
                throw new AlreadyAttemptedEntireFileException();
            }
            return byteChunks[attemptNumber - 1];
        }

        private static int slotTime = 50;

        private static int CalculateWaitTime(int failureCount)
        {
            // 2 ^ failure count
            return random.Next(1 << failureCount) * slotTime;
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
