using StdUnit.Tags;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Reflection;
using System.Windows;
using usbtest.Tags.Logicets;
using R3;
using StdUnit.Tags.R3;

namespace usbtest;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public IServiceProvider? Root { get; private set; }
    internal ITagsProjectCtrl Ctrl { get; private set; } = null!;

    /// <summary>
    /// 观测 ITagsProject
    /// </summary>
    internal Observable<ITagsProject?> ObservableProject { get; private set; } = null!; 

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureServies();
        var app = builder.Build();
        this.Root = app.Services;
        this.Ctrl = this.Root.GetRequiredService<ITagsProjectCtrl>();
        this.Ctrl.OnStartingException = ex =>
        {
            //MessageBox.Show($"Tags处理启动异常:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return Task.FromResult(true);
        };

        // 上游 ObserveStartedOrStopped() 是 hot 事件，启动事件可能早于窗口订阅就发完：
        // 1) 在 StartPollAsync 之前先 Connect，确保不会漏掉启动事件；
        // 2) Replay(1) 缓存最近一个项目，后订阅者（MainWindow）也能立即拿到；
        // 3) Prepend 用 Func 重载，让“当前项目”在订阅时读取，而不是启动时快照成 null。
        var projectStream = this.Ctrl.ObserveStartedOrStopped()
            .Select(evt => evt.Project)
            .Prepend(() => this.Ctrl.Project)
            .DistinctUntilChanged()
            .Replay(1);
        projectStream.Connect();
        this.ObservableProject = projectStream;

        StartTagsPoll(this.Root, this.Ctrl);
        StartWeb(app);
    }

    private void StartTagsPoll(IServiceProvider sp, ITagsProjectCtrl ctrl)
    {
        var th1 = new Thread(async () =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<App>();

            var dir = Path.Combine(AppContext.BaseDirectory, "Tags");
            await ctrl.StartPollAsync(dir, null, (proj, sp, ct) =>
            {
                proj.Logicets.Add(new HeartBeatLogicet(
                    proj.Channels,
                    proj.Tags,
                    loggerFactory.CreateLogger<HeartBeatLogicet>()
                ));

                proj.RunnerStarted += (grp, ch) =>
                {
                    logger.LogInformation("Tags处理开始,grp={grpName}", grp.TagName());
                    return Task.CompletedTask;
                };
                proj.RunnerCrashed += (grp, ch, ex) =>
                {
                    logger.LogError(ex, "Tags处理异常,grp={grpName}", grp.TagName());
                    //MessageBox.Show($"Tags处理发生异常：{ex.Message}");
                    return Task.CompletedTask;
                };

                return Task.CompletedTask;
            });
        });
        th1.IsBackground = true;
        th1.Start();
    }

    private static void StartWeb(WebApplication app)
    {
        app.ConfigureMiddlewares();
        var th2 = new Thread(() =>
        {
            app.Run("http://localhost:3001");
        });
        th2.IsBackground = true;
        th2.Start();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (this.Ctrl is not null)
        {
            await this.Ctrl.StopAsync();
        }
        Environment.Exit(0);
    }

}

