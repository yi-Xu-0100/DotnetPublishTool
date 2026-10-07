using DotnetPublishTool.App.Services;
using DotnetPublishTool.App.ViewModels;
using DotnetPublishTool.Core.Services;

namespace DotnetPublishTool.App;

/// <summary>界面层的组装入口（手写依赖注入，避免引入第三方容器）。</summary>
public static class AppServices
{
    public static MainViewModel CreateMainViewModel(IDialogService dialogs, AppSettingsService? settingsService = null)
    {
        var runner = new DotnetProcessRunner();
        var inspector = new ProjectInspector(runner);
        var profileStore = new PublishProfileStore();
        var workflow = new PublishWorkflow(profileStore, new PublishCommandBuilder(), runner, new ZipPackager());

        var viewModel = new MainViewModel(
            dialogs,
            settingsService ?? new AppSettingsService(),
            inspector,
            profileStore,
            workflow);

        viewModel.Initialize();
        return viewModel;
    }
}
