using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace spectrum.Models
{
    /// <summary>
    /// 摄像头预览服务（Linux 使用 FFmpeg 管道；Windows 使用 OpenCV VideoCapture）
    /// 适配 mini.runtime：Linux 全程不触发 OpenCV videoio。
    /// </summary>
    public class CameraPreviewService : IDisposable
    {
        private Camera? _currentCamera;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _previewTask;
        private bool _isRunning;
        private readonly object _lockObject = new();

        private WriteableBitmap? _previewBitmap;
        private int _previewWidth = 640;
        private int _previewHeight = 480;

        private readonly int _targetFps = 20;
        private readonly int _frameDelayMs;

        // Windows: OpenCV VideoCapture
        private VideoCapture? _videoCapture;

        // Linux: FFmpeg Pipe
        private bool _usePipeBackend;
        private Process? _pipeProc;
        private Stream? _pipeStream;
        private int _pipeW, _pipeH, _pipeStride;  // BGR24: stride=W*3
        private byte[]? _pipeBuffer;

        // 数据保存服务
        private CameraDataSaveService? _dataSaveService;

        public IImage? PreviewImage => _previewBitmap;
        public event EventHandler<IImage?>? PreviewImageUpdated;
        public event EventHandler<string>? StatusUpdated;
        public event EventHandler<VideoStats>? VideoStatsUpdated;

        /// <summary>
        /// 数据保存服务
        /// </summary>
        public CameraDataSaveService DataSaveService => _dataSaveService ??= new CameraDataSaveService();

        public CameraPreviewService()
        {
            _frameDelayMs = Math.Max(1, 1000 / _targetFps);

            if (!CheckOpenCvCoreOnly())
                throw new Exception("OpenCV 初始化失败（core/imgproc/imgcodecs）。");

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                StopPreview();
                Dispose();
            };
        }

        /// <summary>仅检测 OpenCV 核心能力，不触发 videoio。</summary>
        private bool CheckOpenCvCoreOnly()
        {
            try
            {
                using var test = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(128));
                using var gray = new Mat();
                Cv2.CvtColor(test, gray, ColorConversionCodes.BGR2GRAY);
                Debug.WriteLine($"OpenCV 版本: {Cv2.GetVersionString()}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenCV 核心初始化失败: {ex}");
                return false;
            }
        }

        public async Task StartPreview(Camera camera, int width = 640, int height = 480)
        {
            if (camera == null)
            {
                RaiseStatusUpdated("未选择摄像头");
                return;
            }

            lock (_lockObject)
            {
                if (_isRunning) StopPreview();
                _currentCamera = camera;
                _previewWidth = width;
                _previewHeight = height;
                _isRunning = true;
            }

            try
            {
                _previewBitmap = new WriteableBitmap(
                    new PixelSize(_previewWidth, _previewHeight),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                await InitializeVideoBackend();

                lock (_lockObject)
                {
                    _cancellationTokenSource = new CancellationTokenSource();
                    _previewTask = Task.Run(() => PreviewLoop(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
                    RaiseStatusUpdated($"已开始预览: {camera.Name}");
                }
            }
            catch (Exception ex)
            {
                _isRunning = false;
                RaiseStatusUpdated($"预览初始化失败: {ex.Message}");
                Debug.WriteLine($"预览初始化失败: {ex}");
            }
        }

        private async Task InitializeVideoBackend()
        {
            ReleaseAllBackends();

            if (_currentCamera == null)
                throw new Exception("未选择摄像头");

            if (CameraService.IsLinux)
            {
                _usePipeBackend = true;
                await StartFfmpegPipeAsync(_currentCamera.DeviceId, _previewWidth, _previewHeight, _targetFps);
            }
            else if (CameraService.IsWindows)
            {
                _usePipeBackend = false;
                await StartWindowsCaptureAsync();
            }
            else
            {
                throw new PlatformNotSupportedException("当前平台未适配");
            }
        }

        // ========== Linux: FFmpeg Pipe ==========

        private async Task StartFfmpegPipeAsync(string devicePath, int w, int h, int fps)
        {
            if (string.IsNullOrWhiteSpace(devicePath))
                throw new Exception("Linux 设备路径为空（示例：/dev/video0）");
            if (!File.Exists(devicePath))
                throw new Exception($"设备文件不存在: {devicePath}");
            if (!IsCommandAvailable("ffmpeg"))
                throw new Exception("未找到 ffmpeg，请先安装：sudo apt install -y ffmpeg");

            var pipelines = new[]
{
    // 优先 MJPEG：输入 mjpeg，输出强制 1280x720@31，BGRA 原始帧
    $"-hide_banner -loglevel warning -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
    $"-f v4l2 -input_format mjpeg -framerate 31 -video_size {w}x{h} -i {devicePath} " +
    $"-vf \"scale={w}:{h}:flags=fast_bilinear,fps=31,format=bgra\" -pix_fmt bgra -f rawvideo -",

    // 备选 YUYV422：同样输出 BGRA
    $"-hide_banner -loglevel warning -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
    $"-f v4l2 -input_format yuyv422 -framerate 31 -video_size {w}x{h} -i {devicePath} " +
    $"-vf \"scale={w}:{h}:flags=fast_bilinear,fps=31,format=bgra\" -pix_fmt bgra -f rawvideo -",

    // 兜底：让 v4l2 自选输入格式，输出端依旧强制
    $"-hide_banner -loglevel warning -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
    $"-f v4l2 -framerate 31 -video_size {w}x{h} -i {devicePath} " +
    $"-vf \"scale={w}:{h}:flags=fast_bilinear,fps=31,format=bgra\" -pix_fmt bgra -f rawvideo -"
};


            Exception? last = null;
            foreach (var args in pipelines)
            {
                try
                {
                    Debug.WriteLine($"启动 FFmpeg 管道: ffmpeg {args}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) Debug.WriteLine($"[ffmpeg] {e.Data}");
                    };

                    if (!proc.Start()) throw new Exception("ffmpeg 启动失败");
                    proc.BeginErrorReadLine();

                    _pipeProc = proc;
                    _pipeStream = proc.StandardOutput.BaseStream;
                    _pipeW = w; _pipeH = h; _pipeStride = w * 4;
                    _pipeBuffer = new byte[_pipeStride * _pipeH];

                    await Task.Delay(200);
                    if (proc.HasExited)
                        throw new Exception("ffmpeg 进程已退出，可能该像素格式/分辨率不受支持");

                    Debug.WriteLine("FFmpeg 管道就绪");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Debug.WriteLine($"管道方案失败: {ex.Message}");
                    StopFfmpegPipe();
                }
            }

            throw new Exception($"无法启动 FFmpeg 管道：{last?.Message}");
        }

        private void StopFfmpegPipe()
        {
            try { _pipeStream?.Flush(); } catch { }
            try { _pipeStream?.Dispose(); } catch { }
            _pipeStream = null;
            _pipeBuffer = null;

            try
            {
                if (_pipeProc != null && !_pipeProc.HasExited)
                {
                    _pipeProc.Kill(entireProcessTree: true);
                    _pipeProc.WaitForExit(500);
                }
            }
            catch { }
            try { _pipeProc?.Dispose(); } catch { }
            _pipeProc = null;
        }

        private static bool IsCommandAvailable(string name)
        {
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-lc \"command -v {name}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                var outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return !string.IsNullOrWhiteSpace(outp);
            }
            catch { return false; }
        }

        // ========== Windows: OpenCV VideoCapture ==========

        private async Task StartWindowsCaptureAsync()
        {
            ReleaseWindowsCapture();

            var all = await CameraService.GetAvailableCamerasAsync();
            int maxTry = Math.Min(all.Count + 2, 5);
            Exception? last = null;

            for (int i = 0; i < maxTry; i++)
            {
                VideoCapture? cap = null;
                try
                {
                    cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (!cap.IsOpened())
                    {
                        cap.Dispose(); cap = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                    }
                    if (!cap.IsOpened())
                    {
                        cap.Dispose(); cap = new VideoCapture(i, VideoCaptureAPIs.ANY);
                    }

                    if (cap.IsOpened())
                    {
                        cap.Set(VideoCaptureProperties.FrameWidth, _previewWidth);
                        cap.Set(VideoCaptureProperties.FrameHeight, _previewHeight);
                        cap.Set(VideoCaptureProperties.Fps, _targetFps);

                        using var tmp = new Mat();
                        if (cap.Read(tmp) && !tmp.Empty())
                        {
                            _videoCapture = cap;
                            return;
                        }
                    }

                    cap.Dispose();
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { cap?.Dispose(); } catch { }
                }
            }

            throw new Exception($"Windows 打开摄像头失败：{last?.Message ?? "未知错误"}");
        }

        private void ReleaseWindowsCapture()
        {
            try
            {
                if (_videoCapture != null)
                {
                    if (_videoCapture.IsOpened()) _videoCapture.Release();
                    _videoCapture.Dispose();
                }
            }
            catch { }
            finally { _videoCapture = null; }
        }

        private void ReleaseAllBackends()
        {
            StopFfmpegPipe();
            ReleaseWindowsCapture();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // ========== 预览循环 ==========

        private void PreviewLoop(CancellationToken token)
        {
            if (_currentCamera == null || _previewBitmap == null) return;

            try
            {
                if (_usePipeBackend)
                    PipePreviewLoop(token);
                else
                    WindowsPreviewLoop(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                RaiseStatusUpdated($"预览出错: {ex.Message}");
                Debug.WriteLine($"预览出错: {ex}");
            }
        }

        /// <summary>
        /// Linux：从 FFmpeg stdout 读取 BGR24 → 转 BGRA → 写入 WriteableBitmap
        /// </summary>
        private unsafe void PipePreviewLoop(CancellationToken token)
{
    if (_pipeStream == null || _pipeBuffer == null) return;

    var sw = Stopwatch.StartNew();
    int frames = 0;
    double actualFps = 0;

    while (!token.IsCancellationRequested)
    {
        // 读取一整帧（BGRA：W*H*4 字节）
        if (!ReadFull(_pipeStream, _pipeBuffer, _pipeBuffer.Length, token))
        {
            RaiseStatusUpdated("从 FFmpeg 读取帧失败，尝试重连...");
            Thread.Sleep(500);
            try
            {
                StopFfmpegPipe();
                if (_currentCamera != null)
                    StartFfmpegPipeAsync(_currentCamera.DeviceId, _previewWidth, _previewHeight, _targetFps)
                        .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                RaiseStatusUpdated($"重连失败: {ex.Message}");
                Thread.Sleep(1500);
            }
            continue;
        }

        // 将外部 BGRA 缓冲“零拷贝”挂成 Mat，再直接 Blit 到 WriteableBitmap
        try
        {
            fixed (byte* p = _pipeBuffer)
            {
                using var bgra = Mat.FromPixelData(_pipeH, _pipeW, MatType.CV_8UC4, (IntPtr)p, _pipeStride);

                // 保存原始BGRA数据（如果正在录制且启用了原始帧保存选项）
                if (_dataSaveService?.IsRecording == true && _dataSaveService.Options.SaveRawFrames)
                {
                    _dataSaveService.SaveRawFrame(_pipeBuffer, _pipeW, _pipeH, "bin", true);
                }

                var wb = new WriteableBitmap(
                    new PixelSize(_previewWidth, _previewHeight),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                BlitBGRAtoBitmap(bgra, wb, _previewWidth, _previewHeight);

                var old = _previewBitmap;
                _previewBitmap = wb;
                RaisePreviewImageUpdated(_previewBitmap);

                if (old != null)
                {
                    Task.Run(() =>
                    {
                        Thread.Sleep(50);
                        try { old.Dispose(); } catch { }
                    });
                }
            }
        }
        catch
        {
            // 忽略单帧 UI 写入异常，继续下一帧
        }

        frames++;
        if (sw.ElapsedMilliseconds >= 2000)
        {
            actualFps = frames * 1000.0 / sw.ElapsedMilliseconds;
            frames = 0;
            sw.Restart();
            RaiseVideoStatsUpdated(new VideoStats($"{_previewWidth}x{_previewHeight}", actualFps, _frameDelayMs));
        }

        Thread.Sleep(Math.Max(1, _frameDelayMs - 5));
    }
}


        /// <summary>
        /// Windows：VideoCapture 读取 → 调整大小/转 BGRA → 写入 WriteableBitmap
        /// </summary>
        private unsafe void WindowsPreviewLoop(CancellationToken token)
        {
            if (_videoCapture == null) return;

            using var frame = new Mat();
            using var resized = new Mat();
            using var bgra = new Mat();

            while (!token.IsCancellationRequested)
            {
                if (!_videoCapture.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (frame.Width != _previewWidth || frame.Height != _previewHeight)
                    Cv2.Resize(frame, resized, new Size(_previewWidth, _previewHeight));
                else
                    frame.CopyTo(resized);

                if (resized.Channels() == 3)
                    Cv2.CvtColor(resized, bgra, ColorConversionCodes.BGR2BGRA);
                else if (resized.Channels() == 1)
                    Cv2.CvtColor(resized, bgra, ColorConversionCodes.GRAY2BGRA);
                else
                    resized.CopyTo(bgra);

                // 保存原始数据（统一保存为 BGRA）
                if (_dataSaveService?.IsRecording == true && _dataSaveService.Options.SaveRawFrames)
                {
                    int dataSize = _previewWidth * _previewHeight * 4;
                    ReadOnlySpan<byte> span = new ReadOnlySpan<byte>((byte*)bgra.Data.ToPointer(), dataSize);
                    _dataSaveService.SaveRawFrame(span, _previewWidth, _previewHeight, "bin", true);
                }

                try
                {
                    var wb = new WriteableBitmap(
                        new PixelSize(_previewWidth, _previewHeight),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);

                    BlitBGRAtoBitmap(bgra, wb, _previewWidth, _previewHeight);

                    var old = _previewBitmap;
                    _previewBitmap = wb;
                    RaisePreviewImageUpdated(_previewBitmap);

                    if (old != null)
                    {
                        Task.Run(() =>
                        {
                            Thread.Sleep(50);
                            try { old.Dispose(); } catch { }
                        });
                    }
                }
                catch { }

                Thread.Sleep(Math.Max(1, _frameDelayMs - 5));
            }
        }

        private static bool ReadFull(Stream s, byte[] buf, int len, CancellationToken token)
        {
            int pos = 0;
            while (pos < len)
            {
                token.ThrowIfCancellationRequested();
                int n = s.Read(buf, pos, len - pos);
                if (n <= 0) return false;
                pos += n;
            }
            return true;
        }

        /// <summary>把 BGRA Mat 的像素拷到 Avalonia WriteableBitmap。</summary>
        private static unsafe void BlitBGRAtoBitmap(Mat bgra, WriteableBitmap wb, int width, int height)
        {
            if (bgra.Empty()) return;
            using var locked = wb.Lock();
            int bytes = checked(width * height * 4);
            IntPtr src = bgra.Data; // IntPtr，不再对 byte* 调用 ToPointer（避免 CS1061）
            Buffer.MemoryCopy((void*)src, (void*)locked.Address, bytes, bytes);
        }

        private void RaisePreviewImageUpdated(IImage? image)
        {
            try
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    PreviewImageUpdated?.Invoke(this, image);
                else
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => PreviewImageUpdated?.Invoke(this, image));
            }
            catch (Exception ex) { Debug.WriteLine($"UI线程调用出错: {ex.Message}"); }
        }

        private void RaiseStatusUpdated(string status)
        {
            try
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    StatusUpdated?.Invoke(this, status);
                else
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusUpdated?.Invoke(this, status));
            }
            catch (Exception ex) { Debug.WriteLine($"状态更新UI线程调用出错: {ex.Message}"); }
        }

        private void RaiseVideoStatsUpdated(VideoStats stats)
        {
            try
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    VideoStatsUpdated?.Invoke(this, stats);
                else
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => VideoStatsUpdated?.Invoke(this, stats));
            }
            catch (Exception ex) { Debug.WriteLine($"视频统计信息更新UI线程调用出错: {ex.Message}"); }
        }

        public void StopPreview()
        {
            lock (_lockObject)
            {
                if (!_isRunning) return;
                _isRunning = false;

                RaiseStatusUpdated("正在停止预览...");

                try { _cancellationTokenSource?.Cancel(); } catch { }

                try
                {
                    if (_previewTask != null && !_previewTask.Wait(1000))
                        Debug.WriteLine("预览任务未在超时内结束");
                }
                catch { }

                try
                {
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                    _previewTask = null;
                }
                catch { }

                ReleaseAllBackends();

                try { _previewBitmap?.Dispose(); } catch { }
                _previewBitmap = null;
                
                // 通知ViewModel预览图像已清除
                PreviewImageUpdated?.Invoke(this, null);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Thread.Sleep(200);
                RaiseStatusUpdated("已停止预览");
            }
        }

        public void Dispose()
        {
            StopPreview();
        }
    }
}
