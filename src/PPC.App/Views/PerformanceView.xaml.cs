using System.Windows.Controls;

namespace Ppc.App.Views;

/// <summary>性能调节页：模式选择、处理器状态、睿频策略，全部绑定 PerformanceViewModel 的真实状态。</summary>
public partial class PerformanceView : UserControl
{
    public PerformanceView()
    {
        InitializeComponent();
    }
}
