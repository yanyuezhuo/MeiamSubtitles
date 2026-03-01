using Jellyfin.MeiamSub.SubtitleCat.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using System;

namespace Jellyfin.MeiamSub.SubtitleCat
{
    /// <summary>
    /// 插件入口
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2026-03-01</para>
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>
    {
        /// <summary>
        /// 插件ID
        /// </summary>
        public override Guid Id => new Guid("B1A2C3D4-E5F6-7890-ABCD-EF1234567890");

        /// <summary>
        /// 插件名称
        /// </summary>
        public override string Name => "MeiamSub.SubtitleCat";

        /// <summary>
        /// 插件描述
        /// </summary>
        public override string Description => "Download subtitles from SubtitleCat.com";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }
    }
}
