using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace _4kFilter
{
    // NOTE BE VERY CAREFUL EDITING THIS CLASS. THERE ARE A LOT OF THREADING THINGS THAT AREN'T ENFORCED, BUT ARE VERY IMPORTANT
    class ImageProcessingTaskManager
    {
        private static int maxThreads = 10;

        private ReaderWriterLock imageProcessingActionsLock;
        private ReaderWriterLock runningThreadsLock;
        private int _runningThreads;
        // This is a queue and should be enforced inside this class
        private LinkedList<Action> imageProcessingActions;


        public ManualResetEvent StoppedEvent { get; private set; }
        public int WaitTime { get; set; }
        public bool StopWhenTasksCompleted { get; set; }
        public int RunningThreads {
            get
            {
                runningThreadsLock.AcquireReaderLock(1000);
                int retval = _runningThreads;
                runningThreadsLock.ReleaseReaderLock();
                return retval;
            }
            private set
            {
                runningThreadsLock.AcquireWriterLock(1000);
                _runningThreads = value;
                runningThreadsLock.ReleaseWriterLock();
            }
        }
        public bool Running {
            get
            {
                return RunningThreads > 0;
            }
        }
        public int ImageCount
        {
            get
            {
                imageProcessingActionsLock.AcquireReaderLock(100);
                int count = imageProcessingActions.Count;
                imageProcessingActionsLock.ReleaseReaderLock();
                return count;
            }
        }
        public bool HasActionsToRun
        {
            get
            {
                return ImageCount > 0;
            }
        }


        public ImageProcessingTaskManager()
        {
            runningThreadsLock = new ReaderWriterLock();
            imageProcessingActionsLock = new ReaderWriterLock();
            imageProcessingActions = new LinkedList<Action>();
            RunningThreads = 0;
            StopWhenTasksCompleted = false;
            WaitTime = 200;
            StoppedEvent = new ManualResetEvent(false);
        }

        public void AddAction(Action action)
        {
            imageProcessingActionsLock.AcquireWriterLock(1000);
            imageProcessingActions.AddLast(action);
            imageProcessingActionsLock.ReleaseWriterLock();
        }

        public void Start()
        {
            StoppedEvent.Reset();
            for (int i = 0; i < maxThreads; i++)
            {
                Task task = new Task(Run);
                task.Start();
            }
        }

        private void Run()
        {
            RunningThreads++;
            bool running = true;
            while (running)
            {
                imageProcessingActionsLock.AcquireWriterLock(1000);
                if (imageProcessingActions.Count > 0)
                {
                    Action currentTask = imageProcessingActions.First();
                    imageProcessingActions.RemoveFirst();
                    imageProcessingActionsLock.ReleaseWriterLock();

                    currentTask.Invoke();
                }
                else
                {
                    imageProcessingActionsLock.ReleaseWriterLock();
                    if (StopWhenTasksCompleted)
                    {
                        running = false;
                    }
                    else
                    {
                        Thread.Sleep(WaitTime);
                    }
                }
            }
            RunningThreads--;
            if (!Running)
            {
                StoppedEvent.Set();
            }
        }
    }
}
