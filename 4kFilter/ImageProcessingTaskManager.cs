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
        private ReaderWriterLock imageProcessingActionsLock;
        // This is a queue and should be enforced inside this class
        private LinkedList<Action> imageProcessingActions;

        public ManualResetEvent StoppedEvent { get; private set; }
        public int WaitTime { get; set; }
        public bool StopWhenTasksCompleted { get; set; }
        public bool Running { get; private set; }
        public int ImageCount
        {
            get
            {
                imageProcessingActionsLock.AcquireReaderLock(1000);
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
            imageProcessingActionsLock = new ReaderWriterLock();
            imageProcessingActions = new LinkedList<Action>();
            Running = false;
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
            Task task = new Task(Run);
            task.Start();
        }

        // TODO make async (here?)
        private void Run()
        {
            Running = true;
            while (Running)
            {
                while (!HasActionsToRun)
                {
                    if (StopWhenTasksCompleted)
                    {
                        Running = false;
                    }
                    else
                    {
                        Thread.Sleep(WaitTime);
                    }
                }

                imageProcessingActionsLock.AcquireWriterLock(1000);
                Action currentTask = imageProcessingActions.First();
                imageProcessingActions.RemoveFirst();
                imageProcessingActionsLock.ReleaseWriterLock();

                currentTask.Invoke();
            }
            StoppedEvent.Set();
        }
    }
}
