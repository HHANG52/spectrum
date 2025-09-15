using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;

namespace spectrum.Models
{
    /// <summary>
    /// OpenCV 初始化与诊断（适配 Sdcb.OpenCvSharp4.mini.runtime.*）
    /// - 初始化不触发 videoio/highgui，确保在 mini.runtime 上稳定通过
    /// - 输出可用于排障的关键信息
    /// </summary>
    public static class OpenCvInitializer
    {
        private static bool _isInitialized;
        private static bool _isAvailable;
        private static string _errorMessage = "";
        private static string _buildInfoSnippet = "";

        /// <summary>OpenCV 是否可用</summary>
        public static bool IsAvailable => _isAvailable;

        /// <summary>初始化/运行时错误信息</summary>
        public static string ErrorMessage => _errorMessage;

        /// <summary>部分构建信息（关键行）</summary>
        public static string BuildInfoSnippet => _buildInfoSnippet;

        /// <summary>
        /// 初始化 OpenCV（mini.runtime 友好）
        /// </summary>
        /// <param name="tryVideoCaptureProbe">
        /// 是否尝试构造 VideoCapture 做额外探测（默认 false）。
        /// 注意：在 mini.runtime 下很可能失败，仅作为可选诊断，不影响主流程。
        /// </param>
        /// <returns>是否初始化成功</returns>
        public static bool Initialize(bool tryVideoCaptureProbe = false)
        {
            if (_isInitialized) return _isAvailable;
            _isInitialized = true;

            try
            {
                Debug.WriteLine("== OpenCV 初始化开始 (mini.runtime 友好) ==");

                // 0) 打印基础环境
                var nativeSearch = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "";
                Debug.WriteLine($"OS: {RuntimeInformation.OSDescription}");
                Debug.WriteLine($"Arch: {RuntimeInformation.ProcessArchitecture}");
                Debug.WriteLine($"BaseDirectory: {AppContext.BaseDirectory}");
                Debug.WriteLine($"NATIVE_DLL_SEARCH_DIRECTORIES: {nativeSearch}");

                // 打印托管/原生装载位置（帮助确认加载的是哪个包/so）
                var asmOpenCvSharp = typeof(Cv2).Assembly;
                Debug.WriteLine($"OpenCvSharp 程序集: {asmOpenCvSharp.GetName().Name}  v{asmOpenCvSharp.GetName().Version}");
                Debug.WriteLine($"OpenCvSharp 位置: {asmOpenCvSharp.Location}");

                // 1) 轻量对象 + 基础算子（core/imgproc）
                using (var probe = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(128)))
                using (var gray = new Mat())
                {
                    Cv2.CvtColor(probe, gray, ColorConversionCodes.BGR2GRAY);
                }

                // 2) 验证 imgcodecs：编码/解码一张内存图片
                using (var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(200)))
                {
                    // PNG 编码再解码
                    var ok = Cv2.ImEncode(".png", img, out var buf);
                    if (!ok || buf == null || buf.Length == 0)
                        throw new Exception("imgcodecs 编码失败（ImEncode 返回空）");
                    using var decoded = Cv2.ImDecode(buf, ImreadModes.Color);
                    if (decoded.Empty()) throw new Exception("imgcodecs 解码失败（ImDecode 返回空）");
                }

                // 3) 版本与精简构建信息（不会刷屏）
                var version = Cv2.GetVersionString();
                Debug.WriteLine($"OpenCV 版本: {version}");
                _buildInfoSnippet = ExtractKeyBuildInfo(Cv2.GetBuildInformation());
                if (!string.IsNullOrWhiteSpace(_buildInfoSnippet))
                {
                    Debug.WriteLine("== Build Information（关键行）==");
                    Debug.WriteLine(_buildInfoSnippet);
                }

                // 4) 可选：VideoCapture 额外探测（mini.runtime 下可能失败）
                if (tryVideoCaptureProbe)
                {
                    try
                    {
                        using var cap = new VideoCapture(); // 不 open，仅验证类型构造
                        Debug.WriteLine("✓ VideoCapture 类型构造成功（注意：mini.runtime 仍不保证可用）");
                    }
                    catch (Exception vcapEx)
                    {
                        Debug.WriteLine($"[可忽略] VideoCapture 构造失败：{vcapEx.GetType().Name} - {vcapEx.Message}");
                        Debug.WriteLine("提示：mini.runtime 未携带 videoio/highgui；若需摄像头/视频，请改为完整 runtime 或自行编译，并安装 GStreamer/V4L2 等依赖。");
                    }
                }

                _isAvailable = true;
                _errorMessage = "";
                Debug.WriteLine("== OpenCV 初始化成功 ==");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                _isAvailable = false;
                _errorMessage =
                    "找不到 OpenCV 相关原生库（DllNotFoundException）。\n" +
                    "常见原因：\n" +
                    "  • 原生 .so 未随应用正确发布/解压；\n" +
                    "  • 目标架构/发行版不匹配（需 arm64 + Ubuntu 22.04/24.04 兼容）；\n" +
                    "  • 系统缺少基础编解码等依赖库（即便是 mini.runtime 也建议安装）。\n\n" +
                    "排查建议：\n" +
                    "  1) 使用匹配 RID 发布：ubuntu.24.04-arm64（或 ubuntu.22.04-arm64），并启用 IncludeNativeLibrariesForSelfExtract；\n" +
                    "  2) 在设备上使用 ldd 检查 libOpenCvSharpExtern.so / libopencv_core.so 等是否有 => not found；\n" +
                    "  3) 安装基础依赖库（libstdc++6/libtbb12/libjpeg-turbo8/libpng16-16/libtiff6/libopenjp2-7/libwebp7/libopenexr25/liblz4-1/zlib1g）。\n\n" +
                    $"异常详情：{ex}";
                Debug.WriteLine(ex.ToString());
                return false;
            }
            catch (BadImageFormatException ex)
            {
                _isAvailable = false;
                _errorMessage =
                    "OpenCV 原生库与当前进程架构不匹配（BadImageFormatException）。\n" +
                    "请确认：\n" +
                    "  • 设备为 ARM64；\n" +
                    "  • 应用发布 RID 为 ubuntu.24.04-arm64（或 22.04-arm64）；\n" +
                    "  • 未混入 x64/armhf 的 .so 文件。\n\n" +
                    $"异常详情：{ex}";
                Debug.WriteLine(ex.ToString());
                return false;
            }
            catch (TypeInitializationException ex)
            {
                _isAvailable = false;
                _errorMessage =
                    "OpenCV 类型初始化失败（TypeInitializationException）。\n" +
                    "通常是依赖库缺失或符号解析失败，请展开内部异常并按缺失项安装依赖。建议执行 ldd 检查。\n\n" +
                    $"异常详情：{ex}";
                Debug.WriteLine(ex.ToString());
                return false;
            }
            catch (Exception ex)
            {
                _isAvailable = false;
                _errorMessage =
                    "OpenCV 初始化失败（其他异常）。请查看异常详情并按提示排查。\n\n" +
                    $"异常详情：{ex}";
                Debug.WriteLine(ex.ToString());
                return false;
            }
        }

        public static string GetDiagnosticInfo()
{
    var sb = new StringBuilder();

    try
    {
        sb.AppendLine("OpenCV 诊断信息");
        sb.AppendLine("----------------");
        sb.AppendLine($"初始化状态 : {(_isInitialized ? "已初始化" : "未初始化")}");
        sb.AppendLine($"可用状态   : {(_isAvailable ? "可用" : "不可用")}");
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            sb.AppendLine("错误信息   :");
            sb.AppendLine(_errorMessage);
        }

        sb.AppendLine();
        sb.AppendLine("环境信息");
        sb.AppendLine($"  OS          : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"  Arch        : {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"  .NET        : {Environment.Version}");
        sb.AppendLine($"  BaseDir     : {AppContext.BaseDirectory}");

        // OpenCV 版本
        try
        {
            sb.AppendLine();
            sb.AppendLine($"OpenCV 版本  : {Cv2.GetVersionString()}");
        }
        catch (Exception e)
        {
            sb.AppendLine();
            sb.AppendLine($"获取版本失败 : {e.Message}");
        }

        // 关键构建信息
        if (!string.IsNullOrWhiteSpace(_buildInfoSnippet))
        {
            sb.AppendLine();
            sb.AppendLine("Build 信息（关键行）");
            sb.AppendLine(_buildInfoSnippet);
        }
    }
    catch (Exception ex)
    {
        // 即使上面任何一步异常，也返回可读文本，而不是漏掉 return
        sb.AppendLine();
        sb.AppendLine("GetDiagnosticInfo 捕获到异常：");
        sb.AppendLine(ex.ToString());
    }

    // ★ 永远有 return：无论上面发生什么，这里都会返回字符串
    return sb.ToString();
}



        /// <summary>重置（用于测试）</summary>
        public static void Reset()
        {
            _isInitialized = false;
            _isAvailable = false;
            _errorMessage = "";
            _buildInfoSnippet = "";
        }

        /// <summary>
        /// 从完整的 BuildInformation 中提取少量关键行，避免日志过长
        /// </summary>
        private static string ExtractKeyBuildInfo(string full)
        {
            if (string.IsNullOrEmpty(full)) return "";
            var keys = new[]
            {
                "OpenCV modules", "To be built", "Disabled", "Disabled by dependency",
                "imgcodecs", "imgproc", "core",
                "videoio", "highgui", "GStreamer", "V4L", "FFMPEG", "GTK", "GUI"
            };

            var lines = full.Split('\n')
                .Where(l => keys.Any(k => l.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(l => l.TrimEnd())
                .ToArray();

            if (lines.Length == 0) return "";
            var sb = new StringBuilder();
            foreach (var l in lines) sb.AppendLine(l);
            return sb.ToString();
        }
    }
}
