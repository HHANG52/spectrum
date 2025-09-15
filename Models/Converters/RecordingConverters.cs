using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace spectrum.Models.Converters
{
    /// <summary>
    /// 将录制状态布尔值转换为录制按钮文本
    /// </summary>
    public class BoolToRecordingTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isRecording)
            {
                return isRecording ? "停止录制" : "开始录制";
            }
            return "开始录制";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将录制状态布尔值转换为颜色
    /// </summary>
    public class BoolToRecordingColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isRecording)
            {
                return isRecording ? Brushes.Red : Brushes.Gray;
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将预览状态布尔值转换为预览按钮文本
    /// </summary>
    public class BoolToPreviewTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isRunning)
            {
                return isRunning ? "停止预览" : "开始预览";
            }
            return "开始预览";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将分析区域显示状态布尔值转换为按钮文本
    /// </summary>
    public class BoolToAnalysisTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool showAnalysis)
            {
                return showAnalysis ? "隐藏分析区域" : "显示分析区域";
            }
            return "显示分析区域";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将状态消息转换为颜色
    /// </summary>
    public class StatusColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                if (status.Contains("失败") || status.Contains("错误") || status.Contains("异常"))
                    return Brushes.Red;
                if (status.Contains("成功") || status.Contains("已开始") || status.Contains("就绪"))
                    return Brushes.Green;
                if (status.Contains("警告") || status.Contains("重连"))
                    return Brushes.Orange;
            }
            return Brushes.Black;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 检查字符串是否不为空
    /// </summary>
    public class StringNotEmptyConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !string.IsNullOrEmpty(value as string);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将null转换为布尔值
    /// </summary>
    public class NullToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value == null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将数值为0转换为布尔值
    /// </summary>
    public class ZeroToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int intValue)
                return intValue == 0;
            if (value is double doubleValue)
                return Math.Abs(doubleValue) < 0.001;
            return true;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}