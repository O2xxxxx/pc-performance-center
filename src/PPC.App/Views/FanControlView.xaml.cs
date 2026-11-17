using System.Windows.Controls;
using Ppc.App.ViewModels;

namespace Ppc.App.Views;

/// <summary>风扇控制页：状态、模式、手动转速与自绘温度曲线。</summary>
public partial class FanControlView : UserControl
{
    public FanControlView()
    {
        InitializeComponent();

        // 曲线编辑器里拖动/增删节点后，刷新 VM 里「当前温度 → 建议转速」的文本
        CurveEditor.CurveEdited += (_, _) =>
        {
            try
            {
                (DataContext as FanControlViewModel)?.NotifyCurveEdited();
            }
            catch (Exception ex)
            {
                Ppc.Core.Services.AppLog.Instance.Error("刷新曲线预览失败", ex);
            }
        };
    }
}
