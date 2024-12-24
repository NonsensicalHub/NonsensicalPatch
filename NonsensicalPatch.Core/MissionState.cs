namespace NonsensicalPatch.Core
{
    public struct MissionState
    {
        public string MissionName;
        public bool IsIndeterminate;
        public long CurrentSize;
        public long MaxSize;

        public MissionState(string missionName) 
        {
            MissionName = missionName;
            IsIndeterminate = false;
            CurrentSize = 0;
            MaxSize = 0;
        }

        public MissionState(string missionName, bool isIndeterminate, long currentSize, long maxSize)
        {
            MissionName = missionName;
            IsIndeterminate = isIndeterminate;
            CurrentSize = currentSize;
            MaxSize = maxSize;
        }
    }
}
