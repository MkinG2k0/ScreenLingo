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
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

[assembly: System.Reflection.AssemblyTitle("ScreenLingo")]
[assembly: System.Reflection.AssemblyDescription("Screen area OCR translator for Windows")]
[assembly: System.Reflection.AssemblyProduct("ScreenLingo")]
[assembly: System.Reflection.AssemblyVersion("0.4.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.4.2.0")]

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
                    string parsed = TranslationService.ParseResponse("[[[\"ĞÑ‚ĞºÑ€Ñ‹Ñ‚ÑŒ Ğ½Ğ°ÑÑ‚Ñ€Ğ¾Ğ¹ĞºĞ¸\",\"Open settings\",null,null,10]],null,\"en\"]");
                    log.AppendLine("TRANSLATION_PARSER=" + parsed);
                    if (parsed != "ĞÑ‚ĞºÑ€Ñ‹Ñ‚ÑŒ Ğ½Ğ°ÑÑ‚Ñ€Ğ¾Ğ¹ĞºĞ¸") throw new InvalidOperationException("Self-test translation parser failed.");
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
        private readonly Icon appIcon;
        private readonly HotkeyWindow hotkeyWindow;
        private readonly AppSettings settings;
        private readonly MouseHookProc mouseHookCallback;
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
            menu.Items.Add("ĞŸĞµÑ€ĞµĞ²ĞµÑÑ‚Ğ¸ Ğ¾Ğ±Ğ»Ğ°ÑÑ‚ÑŒ   Ctrl+Shift+T", null, delegate { BeginCapture(false); });
            menu.Items.Add("ĞšĞ¾Ğ¿Ğ¸Ñ€Ğ¾Ğ²Ğ°Ñ‚ÑŒ Ğ¸ÑÑ…Ğ¾Ğ´Ğ½Ñ‹Ğ¹ Ñ‚ĞµĞºÑÑ‚   Ctrl+Shift+R", null, delegate { BeginCapture(true); });
            menu.Items.Add("Ğ¡ĞºÑ€Ñ‹Ñ‚ÑŒ Ğ¿ĞµÑ€ĞµĞ²Ğ¾Ğ´", null, delegate { HideOverlay(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Ğ’Ñ‹Ñ…Ğ¾Ğ´", null, delegate { Exit(); });

            appIcon = AppIcon.Create();
            trayIcon = new NotifyIcon();
            trayIcon.Icon = appIcon;
            trayIcon.Text = "ScreenLingo â€” Ğ¿ĞµÑ€ĞµĞ²Ğ¾Ğ´ Ğ¾Ğ±Ğ»Ğ°ÑÑ‚Ğ¸ ÑĞºÑ€Ğ°Ğ½Ğ°";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { BeginCapture(false); };
            trayIcon.ShowBalloonTip(3000, "ScreenLingo Ğ·Ğ°Ğ¿ÑƒÑ‰ĞµĞ½", "Ctrl+Shift+T â€” Ğ¿ĞµÑ€ĞµĞ²ĞµÑÑ‚Ğ¸ Ğ¾Ğ±Ğ»Ğ°ÑÑ‚ÑŒ\nCtrl+Shift+R â€” ÑĞºĞ¾Ğ¿Ğ¸Ñ€Ğ¾Ğ²Ğ°Ñ‚ÑŒ Ğ¸ÑÑ…Ğ¾Ğ´Ğ½Ñ‹Ğ¹ Ñ‚ĞµĞºÑÑ‚", ToolTipIcon.Info);
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == HotkeyCapture) BeginCapture(false);
            if (id == HotkeyCopy) BeginCapture(true);
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
                    trayIcon.Text = "ScreenLingo â€” Ñ€Ğ°ÑĞ¿Ğ¾Ğ·Ğ½Ğ°Ñ Ñ‚ĞµĞºÑÑ‚â€¦";
                    List<OcrLineInfo> lines = await OcrService.RecognizeAsync(image, settings.SourceLanguage);
                    if (lines.Count == 0)
                    {
                        trayIcon.ShowBalloonTip(2200, "Ğ¢ĞµĞºÑÑ‚ Ğ½Ğµ Ğ½Ğ°Ğ¹Ğ´ĞµĞ½", "ĞŸĞ¾Ğ¿Ñ€Ğ¾Ğ±ÑƒĞ¹Ñ‚Ğµ Ğ²Ñ‹Ğ´ĞµĞ»Ğ¸Ñ‚ÑŒ Ğ¾Ğ±Ğ»Ğ°ÑÑ‚ÑŒ Ñ‚Ğ¾Ñ‡Ğ½ĞµĞµ Ğ¸Ğ»Ğ¸ ÑƒĞ²ĞµĞ»Ğ¸Ñ‡Ğ¸Ñ‚ÑŒ Ñ‚ĞµĞºÑÑ‚.", ToolTipIcon.Warning);
                        return;
                    }

                    if (copyOnly)
                    {
                        string sourceText = String.Join(Environment.NewLine, lines.Select(delegate(OcrLineInfo line) { return line.Text; }).ToArray());
                        Clipboard.SetText(sourceText);
                        trayIcon.ShowBalloonTip(1800, "Ğ¢ĞµĞºÑÑ‚ ÑĞºĞ¾Ğ¿Ğ¸Ñ€Ğ¾Ğ²Ğ°Ğ½", sourceText.Length > 100 ? sourceText.Substring(0, 100) + "â€¦" : sourceText, ToolTipIcon.Info);
                        return;
                    }

                    trayIcon.Text = "ScreenLingo â€” Ğ¿ĞµÑ€ĞµĞ²Ğ¾Ğ¶Ñƒâ€¦";
                    TranslationService translator = new TranslationService(settings.TargetLanguage);
                    lines = await translator.TranslateAsync(lines);
                    if (lines.Count == 0) throw new InvalidOperationException("Ğ¡ĞµÑ€Ğ²Ğ¸Ñ Ğ¿ĞµÑ€ĞµĞ²Ğ¾Ğ´Ğ° Ğ½Ğµ Ğ²ĞµÑ€Ğ½ÑƒĞ» Ñ€ĞµĞ·ÑƒĞ»ÑŒÑ‚Ğ°Ñ‚.");

                    overlay = new OverlayForm(selected, lines, image, settings.OverlaySeconds);
                    overlay.FormClosed += delegate { RemoveMouseDismissHook(); overlay = null; };
                    overlay.Show();
                    InstallMouseDismissHook();
                }
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(5000, "ĞĞµ ÑƒĞ´Ğ°Ğ»Ğ¾ÑÑŒ Ğ¿ĞµÑ€ĞµĞ²ĞµÑÑ‚Ğ¸", FriendlyError(ex), ToolTipIcon.Error);
            }
            finally
            {
                working = false;
                trayIcon.Text = "ScreenLingo â€” Ğ¿ĞµÑ€ĞµĞ²Ğ¾Ğ´ Ğ¾Ğ±Ğ»Ğ°ÑÑ‚Ğ¸ ÑĞºÑ€Ğ°Ğ½Ğ°";
            }
        }

        private static string FriendlyError(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
            if (current is HttpRequestException || current is WebException)
                return "ĞĞµÑ‚ Ğ´Ğ¾ÑÑ‚ÑƒĞ¿Ğ° Ğº ÑĞµÑ€Ğ²Ğ¸ÑÑƒ Ğ¿ĞµÑ€ĞµĞ²Ğ¾Ğ´Ğ°. ĞŸÑ€Ğ¾Ğ²ĞµÑ€ÑŒÑ‚Ğµ Ğ¸Ğ½Ñ‚ĞµÑ€Ğ½ĞµÑ‚ Ğ¸Ğ»Ğ¸ VPN.";
            return current.Message.Length > 180 ? current.Message.Substring(0, 180) : current.Message;
        }

        private static Bitmap Capture(Rectangle area)
        {
            Bitmap bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }

        private void HideOverlay()
        {
            RemoveMouseDismissHook();
            if (overlay != null && !overlay.IsDisposed) overlay.Close();
            overlay = null;
        }

        private void InstallMouseDismissHook()
        {
            RemoveMouseDismissHook();
            mouseHook = SetWindowsHookEx(WhMouseLl, mouseHookCallback, GetModuleHandle(null), 0);
        }

        private void RemoveMouseDismissHook()
        {
            if (mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr OnGlobalMouseEvent(int code, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (code >= 0 && (message == WmLeftButtonDown || message == WmRightButtonDown ||
                              message == WmMiddleButtonDown || message == WmXButtonDown))
                HideOverlay();
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void Exit()
        {
            HideOverlay();
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyCapture);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyCopy);
            hotkeyWindow.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            appIcon.Dispose();
            ExitThread();
        }

        private sealed class HotkeyWindow : NativeWindow, IDisposable
        {
            public event Action<int> HotkeyPressed;

            public HotkeyWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override v×^¼¶‰Ëkºwµç]±”¹I¥¡Ğ€´‘¥…µ•Ñ•È°É•Ñ…¹±”¹	½ÑÑ½´€´‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€À°€äÀ¤ì(€€€€€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹1•™Ğ°É•Ñ…¹±”¹	½ÑÑ½´€´‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€äÀ°€äÀ¤ì(€€€€€€€€€€€Á…Ñ ¹±½Í•¥ÕÉ” ¤ì(€€€€€€€€€€€É•ÑÕÉ¸Á…Ñ ì(€€€€€€€ô(€€€ô((€€€¥¹Ñ•É¹…°Í•…±•±…ÍÌM•±•Ñ¥½¹½É´€è½É´(€€€ì(€€€€€€€ÁÉ¥Ù…Ñ”A½¥¹ĞÍÑ…ÉĞì(€€€€€€€ÁÉ¥Ù…Ñ”A½¥¹ĞÕÉÉ•¹Ğì(€€€€€€€ÁÉ¥Ù…Ñ”‰½½°Í•±•Ñ¥¹œì(€€€€€€€ÁÕ‰±¥ŒI•Ñ…¹±”M•±•Ñ•‘MÉ••¹I•Ñ…¹±”ì•ĞìÁÉ¥Ù…Ñ”Í•Ğìô((€€€€€€€ÁÕ‰±¥ŒM•±•Ñ¥½¹½É´ ¤(€€€€€€€ì(€€€€€€€€€€€I•Ñ…¹±”Ù¥ÉÑÕ…±MÉ••¸€ôMåÍÑ•µ%¹™½Éµ…Ñ¥½¸¹Y¥ÉÑÕ…±MÉ••¸ì(€€€€€€€€€€€MÑ…ÉÑA½Í¥Ñ¥½¸€ô½ÉµMÑ…ÉÑA½Í¥Ñ¥½¸¹5…¹Õ…°ì(€€€€€€€€€€€	½Õ¹‘Ì€ôÙ¥ÉÑÕ…±MÉ••¸ì(€€€€€€€€€€€½Éµ	½É‘•ÉMÑå±”€ô½Éµ	½É‘•ÉMÑå±”¹9½¹”ì(€€€€€€€€€€€M¡½İ%¹Q…Í­‰…È€ô™…±Í”ì(€€€€€€€€€€€Q½Á5½ÍĞ€ôÑÉÕ”ì(€€€€€€€€€€€	…­½±½È€ô½±½È¹	±…¬ì(€€€€€€€€€€€=Á…¥Ñä€ô€À¸Èàì(€€€€€€€€€€€ÕÉÍ½È€ôÕÉÍ½ÉÌ¹É½ÍÌì(€€€€€€€€€€€½Õ‰±•	Õ™™•É•€ôÑÉÕ”ì(€€€€€€€€€€€-•åAÉ•Ù¥•Ü€ôÑÉÕ”ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”Ù½¥=¹-•å½İ¸¡-•åÙ•¹ÑÉÌ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡”¹-•å½‘”€ôô-•åÌ¹Í…Á”¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥…±½I•ÍÕ±Ğ€ô¥…±½I•ÍÕ±Ğ¹…¹•°ì(€€€€€€€€€€€€€€€±½Í” ¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€‰…Í”¹=¹-•å½İ¸¡”¤ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”Ù½¥=¹5½ÕÍ•½İ¸¡5½ÕÍ•Ù•¹ÑÉÌ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡”¹	ÕÑÑ½¸€ôô5½ÕÍ•	ÕÑÑ½¹Ì¹1•™Ğ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€ÍÑ…ÉĞ€ô”¹1½…Ñ¥½¸ì(€€€€€€€€€€€€€€€ÕÉÉ•¹Ğ€ô”¹1½…Ñ¥½¸ì(€€€€€€€€€€€€€€€Í•±•Ñ¥¹œ€ôÑÉÕ”ì(€€€€€€€€€€€€€€€%¹Ù…±¥‘…Ñ” ¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€‰…Í”¹=¹5½ÕÍ•½İ¸¡”¤ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”Ù½¥=¹5½ÕÍ•5½Ù”¡5½ÕÍ•Ù•¹ÑÉÌ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡Í•±•Ñ¥¹œ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€ÕÉÉ•¹Ğ€ô”¹1½…Ñ¥½¸ì(€€€€€€€€€€€€€€€%¹Ù…±¥‘…Ñ” ¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€‰…Í”¹=¹5½ÕÍ•5½Ù”¡”¤ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”Ù½¥=¹5½ÕÍ•UÀ¡5½ÕÍ•Ù•¹ÑÉÌ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡Í•±•Ñ¥¹œ€˜˜”¹	ÕÑÑ½¸€ôô5½ÕÍ•	ÕÑÑ½¹Ì¹1•™Ğ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€Í•±•Ñ¥¹œ€ô™…±Í”ì(€€€€€€€€€€€€€€€I•Ñ…¹±”±½…°€ô9½Éµ…±¥é•¡ÍÑ…ÉĞ°”¹1½…Ñ¥½¸¤ì(€€€€€€€€€€€€€€€¥˜€¡±½…°¹]¥‘Ñ €øô€à€˜˜±½…°¹!•¥¡Ğ€øô€à¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€M•±•Ñ•‘MÉ••¹I•Ñ…¹±”€ô¹•ÜI•Ñ…¹±”¡1•™Ğ€¬±½…°¹1•™Ğ°Q½À€¬±½…°¹Q½À°±½…°¹]¥‘Ñ °±½…°¹!•¥¡Ğ¤ì(€€€€€€€€€€€€€€€€€€€¥…±½I•ÍÕ±Ğ€ô¥…±½I•ÍÕ±Ğ¹=,ì(€€€€€€€€€€€€€€€€€€€±½Í” ¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€•±Í”%¹Ù…±¥‘…Ñ” ¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€‰…Í”¹=¹5½ÕÍ•UÀ¡”¤ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”Ù½¥=¹A…¥¹Ğ¡A…¥¹ÑÙ•¹ÑÉÌ”¤(€€€€€€€ì(€€€€€€€€€€€‰…Í”¹=¹A…¥¹Ğ¡”¤ì(€€€€€€€€€€€¥˜€ …Í•±•Ñ¥¹œ¤É•ÑÕÉ¸ì(€€€€€€€€€€€I•Ñ…¹±”É•Ñ…¹±”€ô9½Éµ…±¥é•¡ÍÑ…ÉĞ°ÕÉÉ•¹Ğ¤ì(€€€€€€€€€€€ÕÍ¥¹œ€¡	ÉÕÍ ™¥±°€ô¹•ÜM½±¥‘	ÉÕÍ ¡½±½È¹É½µÉˆ àÀ°€ÈÔÔ°€ÄÔÌ°€À¤¤¤”¹É…Á¡¥Ì¹¥±±I•Ñ…¹±”¡™¥±°°É•Ñ…¹±”¤ì(€€€€€€€€€€€ÕÍ¥¹œ€¡A•¸‰½É‘•È€ô¹•ÜA•¸¡½±½È¹É½µÉˆ ÈÔÔ°€ÈÔÔ°€ÄÜÜ°€ĞÔ¤°€Ì¤¤”¹É…Á¡¥Ì¹É…İI•Ñ…¹±”¡‰½É‘•È°É•Ñ…¹±”¤ì(€€€€€€€€€€€ÍÑÉ¥¹œÍ¥é”€ôÉ•Ñ…¹±”¹]¥‘Ñ €¬€ˆƒ\€ˆ€¬É•Ñ…¹±”¹!•¥¡Ğì(€€€€€€€€€€€ÕÍ¥¹œ€¡½¹Ğ™½¹Ğ€ô¹•Ü½¹Ğ ‰M•½”U$ˆ°€ÄÀ°½¹ÑMÑå±”¹	½±¤¤(€€€€€€€€€€€ÕÍ¥¹œ€¡	ÉÕÍ Ñ•áĞ€ô¹•ÜM½±¥‘	ÉÕÍ ¡½±½È¹]¡¥Ñ”¤¤”¹É…Á¡¥Ì¹É…İMÑÉ¥¹œ¡Í¥é”°™½¹Ğ°Ñ•áĞ°É•Ñ…¹±”¹1•™Ğ€¬€Ğ°5…Ñ ¹5…à È°É•Ñ…¹±”¹Q½À€´€ÈĞ¤¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒI•Ñ…¹±”9½Éµ…±¥é•¡A½¥¹Ğ„°A½¥¹Ğˆ¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸I•Ñ…¹±”¹É½µ1QI¡5…Ñ ¹5¥¸¡„¹`°ˆ¹`¤°5…Ñ ¹5¥¸¡„¹d°ˆ¹d¤°5…Ñ ¹5…à¡„¹`°ˆ¹`¤°5…Ñ ¹5…à¡„¹d°ˆ¹d¤¤ì(€€€€€€€ô(€€€ô((€€€¥¹Ñ•É¹…°Í•…±•±…ÍÌ=É1¥¹•%¹™¼(€€€ì(€€€€€€€ÁÕ‰±¥ŒÍÑÉ¥¹œQ•áĞì(€€€€€€€ÁÕ‰±¥ŒÍÑÉ¥¹œQÉ…¹Í±…Ñ¥½¸ì(€€€€€€€ÁÕ‰±¥ŒI•Ñ…¹±”	½Õ¹‘Ìì(€€€ô((€€€¥¹Ñ•É¹…°ÍÑ…Ñ¥Œ±…ÍÌ=ÉM•ÉÙ¥”(€€€ì(€€€€€€€ÁÕ‰±¥ŒÍÑ…Ñ¥Œ…Íå¹ŒQ…Í¬ñ1¥ÍĞñ=É1¥¹•%¹™¼øøI•½¹¥é•Íå¹Œ¡	¥Ñµ…À‰¥Ñµ…À°ÍÑÉ¥¹œ±…¹Õ…•Q…œ¤(€€€€€€€ì(€€€€€€€€€€€ÕÍ¥¹œ€¡5•µ½ÉåMÑÉ•…´µ•µ½Éä€ô¹•Ü5•µ½ÉåMÑÉ•…´ ¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€‰¥Ñµ…À¹M…Ù”¡µ•µ½Éä°%µ…•½Éµ…Ğ¹A¹œ¤ì(€€€€€€€€€€€€€€€µ•µ½Éä¹A½Í¥Ñ¥½¸€ô€Àì(€€€€€€€€€€€€€€€ÕÍ¥¹œ€¡%I…¹‘½µ•ÍÍMÑÉ•…´É…¹‘½´€ôµ•µ½Éä¹ÍI…¹‘½µ•ÍÍMÑÉ•…´ ¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€	¥Ñµ…Á•½‘•È‘•½‘•È€ô…İ…¥Ğ	¥Ñµ…Á•½‘•È¹É•…Ñ•Íå¹Œ¡É…¹‘½´¤ì(€€€€€€€€€€€€€€€€€€€M½™Ñİ…É•	¥Ñµ…ÀÍ½™Ñİ…É•	¥Ñµ…À€ô…İ…¥Ğ‘•½‘•È¹•ÑM½™Ñİ…É•	¥Ñµ…ÁÍå¹Œ¡	¥Ñµ…ÁA¥á•±½Éµ…Ğ¹	É„à°	¥Ñµ…Á±Á¡…5½‘”¹AÉ•µÕ±Ñ¥Á±¥•¤ì(€€€€€€€€€€€€€€€€€€€=É¹¥¹”•¹¥¹”€ô=É¹¥¹”¹QÉåÉ•…Ñ•É½µ1…¹Õ…”¡¹•Ü1…¹Õ…”¡±…¹Õ…•Q…œ¤¤ì(€€€€€€€€€€€€€€€€€€€¥˜€¡•¹¥¹”€ôô¹Õ±°¤•¹¥¹”€ô=É¹¥¹”¹QÉåÉ•…Ñ•É½µUÍ•ÉAÉ½™¥±•1…¹Õ…•Ì ¤ì(€€€€€€€€€€€€€€€€€€€¥˜€¡•¹¥¹”€ôô¹Õ±°¤Ñ¡É½Ü¹•Ü%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸ ‹BFFBÃB÷BûBËBãFBÔƒF?BßF/BëBûBËBûBäƒBÿBÃBëB×F=HƒBÓBïF<€ˆ€¬±…¹Õ…•Q…œ€¬€ˆƒBÈƒBÿBÃFBÃBóB×FFBÃF]¥¹‘½İÌ¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€=ÉI•ÍÕ±ĞÉ•ÍÕ±Ğ€ô…İ…¥Ğ•¹¥¹”¹I•½¹¥é•Íå¹Œ¡Í½™Ñİ…É•	¥Ñµ…À¤ì(€€€€€€€€€€€€€€€€€€€1¥ÍĞñ=É1¥¹•%¹™¼ø±¥¹•Ì€ô¹•Ü1¥ÍĞñ=É1¥¹•%¹™¼ø ¤ì(€€€€€€€€€€€€€€€€€€€™½É•… €¡=É1¥¹”±¥¹”¥¸É•ÍÕ±Ğ¹1¥¹•Ì¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡MÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡±¥¹”¹Q•áĞ¤ñğ±¥¹”¹]½É‘Ì¹½Õ¹Ğ€ôô€À¤½¹Ñ¥¹Õ”ì(€€€€€€€€€€€€€€€€€€€€€€€‘½Õ‰±”±•™Ğ€ô±¥¹”¹]½É‘Ì¹5¥¸¡‘•±•…Ñ”¡=É]½Éİ½É¤ìÉ•ÑÕÉ¸İ½É¹	½Õ¹‘¥¹I•Ğ¹`ìô¤ì(€€€€€€€€€€€€€€€€€€€€€€€‘½Õ‰±”Ñ½À€ô±¥¹”¹]½É‘Ì¹5¥¸¡‘•±•…Ñ”¡=É]½Éİ½É¤ìÉ•ÑÕÉ¸İ½É¹	½Õ¹‘¥¹I•Ğ¹dìô¤ì(€€€€€€€€€€€€€€€€€€€€€€€‘½Õ‰±”É¥¡Ğ€ô±¥¹”¹]½É‘Ì¹5…à¡‘•±•…Ñ”¡=É]½Éİ½É¤ìÉ•ÑÕÉ¸İ½É¹	½Õ¹‘¥¹I•Ğ¹`€¬İ½É¹	½Õ¹‘¥¹I•Ğ¹]¥‘Ñ ìô¤ì(€€€€€€€€€€€€€€€€€€€€€€€‘½Õ‰±”‰½ÑÑ½´€ô±¥¹”¹]½É‘Ì¹5…à¡‘•±•…Ñ”¡=É]½Éİ½É¤ìÉ•ÑÕÉ¸İ½É¹	½Õ¹‘¥¹I•Ğ¹d€¬İ½É¹	½Õ¹‘¥¹I•Ğ¹!•¥¡Ğìô¤ì(€€€€€€€€€€€€€€€€€€€€€€€±¥¹•Ì¹‘¡¹•Ü=É1¥¹•%¹™¼(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Q•áĞ€ô±¥¹”¹Q•áĞ¹QÉ¥´ ¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€	½Õ¹‘Ì€ôI•Ñ…¹±”¹É½µ1QI ¡¥¹Ğ¥5…Ñ ¹±½½È¡±•™Ğ¤°€¡¥¹Ğ¥5…Ñ ¹±½½È¡Ñ½À¤°€¡¥¹Ğ¥5…Ñ ¹•¥±¥¹œ¡É¥¡Ğ¤°€¡¥¹Ğ¥5…Ñ ¹•¥±¥¹œ¡‰½ÑÑ½´¤¤(€€€€€€€€€€€€€€€€€€€€€€€ô¤ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€Í½™Ñİ…É•	¥Ñµ…À¹¥ÍÁ½Í” ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸±¥¹•Ìì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€¥¹Ñ•É¹…°Í•…±•±…ÍÌQÉ…¹Í±…Ñ¥½¹M•ÉÙ¥”(€€€ì(€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÉ•…‘½¹±ä!ÑÑÁ±¥•¹Ğ±¥•¹Ğ€ôÉ•…Ñ•±¥•¹Ğ ¤ì(€€€€€€€ÁÉ¥Ù…Ñ”É•…‘½¹±äÍÑÉ¥¹œÑ…É•Ñ1…¹Õ…”ì((€€€€€€€ÁÕ‰±¥ŒQÉ…¹Í±…Ñ¥½¹M•ÉÙ¥”¡ÍÑÉ¥¹œÑ…É•Ñ1…¹Õ…”¤(€€€€€€€ì(€€€€€€€€€€€Ñ¡¥Ì¹Ñ…É•Ñ1…¹Õ…”€ôÑ…É•Ñ1…¹Õ…”ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ!ÑÑÁ±¥•¹ĞÉ•…Ñ•±¥•¹Ğ ¤(€€€€€€€ì(€€€€€€€€€€€!ÑÑÁ±¥•¹Ğ±¥•¹Ğ€ô¹•Ü!ÑÑÁ±¥•¹Ğ ¤ì(€€€€€€€€€€€±¥•¹Ğ¹Q¥µ•½ÕĞ€ôQ¥µ•MÁ…¸¹É½µM•½¹‘Ì ÄÈ¤ì(€€€€€€€€€€€±¥•¹Ğ¹•™…Õ±ÑI•ÅÕ•ÍÑ!•…‘•ÉÌ¹UÍ•É•¹Ğ¹A…ÉÍ•‘ ‰5½é¥±±„¼Ô¸ÀMÉ••¹1¥¹¼¼À¸Äˆ¤ì(€€€€€€€€€€€É•ÑÕÉ¸±¥•¹Ğì(€€€€€€€ô((€€€€€€€ÁÕ‰±¥Œ…Íå¹ŒQ…Í¬ñ1¥ÍĞñ=É1¥¹•%¹™¼øøQÉ…¹Í±…Ñ•Íå¹Œ¡1¥ÍĞñ=É1¥¹•%¹™¼ø±¥¹•Ì¤(€€€€€€€ì(€€€€€€€€€€€1¥ÍĞñ=É1¥¹•%¹™¼øÕÍ•™Õ°€ô±¥¹•Ì(€€€€€€€€€€€€€€€€¹]¡•É”¡‘•±•…Ñ”¡=É1¥¹•%¹™¼±¥¹”¤ìÉ•ÑÕÉ¸±¥¹”¹Q•áĞ¹¹ä¡¡…È¹%Í1•ÑÑ•È¤ìô¤(€€€€€€€€€€€€€€€€¹=É‘•É	ä¡‘•±•…Ñ”¡=É1¥¹•%¹™¼±¥¹”¤ìÉ•ÑÕÉ¸±¥¹”¹	½Õ¹‘Ì¹Q½Àìô¤(€€€€€€€€€€€€€€€€¹Q¡•¹	ä¡‘•±•…Ñ”¡=É1¥¹•%¹™¼±¥¹”¤ìÉ•ÑÕÉ¸±¥¹”¹	½Õ¹‘Ì¹1•™Ğìô¤(€€€€€€€€€€€€€€€€¹Q…­” ĞÀ¤(€€€€€€€€€€€€€€€€¹Q½1¥ÍĞ ¤ì(€€€€€€€€€€€1¥ÍĞñ=É1¥¹•%¹™¼ø‰±½­Ì€ô	Õ¥±‘A…É…É…Á¡Ì¡ÕÍ•™Õ°¤ì(€€€€€€€€€€€1¥ÍĞñQ…Í¬øÑ…Í­Ì€ô¹•Ü1¥ÍĞñQ…Í¬ø ¤ì(€€€€€€€€€€€™½É•… €¡=É1¥¹•%¹™¼‰±½¬¥¸‰±½­Ì¤Ñ…Í­Ì¹‘¡QÉ…¹Í±…Ñ•1¥¹•Íå¹Œ¡‰±½¬¤¤ì(€€€€€€€€€€€…İ…¥ĞQ…Í¬¹]¡•¹±°¡Ñ…Í­Ì¹Q½ÉÉ…ä ¤¤ì(€€€€€€€€€€€É•ÑÕÉ¸‰±½­Ì¹]¡•É”¡‘•±•…Ñ”¡=É1¥¹•%¹™¼‰±½¬¤ìÉ•ÑÕÉ¸€…MÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡‰±½¬¹QÉ…¹Í±…Ñ¥½¸¤ìô¤¹Q½1¥ÍĞ ¤ì(€€€€€€€ô((€€€€€€€¥¹Ñ•É¹…°ÍÑ…Ñ¥Œ1¥ÍĞñ=É1¥¹•%¹™¼ø	Õ¥±‘A…É…É…Á¡Ì¡1¥ÍĞñ=É1¥¹•%¹™¼ø±¥¹•Ì¤(€€€€€€€ì(€€€€€€€€€€€1¥ÍĞñ=É1¥¹•%¹™¼ø‰±½­Ì€ô¹•Ü1¥ÍĞñ=É1¥¹•%¹™¼ø ¤ì(€€€€€€€€€€€1¥ÍĞñ=É1¥¹•%¹™¼øÕÉÉ•¹Ğ€ô¹•Ü1¥ÍĞñ=É1¥¹•%¹™¼ø ¤ì(€€€€€€€€€€€I•Ñ…¹±”ÕÉÉ•¹Ñ	½Õ¹‘Ì€ôI•Ñ…¹±”¹µÁÑäì((€€€€€€€€€€€™½É•… €¡=É1¥¹•%¹™¼±¥¹”¥¸±¥¹•Ì¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€‰½½°‰•±½¹Ì€ô™…±Í”ì(€€€€€€€€€€€€€€€¥˜€¡ÕÉÉ•¹Ğ¹½Õ¹Ğ€ø€À¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€=É1¥¹•%¹™¼ÁÉ•Ù¥½ÕÌ€ôÕÉÉ•¹ÑmÕÉÉ•¹Ğ¹½Õ¹Ğ€´€Åtì(€€€€€€€€€€€€€€€€€€€¥¹ĞÙ•ÉÑ¥…±…À€ô±¥¹”¹	½Õ¹‘Ì¹Q½À€´ÁÉ•Ù¥½ÕÌ¹	½Õ¹‘Ì¹	½ÑÑ½´ì(€€€€€€€€€€€€€€€€€€€¥¹Ğ…±±½İ•‘…À€ô€¡¥¹Ğ¤¡5…Ñ ¹5…à¡ÁÉ•Ù¥½ÕÌ¹	½Õ¹‘Ì¹!•¥¡Ğ°±¥¹”¹	½Õ¹‘Ì¹!•¥¡Ğ¤€¨€Ä¸ÌÔ¤ì(€€€€€€€€€€€€€€€€€€€‰½½°¡½É¥é½¹Ñ…±±åI•±…Ñ•€ô±¥¹”¹	½Õ¹‘Ì¹1•™Ğ€ğôÕÉÉ•¹Ñ	½Õ¹‘Ì¹I¥¡Ğ€¬€ĞÀ€˜˜(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€±¥¹”¹	½Õ¹‘Ì¹I¥¡Ğ€øôÕÉÉ•¹Ñ	½Õ¹‘Ì¹1•™Ğ€´€ĞÀì(€€€€€€€€€€€€€€€€€€€‰•±½¹Ì€ôÙ•ÉÑ¥…±…À€ğô…±±½İ•‘…À€˜˜Ù•ÉÑ¥…±…À€øô€µ5…Ñ ¹5…à¡ÁÉ•Ù¥½ÕÌ¹	½Õ¹‘Ì¹!•¥¡Ğ°±¥¹”¹	½Õ¹‘Ì¹!•¥¡Ğ¤€˜˜¡½É¥é½¹Ñ…±±åI•±…Ñ•ì(€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€¥˜€ …‰•±½¹Ì€˜˜ÕÉÉ•¹Ğ¹½Õ¹Ğ€ø€À¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€‰±½­Ì¹‘¡É•…Ñ•A…É…É…Á ¡ÕÉÉ•¹Ğ°ÕÉÉ•¹Ñ	½Õ¹‘Ì¤¤ì(€€€€€€€€€€€€€€€€€€€ÕÉÉ•¹Ğ¹±•…È ¤ì(€€€€€€€€€€€€€€€€€€€ÕÉÉ•¹Ñ	½Õ¹‘Ì€ôI•Ñ…¹±”¹µÁÑäì(€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€ÕÉÉ•¹Ğ¹‘¡±¥¹”¤ì(€€€€€€€€€€€€€€€ÕÉÉ•¹Ñ	½Õ¹‘Ì€ôÕÉÉ•¹Ñ	½Õ¹‘Ì¹%ÍµÁÑä€ü±¥¹”¹	½Õ¹‘Ì€èI•Ñ…¹±”¹U¹¥½¸¡ÕÉÉ•¹Ñ	½Õ¹‘Ì°±¥¹”¹	½Õ¹‘Ì¤ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡ÕÉÉ•¹Ğ¹½Õ¹Ğ€ø€À¤‰±½­Ì¹‘¡É•…Ñ•A…É…É…Á ¡ÕÉÉ•¹Ğ°ÕÉÉ•¹Ñ	½Õ¹‘Ì¤¤ì(€€€€€€€€€€€É•ÑÕÉ¸‰±½­Ìì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ=É1¥¹•%¹™¼É•…Ñ•A…É…É…Á ¡1¥ÍĞñ=É1¥¹•%¹™¼ø±¥¹•Ì°I•Ñ…¹±”‰½Õ¹‘Ì¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸¹•Ü=É1¥¹•%¹™¼(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€Q•áĞ€ôMÑÉ¥¹œ¹)½¥¸ ˆ€ˆ°±¥¹•Ì¹M•±•Ğ¡‘•±•…Ñ”¡=É1¥¹•%¹™¼±¥¹”¤ìÉ•ÑÕÉ¸±¥¹”¹Q•áĞ¹QÉ¥´ ¤ìô¤¹Q½ÉÉ…ä ¤¤°(€€€€€€€€€€€€€€€	½Õ¹‘Ì€ô‰½Õ¹‘Ì(€€€€€€€€€€€ôì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”…Íå¹ŒQ…Í¬QÉ…¹Í±…Ñ•1¥¹•Íå¹Œ¡=É1¥¹•%¹™¼±¥¹”¤(€€€€€€€ì(€€€€€€€€€€€ÍÑÉ¥¹œÕÉ°€ô€‰¡ÑÑÁÌè¼½ÑÉ…¹Í±…Ñ”¹½½±•…Á¥Ì¹½´½ÑÉ…¹Í±…Ñ•}„½Í¥¹±”ı±¥•¹ĞõÑà™Í°õ…ÕÑ¼™Ñ°ôˆ€¬(€€€€€€€€€€€€€€€€€€€€€€€€UÉ¤¹Í…Á•…Ñ…MÑÉ¥¹œ¡Ñ…É•Ñ1…¹Õ…”¤€¬€ˆ™‘ĞõĞ™Äôˆ€¬UÉ¤¹Í…Á•…Ñ…MÑÉ¥¹œ¡±¥¹”¹Q•áĞ¤ì(€€€€€€€€€€€ÍÑÉ¥¹œ©Í½¸€ô…İ…¥Ğ±¥•¹Ğ¹•ÑMÑÉ¥¹Íå¹Œ¡ÕÉ°¤ì(€€€€€€€€€€€±¥¹”¹QÉ…¹Í±…Ñ¥½¸€ôA…ÉÍ•I•ÍÁ½¹Í”¡©Í½¸¤ì(€€€€€€€ô((€€€€€€€¥¹Ñ•É¹…°ÍÑ…Ñ¥ŒÍÑÉ¥¹œA…ÉÍ•I•ÍÁ½¹Í”¡ÍÑÉ¥¹œ©Í½¸¤(€€€€€€€ì(€€€€€€€€€€€)…Ù…MÉ¥ÁÑM•É¥…±¥é•ÈÍ•É¥…±¥é•È€ô¹•Ü)…Ù…MÉ¥ÁÑM•É¥…±¥é•È ¤ì(€€€€€€€€€€€½‰©•ÑmtÉ½½Ğ€ôÍ•É¥…±¥é•È¹•Í•É¥…±¥é•=‰©•Ğ¡©Í½¸¤…Ì½‰©•Ñmtì(€€€€€€€€€€€¥˜€¡É½½Ğ€ôô¹Õ±°ñğÉ½½Ğ¹1•¹Ñ €ôô€À¤É•ÑÕÉ¸MÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€½‰©•ÑmtÍ•µ•¹ÑÌ€ôÉ½½ÑlÁt…Ì½‰©•Ñmtì(€€€€€€€€€€€¥˜€¡Í•µ•¹ÑÌ€ôô¹Õ±°¤É•ÑÕÉ¸MÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€MÑÉ¥¹	Õ¥±‘•ÈÉ•ÍÕ±Ğ€ô¹•ÜMÑÉ¥¹	Õ¥±‘•È ¤ì(€€€€€€€€€€€™½É•… €¡½‰©•Ğ¥Ñ•´¥¸Í•µ•¹ÑÌ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€½‰©•ÑmtÍ•µ•¹Ğ€ô¥Ñ•´…Ì½‰©•Ñmtì(€€€€€€€€€€€€€€€¥˜€¡Í•µ•¹Ğ€„ô¹Õ±°€˜˜Í•µ•¹Ğ¹1•¹Ñ €ø€À€˜˜Í•µ•¹ÑlÁt€„ô¹Õ±°¤É•ÍÕ±Ğ¹ÁÁ•¹¡Í•µ•¹ÑlÁt¹Q½MÑÉ¥¹œ ¤¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€É•ÑÕÉ¸É•ÍÕ±Ğ¹Q½MÑÉ¥¹œ ¤¹QÉ¥´ ¤ì(€€€€€€€ô(€€€ô((€€€¥¹Ñ•É¹…°Í•…±•±…ÍÌ=Ù•É±…å½É´€è½É´(€€€ì(€€€€€€€ÁÉ¥Ù…Ñ”½¹ÍĞ¥¹Ğ]ÍáQÉ…¹ÍÁ…É•¹Ğ€ô€ÁàÀÀÀÀÀÀÈÀì(€€€€€€€ÁÉ¥Ù…Ñ”½¹ÍĞ¥¹Ğ]ÍáQ½½±]¥¹‘½Ü€ô€ÁàÀÀÀÀÀÀàÀì(€€€€€€€ÁÉ¥Ù…Ñ”½¹ÍĞ¥¹Ğ]Íá9½Ñ¥Ù…Ñ”€ô€ÁàÀàÀÀÀÀÀÀì(€€€€€€€ÁÉ¥Ù…Ñ”É•…‘½¹±äQ¥µ•È±½Í•Q¥µ•Èì((€€€€€€€ÁÕ‰±¥Œ=Ù•É±…å½É´¡I•Ñ…¹±”ÍÉ••¹É•„°%¹Õµ•É…‰±”ñ=É1¥¹•%¹™¼ø±¥¹•Ì°	¥Ñµ…À…ÁÑÕÉ•‘%µ…”°¥¹ĞÍ•½¹‘Ì¤(€€€€€€€ì(€€€€€€€€€€€MÑ…ÉÑA½Í¥Ñ¥½¸€ô½ÉµMÑ…ÉÑA½Í¥Ñ¥½¸¹5…¹Õ…°ì(€€€€€€€€€€€	½Õ¹‘Ì€ôÍÉ••¹É•„ì(€€€€€€€€€€€½Éµ	½É‘•ÉMÑå±”€ô½Éµ	½É‘•ÉMÑå±”¹9½¹”ì(€€€€€€€€€€€M¡½İ%¹Q…Í­‰…È€ô™…±Í”ì(€€€€€€€€€€€Q½Á5½ÍĞ€ôÑÉÕ”ì(€€€€€€€€€€€	…­½±½È€ô½±½È¹5…•¹Ñ„ì(€€€€€€€€€€€QÉ…¹ÍÁ…É•¹å-•ä€ô½±½È¹5…•¹Ñ„ì((€€€€€€€€€€€™½É•… €¡=É1¥¹•%¹™¼±¥¹”¥¸±¥¹•Ì¤‘‘QÉ…¹Í±…Ñ¥½¸¡±¥¹”°…ÁÑÕÉ•‘%µ…”¤ì((€€€€€€€€€€€±½Í•Q¥µ•È€ô¹•ÜQ¥µ•È ¤ì(€€€€€€€€€€€±½Í•Q¥µ•È¹%¹Ñ•ÉÙ…°€ôÍ•½¹‘Ì€¨€ÄÀÀÀì(€€€€€€€€€€€±½Í•Q¥µ•È¹Q¥¬€¬ô‘•±•…Ñ”ì±½Í” ¤ìôì(€€€€€€€€€€€±½Í•Q¥µ•È¹MÑ…ÉĞ ¤ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”‰½½°M¡½İ]¥Ñ¡½ÕÑÑ¥Ù…Ñ¥½¸ì•ĞìÉ•ÑÕÉ¸ÑÉÕ”ìôô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”É•…Ñ•A…É…µÌÉ•…Ñ•A…É…µÌ(€€€€€€€ì(€€€€€€€€€€€•Ğ(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•…Ñ•A…É…µÌÁ…É…µ•Ñ•ÉÌ€ô‰…Í”¹É•…Ñ•A…É…µÌì(€€€€€€€€€€€€€€€Á…É…µ•Ñ•ÉÌ¹áMÑå±”ğô]ÍáQÉ…¹ÍÁ…É•¹Ğğ]ÍáQ½½±]¥¹‘½Üğ]Íá9½Ñ¥Ù…Ñ”ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸Á…É…µ•Ñ•ÉÌì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”Ù½¥‘‘QÉ…¹Í±…Ñ¥½¸¡=É1¥¹•%¹™¼±¥¹”°	¥Ñµ…À…ÁÑÕÉ•‘%µ…”¤(€€€€€€€ì(€€€€€€€€€€€I•Ñ…¹±”…É•„€ôI•Ñ…¹±”¹%¹Ñ•ÉÍ•Ğ¡¹•ÜI•Ñ…¹±”¡A½¥¹Ğ¹µÁÑä°…ÁÑÕÉ•‘%µ…”¹M¥é”¤°±¥¹”¹	½Õ¹‘Ì¤ì(€€€€€€€€€€€¥˜€¡…É•„¹]¥‘Ñ €ğ€Èñğ…É•„¹!•¥¡Ğ€ğ€È¤É•ÑÕÉ¸ì(€€€€€€€€€€€½±½È‰…­É½Õ¹€ôM…µÁ±•	…­É½Õ¹¡…ÁÑÕÉ•‘%µ…”°…É•„¤ì(€€€€€€€€€€€‘½Õ‰±”±Õµ¥¹…¹”€ô‰…­É½Õ¹¹H€¨€À¸Èää€¬‰…­É½Õ¹¹€¨€À¸ÔàÜ€¬‰…­É½Õ¹¹€¨€À¸ÄÄĞì(€€€€€€€€€€€½±½È™½É•É½Õ¹€ô±Õµ¥¹…¹”€øô€ÄĞÔ€ü½±½È¹É½µÉˆ Èà°€ÌÀ°€ÌĞ¤€è½±½È¹]¡¥Ñ”ì((€€€€€€€€€€€¥¹Ğ•ÍÑ¥µ…Ñ•‘1¥¹•Ì€ô5…Ñ ¹5…à Ä°€¡¥¹Ğ¥5…Ñ ¹I½Õ¹ ¡‘½Õ‰±”¥±¥¹”¹	½Õ¹‘Ì¹!•¥¡Ğ€¼5…Ñ ¹5…à Ä°5…Ñ ¹5¥¸¡±¥¹”¹	½Õ¹‘Ì¹!•¥¡Ğ°€Ğà¤¤¤¤ì(€€€€€€€€€€€¥¹ĞÍ½ÕÉ•1¥¹•!•¥¡Ğ€ô5…Ñ ¹5…à ÄØ°±¥¹”¹	½Õ¹‘Ì¹!•¥¡Ğ€¼•ÍÑ¥µ…Ñ•‘1¥¹•Ì¤ì(€€€€€€€€€€€¥¹Ğ™½¹ÑM¥é”€ô5…Ñ ¹5…à ÄÀ°5…Ñ ¹5¥¸ ĞÈ°€¡¥¹Ğ¤¡Í½ÕÉ•1¥¹•!•¥¡Ğ€¨€À¸ÜÈ¤¤¤ì(€€€€€€€€€€€1…‰•°±…‰•°€ô¹•Ü1…‰•° ¤ì(€€€€€€€€€€€±…‰•°¹ÕÑ½M¥é”€ô™…±Í”ì(€€€€€€€€€€€±…‰•°¹Q•áĞ€ô±¥¹”¹QÉ…¹Í±…Ñ¥½¸ì(€€€€€€€€€€€±…‰•°¹½É•½±½È€ô™½É•É½Õ¹ì(€€€€€€€€€€€±…‰•°¹	…­½±½È€ô‰…­É½Õ¹ì(€€€€€€€€€€€±…‰•°¹Q•áÑ±¥¸€ô½¹Ñ•¹Ñ±¥¹µ•¹Ğ¹Q½Á1•™Ğì(€€€€€€€€€€€±…‰•°¹A…‘‘¥¹œ€ô¹•ÜA…‘‘¥¹œ Ô°€È°€Ô°€È¤ì(€€€€€€€€€€€±…‰•°¹1½…Ñ¥½¸€ô¹•ÜA½¥¹Ğ¡5…Ñ ¹5…à À°±¥¹”¹	½Õ¹‘Ì¹1•™Ğ€´€Ô¤°5…Ñ ¹5…à À°±¥¹”¹	½Õ¹‘Ì¹Q½À€´€Ì¤¤ì(€€€€€€€€€€€±…‰•°¹M¥é”€ô¹•ÜM¥é”¡5…Ñ ¹5¥¸¡]¥‘Ñ €´±…‰•°¹1•™Ğ°±¥¹”¹	½Õ¹‘Ì¹]¥‘Ñ €¬€ÄÈ¤°5…Ñ ¹5¥¸¡!•¥¡Ğ€´±…‰•°¹Q½À°±¥¹”¹	½Õ¹‘Ì¹!•¥¡Ğ€¬€à¤¤ì((€€€€€€€€€€€½¹Ğ™¥ÑÑ•‘½¹Ğ€ô¹Õ±°ì(€€€€€€€€€€€İ¡¥±”€¡™½¹ÑM¥é”€øô€ä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥˜€¡™¥ÑÑ•‘½¹Ğ€„ô¹Õ±°¤™¥ÑÑ•‘½¹Ğ¹¥ÍÁ½Í” ¤ì(€€€€€€€€€€€€€€€™¥ÑÑ•‘½¹Ğ€ô¹•Ü½¹Ğ ‰M•½”U$ˆ°™½¹ÑM¥é”°½¹ÑMÑå±”¹I•Õ±…È°É…Á¡¥ÍU¹¥Ğ¹A¥á•°¤ì(€€€€€€€€€€€€€€€±…‰•°¹½¹Ğ€ô™¥ÑÑ•‘½¹Ğì(€€€€€€€€€€€€€€€M¥é”µ•…ÍÕÉ•€ôQ•áÑI•¹‘•É•È¹5•…ÍÕÉ•Q•áĞ¡±…‰•°¹Q•áĞ°™¥ÑÑ•‘½¹Ğ°(€€€€€€€€€€€€€€€€€€€¹•ÜM¥é”¡5…Ñ ¹5…à ÈÀ°±…‰•°¹±¥•¹ÑM¥é”¹]¥‘Ñ €´±…‰•°¹A…‘‘¥¹œ¹!½É¥é½¹Ñ…°¤°€ÄÀÀÀÀ¤°(€€€€€€€€€€€€€€€€€€€Q•áÑ½Éµ…Ñ±…Ì¹]½É‘	É•…¬ğQ•áÑ½Éµ…Ñ±…Ì¹9½A…‘‘¥¹œ¤ì(€€€€€€€€€€€€€€€¥˜€¡µ•…ÍÕÉ•¹!•¥¡Ğ€ğô±…‰•°¹±¥•¹ÑM¥é”¹!•¥¡Ğ€´±…‰•°¹A…‘‘¥¹œ¹Y•ÉÑ¥…°¤‰É•…¬ì(€€€€€€€€€€€€€€€™½¹ÑM¥é”´´ì(€€€€€€€€€€€ô(€€€€€€€€€€€½¹ÑÉ½±Ì¹‘¡±…‰•°¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ½±½ÈM…µÁ±•	…­É½Õ¹¡	¥Ñµ…À¥µ…”°I•Ñ…¹±”…É•„¤(€€€€€€€ì(€€€€€€€€€€€1¥ÍĞñ¥¹ĞøÉ•‘Ì€ô¹•Ü1¥ÍĞñ¥¹Ğø ¤ì(€€€€€€€€€€€1¥ÍĞñ¥¹ĞøÉ••¹Ì€ô¹•Ü1¥ÍĞñ¥¹Ğø ¤ì(€€€€€€€€€€€1¥ÍĞñ¥¹Ğø‰±Õ•Ì€ô¹•Ü1¥ÍĞñ¥¹Ğø ¤ì(€€€€€€€€€€€¥¹ĞÍÑ•Á`€ô5…Ñ ¹5…à È°…É•„¹]¥‘Ñ €¼€ÌÔ¤ì(€€€€€€€€€€€¥¹ĞÍÑ•Ád€ô5…Ñ ¹5…à È°…É•„¹!•¥¡Ğ€¼€ÈÀ¤ì((€€€€€€€€€€€™½È€¡¥¹Ğä€ô…É•„¹Q½Àìä€ğ…É•„¹	½ÑÑ½´ìä€¬ôÍÑ•Ád¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™½È€¡¥¹Ğà€ô…É•„¹1•™Ğìà€ğ…É•„¹I¥¡Ğìà€¬ôÍÑ•Á`¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€½±½ÈÁ¥á•°€ô¥µ…”¹•ÑA¥á•°¡à°ä¤ì(€€€€€€€€€€€€€€€€€€€É•‘Ì¹‘¡Á¥á•°¹H¤ì(€€€€€€€€€€€€€€€€€€€É••¹Ì¹‘¡Á¥á•°¹¤ì(€€€€€€€€€€€€€€€€€€€‰±Õ•Ì¹‘¡Á¥á•°¹¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡É•‘Ì¹½Õ¹Ğ€ôô€À¤É•ÑÕÉ¸½±½È¹É½µÉˆ ÈĞÔ°€ÈĞÔ°€ÈĞÔ¤ì(€€€€€€€€€€€É•‘Ì¹M½ÉĞ ¤ìÉ••¹Ì¹M½ÉĞ ¤ì‰±Õ•Ì¹M½ÉĞ ¤ì(€€€€€€€€€€€¥¹Ğµ¥‘‘±”€ôÉ•‘Ì¹½Õ¹Ğ€¼€Èì(€€€€€€€€€€€É•ÑÕÉ¸½±½È¹É½µÉˆ¡É•‘Ímµ¥‘‘±•t°É••¹Ímµ¥‘‘±•t°‰±Õ•Ímµ¥‘‘±•t¤ì(€€€€€€€ô((€€€€€€€ÁÉ½Ñ•Ñ•½Ù•ÉÉ¥‘”Ù½¥¥ÍÁ½Í”¡‰½½°‘¥ÍÁ½Í¥¹œ¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡‘¥ÍÁ½Í¥¹œ€˜˜±½Í•Q¥µ•È€„ô¹Õ±°¤±½Í•Q¥µ•È¹¥ÍÁ½Í” ¤ì(€€€€€€€€€€€‰…Í”¹¥ÍÁ½Í”¡‘¥ÍÁ½Í¥¹œ¤ì(€€€€€€€ô(€€€ô)ô