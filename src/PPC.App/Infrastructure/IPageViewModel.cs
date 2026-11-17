namespace Ppc.App.Infrastructure;

/// <summary>导航页面统一契约。</summary>
public interface IPageViewModel
{
    string Key { get; }
    string Title { get; }
    string Icon { get; }
    string Subtitle { get; }
    Task ActivateAsync();
    Task DeactivateAsync();
}
