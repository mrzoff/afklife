using System;

namespace AfkIsland.World
{
    [Serializable]
    public sealed class WorldGrowthConfig
    {
        public int gridSize = 16;
        public float chunkSizeMeters = 16f;

        // Prototype pace. Home starts at 256 land cells, so k must stay soft
        // or edge growth stalls (dA/dt = g0 / (1 + k*A)).
        // Re-tune after mesh playtests toward the Tech Spec defaults.
        // Do NOT retune while geometry/SDF seams are still being fixed.
        public double baseExpansionCellsPerHour = 10d;
        public double diminishingReturnsK = 0.015d;

        // Per-session sim window. Longer AFK catch-up continues across visits via watermark.
        public long maxOfflineSeconds = 7L * 24L * 3600L;
        public float maxAreaPerSession = 96f;
        public int maxNewChunksPerSession = 2;
        public int maxCellsAdvancedPerSession = 128;

        public double rootsDurationSeconds = 2d * 3600d;
        public double soilDurationSeconds = 4d * 3600d;
        public double mossDurationSeconds = 6d * 3600d;

        public float[] baseSectorWeights =
        {
            1f, 0.85f, 1f, 0.85f, 1f, 0.85f, 1f, 0.85f
        };

        public int algorithmVersion = 1;

        // World events (Seeds) — time gates + caps for offline session.
        public long eventMinSeconds = 20L * 60L;
        public int maxEventsPerSession = 4;
        public float eventChancePerHour = 0.55f;

        public static WorldGrowthConfig CreateDefault()
        {
            return new WorldGrowthConfig();
        }
    }
}
