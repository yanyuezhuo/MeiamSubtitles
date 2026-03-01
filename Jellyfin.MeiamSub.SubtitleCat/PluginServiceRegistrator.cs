using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Jellyfin.MeiamSub.SubtitleCat
{
    /// <summary>
    /// 插件服务注册器
    /// 负责注册插件所需的依赖服务，如 HTTP 客户端和字幕提供程序。
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2026-03-01</para>
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <summary>
        /// 注册服务
        /// </summary>
        /// <param name="serviceCollection">服务集合</param>
        /// <param name="applicationHost">应用程序宿主</param>
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHttpClient("MeiamSub.SubtitleCat", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            });

            serviceCollection.AddSingleton<ISubtitleProvider, SubtitleCatProvider>();
        }
    }
}
