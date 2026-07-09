
namespace FairyGUI
{
    /// <summary>
    /// 
    /// </summary>
    public class Stats
    {
        /// <summary>
        /// 
        /// </summary>
        public static int ObjectCount;

        /// <summary>
        /// 
        /// </summary>
        public static int GraphicsCount;

        /// <summary>
        /// 
        /// </summary>
        public static int LatestObjectCreation;

        /// <summary>
        ///
        /// </summary>
        public static int LatestGraphicsCreation;

        /// <summary>
        /// Combined meshes drawn by mergedBatching containers this frame.
        /// </summary>
        public static int MergedRuns;

        /// <summary>
        /// Elements whose renderer is replaced by a merged mesh this frame.
        /// </summary>
        public static int MergedElements;

        /// <summary>
        /// MergedBatch full rebuilds (re-slice + re-bake everything) performed this frame.
        /// </summary>
        public static int MergedRebuilds;

        /// <summary>
        /// Individual runs re-baked in place (source-level dirt) this frame.
        /// </summary>
        public static int MergedRebakes;
    }
}
