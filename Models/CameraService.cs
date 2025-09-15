using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenCvSharp;

namespace spectrum.Models
{
    /// <summary>
    /// 提供摄像头设备检测和管理功能的服务类
    /// </summary>
    public class CameraService
    {
        /// <summary>
        /// 检测当前系统类型
        /// </summary>
        /// <returns>当前系统类型</returns>
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        
        /// <summary>
        /// 获取系统中所有可用的摄像头设备
        /// </summary>
        /// <returns>摄像头设备列表</returns>
        public static async Task<List<Camera>> GetAvailableCamerasAsync()
        {
            if (IsWindows)
            {
                return GetWindowsCameras();
            }
            else if (IsLinux)
            {
                return await GetLinuxCamerasAsync();
            }
            
            return new List<Camera>();
        }
        
        /// <summary>
        /// 获取Windows系统中的摄像头设备
        /// </summary>
        /// <returns>摄像头设备列表</returns>
        private static List<Camera> GetWindowsCameras()
        {
            var cameras = new List<Camera>();
            
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE (PNPClass = 'Camera' OR PNPClass = 'Image')"))
                {
                    foreach (var device in searcher.Get())
                    {
                        string deviceId = device["DeviceID"]?.ToString() ?? "";
                        string name = device["Caption"]?.ToString() ?? "未知摄像头";
                        string description = device["Description"]?.ToString() ?? "";
                        
                        cameras.Add(new Camera(name, deviceId, description));
                    }
                }
                
                // 如果没有找到摄像头，尝试使用另一种查询方式
                if (cameras.Count == 0)
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%webcam%' OR Name LIKE '%camera%'"))
                    {
                        foreach (var device in searcher.Get())
                        {
                            string deviceId = device["DeviceID"]?.ToString() ?? "";
                            string name = device["Name"]?.ToString() ?? "未知摄像头";
                            string description = device["Description"]?.ToString() ?? "";
                            
                            cameras.Add(new Camera(name, deviceId, description));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取Windows摄像头时出错: {ex.Message}");
            }
            
            return cameras;
        }
        
        /// <summary>
        /// 获取Linux系统中的摄像头设备
        /// </summary>
        /// <returns>摄像头设备列表</returns>
        private static async Task<List<Camera>> GetLinuxCamerasAsync()
        {
            var cameras = new List<Camera>();
            
            try
            {
                // 检查/dev/video*设备
                string[] videoDevices = Directory.GetFiles("/dev", "video*")
                    .Where(f => Regex.IsMatch(f, @"/dev/video\d+$"))
                    .OrderBy(f => f)
                    .ToArray();
                
                Debug.WriteLine($"发现 {videoDevices.Length} 个video设备: {string.Join(", ", videoDevices)}");
                
                var validCameras = new List<Camera>();
                var processedDeviceNames = new HashSet<string>();
                
                foreach (string device in videoDevices)
                {
                    Debug.WriteLine($"检查设备: {device}");
                    
                    // 检查设备是否为真实的摄像头设备
                    if (await IsRealCameraDeviceAsync(device))
                    {
                        // 获取设备详细信息
                        var deviceInfo = await GetLinuxCameraInfoAsync(device);
                        string deviceName = deviceInfo.Name ?? $"摄像头 ({Path.GetFileName(device)})";
                        
                        // 过滤重复设备：同一个摄像头可能有多个video节点
                        // 只保留主要的捕获设备（通常是较小的编号）
                        if (!processedDeviceNames.Contains(deviceName))
                        {
                            processedDeviceNames.Add(deviceName);
                            
                            var camera = new Camera(deviceName, device, deviceInfo.Description ?? "");
                            validCameras.Add(camera);
                            Debug.WriteLine($"添加摄像头: {deviceName} -> {device}");
                        }
                        else
                        {
                            Debug.WriteLine($"跳过重复设备: {deviceName} -> {device}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"设备检查未通过: {device}");
                    }
                }
                
                cameras.AddRange(validCameras);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取Linux摄像头时出错: {ex.Message}");
            }
            
            return cameras;
        }
        
        /// <summary>
        /// 检查设备是否为真实的摄像头设备
        /// </summary>
        /// <param name="devicePath">设备路径</param>
        /// <returns>是否为真实摄像头</returns>
        private static async Task<bool> IsRealCameraDeviceAsync(string devicePath)
        {
            try
            {
                Debug.WriteLine($"检查设备: {devicePath}");
                
                // 1. 基本检查：设备是否存在
                if (!File.Exists(devicePath))
                {
                    Debug.WriteLine($"设备文件不存在: {devicePath}");
                    return false;
                }

                // 2. 预过滤：排除已知的非摄像头设备
                var deviceInfo = await GetLinuxCameraInfoAsync(devicePath);
                if (IsKnownNonCameraDevice(deviceInfo.Name, deviceInfo.Description, deviceInfo.Driver))
                {
                    Debug.WriteLine($"已知非摄像头设备，跳过: {devicePath}");
                    return false;
                }

                // 3. 检查设备权限和可访问性
                if (!await CheckDeviceAccessibilityAsync(devicePath))
                {
                    Debug.WriteLine($"设备不可访问: {devicePath}");
                    return false;
                }

                // 4. 尝试使用v4l2-ctl检查（如果可用）
                var v4l2Result = await CheckV4L2CapabilityAsync(devicePath);
                if (v4l2Result.HasValue)
                {
                    Debug.WriteLine($"v4l2检查结果: {v4l2Result.Value} for {devicePath}");
                    return v4l2Result.Value;
                }

                // 5. 如果v4l2-ctl不可用，使用OpenCV测试
                Debug.WriteLine($"v4l2不可用，使用OpenCV测试: {devicePath}");
                
                var match = Regex.Match(devicePath, @"/dev/video(\d+)");
                if (match.Success)
                {
                    int deviceNum = int.Parse(match.Groups[1].Value);
                    
                    // 只测试合理范围内的设备编号
                    if (deviceNum <= 20)
                    {
                        bool opencvTest = await TestDeviceWithOpenCVAsync(deviceNum);
                        if (opencvTest)
                        {
                            Debug.WriteLine($"OpenCV测试通过: {devicePath}");
                            return true;
                        }
                    }
                }

                Debug.WriteLine($"设备检查未通过: {devicePath}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查设备 {devicePath} 时出错: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查是否为已知的非摄像头设备
        /// </summary>
        private static bool IsKnownNonCameraDevice(string name, string description, string driver)
        {
            var deviceInfo = $"{name} {description} {driver}".ToLower();
            
            // 树莓派特有的非摄像头设备
            var excludePatterns = new[]
            {
                "pispbe",           // 树莓派ISP后端
                "pisp",             // 树莓派ISP相关
                "bcm2835",          // 树莓派芯片相关（非USB摄像头）
                "codec",            // 编解码器
                "encoder",          // 编码器
                "decoder",          // 解码器
                "scaler",           // 缩放器
                "isp",              // 图像信号处理器
                "csi",              // 摄像头串行接口（通常是板载摄像头接口）
                "unicam"            // 树莓派摄像头接口
            };

            foreach (var pattern in excludePatterns)
            {
                if (deviceInfo.Contains(pattern))
                {
                    Debug.WriteLine($"匹配排除模式 '{pattern}': {deviceInfo}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 检查设备可访问性
        /// </summary>
        private static async Task<bool> CheckDeviceAccessibilityAsync(string devicePath)
        {
            try
            {
                // 检查设备文件权限
                var fileInfo = new FileInfo(devicePath);
                if (!fileInfo.Exists)
                    return false;

                // 尝试打开设备进行读取测试
                using (var fs = File.OpenRead(devicePath))
                {
                    // 能打开说明有基本权限
                }

                // 检查设备是否被其他进程占用
                bool isInUse = await IsDeviceInUseAsync(devicePath);
                if (isInUse)
                {
                    Debug.WriteLine($"设备被其他进程占用: {devicePath}");
                    return false;
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Debug.WriteLine($"设备权限不足: {devicePath}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设备访问测试失败: {devicePath}, {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查设备是否被其他进程占用
        /// </summary>
        private static async Task<bool> IsDeviceInUseAsync(string devicePath)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "lsof",
                        Arguments = devicePath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // 如果lsof返回0且有输出，说明设备被占用
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output.Trim()))
                {
                    Debug.WriteLine($"设备占用信息: {output}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查设备占用状态失败: {ex.Message}");
                return false; // 如果无法检查，假设未被占用
            }
        }

        /// <summary>
        /// 使用v4l2-ctl检查设备能力
        /// </summary>
        private static async Task<bool?> CheckV4L2CapabilityAsync(string devicePath)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "v4l2-ctl",
                        Arguments = $"--device={devicePath} --list-formats-ext",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // 检查是否有视频格式输出
                    bool hasFormats = output.Contains("YUYV") || output.Contains("MJPG") || 
                                     output.Contains("RGB") || output.Contains("YUV") ||
                                     output.Contains("H264") || output.Contains("VP8");
                    
                    // 检查不是编解码器设备
                    bool notCodec = !output.ToLower().Contains("codec") && 
                                   !output.ToLower().Contains("encoder") && 
                                   !output.ToLower().Contains("decoder");

                    // 检查是否支持视频捕获
                    bool hasCapture = output.Contains("Video Capture") || output.Contains("capture");

                    bool isValidCamera = hasFormats && notCodec && hasCapture;
                    Debug.WriteLine($"v4l2检查 {devicePath}: 格式={hasFormats}, 非编解码器={notCodec}, 支持捕获={hasCapture}, 结果={isValidCamera}");
                    
                    return isValidCamera;
                }
                else
                {
                    Debug.WriteLine($"v4l2-ctl失败 {devicePath}: 退出码={process.ExitCode}, 错误={error}");
                    return false;
                }
            }
            catch (FileNotFoundException)
            {
                Debug.WriteLine("v4l2-ctl命令不存在");
                return null; // 命令不存在，无法检查
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"v4l2-ctl检查失败: {ex.Message}");
                return null; // 检查失败，无法确定
            }
        }

        /// <summary>
        /// 使用OpenCV快速测试设备
        /// </summary>
        private static async Task<bool> TestDeviceWithOpenCVAsync(int deviceIndex)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 首先检查 OpenCV 是否可用
                    if (!CheckOpenCvAvailability())
                    {
                        Debug.WriteLine("OpenCV 运行时库不可用，跳过 OpenCV 测试");
                        return false;
                    }

                    using var capture = new OpenCvSharp.VideoCapture(deviceIndex, OpenCvSharp.VideoCaptureAPIs.V4L2);
                    if (!capture.IsOpened())
                    {
                        using var capture2 = new OpenCvSharp.VideoCapture(deviceIndex, OpenCvSharp.VideoCaptureAPIs.ANY);
                        if (!capture2.IsOpened())
                            return false;
                        
                        // 尝试读取一帧
                        using var frame = new OpenCvSharp.Mat();
                        return capture2.Read(frame) && !frame.Empty();
                    }

                    // 尝试读取一帧
                    using var testFrame = new OpenCvSharp.Mat();
                    return capture.Read(testFrame) && !testFrame.Empty();
                }
                catch (TypeInitializationException ex)
                {
                    Debug.WriteLine($"OpenCV类型初始化异常，设备 {deviceIndex} 测试失败: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OpenCV测试设备 {deviceIndex} 失败: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 检查 OpenCV 是否可用
        /// </summary>
        /// <returns>是否可用</returns>
        private static bool CheckOpenCvAvailability()
        {
            try
            {
                // 尝试创建一个简单的 Mat 对象来测试 OpenCV 是否可用
                using var testMat = new OpenCvSharp.Mat(1, 1, OpenCvSharp.MatType.CV_8UC3);
                
                // 尝试获取 OpenCV 版本信息
                string version = OpenCvSharp.Cv2.GetVersionString();
                Debug.WriteLine($"OpenCV 版本: {version}");
                
                return true;
            }
            catch (TypeInitializationException ex)
            {
                Debug.WriteLine($"OpenCV 类型初始化异常: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenCV 可用性检查失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 简化的USB设备检查
        /// </summary>
        private static async Task<bool> IsUsbDeviceSimpleAsync(string devicePath)
        {
            try
            {
                // 尝试通过udevadm获取设备信息
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "udevadm",
                        Arguments = $"info --name={devicePath}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    return output.ToLower().Contains("usb") || 
                           output.Contains("ID_BUS=usb") ||
                           output.Contains("SUBSYSTEM=usb");
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"USB检查失败: {ex.Message}");
                return false;
            }
        }



        /// <summary>
        /// 摄像头设备信息结构
        /// </summary>
        private class CameraDeviceInfo
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
            public string? Driver { get; set; }
            public string? BusInfo { get; set; }
        }

        /// <summary>
        /// 获取Linux摄像头设备的详细信息
        /// </summary>
        /// <param name="devicePath">设备路径</param>
        /// <returns>设备信息</returns>
        private static async Task<CameraDeviceInfo> GetLinuxCameraInfoAsync(string devicePath)
        {
            var info = new CameraDeviceInfo();
            
            try
            {
                // 使用v4l2-ctl工具获取设备信息
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "v4l2-ctl",
                        Arguments = $"--device={devicePath} --info",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    // 解析输出以获取设备信息
                    var cardMatch = Regex.Match(output, @"Card\s+type\s*:\s*(.+)");
                    if (cardMatch.Success)
                    {
                        info.Name = cardMatch.Groups[1].Value.Trim();
                    }

                    var driverMatch = Regex.Match(output, @"Driver\s+name\s*:\s*(.+)");
                    if (driverMatch.Success)
                    {
                        info.Driver = driverMatch.Groups[1].Value.Trim();
                    }

                    var busMatch = Regex.Match(output, @"Bus\s+info\s*:\s*(.+)");
                    if (busMatch.Success)
                    {
                        info.BusInfo = busMatch.Groups[1].Value.Trim();
                    }

                    // 构建描述信息
                    var descParts = new List<string>();
                    if (!string.IsNullOrEmpty(info.Driver))
                        descParts.Add($"驱动: {info.Driver}");
                    if (!string.IsNullOrEmpty(info.BusInfo))
                        descParts.Add($"总线: {info.BusInfo}");
                    
                    info.Description = string.Join(", ", descParts);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取Linux摄像头信息时出错: {ex.Message}");
            }
            
            return info;
        }
    }
}