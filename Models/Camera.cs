using System;

namespace spectrum.Models
{
    /// <summary>
    /// 表示摄像头设备的模型类
    /// </summary>
    public class Camera
    {
        /// <summary>
        /// 摄像头设备名称
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 摄像头设备ID或路径
        /// </summary>
        public string DeviceId { get; set; }
        
        /// <summary>
        /// 摄像头设备描述信息
        /// </summary>
        public string Description { get; set; }
        
        public Camera(string name, string deviceId, string description = "")
        {
            Name = name;
            DeviceId = deviceId;
            Description = "摄像头："+ description;
        }
        
        public override string ToString()
        {
            return Name;
        }
    }
}