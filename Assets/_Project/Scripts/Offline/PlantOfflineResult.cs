namespace AfkIsland.Offline
{
    public sealed class PlantOfflineResult
    {
        public string summaryText = string.Empty;
        public int plantsPresent;
        public int stagesAdvanced;
        public int plantsMatured;
        public int unknownPlantsSkipped;
        public int plantsSpawnedByNature;
        public int cellsTargetedByNature;
        public string natureColonizationLine = string.Empty;
    }
}
