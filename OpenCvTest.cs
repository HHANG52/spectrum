using System;
using System.Diagnostics;
using OpenCvSharp;

namespace spectrum
{
    /// <summary>
    /// OpenCV 测试程序，用于验证 OpenCV 是否正确安装和配置
    /// </summary>
    public static class OpenCvTest
    {
        /// <summary>
        /// 运行 OpenCV 测试
        /// </summary>
        public static void RunTest()
        {
            Console.WriteLine("=== OpenCV 测试程序 ===");
            Console.WriteLine();

            try
            {
                // 测试 1: 基本初始化
                Console.WriteLine("测试 1: 基本初始化");
                using var testMat = new Mat(100, 100, MatType.CV_8UC3);
                Console.WriteLine("✓ Mat 对象创建成功");

                // 测试 2: 版本信息
                Console.WriteLine("\n测试 2: 版本信息");
                string version = Cv2.GetVersionString();
                Console.WriteLine($"✓ OpenCV 版本: {version}");

                // 测试 3: VideoCapture 创建
                Console.WriteLine("\n测试 3: VideoCapture 创建");
                using var testCapture = new VideoCapture();
                Console.WriteLine("✓ VideoCapture 对象创建成功");

                // 测试 4: 基本图像处理
                Console.WriteLine("\n测试 4: 基本图像处理");
                using var srcMat = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(128));
                using var dstMat = new Mat();
                Cv2.CvtColor(srcMat, dstMat, ColorConversionCodes.BGR2GRAY);
                Console.WriteLine($"✓ 颜色转换成功: {srcMat.Width}x{srcMat.Height} BGR -> {dstMat.Width}x{dstMat.Height} GRAY");

                // 测试 5: 摄像头枚举（不实际打开）
                Console.WriteLine("\n测试 5: 摄像头枚举测试");
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        using var capture = new VideoCapture(i, VideoCaptureAPIs.ANY);
                        if (capture.IsOpened())
                        {
                            Console.WriteLine($"✓ 找到摄像头索引: {i}");
                            capture.Release();
                        }
                        else
                        {
                            Console.WriteLine($"- 摄像头索引 {i}: 未找到或无法打开");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"- 摄像头索引 {i}: 异常 - {ex.Message}");
                    }
                }

                Console.WriteLine("\n=== 所有测试通过！OpenCV 工作正常 ===");
            }
            catch (TypeInitializationException ex)
            {
                Console.WriteLine($"\n❌ OpenCV 类型初始化失败:");
                Console.WriteLine($"错误: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"内部异常: {ex.InnerException.Message}");
                }
                Console.WriteLine("\n可能的解决方案:");
                Console.WriteLine("1. 确保所有 OpenCV 包版本一致");
                Console.WriteLine("2. 清理并重新构建项目");
                Console.WriteLine("3. 检查是否安装了正确的运行时包");
                Console.WriteLine("4. 在 Linux 上，确保安装了必要的系统库");
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"\n❌ OpenCV 动态库未找到:");
                Console.WriteLine($"错误: {ex.Message}");
                Console.WriteLine("\n可能的解决方案:");
                Console.WriteLine("1. 安装适合当前平台的 OpenCV 运行时包");
                Console.WriteLine("2. 检查项目配置中的运行时包引用");
                Console.WriteLine("3. 确认目标平台与运行时包匹配");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ OpenCV 测试失败:");
                Console.WriteLine($"错误: {ex.Message}");
                Console.WriteLine($"类型: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"内部异常: {ex.InnerException.Message}");
                }
            }

            Console.WriteLine("\n按任意键继续...");
            Console.ReadKey();
        }

        /// <summary>
        /// 获取系统信息
        /// </summary>
        public static void PrintSystemInfo()
        {
            Console.WriteLine("=== 系统信息 ===");
            Console.WriteLine($"操作系统: {Environment.OSVersion}");
            Console.WriteLine($"运行时版本: {Environment.Version}");
            Console.WriteLine($"处理器架构: {Environment.ProcessorCount} 核心");
            Console.WriteLine($"工作目录: {Environment.CurrentDirectory}");
            Console.WriteLine();
        }
    }
}