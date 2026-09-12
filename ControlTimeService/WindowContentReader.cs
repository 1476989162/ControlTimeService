using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ControlTimeService
{
    /// <summary>
    /// 从前台窗口采集 UIA 控件文本与 OCR 画面文字（带节流与缓存）。
    /// </summary>
    public sealed class WindowContentReader : IDisposable
    {
        private const int UiaIntervalMs = 2000;
        private const int OcrIntervalMs = 4000;
        private const int MaxUiaNodes = 120;
        private const int MaxUiaDepth = 8;
        private const int MaxOcrWidth = 960;

        private readonly object _lock = new();
        private IntPtr _cachedHwnd;
        private string _cachedUiaText = string.Empty;
        private string _cachedOcrText = string.Empty;
        private string _cachedBrowserUrl = string.Empty;
        private long _lastUiaTick;
        private long _lastOcrTick;
        private int _ocrRunning;
        private OcrEngine _ocrEngine;
        private bool _ocrEngineTried;

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public string UiaText
        {
            get { lock (_lock) return _cachedUiaText; }
        }

        public string OcrText
        {
            get { lock (_lock) return _cachedOcrText; }
        }

        public string BrowserUrl
        {
            get { lock (_lock) return _cachedBrowserUrl; }
        }

        /// <summary>
        /// 合并标题、地址栏、UIA、OCR 的采样文本。
        /// </summary>
        public string GetCombinedDeepText(string windowTitle)
        {
            lock (_lock)
            {
                return string.Join(" ",
                    windowTitle ?? string.Empty,
                    _cachedBrowserUrl,
                    _cachedUiaText,
                    _cachedOcrText);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _cachedHwnd = IntPtr.Zero;
                _cachedUiaText = string.Empty;
                _cachedOcrText = string.Empty;
                _cachedBrowserUrl = string.Empty;
                _lastUiaTick = 0;
                _lastOcrTick = 0;
            }
        }

        /// <summary>
        /// 在监控 tick 中调用：按节流刷新 UIA，并异步刷新 OCR。
        /// </summary>
        public void Sample(IntPtr hwnd, bool enableUia, bool enableOcr)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd))
            {
                Reset();
                return;
            }

            long now = Environment.TickCount64;

            lock (_lock)
            {
                if (_cachedHwnd != hwnd)
                {
                    _cachedHwnd = hwnd;
                    _cachedUiaText = string.Empty;
                    _cachedOcrText = string.Empty;
                    _cachedBrowserUrl = string.Empty;
                    _lastUiaTick = 0;
                    _lastOcrTick = 0;
                }
            }

            if (enableUia && now - Interlocked.Read(ref _lastUiaTick) >= UiaIntervalMs)
            {
                Interlocked.Exchange(ref _lastUiaTick, now);
                try
                {
                    var (uia, url) = ReadUia(hwnd);
                    lock (_lock)
                    {
                        _cachedUiaText = uia;
                        if (!string.IsNullOrWhiteSpace(url))
                            _cachedBrowserUrl = url;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UIA 采样失败: {ex.Message}");
                }
            }

            if (enableOcr && now - Interlocked.Read(ref _lastOcrTick) >= OcrIntervalMs)
            {
                if (Interlocked.CompareExchange(ref _ocrRunning, 1, 0) == 0)
                {
                    Interlocked.Exchange(ref _lastOcrTick, now);
                    IntPtr target = hwnd;
                    Task.Run(async () =>
                    {
                        try
                        {
                            string text = await RecognizeWindowAsync(target).ConfigureAwait(false);
                            lock (_lock)
                            {
                                if (_cachedHwnd == target)
                                    _cachedOcrText = text ?? string.Empty;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"OCR 采样失败: {ex.Message}");
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _ocrRunning, 0);
                        }
                    });
                }
            }
        }

        private static (string text, string url) ReadUia(IntPtr hwnd)
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return (string.Empty, string.Empty);

            var texts = new List<string>(64);
            string url = string.Empty;
            int visited = 0;

            CollectUia(root, texts, ref url, 0, ref visited);

            // 去重并截断，避免字符串过长
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(1024);
            foreach (var t in texts)
            {
                if (string.IsNullOrWhiteSpace(t))
                    continue;
                var trimmed = t.Trim();
                if (trimmed.Length < 2 || trimmed.Length > 200)
                    continue;
                if (!unique.Add(trimmed))
                    continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(trimmed);
                if (sb.Length > 4000)
                    break;
            }

            return (sb.ToString(), url);
        }

        private static void CollectUia(
            AutomationElement element,
            List<string> texts,
            ref string url,
            int depth,
            ref int visited)
        {
            if (element == null || depth > MaxUiaDepth || visited >= MaxUiaNodes)
                return;

            visited++;

            try
            {
                string name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    texts.Add(name);

                var controlType = element.Current.ControlType;

                // 地址栏 / 编辑框：尝试读 Value
                if (controlType == ControlType.Edit ||
                    controlType == ControlType.Document ||
                    controlType == ControlType.ToolBar)
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj) &&
                        patternObj is ValuePattern valuePattern)
                    {
                        string value = valuePattern.Current.Value;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            texts.Add(value);
                            if (string.IsNullOrEmpty(url) && LooksLikeUrl(value))
                                url = value;
                        }
                    }
                }

                // 浏览器地址栏常见 AutomationId
                string automationId = element.Current.AutomationId ?? string.Empty;
                if (string.IsNullOrEmpty(url) &&
                    (automationId.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                     automationId.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                     automationId.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("地址", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("搜索栏", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Address", StringComparison.OrdinalIgnoreCase)))
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object p) &&
                        p is ValuePattern vp &&
                        !string.IsNullOrWhiteSpace(vp.Current.Value))
                    {
                        url = vp.Current.Value;
                        texts.Add(url);
                    }
                }
            }
            catch
            {
                // 个别控件不可访问
            }

            try
            {
                var walker = TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(element);
                while (child != null && visited < MaxUiaNodes)
                {
                    CollectUia(child, texts, ref url, depth + 1, ref visited);
                    child = walker.GetNextSibling(child);
                }
            }
            catch
            {
                // 树遍历失败时忽略
            }
        }

        private static bool LooksLikeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains(".com", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains(".cn", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("kuaishou", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("xiaohongshu", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("xhs", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("bilibili", StringComparison.OrdinalIgnoreCase);
        }

        private OcrEngine GetOrCreateOcrEngine()
        {
            if (_ocrEngine != null)
                return _ocrEngine;

            if (_ocrEngineTried)
                return null;

            _ocrEngineTried = true;
            try
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_ocrEngine == null)
                {
                    var zh = new Windows.Globalization.Language("zh-Hans");
                    if (OcrEngine.IsLanguageSupported(zh))
                        _ocrEngine = OcrEngine.TryCreateFromLanguage(zh);
                }

                if (_ocrEngine == null)
                {
                    var en = new Windows.Globalization.Language("en");
                    if (OcrEngine.IsLanguageSupported(en))
                        _ocrEngine = OcrEngine.TryCreateFromLanguage(en);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建 OCR 引擎失败: {ex.Message}");
                _ocrEngine = null;
            }

            return _ocrEngine;
        }

        private async Task<string> RecognizeWindowAsync(IntPtr hwnd)
        {
            var engine = GetOrCreateOcrEngine();
            if (engine == null)
                return string.Empty;

            using var bitmap = CaptureWindow(hwnd);
            if (bitmap == null)
                return string.Empty;

            using var scaled = ScaleForOcr(bitmap);
            using var softwareBitmap = await ToSoftwareBitmapAsync(scaled).ConfigureAwait(false);
            if (softwareBitmap == null)
                return string.Empty;

            var result = await engine.RecognizeAsync(softwareBitmap).AsTask().ConfigureAwait(false);
            return result?.Text?.Replace('\n', ' ').Replace('\r', ' ') ?? string.Empty;
        }

        private static Bitmap CaptureWindow(IntPtr hwnd)
        {
            if (!GetClientRect(hwnd, out RECT client) ||
                client.Right - client.Left < 40 ||
                client.Bottom - client.Top < 40)
                return null;

            var topLeft = new POINT { X = client.Left, Y = client.Top };
            if (!ClientToScreen(hwnd, ref topLeft))
                return null;

            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;

            // 多屏/DPI 下限制最大捕获面积
            width = Math.Min(width, 1920);
            height = Math.Min(height, 1080);

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(topLeft.X, topLeft.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }

        private static Bitmap ScaleForOcr(Bitmap source)
        {
            if (source.Width <= MaxOcrWidth)
                return (Bitmap)source.Clone();

            int w = MaxOcrWidth;
            int h = Math.Max(1, (int)(source.Height * (MaxOcrWidth / (double)source.Width)));
            var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(source, 0, 0, w, h);
            }
            return scaled;
        }

        private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var ms = new System.IO.MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                byte[] bytes = ms.ToArray();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync().AsTask().ConfigureAwait(false);
                    await writer.FlushAsync().AsTask().ConfigureAwait(false);
                    writer.DetachStream();
                }
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
        }

        public void Dispose()
        {
            Reset();
            _ocrEngine = null;
        }
    }
}
