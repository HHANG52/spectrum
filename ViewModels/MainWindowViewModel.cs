using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using spectrum.Models;

namespace spectrum.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly CameraPreviewService _previewService;
    private bool _disposedValue;
    
    [ObservableProperty]
    private ObservableCollection<Camera> _cameras = [];
    
    [ObservableProperty]
    private Camera? _selectedCamera;
    
    [ObservableProperty]
    private bool _isLoading;
    
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    
    [ObservableProperty]
    private Avalonia.Media.IImage? _cameraPreviewImage;
    
    [ObservableProperty]
    private bool _isPreviewRunning;
    
    [ObservableProperty]
    private string _videoResolution = "0x0";
    
    [ObservableProperty]
    private string _videoFps = "0.0fps";
    
    [ObservableProperty]
    private string _videoDelay = "0ms";

    [ObservableProperty]
    private ObservableCollection<double> _spectrumData = [];

    [ObservableProperty]
    private ObservableCollection<Point> _spectrumPoints = [];

    [ObservableProperty]
    private Rect _analysisRegion = new Rect(0, 10, 600, 100);

    [ObservableProperty]
    private bool _showAnalysisRegion = true;
    
    [ObservableProperty]
    private int _analysisRegionHeight = 50;
    
    [ObservableProperty]
    private int _analysisRegionStartY = 20;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingStatus = "未录制";

    [ObservableProperty]
    private int _recordedFrameCount;

    // 保存选项
    [ObservableProperty]
    private bool _saveRawFrames = true;

    [ObservableProperty]
    private bool _saveProcessedFrames = false;

    [ObservableProperty]
    private bool _recordVideo = false;
    

    [ObservableProperty]
    private string _rawFrameFormat = "PNG";

    // 预览参数
    private readonly int _previewWidth = 640;
    private readonly int _previewHeight = 480;
    private readonly int _targetFps = 20;
    
        
    partial void OnAnalysisRegionHeightChanged(int value)
    {
        UpdateAnalysisRegion();
    }
    
    partial void OnAnalysisRegionStartYChanged(int value)
    {
        UpdateAnalysisRegion();
    }

    private void UpdateAnalysisRegion()
    {
        // 保持X和Width不变，更新Y和Height
        AnalysisRegion = new Rect(
            AnalysisRegion.X,
            AnalysisRegionStartY,
            AnalysisRegion.Width,
            AnalysisRegionHeight
        );
    }
    public MainWindowViewModel()
    {
        // 首先初始化 OpenCV
        if (!Models.OpenCvInitializer.Initialize())
        {
            StatusMessage = $"OpenCV 初始化失败: {Models.OpenCvInitializer.ErrorMessage}";
            Debug.WriteLine($"OpenCV 诊断信息: {Models.OpenCvInitializer.GetDiagnosticInfo()}");
            return; // 如果 OpenCV 初始化失败，不继续初始化摄像头服务
        }

        try
        {
            // 初始化摄像头预览服务
            _previewService = new CameraPreviewService();
            _previewService.PreviewImageUpdated += OnPreviewImageUpdated;
            _previewService.StatusUpdated += OnPreviewStatusUpdated;
            _previewService.VideoStatsUpdated += OnVideoStatsUpdated;
            
            // 订阅数据保存服务事件
            _previewService.DataSaveService.StatusUpdated += OnDataSaveStatusUpdated;
        }
        catch (Exception ex)
        {
            StatusMessage = $"摄像头服务初始化失败: {ex.Message}";
            Debug.WriteLine($"摄像头服务初始化异常: {ex}");
            return;
        }
        
        // 初始化分析区域，使用AnalysisRegionHeight和AnalysisRegionStartY的初始值
        AnalysisRegion = new Rect(
            AnalysisRegion.X,
            AnalysisRegionStartY,
            AnalysisRegion.Width,
            AnalysisRegionHeight
        );
        
        // 在构造函数中加载摄像头列表
        // 使用Task.Run避免构造函数中的await，并使用ConfigureAwait(false)避免死锁
        _ = Task.Run(async () => await LoadCamerasAsync().ConfigureAwait(false));
    }
    
    /// <summary>
    /// 加载系统中可用的摄像头列表
    /// </summary>
    [RelayCommand]
    private async Task LoadCamerasAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在加载摄像头列表...";
            
            // 检查 OpenCV 是否可用
            if (!Models.OpenCvInitializer.IsAvailable)
            {
                StatusMessage = $"无法加载摄像头：{Models.OpenCvInitializer.ErrorMessage}";
                return;
            }
            
            Cameras.Clear();
            
            var availableCameras = await CameraService.GetAvailableCamerasAsync();
            
            foreach (var camera in availableCameras)
            {
                Cameras.Add(camera);
            }
            
            if (Cameras.Count > 0)
            {
                SelectedCamera = Cameras[0];
                StatusMessage = $"已找到 {Cameras.Count} 个摄像头设备";
            }
            else
            {
                StatusMessage = "未找到可用的摄像头设备";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载摄像头列表时出错: {ex.Message}";
            Debug.WriteLine($"加载摄像头列表时出错: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    /// <summary>
    /// 当选择的摄像头改变时触发
    /// </summary>
    partial void OnSelectedCameraChanged(Camera? value)
    {
        if (value != null)
        {
            Debug.WriteLine($"已选择摄像头: {value.Name}, ID: {value.DeviceId}");
            
            // 只有在用户明确启动预览后，切换摄像头时才重新启动预览
            // 移除自动启动预览的逻辑，避免摄像头被意外打开
            if (IsPreviewRunning)
            {
                // 停止当前预览，但不自动重启
                // 用户需要手动点击预览按钮来启动新摄像头的预览
                StopPreview();
                StatusMessage = $"已选择摄像头: {value.Name}，请点击预览按钮开始预览";
            }
            else
            {
                StatusMessage = $"已选择摄像头: {value.Name}";
            }
        }
    }
    
    /// <summary>
    /// 开始摄像头预览
    /// </summary>
    [RelayCommand]
    public void StartPreview()
    {
        if (SelectedCamera == null)
        {
            StatusMessage = "请先选择摄像头";
            return;
        }
        
        try
        {
            // 停止当前预览
            _previewService.StopPreview();
            
            // 开始新的预览
            _previewService.StartPreview(SelectedCamera, 400, 300);
            IsPreviewRunning = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动预览时出错: {ex.Message}";
            Debug.WriteLine($"启动预览时出错: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 停止摄像头预览
    /// </summary>
    [RelayCommand]
    public void StopPreview()
    {
        try
        {
            _previewService.StopPreview();
            IsPreviewRunning = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"停止预览时出错: {ex.Message}";
            Debug.WriteLine($"停止预览时出错: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 切换预览状态
    /// </summary>
    [RelayCommand]
    private void TogglePreview()
    {
        if (IsPreviewRunning)
        {
            StopPreview();
        }
        else
        {
            StartPreview();
        }
    }

    /// <summary>
    /// 选择保存目录
    /// </summary>
    [RelayCommand]
    private async Task SelectSaveDirectoryAsync()
    {
        try
        {
            var topLevel = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;

            if (topLevel?.StorageProvider != null)
            {
                bool success = await _previewService.DataSaveService.SelectSaveDirectoryAsync(topLevel.StorageProvider);
                if (success)
                {
                    StatusMessage = $"保存目录已选择: {_previewService.DataSaveService.SaveDirectory}";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"选择保存目录失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 开始/停止录制
    /// </summary>
    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    /// <summary>
    /// 开始录制
    /// </summary>
    private void StartRecording()
    {
        if (!IsPreviewRunning)
        {
            StatusMessage = "请先开始预览";
            return;
        }

        if (SelectedCamera == null)
        {
            StatusMessage = "请先选择摄像头";
            return;
        }

        try
        {
            // 更新保存选项
            _previewService.DataSaveService.Options.SaveRawFrames = SaveRawFrames;
            _previewService.DataSaveService.Options.SaveProcessedFrames = SaveProcessedFrames;
            _previewService.DataSaveService.Options.RecordVideo = RecordVideo;

            bool success = _previewService.DataSaveService.StartRecording(_previewWidth, _previewHeight, _targetFps);
            if (success)
            {
                IsRecording = true;
                RecordingStatus = "录制中";
                RecordedFrameCount = 0;
                
                // 创建录制信息文件
                _previewService.DataSaveService.CreateRecordingInfo(_previewWidth, _previewHeight, _targetFps, SelectedCamera.Name);
                
                StatusMessage = "开始录制摄像头数据";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"开始录制失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 停止录制
    /// </summary>
    private void StopRecording()
    {
        try
        {
            _previewService.DataSaveService.StopRecording();
            IsRecording = false;
            RecordingStatus = "已停止";
            StatusMessage = $"录制已停止，共保存 {RecordedFrameCount} 帧";
        }
        catch (Exception ex)
        {
            StatusMessage = $"停止录制失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 保存单张图像
    /// </summary>
    [RelayCommand]
    private async Task SaveSingleImageAsync()
    {
        if (!IsPreviewRunning)
        {
            StatusMessage = "请先开始预览";
            return;
        }

        try
        {
            // 这里需要从当前预览中获取Mat数据
            // 由于当前架构限制，我们先提示用户选择保存目录
            if (string.IsNullOrEmpty(_previewService.DataSaveService.SaveDirectory))
            {
                await SelectSaveDirectoryAsync();
                if (string.IsNullOrEmpty(_previewService.DataSaveService.SaveDirectory))
                {
                    return;
                }
            }

            StatusMessage = "单张图像保存功能需要在预览循环中实现具体的Mat获取逻辑";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存图像失败: {ex.Message}";
        }
    }
    
    /// <summary>
    /// 预览图像更新事件处理
    /// </summary>
    private void OnPreviewImageUpdated(object? sender, Avalonia.Media.IImage? image)
    {
        // 确保在UI线程上安全更新，避免窗口缩放时的空引用异常
        try
        {
            // 确保在UI线程上更新
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnPreviewImageUpdated(sender, image));
                return;
            }
            
            // 创建图像的副本，避免原始图像被释放后出现问题
            IImage? imageCopy = null;
            
            if (image is WriteableBitmap writeableBitmap)
            {
                // 对于WriteableBitmap，创建一个新的副本
                try
                {
                    // 检查WriteableBitmap是否有效
                    if (writeableBitmap.PixelSize.Width <= 0 || writeableBitmap.PixelSize.Height <= 0)
                    {
                        Debug.WriteLine("无效的WriteableBitmap尺寸");
                        imageCopy = null;
                        return;
                    }
                    
                    // 使用更安全的方式创建和复制位图
                    try
                    {
                        // 创建相同尺寸的新WriteableBitmap
                        var newBitmap = new WriteableBitmap(
                            writeableBitmap.PixelSize,
                            writeableBitmap.Dpi,
                            writeableBitmap.Format,
                            writeableBitmap.AlphaFormat);
                        
                        // 复制像素数据
                        using (var srcLock = writeableBitmap.Lock())
                        {
                            // 检查源锁定是否成功
                            if (srcLock == null || srcLock.Address == IntPtr.Zero)
                            {
                                Debug.WriteLine("源WriteableBitmap锁定失败");
                                imageCopy = null;
                                return;
                            }
                            
                            using (var dstLock = newBitmap.Lock())
                            {
                                // 检查目标锁定是否成功
                                if (dstLock == null || dstLock.Address == IntPtr.Zero)
                                {
                                    Debug.WriteLine("目标WriteableBitmap锁定失败");
                                    imageCopy = null;
                                    return;
                                }
                                
                                unsafe
                                {
                                    int size = srcLock.RowBytes * writeableBitmap.PixelSize.Height;
                                    if (size > 0)
                                    {
                                        Buffer.MemoryCopy(
                                            srcLock.Address.ToPointer(),
                                            dstLock.Address.ToPointer(),
                                            size,
                                            size);
                                    }
                                }
                            }
                        }
                        
                        imageCopy = newBitmap;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"复制WriteableBitmap时出错: {ex.Message}");
                        // 如果复制失败，尝试使用原始图像
                        imageCopy = image;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"处理WriteableBitmap时出错: {ex.Message}");
                    // 如果处理失败，使用null
                    imageCopy = null;
                }
            }
            else
            {
                // 对于其他类型的图像，直接使用原始图像
                imageCopy = image;
            }
            
            // 安全地更新图像引用
            var oldImage = CameraPreviewImage;
            
            // 更新图像引用，包括处理null值的情况
            if (imageCopy != null)
            {
                CameraPreviewImage = imageCopy;
            }
            else if (image != null)
            {
                // 如果复制失败但原始图像可用，使用原始图像
                CameraPreviewImage = image;
            }
            // 如果有预览图像，分析光谱数据
            if (CameraPreviewImage != null && ShowAnalysisRegion)
            {
                // 在UI线程上分析光谱数据
                // 使用 _ = 显式忽略任务，表明我们不需要等待它完成
                _ = Task.Run(() => AnalyzeSpectrumData());
            }
            
            // 延迟释放旧图像资源，避免UI正在使用时释放
            if (oldImage != null && oldImage != image && oldImage is IDisposable disposable)
            {
                // 使用 _ = 显式忽略任务，表明我们不需要等待它完成
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false); // 增加延迟时间，确保UI完成使用
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"释放旧图像资源时出错: {ex.Message}");
                        // 忽略释放错误
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新预览图像时出错: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 预览状态更新事件处理
    /// </summary>
    private void OnPreviewStatusUpdated(object? sender, string status)
    {
        // CameraPreviewService已经确保在UI线程调用，直接更新
        StatusMessage = status;
    }
    
    /// <summary>
    /// 视频统计信息更新事件处理
    /// </summary>
    private void OnVideoStatsUpdated(object? sender, VideoStats stats)
    {
        // CameraPreviewService已经确保在UI线程调用，直接更新
        VideoResolution = stats.Resolution;
        VideoFps = $"{stats.Fps:F1}fps";
        VideoDelay = $"{stats.DelayMs}ms";
    }

    /// <summary>
    /// 数据保存状态更新事件处理
    /// </summary>
    private void OnDataSaveStatusUpdated(object? sender, string status)
    {
        // 确保在UI线程上更新
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnDataSaveStatusUpdated(sender, status));
            return;
        }

        RecordingStatus = status;
        
        // 更新录制帧数（从状态消息中提取）
        if (status.Contains("已保存") && status.Contains("帧"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(status, @"已保存 (\d+) 帧");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int frameCount))
            {
                RecordedFrameCount = frameCount;
            }
        }
    }

    /// <summary>
    /// 分析光谱数据
    /// </summary>
    public void AnalyzeSpectrumData()
    {
        // 确保在UI线程上执行
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(AnalyzeSpectrumData);
            return;
        }
        
        try
        {
            if (CameraPreviewImage == null || !ShowAnalysisRegion)
            {
                return;
            }

            // 获取分析区域
            var region = AnalysisRegion;
            if (region.Width <= 0 || region.Height <= 0)
            {
                return;
            }

            // 创建一个本地副本，避免在处理过程中图像被释放
            var localImage = CameraPreviewImage;
            
            // 清除旧数据
            SpectrumData.Clear();

            // 直接从CameraPreviewImage获取像素数据
            if (localImage is WriteableBitmap bitmap)
            {
                try
                {
                    // 获取图像尺寸
                    int imageWidth = bitmap.PixelSize.Width;
                    int imageHeight = bitmap.PixelSize.Height;
                    
                    // 确保分析区域在图像范围内
                    int startX = Math.Max(0, (int)region.X);
                    int startY = Math.Max(0, (int)region.Y);
                    int endX = Math.Min(imageWidth - 1, (int)(region.X + region.Width));
                    int endY = Math.Min(imageHeight - 1, (int)(region.Y + region.Height));
                    
                    int regionWidth = endX - startX + 1;
                    if (regionWidth <= 0)
                    {
                        Debug.WriteLine("分析区域宽度无效");
                        return;
                    }
                    
                    // 创建临时数据集合，避免直接修改ObservableCollection
                    var tempData = new System.Collections.Generic.List<double>();
                    
                    // 锁定位图以获取像素数据
                    using (var lockedBitmap = bitmap.Lock())
                    {
                        unsafe
                        {
                            byte* pixelData = (byte*)lockedBitmap.Address;
                            int stride = lockedBitmap.RowBytes;
                            
                            // 对每一列计算平均亮度值
                            for (int x = startX; x <= endX; x++)
                            {
                                double columnSum = 0;
                                int pixelCount = 0;
                                
                                // 对当前列的每个像素计算亮度值
                                for (int y = startY; y <= endY; y++)
                                {
                                    // 获取像素在一维数组中的索引 (BGRA格式，每像素4字节)
                                    int pixelIndex = y * stride + x * 4;
                                    
                                    // 确保索引在有效范围内
                                    if (pixelIndex + 2 < stride * imageHeight)
                                    {
                                        // 获取RGB值
                                        byte b = pixelData[pixelIndex];
                                        byte g = pixelData[pixelIndex + 1];
                                        byte r = pixelData[pixelIndex + 2];
                                        
                                        // 计算亮度 (使用标准亮度公式)
                                        // Y = 0.299R + 0.587G + 0.114B
                                        double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                                        
                                        columnSum += luminance;
                                        pixelCount++;
                                    }
                                }
                                
                                // 计算该列的平均亮度
                                double averageLuminance = pixelCount > 0 ? columnSum / pixelCount : 0;
                                
                                // 添加到临时数据集合
                                tempData.Add(averageLuminance);
                            }
                        }
                    }
                    
                    // 将临时数据复制到ObservableCollection
                    foreach (var value in tempData)
                    {
                        SpectrumData.Add(value);
                    }
                    
                    // 归一化光谱数据到0-100范围，便于显示
                    NormalizeSpectrumData();
                    
                    StatusMessage = $"已分析区域: X={region.X:F0}, Y={region.Y:F0}, 宽={region.Width:F0}, 高={region.Height:F0}, 数据点数={SpectrumData.Count}";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"分析WriteableBitmap时出错: {ex.Message}");
                    StatusMessage = $"分析图像数据时出错: {ex.Message}";
                }
            }
            else if (localImage is Bitmap regularBitmap)
            {
                try
                {
                    // 对于普通Bitmap，我们使用简单的替代方案
                    StatusMessage = "正在处理图像数据...";
                    
                    // 生成与区域宽度相匹配的数据点数
                    int dataPoints = (int)region.Width;
                    for (int i = 0; i < dataPoints; i++)
                    {
                        // 使用正弦波模拟光谱数据
                        double value = 50 + 30 * Math.Sin(i * Math.PI / dataPoints);
                        SpectrumData.Add(value);
                    }
                    
                    StatusMessage = $"已分析区域: X={region.X:F0}, Y={region.Y:F0}, 宽={region.Width:F0}, 高={region.Height:F0}, 数据点数={SpectrumData.Count} (模拟数据)";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"处理Bitmap时出错: {ex.Message}");
                    StatusMessage = $"处理图像数据时出错: {ex.Message}";
                }
            }
            else
            {
                StatusMessage = "无法获取图像数据，请确保摄像头正常工作";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"分析光谱数据时出错: {ex.Message}");
            StatusMessage = $"分析光谱数据时出错: {ex.Message}";
        }
    }

    /// <summary>
    /// 更新分析区域
    /// </summary>
    [RelayCommand]
    private void UpdateAnalysisRegion(Rect rect)
    {
        AnalysisRegion = rect;
        AnalyzeSpectrumData();
    }
    
    /// <summary>
    /// 更新分析区域（带坐标和尺寸参数）
    /// </summary>
    public void UpdateAnalysisRegionWithParams(double x, double y, double width, double height)
    {
        AnalysisRegion = new Rect(x, y, width, height);
        AnalyzeSpectrumData();
    }

    /// <summary>
    /// 切换分析区域显示
    /// </summary>
    [RelayCommand]
    private void ToggleAnalysisRegion()
    {
        ShowAnalysisRegion = !ShowAnalysisRegion;
        if (ShowAnalysisRegion)
        {
            AnalyzeSpectrumData();
        }
    }
    
    /// <summary>
    /// 归一化光谱数据到0-100范围
    /// </summary>
    private void NormalizeSpectrumData()
    {
        if (SpectrumData.Count == 0)
        {
            SpectrumPoints.Clear();
            return;
        }
            
        // 找出最大值和最小值
        double minValue = double.MaxValue;
        double maxValue = double.MinValue;
        
        foreach (var value in SpectrumData)
        {
            minValue = Math.Min(minValue, value);
            maxValue = Math.Max(maxValue, value);
        }
        
        // 如果最大值和最小值相等，则所有值都设为50
        if (Math.Abs(maxValue - minValue) < 0.001)
        {
            for (int i = 0; i < SpectrumData.Count; i++)
            {
                SpectrumData[i] = 50;
            }
        }
        else
        {
            // 归一化到0-100范围
            double range = maxValue - minValue;
            for (int i = 0; i < SpectrumData.Count; i++)
            {
                double normalizedValue = ((SpectrumData[i] - minValue) / range) * 100;
                SpectrumData[i] = normalizedValue;
            }
        }
        
        // 生成Polyline的点集
        GenerateSpectrumPoints();
    }

    /// <summary>
    /// 生成光谱数据点集用于Polyline绘制
    /// </summary>
    private void GenerateSpectrumPoints()
    {
        SpectrumPoints.Clear();
        
        if (SpectrumData.Count == 0)
        {
            return;
        }

        // 与XAML中Canvas的尺寸匹配
        double canvasWidth = 730;   // 可绘制区域宽度
        double canvasHeight = 150;  // 可绘制区域高度
        
        // 如果只有一个数据点
        if (SpectrumData.Count == 1)
        {
            double y = canvasHeight - (SpectrumData[0] * canvasHeight / 100);
            SpectrumPoints.Add(new Point(canvasWidth / 2, y));
            return;
        }
        
        // 计算点之间的水平间距
        double xStep = SpectrumData.Count > 1 ? canvasWidth / (SpectrumData.Count - 1) : 0;
        
        for (int i = 0; i < SpectrumData.Count; i++)
        {
            // X坐标从0开始到canvasWidth
            double x = i * xStep;
            // Y坐标从canvasHeight到0(翻转坐标系，使0在顶部)
            double y = canvasHeight - (SpectrumData[i] * canvasHeight / 100);
            SpectrumPoints.Add(new Point(x, y));
        }
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // 释放托管资源
                _previewService.PreviewImageUpdated -= OnPreviewImageUpdated;
                _previewService.StatusUpdated -= OnPreviewStatusUpdated;
                _previewService.VideoStatsUpdated -= OnVideoStatsUpdated;
                _previewService.DataSaveService.StatusUpdated -= OnDataSaveStatusUpdated;
                _previewService.Dispose();
            }
            
            _disposedValue = true;
        }
    }
    
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}