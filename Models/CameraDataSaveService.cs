using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using OpenCvSharp;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace spectrum.Models
{
    /// <summary>
    /// 帧头结构（32字节）
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FrameHeader
    {
        public uint FrameNumber; // 4字节：帧序号
        public ulong Timestamp; // 8字节：时间戳（毫秒）
        public ushort Width; // 2字节：画面宽度
        public ushort Height; // 2字节：画面高度
        public ushort BrightestRow; // 2字节：最亮行号
        public uint DataSize; // 4字节：数据大小
        public ushort HeaderVersion; // 2字节：头版本
        public ushort Reserved1; // 2字节：保留
        public uint Reserved2; // 4字节：保留
    }

    /// <summary>
    /// 帧尾结构（8字节）
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FrameFooter
    {
        public uint Checksum; // 4字节：校验和
        public uint FrameEndMarker; // 4字节：帧结束标记
    }

    /// <summary>
    /// 文件头结构（64字节）
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FileHeader
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Magic; // 16字节：文件标识

        public uint FileVersion; // 4字节：文件版本
        public uint FrameCount; // 4字节：帧数量
        public ushort FrameWidth; // 2字节：帧宽度
        public ushort FrameHeight; // 2字节：帧高度
        public uint FrameSize; // 4字节：每帧大小
        public ulong StartTimestamp; // 8字节：开始时间戳
        public ulong EndTimestamp; // 8字节：结束时间戳

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string CameraInfo; // 16字节：摄像头信息
    }

    /// <summary>
    /// 最亮行信息
    /// </summary>
    public class BrightestRowInfo
    {
        /// <summary>
        /// 行号
        /// </summary>
        public ushort RowIndex { get; set; }

        /// <summary>
        /// 该行的像素数据
        /// </summary>
        public byte[]? RowData { get; set; }

        /// <summary>
        /// 平均亮度值
        /// </summary>
        public uint AverageBrightness { get; set; }
    }


    /// <summary>
    /// 保存选项配置
    /// </summary>
    public class SaveOptions
    {
        /// <summary>
        /// 是否保存原始帧（默认true）
        /// </summary>
        public bool SaveRawFrames { get; set; } = true;

        /// <summary>
        /// 是否保存处理后的图像（默认false）
        /// </summary>
        public bool SaveProcessedFrames { get; set; } = false;

        /// <summary>
        /// 是否录制视频文件（默认false）
        /// </summary>
        public bool RecordVideo { get; set; } = false;


        /// <summary>
        /// 每文件最大帧数（默认512）
        /// </summary>
        public int MaxFramesPerFile { get; set; } = 512;

        /// <summary>
        /// 是否启用帧头帧尾结构（默认true）
        /// </summary>
        public bool EnableFrameStructure { get; set; } = true;
    }

    /// <summary>
    /// 摄像头原始数据保存服务
    /// 支持保存原始帧数据、处理后的图像和视频录制
    /// </summary>
    public class CameraDataSaveService : IDisposable
    {
        private string? _saveDirectory;
        private bool _isRecording;
        private VideoWriter? _videoWriter;
        private int _frameCounter;
        private int _fileFrameCounter;
        private int _currentFileIndex;
        private FileStream? _currentBinaryFile;
        private readonly object _lockObject = new();
        private const uint FRAME_END_MARKER = 0xAA55AA55;
        private const string FILE_MAGIC = "SPECTRUM_RAW_DATA";
        private const uint FILE_VERSION = 1;

        public event EventHandler<string>? StatusUpdated;

        /// <summary>
        /// 是否正在录制
        /// </summary>
        public bool IsRecording => _isRecording;

        /// <summary>
        /// 当前保存目录
        /// </summary>
        public string? SaveDirectory => _saveDirectory;

        /// <summary>
        /// 已保存的帧数
        /// </summary>
        public int SavedFrameCount => _frameCounter;

        /// <summary>
        /// 保存选项
        /// </summary>
        public SaveOptions Options { get; set; } = new SaveOptions();

        /// <summary>
        /// 选择保存目录
        /// </summary>
        public async Task<bool> SelectSaveDirectoryAsync(IStorageProvider storageProvider)
        {
            try
            {
                var options = new FolderPickerOpenOptions
                {
                    Title = "选择摄像头数据保存目录",
                    AllowMultiple = false
                };

                var result = await storageProvider.OpenFolderPickerAsync(options);

                if (result.Count > 0)
                {
                    _saveDirectory = result[0].Path.LocalPath;
                    RaiseStatusUpdated($"已选择保存目录: {_saveDirectory}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                RaiseStatusUpdated($"选择保存目录失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 开始录制摄像头数据
        /// </summary>
        public bool StartRecording(int width, int height, double fps = 30.0, string cameraInfo = "")
        {
            lock (_lockObject)
            {
                if (_isRecording)
                {
                    RaiseStatusUpdated("已在录制中");
                    return false;
                }

                if (string.IsNullOrEmpty(_saveDirectory))
                {
                    RaiseStatusUpdated("请先选择保存目录");
                    return false;
                }

                try
                {
                    // 创建保存目录（如果不存在）
                    Directory.CreateDirectory(_saveDirectory);

                    // 创建时间戳文件夹
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string sessionDir = Path.Combine(_saveDirectory, $"CameraData_{timestamp}");
                    Directory.CreateDirectory(sessionDir);

                    // 根据选项创建子目录
                    if (Options.SaveRawFrames)
                    {
                        Directory.CreateDirectory(Path.Combine(sessionDir, "RawFrames"));
                    }

                    if (Options.SaveProcessedFrames)
                    {
                        Directory.CreateDirectory(Path.Combine(sessionDir, "ProcessedFrames"));
                    }

                    // 根据选项初始化视频写入器
                    if (Options.RecordVideo)
                    {
                        string videoPath = Path.Combine(sessionDir, "recording.mp4");
                        _videoWriter = new VideoWriter(videoPath, FourCC.H264, fps, new Size(width, height));

                        if (!_videoWriter.IsOpened())
                        {
                            _videoWriter.Dispose();
                            _videoWriter = null;
                            RaiseStatusUpdated("无法创建视频文件");
                            return false;
                        }
                    }

                    // 初始化二进制文件记录
                    _frameCounter = 0;
                    _fileFrameCounter = 0;
                    _currentFileIndex = 0;
                    _currentBinaryFile = null;

                    _saveDirectory = sessionDir; // 更新为会话目录
                    _isRecording = true;

                    // 创建第一个二进制文件
                    if (Options.EnableFrameStructure)
                    {
                        CreateNewBinaryFile(width, height, cameraInfo);
                    }

                    string optionsInfo = GetOptionsDescription();
                    RaiseStatusUpdated($"开始录制到: {sessionDir} ({optionsInfo})");
                    return true;
                }
                catch (Exception ex)
                {
                    RaiseStatusUpdated($"开始录制失败: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 停止录制
        /// </summary>
        public void StopRecording()
        {
            lock (_lockObject)
            {
                if (!_isRecording) return;

                try
                {
                    _videoWriter?.Release();
                    _videoWriter?.Dispose();
                    _videoWriter = null;

                    // 关闭当前二进制文件
                    if (_currentBinaryFile != null)
                    {
                        _currentBinaryFile.Close();
                        _currentBinaryFile.Dispose();
                        _currentBinaryFile = null;
                    }

                    _isRecording = false;
                    RaiseStatusUpdated($"录制已停止，共保存 {_frameCounter} 帧");
                }
                catch (Exception ex)
                {
                    RaiseStatusUpdated($"停止录制时出错: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 保存原始帧数据（Windows预览循环使用）
        /// </summary>
        public void SaveRawFrame(byte[] frameData, int width, int height, string format = "bin",
            bool saveProcessed = true)
        {
            if (!_isRecording || frameData == null || frameData.Length == 0) return;

            Task.Run(() =>
            {
                try
                {
                    lock (_lockObject)
                    {
                        if (!_isRecording) return;

                        // 使用帧头帧尾结构保存二进制数据
                        if (Options.EnableFrameStructure && Options.SaveRawFrames)
                        {
                            // 自动检测像素格式：计算每像素字节数
                            int bytesPerPixel = frameData.Length / (width * height);
                            WriteFrameWithStructure(frameData, width, height, bytesPerPixel);
                        }
                        else if (Options.SaveRawFrames)
                        {
                            // 传统保存方式
                            string frameFileName = $"frame_{_frameCounter:D6}.{format}";
                            string rawPath = Path.Combine(_saveDirectory!, "RawFrames", frameFileName);
                            File.WriteAllBytes(rawPath, frameData);
                        }

                        // 根据选项保存处理后的帧（如果需要）
                        if (Options.SaveProcessedFrames && saveProcessed)
                        {
                            // 将原始数据转换为Mat进行处理
                            unsafe
                            {
                                fixed (byte* ptr = frameData)
                                {
                                    using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC3, (IntPtr)ptr,
                                        width * 3);
                                    using var processed = new Mat();

                                    // 转换为灰度图作为处理示例
                                    Cv2.CvtColor(mat, processed, ColorConversionCodes.BGR2GRAY);

                                    string processedPath = Path.Combine(_saveDirectory!, "ProcessedFrames",
                                        $"processed_{_frameCounter:D6}.png");
                                    Cv2.ImWrite(processedPath, processed);
                                }
                            }
                        }

                        // 根据选项写入视频文件
                        if (Options.RecordVideo)
                        {
                            unsafe
                            {
                                fixed (byte* ptr = frameData)
                                {
                                    using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC3, (IntPtr)ptr,
                                        width * 3);
                                    _videoWriter?.Write(mat);
                                }
                            }
                        }

                        _frameCounter++;

                        // 每100帧更新一次状态
                        if (_frameCounter % 100 == 0)
                        {
                            RaiseStatusUpdated($"已保存 {_frameCounter} 帧");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"保存帧数据时出错: {ex.Message}");
                }
            });
        }
        

        /// <summary>
        /// 创建录制信息文件
        /// </summary>
        public void CreateRecordingInfo(int width, int height, double fps, string cameraName)
        {
            if (string.IsNullOrEmpty(_saveDirectory)) return;

            try
            {
                string infoPath = Path.Combine(_saveDirectory, "recording_info.txt");
                string info = $"录制信息\n" +
                              $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                              $"摄像头: {cameraName}\n" +
                              $"分辨率: {width}x{height}\n" +
                              $"帧率: {fps:F1} fps\n" +
                              $"操作系统: {Environment.OSVersion}\n" +
                              $"应用版本: spectrum v1.0\n";

                File.WriteAllText(infoPath, info);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建录制信息文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前保存选项的描述
        /// </summary>
        private string GetOptionsDescription()
        {
            var options = new List<string>();

            if (Options.SaveProcessedFrames)
                options.Add("处理后图像");

            if (Options.RecordVideo)
                options.Add("视频录制");


            return options.Count != 0 ? string.Join(", ", options) : "仅计数";
        }

        private void RaiseStatusUpdated(string status)
        {
            StatusUpdated?.Invoke(this, status);
        }

        /// <summary>
        /// 创建新的二进制文件
        /// </summary>
        private void CreateNewBinaryFile(int width, int height, string cameraInfo)
        {
            try
            {
                // 关闭当前文件（如果存在）
                _currentBinaryFile?.Close();
                _currentBinaryFile?.Dispose();

                // 创建新文件名
                string fileName = $"raw_data_{_currentFileIndex:D4}.bin";
                string filePath = Path.Combine(_saveDirectory!, fileName);

                // 创建新文件
                _currentBinaryFile = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _fileFrameCounter = 0;

                RaiseStatusUpdated($"创建新数据文件: {fileName}");
            }
            catch (Exception ex)
            {
                RaiseStatusUpdated($"创建二进制文件失败: {ex.Message}");
                _currentBinaryFile = null;
            }
        }
        

        /// <summary>
        /// 写入带帧头帧尾的二进制数据
        /// </summary>
        private void WriteFrameWithStructure(byte[] frameData, int width, int height, int bytesPerPixel)
        {
            if (_currentBinaryFile == null) return;

            try
            {
                // 查找最亮行
                var brightestRowInfo = FindBrightestRow(frameData, width, height, bytesPerPixel);
                Debug.WriteLine($"最亮行: {brightestRowInfo.RowIndex}");

                // 创建帧头
                var frameHeader = new FrameHeader
                {
                    FrameNumber = (uint)_frameCounter,
                    Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Width = (ushort)width,
                    Height = (ushort)height,
                    BrightestRow = brightestRowInfo.RowIndex,
                    DataSize = (uint)frameData.Length,
                    HeaderVersion = 1,
                    Reserved1 = 0,
                    Reserved2 = 0
                };

                // 创建帧尾
                var frameFooter = new FrameFooter
                {
                    Checksum = CalculateChecksum(frameData),
                    FrameEndMarker = FRAME_END_MARKER
                };

                // 写入帧头
                byte[] headerBytes = StructureToByteArray(frameHeader);
                _currentBinaryFile.Write(headerBytes, 0, headerBytes.Length);

                // 写入帧数据
                _currentBinaryFile.Write(frameData, 0, frameData.Length);

                // 写入帧尾
                byte[] footerBytes = StructureToByteArray(frameFooter);
                _currentBinaryFile.Write(footerBytes, 0, footerBytes.Length);

                _fileFrameCounter++;

                // 如果达到最大帧数，创建新文件
                if (_fileFrameCounter >= Options.MaxFramesPerFile)
                {
                    _currentBinaryFile.Close();
                    _currentBinaryFile.Dispose();
                    _currentBinaryFile = null;

                    _currentFileIndex++;
                    CreateNewBinaryFile(width, height, "");
                }
            }
            catch (Exception ex)
            {
                RaiseStatusUpdated($"写入帧数据失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 查找最亮行（支持灰度图、BGR、BGRA格式）
        /// </summary>
        private BrightestRowInfo FindBrightestRow(byte[] pixelData, int width, int height, int bytesPerPixel = 1)
        {
            var result = new BrightestRowInfo { RowIndex = 0, RowData = null, AverageBrightness = 0 };

            // 输入验证
            if (pixelData == null || pixelData.Length == 0)
                return result;

            int expectedLength = width * height * bytesPerPixel;
            if (pixelData.Length < expectedLength)
            {
                Debug.WriteLine($"像素数据长度不足，预期: {expectedLength}, 实际: {pixelData.Length}");
                return result;
            }

            ushort brightestRow = 0;
            uint maxBrightness = uint.MinValue; // 初始化为最小值
            byte[]? brightestRowData = null;

            unsafe
            {
                fixed (byte* ptr = pixelData)
                {
                    for (int y = 0; y < height; y++)
                    {
                        long rowStartOffset = (long)y * width * bytesPerPixel;
                        if (rowStartOffset >= pixelData.Length)
                        {
                            Debug.WriteLine($"行 {y} 起始位置超出范围");
                            continue;
                        }

                        uint rowBrightness = 0;
                        int validPixels = 0;

                        for (int x = 0; x < width; x++)
                        {
                            long pixelOffset = rowStartOffset + x * bytesPerPixel;
                            if (pixelOffset + bytesPerPixel - 1 >= pixelData.Length)
                            {
                                Debug.WriteLine($"像素 ({x},{y}) 超出范围");
                                break;
                            }

                            uint brightness = 0;

                            // 增加对1字节/像素（灰度图）的支持
                            if (bytesPerPixel == 1)
                            {
                                // 灰度图：单个字节直接表示亮度（0-255）
                                brightness = ptr[pixelOffset];
                            }
                            else if (bytesPerPixel == 3)
                            {
                                // BGR格式
                                byte b = ptr[pixelOffset];
                                byte g = ptr[pixelOffset + 1];
                                byte r = ptr[pixelOffset + 2];
                                brightness = (uint)(0.299 * r + 0.587 * g + 0.114 * b);
                            }
                            else if (bytesPerPixel == 4)
                            {
                                // BGRA格式（忽略Alpha通道）
                                byte b = ptr[pixelOffset];
                                byte g = ptr[pixelOffset + 1];
                                byte r = ptr[pixelOffset + 2];
                                brightness = (uint)(0.299 * r + 0.587 * g + 0.114 * b);
                            }
                            else
                            {
                                Debug.WriteLine($"不支持的像素格式: {bytesPerPixel}字节/像素");
                                return result; // 遇到不支持的格式时返回
                            }

                            rowBrightness += brightness;
                            validPixels++;
                        }

                        if (validPixels > 0)
                        {
                            uint avgBrightness = rowBrightness / (uint)validPixels;
                            if (avgBrightness > maxBrightness)
                            {
                                maxBrightness = avgBrightness;
                                brightestRow = (ushort)y;

                                // 提取当前最亮行的像素数据
                                brightestRowData = new byte[width * bytesPerPixel];
                                Marshal.Copy((IntPtr)(ptr + rowStartOffset), brightestRowData, 0,
                                    brightestRowData.Length);
                            }
                        }
                    }
                }
            }
            
            if (brightestRowData != null)
            {
                   var decimalStr = string.Join(", ", brightestRowData);
                   Debug.WriteLine("最亮内容 十进制字节值: " + decimalStr);
            }
            result.RowIndex = brightestRow;
            result.RowData = brightestRowData;
            result.AverageBrightness = maxBrightness;
            return result;
        }


        /// <summary>
        /// 计算校验和
        /// </summary>
        private uint CalculateChecksum(byte[] data)
        {
            uint checksum = 0;
            foreach (byte b in data)
            {
                checksum = (checksum << 5) + checksum + b;
            }

            return checksum;
        }

        /// <summary>
        /// 结构体转字节数组
        /// </summary>
        private byte[] StructureToByteArray<T>(T structure) where T : struct
        {
            int size = Marshal.SizeOf(structure);
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(structure, ptr, false);
                Marshal.Copy(ptr, arr, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return arr;
        }

        /// <summary>
        /// 将字节数组转换为结构体
        /// </summary>
        private T ByteArrayToStructure<T>(byte[] byteArray) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            if (byteArray.Length < size)
                throw new ArgumentException("字节数组长度不足");

            IntPtr ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.Copy(byteArray, 0, ptr, size);
                return Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Dispose()
        {
            StopRecording();
            _currentBinaryFile?.Dispose();
        }
    }
}