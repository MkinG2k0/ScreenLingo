using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

[assembly: System.Reflection.AssemblyTitle("ScreenLingo")]
[assembly: System.Reflection.AssemblyDescription("Screen area OCR translator for Windows")]
[assembly: System.Reflection.AssemblyProduct("ScreenLingo")]
[assembly: System.Reflection.AssemblyVersion("0.4.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.4.0.0")]

namespace ScreenLingo
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Length > 0 && args[0].StartsWith("--self-test=", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = args[0].Substring("--self-test=".Length).Trim('"');
                RunSelfTest(logPath).GetAwaiter().GetResult();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }

        private static async Task RunSelfTest(string logPath)
        {
            StringBuilder log = new StringBuilder();
            try
            {
                using (Bitmap sample = new Bitmap(700, 150, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(sample))
                using (Font font = new Font("Segoe UI", 36, FontStyle.Regular))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawString("Open settings", font, Brushes.Black, new PointF(20, 35));
                    List<OcrLineInfo> lines = await OcrService.RecognizeAsync(sample, "en-US");
                    log.AppendLine("OCR_LINES=" + lines.Count);
                    log.AppendLine("OCR_TEXT=" + String.Join(" | ", lines.Select(delegate(OcrLineInfo line) { return line.Text; }).ToArray()));
                    if (lines.Count == 0) throw new InvalidOperationException("Self-test OCR found no text.");
                    string parsed = TranslationService.ParseResponse("[[[\"Открыть настройки\",\"Open settings\",null,null,10]],null,\"en\"]");
                    log.AppendLine("TRANSLATION_PARSER=" + parsed);
                    if (parsed != "Открыть настройки") throw new InvalidOperationException("Self-test translation parser failed.");
                    List<OcrLineInfo> paragraphSample = new List<OcrLineInfo>
                    {
                        new OcrLineInfo { Text = "Mike is ten. He is a schoolboy.", Bounds = new Rectangle(10, 10, 420, 36) },
                        new OcrLineInfo { Text = "His hobby is football.", Bounds = new Rectangle(10, 54, 360, 36) },
                        new OcrLineInfo { Text = "He likes to play the guitar.", Bounds = new Rectangle(10, 98, 400, 36) }
                    };
                    List<OcrLineInfo> paragraphs = TranslationService.BuildParagraphs(paragraphSample);
                    log.AppendLine("PARAGRAPHS=" + paragraphs.Count + "; TEXT=" + paragraphs[0].Text);
                    if (paragraphs.Count != 1) throw new InvalidOperationException("Self-test paragraph grouping failed.");
                    try
                    {
                        List<OcrLineInfo> translated = await new TranslationService("ru").TranslateAsync(lines);
                        log.AppendLine("TRANSLATION_NETWORK=" + String.Join(" | ", translated.Select(delegate(OcrLineInfo line) { return line.Translation; }).ToArray()));
                    }
                    catch (Exception networkError)
                    {
                        log.AppendLine("TRANSLATION_NETWORK=SKIPPED: " + networkError.GetBaseException().Message);
                    }
                }
                log.AppendLine("RESULT=LOCAL_PASS");
            }
            catch (Exception ex)
            {
                log.AppendLine("RESULT=FAIL");
                log.AppendLine(ex.ToString());
            }
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
        }
    }

    internal sealed class AppSettings
    {
        public string SourceLanguage = "en-US";
        public string TargetLanguage = "ru";
        public int OverlaySeconds = 18;

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScreenLingo.ini");
            if (!File.Exists(path)) return settings;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int separator = line.IndexOf('=');
                if (separator < 1) continue;
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (key.Equals("SourceLanguage", StringComparison.OrdinalIgnoreCase)) settings.SourceLanguage = value;
                if (key.Equals("TargetLanguage", StringComparison.OrdinalIgnoreCase)) settings.TargetLanguage = value;
                int seconds;
                if (key.Equals("OverlaySeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out seconds))
                    settings.OverlaySeconds = Math.Max(3, Math.Min(120, seconds));
            }
            return settings;
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "ScreenLingo";
        private const int HotkeyCapture = 100;
        private const int HotkeyCopy = 101;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const int WmHotkey = 0x0312;
        private const int WhMouseLl = 14;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmRightButtonDown = 0x0204;
        private const int WmMiddleButtonDown = 0x0207;
        private const int WmXButtonDown = 0x020B;

        private readonly NotifyIcon trayIcon;
        private readonly HotkeyWindow hotkeyWindow;
        private readonly AppSettings settings;
        private readonly MouseHookProc mouseHookCallback;
        private readonly ToolStripMenuItem autostartItem;
        private OverlayForm overlay;
        private IntPtr mouseHook;
        private bool working;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, MouseHookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        private delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);

        public TrayApplicationContext()
        {
            settings = AppSettings.Load();
            mouseHookCallback = OnGlobalMouseEvent;
            hotkeyWindow = new HotkeyWindow();
            hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
            RegisterHotKey(hotkeyWindow.Handle, HotkeyCapture, ModControl | ModShift, (uint)Keys.T);
            RegisterHotKey(hotkeyWindow.Handle, HotkeyCopy, ModControl | ModShift, (uint)Keys.R);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Перевести область   Ctrl+Shift+T", null, delegate { BeginCapture(false); });
            menu.Items.Add("Копировать исходный текст   Ctrl+Shift+R", null, delegate { BeginCapture(true); });
            menu.Items.Add("Скрыть перевод", null, delegate { HideOverlay(); });
            menu.Items.Add(new ToolStripSeparator());
            autostartItem = new ToolStripMenuItem("Запускать вместе с Windows");
            autostartItem.Checked = IsAutostartEnabled();
            autostartItem.Click += delegate { ToggleAutostart(); };
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { Exit(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Information;
            trayIcon.Text = "ScreenLingo — перевод области экрана";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { BeginCapture(false); };
            trayIcon.ShowBalloonTip(3000, "ScreenLingo запущен", "Ctrl+Shift+T — перевести область\nCtrl+Shift+R — скопировать исходный текст", ToolTipIcon.Info);
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == HotkeyCapture) BeginCapture(false);
            if (id == HotkeyCopy) BeginCapture(true);
        }

        private static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false))
                {
                    string value = key == null ? null : key.GetValue(StartupValueName) as string;
                    if (String.IsNullOrWhiteSpace(value)) return false;
                    string registeredPath = value.Trim().Trim('"');
                    return String.Equals(Path.GetFullPath(registeredPath), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private void ToggleAutostart()
        {
            try
            {
                bool enable = !IsAutostartEnabled();
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
                {
                    if (enable)
                        key.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else
                        key.DeleteValue(StartupValueName, false);
                }
                autostartItem.Checked = enable;
                trayIcon.ShowBalloonTip(1600, "ScreenLingo", enable ? "Автозапуск включён." : "Автозапуск выключен.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                autostartItem.Checked = IsAutostartEnabled();
                trayIcon.ShowBalloonTip(3500, "Не удалось изменить автозапуск", ex.Message, ToolTipIcon.Error);
            }
        }

        private async void BeginCapture(bool copyOnly)
        {
            if (working) return;
            working = true;
            try
            {
                HideOverlay();
                Rectangle selected;
                using (SelectionForm selector = new SelectionForm())
                {
                    if (selector.ShowDialog() != DialogResult.OK) return;
                    selected = selector.SelectedScreenRectangle;
                }

                await Task.Delay(90);
                using (Bitmap image = Capture(selected))
                {
                    trayIcon.Text = "ScreenLingo — распознаю текст…";
                    List<OcrLineInfo> lines = await OcrService.RecognizeAsync(image, settings.SourceLanguage);
                    if (lines.Count == 0)
                    {
                        trayIcon.ShowBalloonTip(2200, "Текст не найден", "Попробуйте выделить область точнее или увеличить текст.", ToolTipIcon.Warning);
                        return;
                    }

                    if (copyOnly)
                    {
                        string sourceText = String.Join(Environment.NewLine, lines.Select(delegate(OcrLineInfo line) { return line.Text; }).ToArray());
                        Clipboard.SetText(sourceText);
                        trayIcon.ShowBalloonTip(1800, "Текст скопирован", sourceText.Length > 100 ? sourceText.Substring(0, 100) + "…" : sourceText, ToolTipIcon.Info);
                        return;
                    }

                    trayIcon.Text = "ScreenLingo — перевожу…";
                    TranslationService translator = new TranslationService(settings.TargetLanguage);
                    lines = await translator.TranslateAsync(lines);
                    if (lines.Count == 0) throw new InvalidOperationException("Сервис перевода не вернул результат.");

                    overlay = new OverlayForm(selected, lines, image, settings.OverlaySeconds);
                    overlay.FormClosed += delegate { RemoveMouseDismissHook(); overlay = null; };
                    overlay.Show();
                    InstallMouseDismissHook();
                }
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(5000, "Не удалось перевести", FriendlyError(ex), ToolTipIcon.Error);
            }
            finally
            {
                working = false;
                trayIcon.Text = "ScreenLingo — перевод области экрана";
            }
        }

        private static string FriendlyError(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
            if (current is HttpRequestException || current is WebException)
                return "Нет доступа к сервису перево�m�G����ƭy�= WmHotkey && HotkeyPressed != null) HotkeyPressed(message.WParam.ToInt32());
                base.WndProc(ref message);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }
    }

    internal sealed class SelectionForm : Form
    {
        private Point start;
        private Point current;
        private bool selecting;
        public Rectangle SelectedScreenRectangle { get; private set; }

        public SelectionForm()
        {
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            StartPosition = FormStartPosition.Manual;
            Bounds = virtualScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.28;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                start = e.Location;
                current = e.Location;
                selecting = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (selecting)
            {
                current = e.Location;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (selecting && e.Button == MouseButtons.Left)
            {
                selecting = false;
                Rectangle local = Normalized(start, e.Location);
                if (local.Width >= 8 && local.Height >= 8)
                {
                    SelectedScreenRectangle = new Rectangle(Left + local.Left, Top + local.Top, local.Width, local.Height);
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!selecting) return;
            Rectangle rectangle = Normalized(start, current);
            using (Brush fill = new SolidBrush(Color.FromArgb(80, 255, 153, 0))) e.Graphics.FillRectangle(fill, rectangle);
            using (Pen border = new Pen(Color.FromArgb(255, 255, 177, 45), 3)) e.Graphics.DrawRectangle(border, rectangle);
            string size = rectangle.Width + " × " + rectangle.Height;
            using (Font font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (Brush text = new SolidBrush(Color.White)) e.Graphics.DrawString(size, font, text, rectangle.Left + 4, Math.Max(2, rectangle.Top - 24));
        }

        private static Rectangle Normalized(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
    }

    internal sealed class OcrLineInfo
    {
        public string Text;
        public string Translation;
        public Rectangle Bounds;
    }

    internal static class OcrService
    {
        public static async Task<List<OcrLineInfo>> RecognizeAsync(Bitmap bitmap, string languageTag)
        {
            using (MemoryStream memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                using (IRandomAccessStream random = memory.AsRandomAccessStream())
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);
                    SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    OcrEngine engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                    if (engine == null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (engine == null) throw new InvalidOperationException("Установите языковой пакет OCR для " + languageTag + " в параметрах Windows.");
                    OcrResult result = await engine.RecognizeAsync(softwareBitmap);
                    List<OcrLineInfo> lines = new List<OcrLineInfo>();
                    foreach (OcrLine line in result.Lines)
                    {
                        if (String.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
                        double left = line.Words.Min(delegate(OcrWord word) { return word.BoundingRect.X; });
                        double top = line.Words.Min(delegate(OcrWord word) { return word.BoundingRect.Y; });
                        double right = line.Words.Max(delegate(OcrWord word) { return word.BoundingRect.X + word.BoundingRect.Width; });
                        double bottom = line.Words.Max(delegate(OcrWord word) { return word.BoundingRect.Y + word.BoundingRect.Height; });
                        lines.Add(new OcrLineInfo
                        {
                            Text = line.Text.Trim(),
                            Bounds = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom))
                        });
                    }
                    softwareBitmap.Dispose();
                    return lines;
                }
            }
        }
    }

    internal sealed class TranslationService
    {
        private static readonly HttpClient Client = CreateClient();
        private readonly string targetLanguage;

        public TranslationService(string targetLanguage)
        {
            this.targetLanguage = targetLanguage;
        }

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 ScreenLingo/0.1");
            return client;
        }

        public async Task<List<OcrLineInfo>> TranslateAsync(List<OcrLineInfo> lines)
        {
            List<OcrLineInfo> useful = lines
                .Where(delegate(OcrLineInfo line) { return line.Text.Any(Char.IsLetter); })
                .OrderBy(delegate(OcrLineInfo line) { return line.Bounds.Top; })
                .ThenBy(delegate(OcrLineInfo line) { return line.Bounds.Left; })
                .Take(40)
                .ToList();
            List<OcrLineInfo> blocks = BuildParagraphs(useful);
            List<Task> tasks = new List<Task>();
            foreach (OcrLineInfo block in blocks) tasks.Add(TranslateLineAsync(block));
            await Task.WhenAll(tasks.ToArray());
            return blocks.Where(delegate(OcrLineInfo block) { return !String.IsNullOrWhiteSpace(block.Translation); }).ToList();
        }

        internal static List<OcrLineInfo> BuildParagraphs(List<OcrLineInfo> lines)
        {
            List<OcrLineInfo> blocks = new List<OcrLineInfo>();
            List<OcrLineInfo> current = new List<OcrLineInfo>();
            Rectangle currentBounds = Rectangle.Empty;

            foreach (OcrLineInfo line in lines)
            {
                bool belongs = false;
                if (current.Count > 0)
                {
                    OcrLineInfo previous = current[current.Count - 1];
                    int verticalGap = line.Bounds.Top - previous.Bounds.Bottom;
                    int allowedGap = (int)(Math.Max(previous.Bounds.Height, line.Bounds.Height) * 1.35);
                    bool horizontallyRelated = line.Bounds.Left <= currentBounds.Right + 40 &&
                                               line.Bounds.Right >= currentBounds.Left - 40;
                    belongs = verticalGap <= allowedGap && verticalGap >= -Math.Max(previous.Bounds.Height, line.Bounds.Height) && horizontallyRelated;
                }

                if (!belongs && current.Count > 0)
                {
                    blocks.Add(CreateParagraph(current, currentBounds));
                    current.Clear();
                    currentBounds = Rectangle.Empty;
                }

                current.Add(line);
                currentBounds = currentBounds.IsEmpty ? line.Bounds : Rectangle.Union(currentBounds, line.Bounds);
            }

            if (current.Count > 0) blocks.Add(CreateParagraph(current, currentBounds));
            return blocks;
        }

        private static OcrLineInfo CreateParagraph(List<OcrLineInfo> lines, Rectangle bounds)
        {
            return new OcrLineInfo
            {
                Text = String.Join(" ", lines.Select(delegate(OcrLineInfo line) { return line.Text.Trim(); }).ToArray()),
                Bounds = bounds
            };
        }

        private async Task TranslateLineAsync(OcrLineInfo line)
        {
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" +
                         Uri.EscapeDataString(targetLanguage) + "&dt=t&q=" + Uri.EscapeDataString(line.Text);
            string json = await Client.GetStringAsync(url);
            line.Translation = ParseResponse(json);
        }

        internal static string ParseResponse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object[] root = serializer.DeserializeObject(json) as object[];
            if (root == null || root.Length == 0) return String.Empty;
            object[] segments = root[0] as object[];
            if (segments == null) return String.Empty;
            StringBuilder result = new StringBuilder();
            foreach (object item in segments)
            {
                object[] segment = item as object[];
                if (segment != null && segment.Length > 0 && segment[0] != null) result.Append(segment[0].ToString());
            }
            return result.ToString().Trim();
        }
    }

    internal sealed class OverlayForm : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private readonly Timer closeTimer;

        public OverlayForm(Rectangle screenArea, IEnumerable<OcrLineInfo> lines, Bitmap capturedImage, int seconds)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = screenArea;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;

            foreach (OcrLineInfo line in lines) AddTranslation(line, capturedImage);

            closeTimer = new Timer();
            closeTimer.Interval = seconds * 1000;
            closeTimer.Tick += delegate { Close(); };
            closeTimer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }

        private void AddTranslation(OcrLineInfo line, Bitmap capturedImage)
        {
            Rectangle area = Rectangle.Intersect(new Rectangle(Point.Empty, capturedImage.Size), line.Bounds);
            if (area.Width < 2 || area.Height < 2) return;
            Color background = SampleBackground(capturedImage, area);
            double luminance = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
            Color foreground = luminance >= 145 ? Color.FromArgb(28, 30, 34) : Color.White;

            int estimatedLines = Math.Max(1, (int)Math.Round((double)line.Bounds.Height / Math.Max(1, Math.Min(line.Bounds.Height, 48))));
            int sourceLineHeight = Math.Max(16, line.Bounds.Height / estimatedLines);
            int fontSize = Math.Max(10, Math.Min(42, (int)(sourceLineHeight * 0.72)));
            Label label = new Label();
            label.AutoSize = false;
            label.Text = line.Translation;
            label.ForeColor = foreground;
            label.BackColor = background;
            label.TextAlign = ContentAlignment.TopLeft;
            label.Padding = new Padding(5, 2, 5, 2);
            label.Location = new Point(Math.Max(0, line.Bounds.Left - 5), Math.Max(0, line.Bounds.Top - 3));
            label.Size = new Size(Math.Min(Width - label.Left, line.Bounds.Width + 12), Math.Min(Height - label.Top, line.Bounds.Height + 8));

            Font fittedFont = null;
            while (fontSize >= 9)
            {
                if (fittedFont != null) fittedFont.Dispose();
                fittedFont = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                label.Font = fittedFont;
                Size measured = TextRenderer.MeasureText(label.Text, fittedFont,
                    new Size(Math.Max(20, label.ClientSize.Width - label.Padding.Horizontal), 10000),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (measured.Height <= label.ClientSize.Height - label.Padding.Vertical) break;
                fontSize--;
            }
            Controls.Add(label);
        }

        private static Color SampleBackground(Bitmap image, Rectangle area)
        {
            List<int> reds = new List<int>();
            List<int> greens = new List<int>();
            List<int> blues = new List<int>();
            int stepX = Math.Max(2, area.Width / 35);
            int stepY = Math.Max(2, area.Height / 20);

            for (int y = area.Top; y < area.Bottom; y += stepY)
            {
                for (int x = area.Left; x < area.Right; x += stepX)
                {
                    Color pixel = image.GetPixel(x, y);
                    reds.Add(pixel.R);
                    greens.Add(pixel.G);
                    blues.Add(pixel.B);
                }
            }

            if (reds.Count == 0) return Color.FromArgb(245, 245, 245);
            reds.Sort(); greens.Sort(); blues.Sort();
            int middle = reds.Count / 2;
            return Color.FromArgb(reds[middle], greens[middle], blues[middle]);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && closeTimer != null) closeTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
