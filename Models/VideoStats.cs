namespace spectrum.Models
{
    /// <summary>
    /// 视频统计信息
    /// </summary>
    public class VideoStats
    {
        /// <summary>
        /// 画面尺寸
        /// </summary>
        public string Resolution { get; set; }
        
        /// <summary>
        /// 当前帧率
        /// </summary>
        public double Fps { get; set; }
        
        /// <summary>
        /// 当前延迟（毫秒）
        /// </summary>
        public int DelayMs { get; set; }
        
        public VideoStats(string resolution, double fps, int delayMs)
        {
            Resolution = resolution;
            Fps = fps;
            DelayMs = delayMs;
        }
    }
}