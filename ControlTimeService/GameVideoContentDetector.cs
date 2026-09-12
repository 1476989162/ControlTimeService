using System;
using System.Linq;

namespace ControlTimeService
{
    /// <summary>
    /// 游戏视频内容识别：窗口标题 / UIA / OCR 共用关键词与白名单。
    /// 楚新钓（及异写）始终放行。
    /// </summary>
    public static class GameVideoContentDetector
    {
        public static readonly string[] AllowedExceptions =
        {
            "楚新钓", "楚心钓"
        };

        public static readonly string[] GameKeywords =
        {
            "游戏", "小游戏", "秒玩", "即玩", "试玩", "闯关",
            "抖音游戏", "抖音小游戏", "快手游戏", "快手小游戏", "即点即玩",
            "电竞", "手游", "端游", "网游", "单机游戏",
            "王者", "王者荣耀", "原神", "吃鸡", "和平精英", "英雄联盟", "LOL", "DOTA",
            "游戏主播", "游戏直播", "游戏实况", "游戏攻略", "游戏解说", "游戏视频",
            "实况", "攻略", "解说", "通关", "战绩", "排位", "Steam",
            "minecraft", "我的世界", "蛋仔", "蛋仔派对", "迷你世界", "第五人格",
            "明日方舟", "崩坏", "阴阳师", "火影", "穿越火线", "DNF",
            "修狗", "地铁逃生", "我开始贼溜", "金铲铲", "三角洲行动",
            "鸣潮", "无畏契约", "瓦罗兰特", "永劫无间", "CS2", "CS:GO",
            "Roblox", "Fortnite", "绝地求生", "PUBG", "APEX",
            "手游推荐", "新游", "开箱", "抽卡", "氪金", "上分", "连胜", "连败"
        };

        public static readonly string[] ShortVideoPlatformHints =
        {
            "抖音", "douyin", "aweme", "快手", "kuaishou", "kwai",
            "小红书", "xiaohongshu", "xhs", "rednote",
            "xiaohongshu.com", "www.xiaohongshu.com",
            "西瓜视频", "ixigua", "火山小视频", "huoshan",
            "哔哩哔哩", "bilibili", "b23.tv", "B站",
            "抖音网页版", "douyin.com", "www.douyin.com",
            "kuaishou.com", "www.kuaishou.com",
            "直播", "短视频", "推荐"
        };

        /// <summary>小红书进程 / 标题 / 网址特征（供 AppMonitor 全禁开关使用）。</summary>
        public static readonly string[] XiaohongshuProcessHints =
        {
            "xiaohongshu", "xhs", "rednote", "xingin"
        };

        public static readonly string[] XiaohongshuTitleHints =
        {
            "小红书", "xiaohongshu", "xhs", "rednote",
            "xiaohongshu.com", "www.xiaohongshu.com"
        };

        public static readonly string[] BrowserProcessHints =
        {
            "chrome", "msedge", "msedgewebview2", "firefox", "brave",
            "opera", "vivaldi", "360chrome", "360se", "qqbrowser",
            "sogouexplorer", "iexplore", "chromium", "edgedev", "browser"
        };

        public static bool ContainsAllowedException(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return AllowedExceptions.Any(k =>
                text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        public static bool ContainsGameKeyword(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return GameKeywords.Any(k =>
                text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        public static bool LooksLikeShortVideoPlatform(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ShortVideoPlatformHints.Any(k =>
                text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsXiaohongshu(string processName, string title)
        {
            if (!string.IsNullOrWhiteSpace(processName) &&
                XiaohongshuProcessHints.Any(p =>
                    processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (!string.IsNullOrWhiteSpace(title) &&
                XiaohongshuTitleHints.Any(k =>
                    title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        public static bool LooksLikeDouyinEmbed(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // 豆包内出现抖音相关标识即视为内嵌抖音浏览
            return text.Contains("抖音", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("aweme", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBrowserProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return false;

            return BrowserProcessHints.Any(p =>
                processName.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 综合标题、UIA、OCR 文本判断是否为需拦截的游戏视频内容。
        /// </summary>
        public static bool IsGameVideoContent(
            string processName,
            string windowTitle,
            string uiaText,
            string ocrText,
            bool isDouyin,
            bool isDoubao,
            bool isKuaishou,
            bool isBrowser,
            bool isXiaohongshu = false)
        {
            string combined = string.Join(" ",
                windowTitle ?? string.Empty,
                uiaText ?? string.Empty,
                ocrText ?? string.Empty);

            if (ContainsAllowedException(combined))
                return false;

            if (ContainsGameKeyword(combined))
                return true;

            // 豆包内嵌抖音：标题/控件出现抖音浏览特征时纳入监控
            if (isDoubao && LooksLikeDouyinEmbed(combined))
                return true;

            // 浏览器打开短视频站且已有平台特征时，依赖深度文本；若仅平台无游戏词则不拦截
            // （游戏词已在上方命中；此处不再把「仅打开抖音网页」当作游戏）
            if (isBrowser && LooksLikeShortVideoPlatform(combined) && ContainsGameKeyword(combined))
                return true;

            // 抖音/快手/小红书进程：仅靠标题「抖音」不够，必须有游戏关键词（来自 UIA/OCR）
            if ((isDouyin || isKuaishou || isXiaohongshu) && ContainsGameKeyword(combined))
                return true;

            return false;
        }

        public static string FindMatchedKeyword(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return GameKeywords.FirstOrDefault(k =>
                text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
    }
}
