using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using spectrum.ViewModels;
using System;

namespace spectrum.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    
    public MainWindow()
    {
        InitializeComponent();
        
        // 确保视图模型正确初始化
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        
        // 注册窗口关闭事件
        Closing += MainWindow_Closing;
        
        // 当选择摄像头后自动开始预览
        Loaded += MainWindow_Loaded;
    }
    
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        
        // 初始化分析区域
        if (_viewModel != null)
        {
            // 设置默认分析区域为图像中心的一个矩形
            _viewModel.AnalysisRegion = new Rect(0, 100, 600, 50);
        }
    }
    
    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        // 窗口关闭时释放资源
        if (_viewModel != null)
        {
            _viewModel.StopPreview();
            _viewModel.Dispose();
        }
    }
}