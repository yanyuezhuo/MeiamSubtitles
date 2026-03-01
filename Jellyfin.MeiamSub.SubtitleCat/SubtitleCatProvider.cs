using Jellyfin.MeiamSub.SubtitleCat.Model;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.MeiamSub.SubtitleCat
{
    /// <summary>
    /// SubtitleCat 字幕提供程序
    /// 负责与 subtitlecat.com 进行交互，通过文件名搜索并下载字幕。
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2026-03-01</para>
    /// </summary>
    public class SubtitleCatProvider : ISubtitleProvider, IHasOrder
    {
        #region 变量声明

        private readonly ILogger<SubtitleCatProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private const string BaseUrl = "https://www.subtitlecat.com";
        private const string SearchUrl = BaseUrl + "/index.php?search={0}";

        public int Order => 100;
        public string Name => "MeiamSub.SubtitleCat";

        /// <summary>
        /// 支持电影、剧集
        /// </summary>
        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

        #endregion

        #region 构造函数

        public SubtitleCatProvider(ILogger<SubtitleCatProvider> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _logger.LogInformation($"{Name} Init");
        }

        #endregion

        #region 查询字幕

        /// <summary>
        /// 搜索字幕 (ISubtitleProvider 接口实现)
        /// 根据媒体信息请求字幕列表。
        /// </summary>
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("{Provider} Search | Received request for {Path}", Name, request?.MediaPath ?? "NULL");

            var subtitles = await SearchSubtitlesAsync(request, cancellationToken);

            return subtitles;
        }

        /// <summary>
        /// 查询字幕
        /// </summary>
        private async Task<IEnumerable<RemoteSubtitleInfo>> SearchSubtitlesAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (request == null)
                {
                    _logger.LogInformation("{Provider} Search | Request is null", Name);
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                var language = NormalizeLanguage(request.Language);
                var subtitleCatLang = MapToSubtitleCatLanguage(language);

                if (string.IsNullOrEmpty(subtitleCatLang))
                {
                    _logger.LogInformation("{Provider} Search | Cannot map language '{Lang}' to SubtitleCat language code, skip.", Name, request.Language);
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                if (string.IsNullOrEmpty(request.MediaPath))
                {
                    _logger.LogInformation("{Provider} Search | MediaPath is empty, skip.", Name);
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                var fileName = Path.GetFileNameWithoutExtension(request.MediaPath);

                // 清理文件名以获得更好的搜索词
                var searchTerm = CleanFileName(fileName);

                _logger.LogInformation("{Provider} Search | Target -> {FileName} | SearchTerm -> {Term} | Language -> {Lang} ({CatLang})",
                    Name, fileName, searchTerm, language, subtitleCatLang);

                // 第一步：搜索
                var searchResults = await SearchOnSiteAsync(searchTerm, cancellationToken);

                if (searchResults.Count == 0)
                {
                    _logger.LogInformation("{Provider} Search | No search results found.", Name);
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.LogInformation("{Provider} Search | Found {Count} search results.", Name, searchResults.Count);

                var remoteSubtitles = new List<RemoteSubtitleInfo>();

                // 第二步：访问各结果的详情页，提取对应语言的下载链接
                // 限制最多查看前5个结果，避免请求过多
                var resultsToCheck = searchResults.Take(5).ToList();

                foreach (var result in resultsToCheck)
                {
                    try
                    {
                        var downloadLinks = await GetDownloadLinksAsync(result.DetailUrl, subtitleCatLang, cancellationToken);

                        foreach (var link in downloadLinks)
                        {
                            remoteSubtitles.Add(new RemoteSubtitleInfo()
                            {
                                Id = Base64Encode(JsonSerializer.Serialize(new DownloadSubInfo
                                {
                                    Url = link,
                                    Format = "srt",
                                    Language = request.Language,
                                    TwoLetterISOLanguageName = request.TwoLetterISOLanguageName,
                                })),
                                Name = $"[MEIAMSUB] {result.Title} | {request.TwoLetterISOLanguageName} | SubtitleCat",
                                Author = "SubtitleCat",
                                ProviderName = Name,
                                Format = "srt",
                                Comment = $"Source: subtitlecat.com",
                                IsHashMatch = false
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{Provider} Search | Error fetching detail page {Url}", Name, result.DetailUrl);
                    }
                }

                _logger.LogInformation("{Provider} Search | Summary -> Found {Count} subtitles", Name, remoteSubtitles.Count);

                return remoteSubtitles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Provider} Search | Exception -> {Message}", Name, ex.Message);
            }

            _logger.LogInformation("{Provider} Search | Summary -> Found 0 subtitles", Name);
            return Array.Empty<RemoteSubtitleInfo>();
        }

        /// <summary>
        /// 在 subtitlecat.com 上搜索
        /// </summary>
        private async Task<List<SearchResult>> SearchOnSiteAsync(string searchTerm, CancellationToken cancellationToken)
        {
            var results = new List<SearchResult>();

            var url = string.Format(SearchUrl, Uri.EscapeDataString(searchTerm));

            _logger.LogInformation("{Provider} Search | Requesting -> {Url}", Name, url);

            using var httpClient = _httpClientFactory.CreateClient(Name);
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Provider} Search | HTTP {Status} from search page", Name, response.StatusCode);
                return results;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            // 解析搜索结果页面中的链接
            // 格式: <a href="/subs/{id}/{filename}.html">Title</a>
            // 或完整URL: https://www.subtitlecat.com/subs/{id}/{filename}.html
            var regex = new Regex(
                @"href\s*=\s*[""'](?:https?://www\.subtitlecat\.com)?(/subs/\d+/[^""']+\.html)[""'][^>]*>([^<]+)</a>",
                RegexOptions.IgnoreCase);

            var matches = regex.Matches(html);

            foreach (Match match in matches)
            {
                var detailPath = match.Groups[1].Value;
                var title = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();

                // 跳过 "Load More" 等非字幕链接
                if (title.Equals("Load More", StringComparison.OrdinalIgnoreCase))
                    continue;

                var detailUrl = BaseUrl + detailPath;

                // 去重
                if (results.Any(r => r.DetailUrl == detailUrl))
                    continue;

                results.Add(new SearchResult
                {
                    Title = title,
                    DetailUrl = detailUrl
                });
            }

            return results;
        }

        /// <summary>
        /// 从详情页获取指定语言的下载链接
        /// </summary>
        private async Task<List<string>> GetDownloadLinksAsync(string detailUrl, string subtitleCatLang, CancellationToken cancellationToken)
        {
            var links = new List<string>();

            _logger.LogInformation("{Provider} Detail | Requesting -> {Url}", Name, detailUrl);

            using var httpClient = _httpClientFactory.CreateClient(Name);
            var response = await httpClient.GetAsync(detailUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Provider} Detail | HTTP {Status} from detail page", Name, response.StatusCode);
                return links;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            // 从详情页解析下载链接
            // 格式: href="/subs/{n}/{filename}-{langCode}.srt" 或完整URL
            // 匹配对应语言的 .srt 下载链接
            var pattern = string.Format(
                @"href\s*=\s*[""'](?:https?://www\.subtitlecat\.com)?(/subs/\d+/[^""']+-{0}\.srt)[""']",
                Regex.Escape(subtitleCatLang));

            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var matches = regex.Matches(html);

            foreach (Match match in matches)
            {
                var downloadPath = match.Groups[1].Value;
                var downloadUrl = BaseUrl + downloadPath;

                if (!links.Contains(downloadUrl))
                {
                    links.Add(downloadUrl);
                    _logger.LogInformation("{Provider} Detail | Found download link -> {Url}", Name, downloadUrl);
                }
            }

            return links;
        }

        #endregion

        #region 下载字幕

        /// <summary>
        /// 获取字幕内容 (ISubtitleProvider 接口实现)
        /// 根据字幕 ID 下载具体的字幕文件流。
        /// </summary>
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            _logger.LogInformation("{Provider} DownloadSub | Request -> {Id}", Name, id);

            return await DownloadSubAsync(id, cancellationToken);
        }

        /// <summary>
        /// 下载字幕
        /// </summary>
        private async Task<SubtitleResponse> DownloadSubAsync(string info, CancellationToken cancellationToken)
        {
            try
            {
                var downloadSub = JsonSerializer.Deserialize<DownloadSubInfo>(Base64Decode(info));

                if (downloadSub == null)
                {
                    return new SubtitleResponse();
                }

                _logger.LogInformation("{Provider} DownloadSub | Url -> {Url} | Format -> {Format} | Language -> {Lang}",
                    Name, downloadSub.Url, downloadSub.Format, downloadSub.Language);

                using var httpClient = _httpClientFactory.CreateClient(Name);
                var response = await httpClient.GetAsync(downloadSub.Url, cancellationToken);

                _logger.LogInformation("{Provider} DownloadSub | Response -> {Status}", Name, response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                    return new SubtitleResponse()
                    {
                        Language = downloadSub.Language,
                        IsForced = false,
                        Format = downloadSub.Format,
                        Stream = stream,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Provider} DownloadSub | Exception -> [{Type}] {Message}", Name, ex.GetType().Name, ex.Message);
            }

            return new SubtitleResponse();
        }

        #endregion

        #region 内部方法

        /// <summary>
        /// Base64 编码
        /// </summary>
        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        /// <summary>
        /// Base64 解码
        /// </summary>
        public static string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }

        /// <summary>
        /// 清理文件名，提取有效的搜索词
        /// 去掉分辨率、编码、来源标签等干扰词
        /// </summary>
        private static string CleanFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return fileName;

            // 将常见分隔符替换为空格
            var cleaned = fileName.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

            // 移除常见的分辨率、编码格式、来源标签
            var patterns = new[]
            {
                @"\b(720p|1080p|2160p|4K|UHD)\b",
                @"\b(BluRay|BrRip|BDRip|WEBRip|WEB|HDTV|DVDRip|HDRip|Remux|TS|CAM|Peacock)\b",
                @"\b(x264|x265|H264|H265|HEVC|VC\s*1|AVC|XviD)\b",
                @"\b(DTS|DTS\s*HD|MA|AAC|AC3|FLAC|TrueHD|Atmos)\b",
                @"\b(YIFY|RARBG|SPARKS|FGT|SWTYBLZ|TiMPE|PANAM|ViSiON|HomeTheater)\b",
                @"\b(5\.1|7\.1|2\.0)\b",
                @"\b(eng|en|chi|chn|zho|track\d+)\b",
                @"\b(Ripped\s+By\s+\w+(\s+at\s+\w+)?)\b",
                @"\s+",
            };

            foreach (var pattern in patterns)
            {
                cleaned = Regex.Replace(cleaned, pattern, " ", RegexOptions.IgnoreCase);
            }

            // 去掉前后空格和多余空格
            cleaned = Regex.Replace(cleaned.Trim(), @"\s+", " ");

            return cleaned;
        }

        /// <summary>
        /// 规范化语言代码
        /// </summary>
        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language)) return language;

            if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zho", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("chi", StringComparison.OrdinalIgnoreCase))
            {
                return "chi";
            }
            if (language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("eng", StringComparison.OrdinalIgnoreCase))
            {
                return "eng";
            }

            return language;
        }

        /// <summary>
        /// 将 Jellyfin 语言代码映射到 SubtitleCat 使用的语言代码
        /// SubtitleCat 使用 ISO 639-1 双字母代码 (如 zh-CN, en, ja 等)
        /// </summary>
        private static string MapToSubtitleCatLanguage(string jellyfinLang)
        {
            if (string.IsNullOrEmpty(jellyfinLang)) return null;

            var langMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 中文
                { "chi", "zh-CN" },
                { "zho", "zh-CN" },
                { "zh", "zh-CN" },
                { "zh-CN", "zh-CN" },
                { "zh-TW", "zh-TW" },
                { "zh-HK", "zh-TW" },

                // 英语
                { "eng", "en" },
                { "en", "en" },

                // 日语
                { "jpn", "ja" },
                { "ja", "ja" },

                // 韩语
                { "kor", "ko" },
                { "ko", "ko" },

                // 法语
                { "fre", "fr" },
                { "fra", "fr" },
                { "fr", "fr" },

                // 德语
                { "ger", "de" },
                { "deu", "de" },
                { "de", "de" },

                // 西班牙语
                { "spa", "es" },
                { "es", "es" },

                // 葡萄牙语
                { "por", "pt-BR" },
                { "pt", "pt-BR" },

                // 俄语
                { "rus", "ru" },
                { "ru", "ru" },

                // 阿拉伯语
                { "ara", "ar" },
                { "ar", "ar" },

                // 印地语
                { "hin", "hi" },
                { "hi", "hi" },

                // 泰语
                { "tha", "th" },
                { "th", "th" },

                // 越南语
                { "vie", "vi" },
                { "vi", "vi" },

                // 意大利语
                { "ita", "it" },
                { "it", "it" },

                // 荷兰语
                { "dut", "nl" },
                { "nld", "nl" },
                { "nl", "nl" },

                // 波兰语
                { "pol", "pl" },
                { "pl", "pl" },

                // 土耳其语
                { "tur", "tr" },
                { "tr", "tr" },

                // 印尼语
                { "ind", "id" },
                { "id", "id" },

                // 马来语
                { "may", "ms" },
                { "msa", "ms" },
                { "ms", "ms" },

                // 瑞典语
                { "swe", "sv" },
                { "sv", "sv" },

                // 丹麦语
                { "dan", "da" },
                { "da", "da" },

                // 挪威语
                { "nor", "no" },
                { "nob", "no" },
                { "no", "no" },

                // 芬兰语
                { "fin", "fi" },
                { "fi", "fi" },

                // 捷克语
                { "cze", "cs" },
                { "ces", "cs" },
                { "cs", "cs" },

                // 罗马尼亚语
                { "rum", "ro" },
                { "ron", "ro" },
                { "ro", "ro" },

                // 匈牙利语
                { "hun", "hu" },
                { "hu", "hu" },

                // 希腊语
                { "gre", "el" },
                { "ell", "el" },
                { "el", "el" },

                // 保加利亚语
                { "bul", "bg" },
                { "bg", "bg" },

                // 克罗地亚语
                { "hrv", "hr" },
                { "hr", "hr" },

                // 希伯来语
                { "heb", "iw" },
                { "he", "iw" },
            };

            return langMap.TryGetValue(jellyfinLang, out var catLang) ? catLang : null;
        }

        #endregion

        #region 内部类

        /// <summary>
        /// 搜索结果
        /// </summary>
        private class SearchResult
        {
            public string Title { get; set; }
            public string DetailUrl { get; set; }
        }

        #endregion
    }
}
