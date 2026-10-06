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
using System.Security.Cryptography;
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
[assembly: System.Reflection.AssemblyVersion("0.9.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.9.2.0")]

namespace ScreenLingo
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [STAThread]
        private static void Main(string[] args)
        {
            EnableDpiAwareness();
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

        private static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
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
                    lines[0].Translation = "Открыть настройки";
                    using (Bitmap rendered = OverlayRenderer.Render(sample, lines))
                    {
                        int visiblePixels = 0;
                        int semiTransparentPixels = 0;
                        for (int y = 0; y < rendered.Height; y += 2)
                            for (int x = 0; x < rendered.Width; x += 2)
                            {
                                int alpha = rendered.GetPixel(x, y).A;
                                if (alpha > 0) visiblePixels++;
                                if (alpha > 0 && alpha < 255) semiTransparentPixels++;
                            }
                        log.AppendLine("OVERLAY_VISIBLE_PIXELS=" + visiblePixels);
                        log.AppendLine("OVERLAY_SEMITRANSPARENT_PIXELS=" + semiTransparentPixels);
                        if (visiblePixels < 20) throw new InvalidOperationException("Self-test overlay renderer produced no visible content.");
                        if (semiTransparentPixels > 0) throw new InvalidOperationException("Self-test overlay renderer produced transparency-key color fringes.");
                        using (Bitmap preview = new Bitmap(sample))
                        using (Graphics previewGraphics = Graphics.FromImage(preview))
                        {
                            previewGraphics.DrawImageUnscaled(rendered, Point.Empty);
                            string previewPath = Path.ChangeExtension(logPath, ".png");
                            preview.Save(previewPath, ImageFormat.Png);
                            log.AppendLine("OVERLAY_PREVIEW=" + previewPath);
                        }
                    }
                    lines[0].Translation = null;
                    string parsed = TranslationService.ParseResponse("[[[\"Открыть настройки\",\"Open settings\",null,null,10]],null,\"en\"]");
                    log.AppendLine("TRANSLATION_PARSER=" + parsed);
                    if (parsed != "Открыть настройки") throw new InvalidOperationException("Self-test translation parser failed.");
                    string deepLParsed = TranslationService.ParseDeepLResponse("{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"Открыть настройки\"}]}");
                    string libreParsed = TranslationService.ParseLibreResponse("{\"translatedText\":\"Открыть настройки\"}");
                    log.AppendLine("DEEPL_PARSER=" + deepLParsed);
                    log.AppendLine("LIBRE_PARSER=" + libreParsed);
                    if (deepLParsed != "Открыть настройки" || libreParsed != "Открыть настройки") throw new InvalidOperationException("Self-test provider parser failed.");
                    string yandexOcrJson = "{\"result\":{\"textAnnotation\":{\"blocks\":[{\"lines\":[{\"boundingBox\":{\"vertices\":[{\"x\":\"10\",\"y\":\"20\"},{\"x\":\"110\",\"y\":\"20\"},{\"x\":\"110\",\"y\":\"50\"},{\"x\":\"10\",\"y\":\"50\"}]},\"text\":\"Open settings\",\"words\":[]}]}]}}}";
                    List<OcrLineInfo> yandexLines = YandexCloudService.ParseOcrResponse(yandexOcrJson);
                    List<string> yandexTranslations = YandexCloudService.ParseTranslationResponse("{\"translations\":[{\"text\":\"Открыть настройки\",\"detectedLanguageCode\":\"en\"}]}");
                    log.AppendLine("YANDEX_OCR_PARSER=" + yandexLines.Count + "; YANDEX_TRANSLATION_PARSER=" + String.Join(" | ", yandexTranslations.ToArray()));
                    if (yandexLines.Count != 1 || yandexLines[0].Text != "Open settings" || yandexTranslations.Count != 1 || yandexTranslations[0] != "Открыть настройки")
                        throw new InvalidOperationException("Self-test Yandex parser failed.");
                    uint hotkeyModifiers;
                    Keys hotkeyKey;
                    bool hotkeyParsed = HotkeyUtility.TryParse("Ctrl+Alt+F8", out hotkeyModifiers, out hotkeyKey);
                    log.AppendLine("HOTKEY_PARSER=" + hotkeyParsed + "; KEY=" + hotkeyKey);
                    if (!hotkeyParsed || hotkeyKey != Keys.F8) throw new InvalidOperationException("Self-test hotkey parser failed.");
                    bool protectedSettings = AppSettings.TestProtectionRoundTrip("screenlingo-test-key");
                    log.AppendLine("SETTINGS_DPAPI=" + protectedSettings);
                    if (!protectedSettings) throw new InvalidOperationException("Self-test settings encryption failed.");
                    string startupCommand = StartupRegistration.Command(@"C:\Apps\ScreenLingo.exe");
                    log.AppendLine("STARTUP_COMMAND=" + startupCommand);
                    if (startupCommand != "\"C:\\Apps\\ScreenLingo.exe\" --tray") throw new InvalidOperationException("Startup command is invalid.");
                    List<OcrLineInfo> paragraphSample = new List<OcrLineInfo>
                    {
                        new OcrLineInfo { Text = "Mike is ten. He is a schoolboy.", Bounds = new Rectangle(10, 10, 420, 36) },
                        new OcrLineInfo { Text = "His hobby is football.", Bounds = new Rectangle(10, 54, 360, 36) },
                        new OcrLineInfo { Text = "He likes to play the guitar.", Bounds = new Rectangle(10, 98, 400, 36) }
                    };
                    List<OcrLineInfo> paragraphs = TranslationService.BuildParagraphs(paragraphSample);
                    log.AppendLine("PARAGRAPHS=" + paragraphs.Count + "; TEXT=" + paragraphs[0].Text);
                    if (paragraphs.Count != 1) throw new InvalidOperationException("Self-test paragraph grouping failed.");
                    List<OcrLineInfo> scatteredSample = new List<OcrLineInfo>
                    {
                        new OcrLineInfo { Text = "comfort", Bounds = new Rectangle(320, 10, 120, 28) },
                        new OcrLineInfo { Text = "on difficult days", Bounds = new Rectangle(325, 44, 155, 22) },
                        new OcrLineInfo { Text = "smiles", Bounds = new Rectangle(80, 34, 100, 28) },
                        new OcrLineInfo { Text = "when sadness intrudes", Bounds = new Rectangle(70, 68, 190, 22) },
                        new OcrLineInfo { Text = "laughter", Bounds = new Rectangle(540, 62, 135, 28) },
                        new OcrLineInfo { Text = "to kiss your lips", Bounds = new Rectangle(545, 96, 145, 22) }
                    };
                    List<OcrLineInfo> scatteredBlocks = TranslationService.BuildParagraphs(scatteredSample);
                    log.AppendLine("SCATTERED_BLOCKS=" + scatteredBlocks.Count + "; TEXT=" + String.Join(" | ", scatteredBlocks.Select(delegate(OcrLineInfo block) { return block.Text; }).ToArray()));
                    if (scatteredBlocks.Count != 3) throw new InvalidOperationException("Self-test scattered layout grouping failed.");
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

    internal static class StartupRegistration
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ScreenLingo";

        internal static string Command(string executable)
        {
            return "\"" + executable + "\" --tray";
        }

        internal static bool Enabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    string value = key == null ? null : key.GetValue(ValueName) as string;
                    return String.Equals(value, Command(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        internal static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (enabled) key.SetValue(ValueName, Command(Application.ExecutablePath), RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    internal sealed class AppSettings
    {
        public string SourceLanguage = "auto";
        public string TargetLanguage = "ru";
        public string TranslationProvider = "Google";
        public string TranslationApiKey = String.Empty;
        public string TranslationEndpoint = "https://libretranslate.com";
        public string YandexApiKey = String.Empty;
        public string YandexFolderId = String.Empty;
        public int OverlaySeconds = 18;
        public int WatchIntervalMs = 2000;
        public string CaptureHotkey = "Ctrl+Shift+T";
        public string CopyHotkey = "Ctrl+Shift+R";
        public string RepeatHotkey = "Ctrl+Shift+Y";
        public string WatchHotkey = "Ctrl+Shift+W";

        private static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScreenLingo.ini"); }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            string path = SettingsPath;
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
                if (key.Equals("TranslationProvider", StringComparison.OrdinalIgnoreCase)) settings.TranslationProvider = value;
                if (key.Equals("TranslationEndpoint", StringComparison.OrdinalIgnoreCase)) settings.TranslationEndpoint = value;
                if (key.Equals("TranslationApiKeyProtected", StringComparison.OrdinalIgnoreCase)) settings.TranslationApiKey = Unprotect(value);
                if (key.Equals("YandexApiKeyProtected", StringComparison.OrdinalIgnoreCase)) settings.YandexApiKey = Unprotect(value);
                if (key.Equals("YandexFolderId", StringComparison.OrdinalIgnoreCase)) settings.YandexFolderId = value;
                if (key.Equals("CaptureHotkey", StringComparison.OrdinalIgnoreCase)) settings.CaptureHotkey = value;
                if (key.Equals("CopyHotkey", StringComparison.OrdinalIgnoreCase)) settings.CopyHotkey = value;
                if (key.Equals("RepeatHotkey", StringComparison.OrdinalIgnoreCase)) settings.RepeatHotkey = value;
                if (key.Equals("WatchHotkey", StringComparison.OrdinalIgnoreCase)) settings.WatchHotkey = value;
                int seconds;
                if (key.Equals("OverlaySeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out seconds))
                    settings.OverlaySeconds = Math.Max(3, Math.Min(120, seconds));
                int interval;
                if (key.Equals("WatchIntervalMs", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out interval))
                    settings.WatchIntervalMs = Math.Max(750, Math.Min(30000, interval));
            }
            return settings;
        }

        public void Save()
        {
            string contents =
                "# Язык OCR должен быть установлен в параметрах языка Windows." + Environment.NewLine +
                "SourceLanguage=" + SourceLanguage + Environment.NewLine +
                "TargetLanguage=" + TargetLanguage + Environment.NewLine + Environment.NewLine +
                "# Сервис перевода: Google, DeepL, LibreTranslate или Yandex Cloud." + Environment.NewLine +
                "TranslationProvider=" + TranslationProvider + Environment.NewLine +
                "TranslationEndpoint=" + TranslationEndpoint + Environment.NewLine +
                "TranslationApiKeyProtected=" + Protect(TranslationApiKey) + Environment.NewLine +
                "YandexApiKeyProtected=" + Protect(YandexApiKey) + Environment.NewLine +
                "YandexFolderId=" + YandexFolderId + Environment.NewLine + Environment.NewLine +
                "# Через сколько секунд скрывать перевод." + Environment.NewLine +
                "OverlaySeconds=" + OverlaySeconds + Environment.NewLine + Environment.NewLine +
                "# Интервал проверки области в режиме наблюдения, в миллисекундах." + Environment.NewLine +
                "WatchIntervalMs=" + WatchIntervalMs + Environment.NewLine + Environment.NewLine +
                "# Глобальные горячие клавиши." + Environment.NewLine +
                "CaptureHotkey=" + CaptureHotkey + Environment.NewLine +
                "CopyHotkey=" + CopyHotkey + Environment.NewLine +
                "RepeatHotkey=" + RepeatHotkey + Environment.NewLine +
                "WatchHotkey=" + WatchHotkey + Environment.NewLine;
            File.WriteAllText(SettingsPath, contents, new UTF8Encoding(false));
        }

        public void ResetHotkeys()
        {
            CaptureHotkey = "Ctrl+Shift+T";
            CopyHotkey = "Ctrl+Shift+R";
            RepeatHotkey = "Ctrl+Shift+Y";
            WatchHotkey = "Ctrl+Shift+W";
        }

        private static string Protect(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            try
            {
                byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch { return String.Empty; }
        }

        internal static bool TestProtectionRoundTrip(string value)
        {
            return Unprotect(Protect(value)) == value;
        }
    }

    internal static class HotkeyUtility
    {
        public const uint Alt = 0x0001;
        public const uint Control = 0x0002;
        public const uint Shift = 0x0004;
        public const uint Windows = 0x0008;

        public static bool TryParse(string value, out uint modifiers, out Keys key)
        {
            modifiers = 0;
            key = Keys.None;
            if (String.IsNullOrWhiteSpace(value)) return false;
            foreach (string rawPart in value.Split('+'))
            {
                string part = rawPart.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= Control;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= Shift;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= Alt;
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= Windows;
                else
                {
                    Keys parsed;
                    if (key != Keys.None || !Enum.TryParse(part, true, out parsed) || IsModifierKey(parsed)) return false;
                    key = parsed;
                }
            }
            return modifiers != 0 && key != Keys.None;
        }

        public static string FromKeyEvent(KeyEventArgs e)
        {
            if (IsModifierKey(e.KeyCode)) return String.Empty;
            List<string> parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Shift) parts.Add("Shift");
            if (e.Alt) parts.Add("Alt");
            if ((e.Modifiers & Keys.LWin) == Keys.LWin || (e.Modifiers & Keys.RWin) == Keys.RWin) parts.Add("Win");
            if (parts.Count == 0) return String.Empty;
            parts.Add(e.KeyCode.ToString());
            return String.Join("+", parts.ToArray());
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                   key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                   key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                   key == Keys.LWin || key == Keys.RWin;
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const int HotkeyCapture = 100;
        private const int HotkeyCopy = 101;
        private const int HotkeyRepeat = 102;
        private const int HotkeyWatch = 103;
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
        private readonly Timer watchTimer;
        private readonly ToolStripMenuItem captureMenuItem;
        private readonly ToolStripMenuItem copyMenuItem;
        private readonly ToolStripMenuItem repeatMenuItem;
        private readonly ToolStripMenuItem watchMenuItem;
        private OverlayForm overlay;
        private IntPtr mouseHook;
        private bool working;
        private bool hasLastArea;
        private bool watchMode;
        private Rectangle lastArea;
        private Rectangle watchArea;
        private string lastWatchText = String.Empty;

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

            ContextMenuStrip menu = new ContextMenuStrip();
            captureMenuItem = new ToolStripMenuItem(String.Empty, null, delegate { BeginCapture(false); });
            copyMenuItem = new ToolStripMenuItem(String.Empty, null, delegate { BeginCapture(true); });
            menu.Items.Add(captureMenuItem);
            menu.Items.Add(copyMenuItem);
            repeatMenuItem = new ToolStripMenuItem(String.Empty, null, delegate { RepeatLastArea(); });
            repeatMenuItem.Enabled = false;
            menu.Items.Add(repeatMenuItem);
            watchMenuItem = new ToolStripMenuItem(String.Empty, null, delegate { ToggleWatchMode(); });
            menu.Items.Add(watchMenuItem);
            menu.Items.Add("Скрыть перевод", null, delegate { HideOverlay(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Настройки…", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { Exit(); });

            appIcon = AppIcon.Create();
            trayIcon = new NotifyIcon();
            trayIcon.Icon = appIcon;
            trayIcon.Text = "ScreenLingo — перевод области экрана";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { BeginCapture(false); };
            watchTimer = new Timer();
            watchTimer.Interval = settings.WatchIntervalMs;
            watchTimer.Tick += delegate { WatchTick(); };

            UpdateMenuHotkeys();
            if (!RegisterConfiguredHotkeys())
            {
                settings.ResetHotkeys();
                RegisterConfiguredHotkeys();
                UpdateMenuHotkeys();
                trayIcon.ShowBalloonTip(4000, "Конфликт горячих клавиш", "Настроенные сочетания заняты. Временно включены стандартные сочетания.", ToolTipIcon.Warning);
            }
            else
            {
                trayIcon.ShowBalloonTip(3000, "ScreenLingo запущен", settings.CaptureHotkey + " — перевести область\n" + settings.WatchHotkey + " — режим наблюдения", ToolTipIcon.Info);
            }
        }

        private bool RegisterConfiguredHotkeys()
        {
            UnregisterConfiguredHotkeys();
            bool success = RegisterConfiguredHotkey(HotkeyCapture, settings.CaptureHotkey) &&
                           RegisterConfiguredHotkey(HotkeyCopy, settings.CopyHotkey) &&
                           RegisterConfiguredHotkey(HotkeyRepeat, settings.RepeatHotkey) &&
                           RegisterConfiguredHotkey(HotkeyWatch, settings.WatchHotkey);
            if (!success) UnregisterConfiguredHotkeys();
            return success;
        }

        private bool RegisterConfiguredHotkey(int id, string value)
        {
            uint modifiers;
            Keys key;
            return HotkeyUtility.TryParse(value, out modifiers, out key) && RegisterHotKey(hotkeyWindow.Handle, id, modifiers, (uint)key);
        }

        private void UnregisterConfiguredHotkeys()
        {
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyCapture);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyCopy);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyRepeat);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyWatch);
        }

        private void UpdateMenuHotkeys()
        {
            captureMenuItem.Text = "Перевести область   " + settings.CaptureHotkey;
            copyMenuItem.Text = "Копировать исходный текст   " + settings.CopyHotkey;
            repeatMenuItem.Text = "Повторить последнюю область   " + settings.RepeatHotkey;
            watchMenuItem.Text = "Наблюдать за областью   " + settings.WatchHotkey;
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == HotkeyCapture) BeginCapture(false);
            if (id == HotkeyCopy) BeginCapture(true);
            if (id == HotkeyRepeat) RepeatLastArea();
            if (id == HotkeyWatch) ToggleWatchMode();
        }

        private async void BeginCapture(bool copyOnly)
        {
            if (working) return;
            StopWatchMode(false);
            HideOverlay();
            Rectangle selected;
            using (SelectionForm selector = new SelectionForm())
            {
                if (selector.ShowDialog() != DialogResult.OK) return;
                selected = selector.SelectedScreenRectangle;
            }
            RememberArea(selected);
            await ProcessAreaAsync(selected, copyOnly, false);
        }

        private async void RepeatLastArea()
        {
            if (working) return;
            if (!hasLastArea)
            {
                trayIcon.ShowBalloonTip(2200, "Область ещё не выбрана", "Сначала выделите область с помощью Ctrl+Shift+T.", ToolTipIcon.Info);
                return;
            }
            StopWatchMode(false);
            await ProcessAreaAsync(lastArea, false, false);
        }

        private async void ToggleWatchMode()
        {
            if (watchMode)
            {
                StopWatchMode(true);
                return;
            }
            if (working) return;

            HideOverlay();
            Rectangle selected;
            using (SelectionForm selector = new SelectionForm())
            {
                if (selector.ShowDialog() != DialogResult.OK) return;
                selected = selector.SelectedScreenRectangle;
            }

            RememberArea(selected);
            watchArea = selected;
            lastWatchText = String.Empty;
            watchMode = true;
            watchMenuItem.Checked = true;
            trayIcon.ShowBalloonTip(1800, "Наблюдение включено", "Область будет переводиться при изменении текста. Ctrl+Shift+W — остановить.", ToolTipIcon.Info);
            await ProcessAreaAsync(watchArea, false, true);
            if (watchMode) watchTimer.Start();
        }

        private async void WatchTick()
        {
            if (!watchMode || working) return;
            await ProcessAreaAsync(watchArea, false, true);
        }

        private async Task ProcessAreaAsync(Rectangle selected, bool copyOnly, bool watchScan)
        {
            if (working) return;
            working = true;
            OverlayForm hiddenWatchOverlay = null;
            try
            {
                if (watchScan && overlay != null && !overlay.IsDisposed)
                {
                    hiddenWatchOverlay = overlay;
                    RemoveMouseDismissHook();
                    hiddenWatchOverlay.Hide();
                }
                else HideOverlay();
                await Task.Delay(90);
                using (Bitmap image = Capture(selected))
                {
                    trayIcon.Text = watchScan ? "ScreenLingo — проверяю область…" : "ScreenLingo — распознаю текст…";
                    List<OcrLineInfo> lines;
                    if (settings.TranslationProvider.Equals("Yandex Cloud", StringComparison.OrdinalIgnoreCase))
                        lines = await YandexCloudService.RecognizeAsync(image, settings.SourceLanguage, settings.YandexApiKey, settings.YandexFolderId);
                    else
                        lines = await OcrService.RecognizeAsync(image, settings.SourceLanguage);
                    if (lines.Count == 0)
                    {
                        if (hiddenWatchOverlay != null)
                        {
                            hiddenWatchOverlay.Close();
                            hiddenWatchOverlay = null;
                        }
                        if (watchScan) lastWatchText = String.Empty;
                        else trayIcon.ShowBalloonTip(2200, "Текст не найден", "Попробуйте выделить область точнее или увеличить текст.", ToolTipIcon.Warning);
                        return;
                    }

                    string sourceText = String.Join(Environment.NewLine, lines.Select(delegate(OcrLineInfo line) { return line.Text; }).ToArray());
                    if (watchScan && sourceText == lastWatchText)
                    {
                        if (hiddenWatchOverlay != null)
                        {
                            hiddenWatchOverlay.Show();
                            overlay = hiddenWatchOverlay;
                            hiddenWatchOverlay = null;
                            InstallMouseDismissHook();
                        }
                        return;
                    }
                    if (hiddenWatchOverlay != null)
                    {
                        hiddenWatchOverlay.Close();
                        hiddenWatchOverlay = null;
                    }
                    if (watchScan) lastWatchText = sourceText;

                    if (copyOnly)
                    {
                        Clipboard.SetText(sourceText);
                        trayIcon.ShowBalloonTip(1800, "Текст скопирован", sourceText.Length > 100 ? sourceText.Substring(0, 100) + "…" : sourceText, ToolTipIcon.Info);
                        return;
                    }

                    trayIcon.Text = "ScreenLingo — перевожу…";
                    TranslationService translator = new TranslationService(settings);
                    lines = await translator.TranslateAsync(lines);
                    if (lines.Count == 0) throw new InvalidOperationException("Сервис перевода не вернул результат.");

                    overlay = new OverlayForm(selected, lines, image, watchScan ? 86400 : settings.OverlaySeconds);
                    overlay.FormClosed += delegate { RemoveMouseDismissHook(); overlay = null; };
                    overlay.Show();
                    InstallMouseDismissHook();
                }
            }
            catch (Exception ex)
            {
                if (hiddenWatchOverlay != null && !hiddenWatchOverlay.IsDisposed) hiddenWatchOverlay.Close();
                if (watchScan) StopWatchMode(false);
                trayIcon.ShowBalloonTip(5000, "Не удалось перевести", FriendlyError(ex), ToolTipIcon.Error);
            }
            finally
            {
                working = false;
                trayIcon.Text = watchMode ? "ScreenLingo — наблюдение за областью" : "ScreenLingo — перевод области экрана";
            }
        }

        private void RememberArea(Rectangle area)
        {
            lastArea = area;
            hasLastArea = true;
            repeatMenuItem.Enabled = true;
        }

        private void StopWatchMode(bool notify)
        {
            if (!watchMode) return;
            watchMode = false;
            watchTimer.Stop();
            watchMenuItem.Checked = false;
            lastWatchText = String.Empty;
            if (notify) trayIcon.ShowBalloonTip(1500, "Наблюдение выключено", "Автоматический перевод области остановлен.", ToolTipIcon.Info);
        }

        private void ShowSettings()
        {
            if (working) return;
            UnregisterConfiguredHotkeys();
            using (SettingsForm form = new SettingsForm(settings))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    RegisterConfiguredHotkeys();
                    return;
                }
                string previousCapture = settings.CaptureHotkey;
                string previousCopy = settings.CopyHotkey;
                string previousRepeat = settings.RepeatHotkey;
                string previousWatch = settings.WatchHotkey;
                settings.CaptureHotkey = form.CaptureHotkey;
                settings.CopyHotkey = form.CopyHotkey;
                settings.RepeatHotkey = form.RepeatHotkey;
                settings.WatchHotkey = form.WatchHotkey;
                if (!RegisterConfiguredHotkeys())
                {
                    settings.CaptureHotkey = previousCapture;
                    settings.CopyHotkey = previousCopy;
                    settings.RepeatHotkey = previousRepeat;
                    settings.WatchHotkey = previousWatch;
                    RegisterConfiguredHotkeys();
                    trayIcon.ShowBalloonTip(4000, "Горячие клавиши заняты", "Одно из новых сочетаний уже используется другой программой.", ToolTipIcon.Warning);
                    return;
                }

                settings.SourceLanguage = form.SelectedSourceLanguageCode;
                settings.TargetLanguage = form.SelectedLanguageCode;
                settings.TranslationProvider = form.SelectedProvider;
                settings.TranslationApiKey = form.ApiKey;
                settings.TranslationEndpoint = form.Endpoint;
                settings.YandexApiKey = form.YandexApiKey;
                settings.YandexFolderId = form.YandexFolderId;
                settings.OverlaySeconds = form.OverlaySeconds;
                settings.WatchIntervalMs = form.WatchIntervalMs;
                watchTimer.Interval = settings.WatchIntervalMs;
                UpdateMenuHotkeys();
                try
                {
                    settings.Save();
                }
                catch (Exception ex)
                {
                    trayIcon.ShowBalloonTip(5000, "Не удалось сохранить настройки", FriendlyError(ex), ToolTipIcon.Error);
                    return;
                }

                try
                {
                    if (form.StartWithWindows != StartupRegistration.Enabled())
                        StartupRegistration.Set(form.StartWithWindows);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Остальные настройки сохранены, но не удалось изменить автозапуск:\n" + FriendlyError(ex),
                        "ScreenLingo",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                lastWatchText = String.Empty;
                trayIcon.ShowBalloonTip(2200, "Настройки сохранены", form.SelectedProvider + " · язык: " + form.SelectedLanguageName, ToolTipIcon.Info);
            }
        }

        private static string FriendlyError(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
            if (current is HttpRequestException || current is WebException)
                return "Нет доступа к сервису перевода. Проверьте интернет или VPN.";
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
            StopWatchMode(false);
            HideOverlay();
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyCapture);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyCopy);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyRepeat);
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyWatch);
            hotkeyWindow.Dispose();
            watchTimer.Dispose();
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

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == WmHotkey && HotkeyPressed != null) HotkeyPressed(message.WParam.ToInt32());
                base.WndProc(ref message);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }
    }

    internal static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr iconHandle);

        public static Icon Create()
        {
            const int size = 32;
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);

                RectangleF body = new RectangleF(1.5f, 1.5f, 29f, 29f);
                using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRectangle(body, 8f))
                using (System.Drawing.Drawing2D.LinearGradientBrush background =
                    new System.Drawing.Drawing2D.LinearGradientBrush(body, Color.FromArgb(38, 198, 218), Color.FromArgb(124, 77, 255), 45f))
                {
                    graphics.FillPath(background, path);
                }

                using (Pen shine = new Pen(Color.FromArgb(105, 255, 255, 255), 1f))
                using (System.Drawing.Drawing2D.GraphicsPath outline = RoundedRectangle(new RectangleF(2.5f, 2.5f, 27f, 27f), 7f))
                    graphics.DrawPath(shine, outline);

                using (Font font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (Brush shadow = new SolidBrush(Color.FromArgb(75, 0, 0, 0)))
                {
                    graphics.DrawString("A", font, shadow, new RectangleF(4.8f, 5.5f, 18f, 18f), format);
                    graphics.DrawString("A", font, Brushes.White, new RectangleF(4f, 4.5f, 18f, 18f), format);
                }

                using (Pen arrow = new Pen(Color.White, 2.2f))
                {
                    arrow.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    arrow.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    graphics.DrawLine(arrow, 16f, 23f, 25f, 23f);
                    graphics.DrawLine(arrow, 25f, 23f, 22f, 20f);
                    graphics.DrawLine(arrow, 25f, 23f, 22f, 26f);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            float diameter = radius * 2f;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LanguageOption
    {
        public readonly string Name;
        public readonly string Code;

        public LanguageOption(string name, string code)
        {
            Name = name;
            Code = code;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox sourceLanguageBox;
        private readonly ComboBox languageBox;
        private readonly ComboBox providerBox;
        private readonly TextBox apiKeyBox;
        private readonly TextBox endpointBox;
        private readonly Label apiKeyLabel;
        private readonly Label endpointLabel;
        private readonly TextBox yandexApiKeyBox;
        private readonly TextBox yandexFolderIdBox;
        private readonly Label yandexApiKeyLabel;
        private readonly Label yandexFolderIdLabel;
        private readonly Label providerHint;
        private readonly TextBox captureHotkeyBox;
        private readonly TextBox copyHotkeyBox;
        private readonly TextBox repeatHotkeyBox;
        private readonly TextBox watchHotkeyBox;
        private readonly NumericUpDown overlaySecondsBox;
        private readonly NumericUpDown watchIntervalBox;
        private readonly CheckBox startWithWindowsBox;

        public string SelectedSourceLanguageCode { get { return ((LanguageOption)sourceLanguageBox.SelectedItem).Code; } }
        public string SelectedLanguageCode { get { return ((LanguageOption)languageBox.SelectedItem).Code; } }
        public string SelectedLanguageName { get { return ((LanguageOption)languageBox.SelectedItem).Name; } }
        public string SelectedProvider { get { return Convert.ToString(providerBox.SelectedItem); } }
        public string ApiKey { get { return apiKeyBox.Text.Trim(); } }
        public string Endpoint { get { return endpointBox.Text.Trim(); } }
        public string YandexApiKey { get { return yandexApiKeyBox.Text.Trim(); } }
        public string YandexFolderId { get { return yandexFolderIdBox.Text.Trim(); } }
        public string CaptureHotkey { get { return captureHotkeyBox.Text; } }
        public string CopyHotkey { get { return copyHotkeyBox.Text; } }
        public string RepeatHotkey { get { return repeatHotkeyBox.Text; } }
        public string WatchHotkey { get { return watchHotkeyBox.Text; } }
        public int OverlaySeconds { get { return (int)overlaySecondsBox.Value; } }
        public int WatchIntervalMs { get { return (int)(watchIntervalBox.Value * 1000); } }
        public bool StartWithWindows { get { return startWithWindowsBox.Checked; } }

        public SettingsForm(AppSettings settings)
        {
            Text = "Настройки ScreenLingo";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(560, 610);
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(247, 248, 251);

            TabControl tabs = new TabControl();
            tabs.Location = new Point(16, 14);
            tabs.Size = new Size(528, 530);
            Controls.Add(tabs);

            TabPage translationPage = new TabPage("Перевод");
            translationPage.BackColor = Color.White;
            tabs.TabPages.Add(translationPage);

            AddLabel(translationPage, "Язык текста на экране (OCR)", 24, 22, true);
            sourceLanguageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(27, 54), Size = new Size(448, 30) };
            sourceLanguageBox.Items.AddRange(CreateSourceLanguages());
            SelectLanguage(sourceLanguageBox, settings.SourceLanguage);
            translationPage.Controls.Add(sourceLanguageBox);

            AddLabel(translationPage, "Язык перевода", 24, 102, true);
            languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(27, 134), Size = new Size(448, 30) };
            languageBox.Items.AddRange(CreateLanguages());
            SelectLanguage(languageBox, settings.TargetLanguage);
            translationPage.Controls.Add(languageBox);

            AddLabel(translationPage, "Сервис перевода", 24, 182, true);
            providerBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(27, 214), Size = new Size(448, 30) };
            providerBox.Items.AddRange(new object[] { "Google", "DeepL", "LibreTranslate", "Yandex Cloud" });
            providerBox.SelectedItem = providerBox.Items.Cast<object>().FirstOrDefault(delegate(object item) { return Convert.ToString(item).Equals(settings.TranslationProvider, StringComparison.OrdinalIgnoreCase); });
            if (providerBox.SelectedIndex < 0) providerBox.SelectedIndex = 0;
            translationPage.Controls.Add(providerBox);

            apiKeyLabel = AddLabel(translationPage, "API-ключ", 24, 262, false);
            apiKeyBox = new TextBox { Location = new Point(27, 288), Size = new Size(448, 28), UseSystemPasswordChar = true, Text = settings.TranslationApiKey };
            translationPage.Controls.Add(apiKeyBox);

            endpointLabel = AddLabel(translationPage, "Адрес LibreTranslate", 24, 332, false);
            endpointBox = new TextBox { Location = new Point(27, 358), Size = new Size(448, 28), Text = settings.TranslationEndpoint };
            translationPage.Controls.Add(endpointBox);

            yandexApiKeyLabel = AddLabel(translationPage, "API-ключ Yandex Cloud", 24, 262, false);
            yandexApiKeyBox = new TextBox { Location = new Point(27, 288), Size = new Size(448, 28), UseSystemPasswordChar = true, Text = settings.YandexApiKey };
            translationPage.Controls.Add(yandexApiKeyBox);

            yandexFolderIdLabel = AddLabel(translationPage, "Folder ID", 24, 332, false);
            yandexFolderIdBox = new TextBox { Location = new Point(27, 358), Size = new Size(448, 28), Text = settings.YandexFolderId };
            translationPage.Controls.Add(yandexFolderIdBox);

            providerHint = new Label { Location = new Point(27, 402), Size = new Size(448, 72), ForeColor = Color.FromArgb(86, 90, 100) };
            translationPage.Controls.Add(providerHint);
            providerBox.SelectedIndexChanged += delegate { UpdateProviderFields(); };
            UpdateProviderFields();

            TabPage controlsPage = new TabPage("Управление");
            controlsPage.BackColor = Color.White;
            tabs.TabPages.Add(controlsPage);
            AddLabel(controlsPage, "Горячие клавиши", 24, 20, true);
            captureHotkeyBox = AddHotkeyRow(controlsPage, "Перевести область", settings.CaptureHotkey, 60);
            copyHotkeyBox = AddHotkeyRow(controlsPage, "Копировать оригинал", settings.CopyHotkey, 104);
            repeatHotkeyBox = AddHotkeyRow(controlsPage, "Повторить область", settings.RepeatHotkey, 148);
            watchHotkeyBox = AddHotkeyRow(controlsPage, "Режим наблюдения", settings.WatchHotkey, 192);

            Button defaultsButton = new Button { Text = "Сбросить клавиши", Location = new Point(304, 236), Size = new Size(171, 32) };
            defaultsButton.Click += delegate
            {
                captureHotkeyBox.Text = "Ctrl+Shift+T";
                copyHotkeyBox.Text = "Ctrl+Shift+R";
                repeatHotkeyBox.Text = "Ctrl+Shift+Y";
                watchHotkeyBox.Text = "Ctrl+Shift+W";
            };
            controlsPage.Controls.Add(defaultsButton);

            AddLabel(controlsPage, "Время показа перевода, секунд", 24, 292, false);
            overlaySecondsBox = new NumericUpDown { Location = new Point(350, 288), Size = new Size(125, 28), Minimum = 3, Maximum = 120, Value = Math.Max(3, Math.Min(120, settings.OverlaySeconds)) };
            controlsPage.Controls.Add(overlaySecondsBox);
            AddLabel(controlsPage, "Интервал наблюдения, секунд", 24, 340, false);
            decimal intervalSeconds = Math.Max(0.75m, Math.Min(30m, settings.WatchIntervalMs / 1000m));
            watchIntervalBox = new NumericUpDown { Location = new Point(350, 336), Size = new Size(125, 28), DecimalPlaces = 2, Increment = 0.25m, Minimum = 0.75m, Maximum = 30m, Value = intervalSeconds };
            controlsPage.Controls.Add(watchIntervalBox);

            startWithWindowsBox = new CheckBox
            {
                Text = "Запускать вместе с Windows · сразу в трее",
                Location = new Point(27, 392),
                Size = new Size(448, 32),
                Checked = StartupRegistration.Enabled()
            };
            controlsPage.Controls.Add(startWithWindowsBox);

            Button saveButton = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Size = new Size(120, 36), Location = new Point(424, 562), BackColor = Color.FromArgb(77, 112, 245), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            saveButton.FlatAppearance.BorderSize = 0;
            Controls.Add(saveButton);
            Button cancelButton = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Size = new Size(105, 36), Location = new Point(309, 562) };
            Controls.Add(cancelButton);
            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private static object[] CreateLanguages()
        {
            return new object[]
            {
                new LanguageOption("Русский", "ru"), new LanguageOption("Английский", "en"),
                new LanguageOption("Украинский", "uk"), new LanguageOption("Немецкий", "de"),
                new LanguageOption("Французский", "fr"), new LanguageOption("Испанский", "es"),
                new LanguageOption("Итальянский", "it"), new LanguageOption("Португальский", "pt"),
                new LanguageOption("Польский", "pl"), new LanguageOption("Турецкий", "tr"),
                new LanguageOption("Китайский (упрощённый)", "zh-CN"), new LanguageOption("Японский", "ja"),
                new LanguageOption("Корейский", "ko")
            };
        }

        private static object[] CreateSourceLanguages()
        {
            return new object[]
            {
                new LanguageOption("Автоматически — языки Windows", "auto"),
                new LanguageOption("Русский", "ru-RU"), new LanguageOption("Английский", "en-US"),
                new LanguageOption("Украинский", "uk-UA"), new LanguageOption("Немецкий", "de-DE"),
                new LanguageOption("Французский", "fr-FR"), new LanguageOption("Испанский", "es-ES"),
                new LanguageOption("Итальянский", "it-IT"), new LanguageOption("Португальский", "pt-BR"),
                new LanguageOption("Польский", "pl-PL"), new LanguageOption("Турецкий", "tr-TR"),
                new LanguageOption("Китайский (упрощённый)", "zh-CN"), new LanguageOption("Японский", "ja-JP"),
                new LanguageOption("Корейский", "ko-KR")
            };
        }

        private static void SelectLanguage(ComboBox box, string code)
        {
            int index = 0;
            for (int i = 0; i < box.Items.Count; i++)
                if (((LanguageOption)box.Items[i]).Code.Equals(code, StringComparison.OrdinalIgnoreCase)) index = i;
            box.SelectedIndex = index;
        }

        private static Label AddLabel(Control parent, string text, int x, int y, bool heading)
        {
            Label label = new Label { Text = text, AutoSize = true, Location = new Point(x, y) };
            if (heading) label.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold, GraphicsUnit.Point);
            parent.Controls.Add(label);
            return label;
        }

        private static TextBox AddHotkeyRow(Control parent, string label, string value, int y)
        {
            AddLabel(parent, label, 27, y + 5, false);
            TextBox box = new TextBox { Location = new Point(275, y), Size = new Size(200, 28), ReadOnly = true, Text = value, BackColor = Color.White };
            box.KeyDown += CaptureHotkeyInput;
            parent.Controls.Add(box);
            return box;
        }

        private static void CaptureHotkeyInput(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            string value = HotkeyUtility.FromKeyEvent(e);
            if (value.Length > 0) ((TextBox)sender).Text = value;
        }

        private void UpdateProviderFields()
        {
            string provider = SelectedProvider;
            bool deepL = provider.Equals("DeepL", StringComparison.OrdinalIgnoreCase);
            bool libre = provider.Equals("LibreTranslate", StringComparison.OrdinalIgnoreCase);
            bool yandex = provider.Equals("Yandex Cloud", StringComparison.OrdinalIgnoreCase);
            apiKeyLabel.Visible = apiKeyBox.Visible = deepL || libre;
            endpointLabel.Visible = endpointBox.Visible = libre;
            yandexApiKeyLabel.Visible = yandexApiKeyBox.Visible = yandex;
            yandexFolderIdLabel.Visible = yandexFolderIdBox.Visible = yandex;
            if (deepL) providerHint.Text = "Нужен ключ DeepL API. Free-ключ с окончанием :fx автоматически использует api-free.deepl.com.";
            else if (libre) providerHint.Text = "Можно указать собственный сервер LibreTranslate. API-ключ для self-hosted сервера обычно не требуется.";
            else if (yandex) providerHint.Text = "Снимок отправляется в Yandex Vision OCR, затем текст переводится через Yandex Translate. Нужны API-ключ и Folder ID.";
            else providerHint.Text = "Google работает без ключа, но использует неофициальный веб-интерфейс и может ограничивать запросы.";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                if (SelectedProvider == "DeepL" && String.IsNullOrWhiteSpace(ApiKey))
                {
                    MessageBox.Show(this, "Для DeepL необходимо указать API-ключ.", "ScreenLingo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                if (SelectedProvider == "Yandex Cloud" && (String.IsNullOrWhiteSpace(YandexApiKey) || String.IsNullOrWhiteSpace(YandexFolderId)))
                {
                    MessageBox.Show(this, "Для Yandex Cloud укажите API-ключ и Folder ID.", "ScreenLingo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                Uri endpointUri;
                if (SelectedProvider == "LibreTranslate" && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out endpointUri) || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps)))
                {
                    MessageBox.Show(this, "Укажите корректный адрес LibreTranslate, начиная с http:// или https://.", "ScreenLingo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                string[] hotkeys = { CaptureHotkey, CopyHotkey, RepeatHotkey, WatchHotkey };
                uint modifiers;
                Keys key;
                if (hotkeys.Any(delegate(string value) { return !HotkeyUtility.TryParse(value, out modifiers, out key); }) || hotkeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hotkeys.Length)
                {
                    MessageBox.Show(this, "Все горячие клавиши должны быть корректными и не повторяться.", "ScreenLingo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
            }
            base.OnFormClosing(e);
        }
    }

    internal sealed class SelectionForm : Form
    {
        private const int EscapeHotkeyId = 201;
        private const int WmHotkey = 0x0312;
        private readonly Bitmap frozenScreen;
        private Point start;
        private Point current;
        private bool selecting;
        private bool escapeHotkeyRegistered;
        public Rectangle SelectedScreenRectangle { get; private set; }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public SelectionForm()
        {
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            StartPosition = FormStartPosition.Manual;
            Bounds = virtualScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;

            frozenScreen = new Bitmap(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(frozenScreen))
                graphics.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0, virtualScreen.Size, CopyPixelOperation.SourceCopy);
            current = new Point(Cursor.Position.X - virtualScreen.Left, Cursor.Position.Y - virtualScreen.Top);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Focus();
            escapeHotkeyRegistered = RegisterHotKey(Handle, EscapeHotkeyId, 0, (uint)Keys.Escape);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape)
            {
                CancelSelection();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == EscapeHotkeyId)
            {
                CancelSelection();
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (escapeHotkeyRegistered)
            {
                UnregisterHotKey(Handle, EscapeHotkeyId);
                escapeHotkeyRegistered = false;
            }
            base.OnFormClosed(e);
        }

        private void CancelSelection()
        {
            DialogResult = DialogResult.Cancel;
            Close();
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
            current = e.Location;
            Invalidate();
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
            e.Graphics.DrawImageUnscaled(frozenScreen, Point.Empty);
            using (Brush shade = new SolidBrush(Color.FromArgb(105, 0, 0, 0))) e.Graphics.FillRectangle(shade, ClientRectangle);

            if (selecting)
            {
                Rectangle rectangle = Normalized(start, current);
                if (rectangle.Width > 0 && rectangle.Height > 0)
                    e.Graphics.DrawImage(frozenScreen, rectangle, rectangle, GraphicsUnit.Pixel);
                using (Pen border = new Pen(Color.FromArgb(255, 38, 198, 218), 2)) e.Graphics.DrawRectangle(border, rectangle);
                string size = rectangle.Width + " × " + rectangle.Height;
                DrawBadge(e.Graphics, size, new Point(rectangle.Left + 4, Math.Max(4, rectangle.Top - 28)));
            }
            else
            {
                DrawBadge(e.Graphics, "Выделите область · Esc — отмена", new Point(18, 18));
            }

            DrawMagnifier(e.Graphics, current);
        }

        private void DrawMagnifier(Graphics graphics, Point cursorPoint)
        {
            const int sourceSize = 44;
            const int zoomSize = 132;
            int sourceLeft = Math.Max(0, Math.Min(frozenScreen.Width - sourceSize, cursorPoint.X - sourceSize / 2));
            int sourceTop = Math.Max(0, Math.Min(frozenScreen.Height - sourceSize, cursorPoint.Y - sourceSize / 2));
            Rectangle source = new Rectangle(sourceLeft, sourceTop, sourceSize, sourceSize);

            int x = cursorPoint.X + 24;
            int y = cursorPoint.Y + 24;
            if (x + zoomSize + 5 > ClientSize.Width) x = cursorPoint.X - zoomSize - 24;
            if (y + zoomSize + 5 > ClientSize.Height) y = cursorPoint.Y - zoomSize - 24;
            x = Math.Max(5, x);
            y = Math.Max(5, y);
            Rectangle destination = new Rectangle(x, y, zoomSize, zoomSize);

            System.Drawing.Drawing2D.InterpolationMode oldInterpolation = graphics.InterpolationMode;
            System.Drawing.Drawing2D.PixelOffsetMode oldPixelOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(frozenScreen, destination, source, GraphicsUnit.Pixel);
            graphics.InterpolationMode = oldInterpolation;
            graphics.PixelOffsetMode = oldPixelOffset;

            using (Pen frame = new Pen(Color.White, 3)) graphics.DrawRectangle(frame, destination);
            using (Pen cross = new Pen(Color.FromArgb(235, 255, 92, 92), 1))
            {
                int centerX = destination.Left + destination.Width / 2;
                int centerY = destination.Top + destination.Height / 2;
                graphics.DrawLine(cross, centerX - 10, centerY, centerX + 10, centerY);
                graphics.DrawLine(cross, centerX, centerY - 10, centerX, centerY + 10);
            }
        }

        private static void DrawBadge(Graphics graphics, string value, Point location)
        {
            using (Font font = new Font("Segoe UI", 10, FontStyle.Bold))
            {
                Size size = TextRenderer.MeasureText(value, font, new Size(1200, 100), TextFormatFlags.NoPadding);
                Rectangle box = new Rectangle(location.X, location.Y, size.Width + 14, size.Height + 8);
                using (Brush background = new SolidBrush(Color.FromArgb(225, 25, 28, 34))) graphics.FillRectangle(background, box);
                TextRenderer.DrawText(graphics, value, font, new Point(box.Left + 7, box.Top + 4), Color.White, TextFormatFlags.NoPadding);
            }
        }

        private static Rectangle Normalized(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && frozenScreen != null) frozenScreen.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class OcrLineInfo
    {
        public string Text;
        public string Translation;
        public Rectangle Bounds;
        public List<Rectangle> WordBounds = new List<Rectangle>();
        public List<Rectangle> LineBounds = new List<Rectangle>();
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
                    OcrEngine engine = null;
                    if (!String.IsNullOrWhiteSpace(languageTag) && !languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                    if (engine == null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (engine == null) throw new InvalidOperationException("Установите языковой пакет OCR в параметрах языка Windows.");
                    OcrResult result = await engine.RecognizeAsync(softwareBitmap);
                    List<OcrLineInfo> lines = new List<OcrLineInfo>();
                    foreach (OcrLine line in result.Lines)
                    {
                        if (String.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
                        List<Rectangle> wordBounds = line.Words.Select(delegate(OcrWord word)
                        {
                            return Rectangle.FromLTRB(
                                (int)Math.Floor(word.BoundingRect.X),
                                (int)Math.Floor(word.BoundingRect.Y),
                                (int)Math.Ceiling(word.BoundingRect.X + word.BoundingRect.Width),
                                (int)Math.Ceiling(word.BoundingRect.Y + word.BoundingRect.Height));
                        }).ToList();
                        double left = line.Words.Min(delegate(OcrWord word) { return word.BoundingRect.X; });
                        double top = line.Words.Min(delegate(OcrWord word) { return word.BoundingRect.Y; });
                        double right = line.Words.Max(delegate(OcrWord word) { return word.BoundingRect.X + word.BoundingRect.Width; });
                        double bottom = line.Words.Max(delegate(OcrWord word) { return word.BoundingRect.Y + word.BoundingRect.Height; });
                        Rectangle lineBounds = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
                        lines.Add(new OcrLineInfo
                        {
                            Text = line.Text.Trim(),
                            Bounds = lineBounds,
                            WordBounds = wordBounds,
                            LineBounds = new List<Rectangle> { lineBounds }
                        });
                    }
                    softwareBitmap.Dispose();
                    return lines;
                }
            }
        }
    }

    internal static class YandexCloudService
    {
        private const string OcrUrl = "https://ocr.api.cloud.yandex.net/ocr/v1/recognizeText";
        private const string TranslateUrl = "https://translate.api.cloud.yandex.net/translate/v2/translate";
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(25);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenLingo/0.9");
            return client;
        }

        public static async Task<List<OcrLineInfo>> RecognizeAsync(Bitmap bitmap, string languageTag, string apiKey, string folderId)
        {
            ValidateCredentials(apiKey, folderId);
            string imageBase64;
            using (MemoryStream memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                imageBase64 = Convert.ToBase64String(memory.ToArray());
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string languageCode = NormalizeLanguageCode(languageTag);
            string body = serializer.Serialize(new Dictionary<string, object>
            {
                { "mimeType", "PNG" },
                { "languageCodes", new string[] { languageCode } },
                { "model", "page" },
                { "content", imageBase64 }
            });

            using (HttpRequestMessage request = CreateRequest(HttpMethod.Post, OcrUrl, apiKey, folderId, body))
            using (HttpResponseMessage response = await Client.SendAsync(request))
            {
                string json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Yandex Vision OCR вернул ошибку " + (int)response.StatusCode + ": " + ErrorMessage(json));
                return ParseOcrResponse(json);
            }
        }

        public static async Task<List<string>> TranslateAsync(IEnumerable<string> texts, string targetLanguage, string apiKey, string folderId)
        {
            ValidateCredentials(apiKey, folderId);
            string[] sourceTexts = texts.Where(delegate(string text) { return !String.IsNullOrWhiteSpace(text); }).ToArray();
            if (sourceTexts.Length == 0) return new List<string>();

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string body = serializer.Serialize(new Dictionary<string, object>
            {
                { "targetLanguageCode", NormalizeLanguageCode(targetLanguage) },
                { "format", "PLAIN_TEXT" },
                { "texts", sourceTexts },
                { "folderId", folderId.Trim() },
                { "speller", true }
            });

            using (HttpRequestMessage request = CreateRequest(HttpMethod.Post, TranslateUrl, apiKey, folderId, body))
            using (HttpResponseMessage response = await Client.SendAsync(request))
            {
                string json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Yandex Translate вернул ошибку " + (int)response.StatusCode + ": " + ErrorMessage(json));
                return ParseTranslationResponse(json);
            }
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string apiKey, string folderId, string body)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Api-Key " + apiKey.Trim());
            request.Headers.TryAddWithoutValidation("x-folder-id", folderId.Trim());
            request.Headers.TryAddWithoutValidation("x-data-logging-enabled", "false");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }

        private static void ValidateCredentials(string apiKey, string folderId)
        {
            if (String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(folderId))
                throw new InvalidOperationException("Для Yandex Cloud укажите API-ключ и Folder ID в настройках.");
        }

        private static string NormalizeLanguageCode(string languageTag)
        {
            if (String.IsNullOrWhiteSpace(languageTag) || languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "*";
            string value = languageTag.Trim();
            int separator = value.IndexOf('-');
            if (separator > 0) value = value.Substring(0, separator);
            return value.ToLowerInvariant();
        }

        internal static List<OcrLineInfo> ParseOcrResponse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            Dictionary<string, object> result = DictionaryValue(root, "result") ?? root;
            Dictionary<string, object> annotation = DictionaryValue(result, "textAnnotation");
            List<OcrLineInfo> lines = new List<OcrLineInfo>();
            if (annotation == null) return lines;

            foreach (object blockObject in ArrayValue(annotation, "blocks"))
            {
                Dictionary<string, object> block = blockObject as Dictionary<string, object>;
                if (block == null) continue;
                foreach (object lineObject in ArrayValue(block, "lines"))
                {
                    Dictionary<string, object> line = lineObject as Dictionary<string, object>;
                    if (line == null) continue;
                    string text = StringValue(line, "text").Trim();
                    if (String.IsNullOrWhiteSpace(text)) continue;
                    Rectangle lineBounds = ParseBounds(DictionaryValue(line, "boundingBox"));
                    List<Rectangle> wordBounds = new List<Rectangle>();
                    foreach (object wordObject in ArrayValue(line, "words"))
                    {
                        Dictionary<string, object> word = wordObject as Dictionary<string, object>;
                        Rectangle bounds = ParseBounds(DictionaryValue(word, "boundingBox"));
                        if (bounds.Width > 0 && bounds.Height > 0) wordBounds.Add(bounds);
                    }
                    if ((lineBounds.Width <= 0 || lineBounds.Height <= 0) && wordBounds.Count > 0)
                        lineBounds = wordBounds.Aggregate(Rectangle.Union);
                    if (lineBounds.Width <= 0 || lineBounds.Height <= 0) continue;
                    lines.Add(new OcrLineInfo
                    {
                        Text = text,
                        Bounds = lineBounds,
                        WordBounds = wordBounds.Count > 0 ? wordBounds : new List<Rectangle> { lineBounds },
                        LineBounds = new List<Rectangle> { lineBounds }
                    });
                }
            }
            return lines;
        }

        internal static List<string> ParseTranslationResponse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            List<string> translations = new List<string>();
            foreach (object itemObject in ArrayValue(root, "translations"))
            {
                Dictionary<string, object> item = itemObject as Dictionary<string, object>;
                translations.Add(StringValue(item, "text").Trim());
            }
            return translations;
        }

        private static Rectangle ParseBounds(Dictionary<string, object> polygon)
        {
            List<Point> points = new List<Point>();
            foreach (object vertexObject in ArrayValue(polygon, "vertices"))
            {
                Dictionary<string, object> vertex = vertexObject as Dictionary<string, object>;
                int x;
                int y;
                if (vertex != null && Int32.TryParse(StringValue(vertex, "x"), out x) && Int32.TryParse(StringValue(vertex, "y"), out y))
                    points.Add(new Point(x, y));
            }
            if (points.Count == 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(points.Min(delegate(Point point) { return point.X; }), points.Min(delegate(Point point) { return point.Y; }),
                points.Max(delegate(Point point) { return point.X; }), points.Max(delegate(Point point) { return point.Y; }));
        }

        private static Dictionary<string, object> DictionaryValue(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static object[] ArrayValue(Dictionary<string, object> dictionary, string key)
        {
            object value;
            object[] array;
            return dictionary != null && dictionary.TryGetValue(key, out value) && (array = value as object[]) != null ? array : new object[0];
        }

        private static string StringValue(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : String.Empty;
        }

        private static string ErrorMessage(string json)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                string message = StringValue(root, "message");
                if (message.Length == 0) message = StringValue(root, "error");
                if (message.Length > 0) return message;
            }
            catch { }
            return json.Length > 160 ? json.Substring(0, 160) : json;
        }
    }

    internal sealed class TranslationService
    {
        private static readonly HttpClient Client = CreateClient();
        private readonly string targetLanguage;
        private readonly string provider;
        private readonly string apiKey;
        private readonly string endpoint;
        private readonly string yandexApiKey;
        private readonly string yandexFolderId;

        public TranslationService(string targetLanguage)
        {
            this.targetLanguage = targetLanguage;
            provider = "Google";
            apiKey = String.Empty;
            endpoint = String.Empty;
            yandexApiKey = String.Empty;
            yandexFolderId = String.Empty;
        }

        public TranslationService(AppSettings settings)
        {
            targetLanguage = settings.TargetLanguage;
            provider = settings.TranslationProvider;
            apiKey = settings.TranslationApiKey;
            endpoint = settings.TranslationEndpoint;
            yandexApiKey = settings.YandexApiKey;
            yandexFolderId = settings.YandexFolderId;
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
            if (provider.Equals("Yandex Cloud", StringComparison.OrdinalIgnoreCase))
            {
                List<string> translations = await YandexCloudService.TranslateAsync(
                    blocks.Select(delegate(OcrLineInfo block) { return block.Text; }), targetLanguage, yandexApiKey, yandexFolderId);
                for (int index = 0; index < blocks.Count && index < translations.Count; index++)
                    blocks[index].Translation = translations[index];
                return blocks.Where(delegate(OcrLineInfo block) { return !String.IsNullOrWhiteSpace(block.Translation); }).ToList();
            }
            List<Task> tasks = new List<Task>();
            foreach (OcrLineInfo block in blocks) tasks.Add(TranslateLineAsync(block));
            await Task.WhenAll(tasks.ToArray());
            return blocks.Where(delegate(OcrLineInfo block) { return !String.IsNullOrWhiteSpace(block.Translation); }).ToList();
        }

        internal static List<OcrLineInfo> BuildParagraphs(List<OcrLineInfo> lines)
        {
            List<List<OcrLineInfo>> groups = new List<List<OcrLineInfo>>();
            List<Rectangle> groupBounds = new List<Rectangle>();
            List<OcrLineInfo> ordered = lines.OrderBy(delegate(OcrLineInfo line) { return line.Bounds.Top; })
                .ThenBy(delegate(OcrLineInfo line) { return line.Bounds.Left; }).ToList();
            bool scatteredLayout = IsScatteredLayout(ordered);

            foreach (OcrLineInfo line in ordered)
            {
                int bestGroup = -1;
                double bestScore = Double.MaxValue;
                for (int index = 0; index < groups.Count; index++)
                {
                    if (scatteredLayout && groups[index].Count >= 2) continue;
                    OcrLineInfo previous = groups[index].OrderByDescending(delegate(OcrLineInfo item) { return item.Bounds.Bottom; }).First();
                    double score;
                    if (CanJoinParagraph(previous.Bounds, groupBounds[index], line.Bounds, scatteredLayout, out score) && score < bestScore)
                    {
                        bestScore = score;
                        bestGroup = index;
                    }
                }

                if (bestGroup < 0)
                {
                    groups.Add(new List<OcrLineInfo> { line });
                    groupBounds.Add(line.Bounds);
                }
                else
                {
                    groups[bestGroup].Add(line);
                    groupBounds[bestGroup] = Rectangle.Union(groupBounds[bestGroup], line.Bounds);
                }
            }

            List<OcrLineInfo> blocks = new List<OcrLineInfo>();
            for (int index = 0; index < groups.Count; index++)
            {
                List<OcrLineInfo> group = groups[index].OrderBy(delegate(OcrLineInfo line) { return line.Bounds.Top; })
                    .ThenBy(delegate(OcrLineInfo line) { return line.Bounds.Left; }).ToList();
                blocks.Add(CreateParagraph(group, groupBounds[index]));
            }
            return blocks.OrderBy(delegate(OcrLineInfo block) { return block.Bounds.Top; })
                .ThenBy(delegate(OcrLineInfo block) { return block.Bounds.Left; }).ToList();
        }

        private static bool IsScatteredLayout(List<OcrLineInfo> lines)
        {
            int separatedRows = 0;
            for (int first = 0; first < lines.Count; first++)
            {
                for (int second = first + 1; second < lines.Count; second++)
                {
                    Rectangle a = lines[first].Bounds;
                    Rectangle b = lines[second].Bounds;
                    if (b.Top > a.Bottom) break;
                    int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                    int smallerHeight = Math.Max(1, Math.Min(a.Height, b.Height));
                    int horizontalGap = Math.Max(0, Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right));
                    if (overlap > smallerHeight * 0.3 && horizontalGap > Math.Max(18, smallerHeight))
                    {
                        separatedRows++;
                        if (separatedRows >= 2) return true;
                    }
                }
            }
            return false;
        }

        private static bool CanJoinParagraph(Rectangle previous, Rectangle group, Rectangle current, bool scatteredLayout, out double score)
        {
            score = Double.MaxValue;
            int smallerHeight = Math.Max(1, Math.Min(previous.Height, current.Height));
            int largerHeight = Math.Max(previous.Height, current.Height);
            int verticalOverlap = Math.Min(previous.Bottom, current.Bottom) - Math.Max(previous.Top, current.Top);
            if (verticalOverlap > smallerHeight * 0.45) return false;

            int verticalGap = current.Top - previous.Bottom;
            int allowedGap = (int)(largerHeight * (scatteredLayout ? 0.85 : 1.45)) + 4;
            if (verticalGap < -smallerHeight / 2 || verticalGap > allowedGap) return false;

            int horizontalOverlap = Math.Min(previous.Right, current.Right) - Math.Max(previous.Left, current.Left);
            int minimumWidth = Math.Max(1, Math.Min(previous.Width, current.Width));
            int previousCenter = previous.Left + previous.Width / 2;
            int currentCenter = current.Left + current.Width / 2;
            int leftDifference = Math.Abs(previous.Left - current.Left);
            int centerDifference = Math.Abs(previousCenter - currentCenter);
            bool overlaps = horizontalOverlap >= minimumWidth * 0.18;
            bool leftAligned = leftDifference <= Math.Max(22, largerHeight * 2);
            bool centerAligned = centerDifference <= Math.Max(28, Math.Max(previous.Width, current.Width) / 3);
            if (scatteredLayout)
            {
                bool strongOverlap = horizontalOverlap >= minimumWidth * 0.25;
                bool strongCenter = centerDifference <= Math.Max(22, Math.Max(previous.Width, current.Width) / 4);
                if (!strongOverlap && !strongCenter) return false;
            }
            else if (!overlaps && !leftAligned && !centerAligned) return false;

            int groupCenter = group.Left + group.Width / 2;
            int distanceFromGroup = Math.Abs(currentCenter - groupCenter);
            if (group.Width > 0 && distanceFromGroup > Math.Max(group.Width, current.Width) * 0.8 + largerHeight) return false;

            score = Math.Max(0, verticalGap) * 4.0 + Math.Min(leftDifference, centerDifference);
            return true;
        }

        private static OcrLineInfo CreateParagraph(List<OcrLineInfo> lines, Rectangle bounds)
        {
            return new OcrLineInfo
            {
                Text = String.Join(" ", lines.Select(delegate(OcrLineInfo line) { return line.Text.Trim(); }).ToArray()),
                Bounds = bounds,
                WordBounds = lines.SelectMany(delegate(OcrLineInfo line) { return line.WordBounds; }).ToList(),
                LineBounds = lines.SelectMany(delegate(OcrLineInfo line) { return line.LineBounds; }).ToList()
            };
        }

        private async Task TranslateLineAsync(OcrLineInfo line)
        {
            if (provider.Equals("DeepL", StringComparison.OrdinalIgnoreCase))
            {
                line.Translation = await TranslateDeepLAsync(line.Text);
                return;
            }
            if (provider.Equals("LibreTranslate", StringComparison.OrdinalIgnoreCase))
            {
                line.Translation = await TranslateLibreAsync(line.Text);
                return;
            }

            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" +
                         Uri.EscapeDataString(targetLanguage) + "&dt=t&q=" + Uri.EscapeDataString(line.Text);
            string json = await Client.GetStringAsync(url);
            line.Translation = ParseResponse(json);
        }

        private async Task<string> TranslateDeepLAsync(string text)
        {
            if (String.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Для DeepL укажите API-ключ в настройках.");
            string host = apiKey.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
                ? "https://api-free.deepl.com/v2/translate"
                : "https://api.deepl.com/v2/translate";
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string body = serializer.Serialize(new Dictionary<string, object>
            {
                { "text", new string[] { text } },
                { "target_lang", DeepLTargetLanguage(targetLanguage) }
            });
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, host))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey.Trim());
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await Client.SendAsync(request))
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("DeepL вернул ошибку " + (int)response.StatusCode + ". Проверьте API-ключ и тариф.");
                    return ParseDeepLResponse(json);
                }
            }
        }

        private async Task<string> TranslateLibreAsync(string text)
        {
            string baseUrl = String.IsNullOrWhiteSpace(endpoint) ? "https://libretranslate.com" : endpoint.Trim().TrimEnd('/');
            string url = baseUrl.EndsWith("/translate", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/translate";
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "q", text },
                { "source", "auto" },
                { "target", targetLanguage },
                { "format", "text" }
            };
            if (!String.IsNullOrWhiteSpace(apiKey)) payload["api_key"] = apiKey.Trim();
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(serializer.Serialize(payload), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await Client.SendAsync(request))
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("LibreTranslate вернул ошибку " + (int)response.StatusCode + ". Проверьте адрес сервера и API-ключ.");
                    return ParseLibreResponse(json);
                }
            }
        }

        internal static string ParseDeepLResponse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object translationsValue;
            object[] translations;
            if (root == null || !root.TryGetValue("translations", out translationsValue) || (translations = translationsValue as object[]) == null || translations.Length == 0)
                return String.Empty;
            Dictionary<string, object> translation = translations[0] as Dictionary<string, object>;
            object translatedText;
            return translation != null && translation.TryGetValue("text", out translatedText) ? Convert.ToString(translatedText) : String.Empty;
        }

        internal static string ParseLibreResponse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object translatedText;
            return root != null && root.TryGetValue("translatedText", out translatedText) ? Convert.ToString(translatedText) : String.Empty;
        }

        private static string DeepLTargetLanguage(string language)
        {
            if (language.Equals("en", StringComparison.OrdinalIgnoreCase)) return "EN-US";
            if (language.Equals("pt", StringComparison.OrdinalIgnoreCase)) return "PT-PT";
            if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return "ZH-HANS";
            return language.ToUpperInvariant();
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

    internal static class OverlayRenderer
    {
        private sealed class TextStyle
        {
            public Color Foreground;
            public double Complexity;
            public bool Bold;
        }

        public static Bitmap Render(Bitmap source, IEnumerable<OcrLineInfo> lines)
        {
            Bitmap result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                foreach (OcrLineInfo line in lines)
                {
                    if (String.IsNullOrWhiteSpace(line.Translation)) continue;
                    RenderBlock(source, result, graphics, line);
                }
            }
            return result;
        }

        private static void RenderBlock(Bitmap source, Bitmap result, Graphics graphics, OcrLineInfo line)
        {
            Rectangle imageBounds = new Rectangle(Point.Empty, source.Size);
            int maximumLineHeight = line.LineBounds != null && line.LineBounds.Count > 0
                ? line.LineBounds.Max(delegate(Rectangle bounds) { return bounds.Height; })
                : Math.Max(8, line.Bounds.Height);
            Rectangle region = Rectangle.Intersect(imageBounds, Rectangle.Inflate(line.Bounds, Math.Max(7, maximumLineHeight), Math.Max(5, maximumLineHeight / 3)));
            if (region.Width < 2 || region.Height < 2) return;

            graphics.DrawImage(source, region, region, GraphicsUnit.Pixel);

            List<Rectangle> words = line.WordBounds != null && line.WordBounds.Count > 0
                ? line.WordBounds
                : new List<Rectangle> { line.Bounds };
            bool[,] mask;
            TextStyle style = BuildMask(source, region, words, line.LineBounds, out mask);
            RestoreTextPixels(source, result, region, mask);
            DrawTranslation(graphics, source.Size, line, style, region);
        }

        private static TextStyle BuildMask(Bitmap source, Rectangle region, List<Rectangle> words, List<Rectangle> lineBounds, out bool[,] mask)
        {
            mask = new bool[region.Width, region.Height];
            List<Color> foregroundSamples = new List<Color>();
            List<Color> backgroundSamples = new List<Color>();
            int candidateCount = 0;
            int wordArea = 0;
            double complexitySum = 0;

            foreach (Rectangle rawWord in words)
            {
                Rectangle word = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), Rectangle.Inflate(rawWord, 1, 1));
                if (word.Width < 2 || word.Height < 2) continue;
                List<Color> border = SampleBorder(source, word, 3);
                Color background = MedianColor(border);
                backgroundSamples.AddRange(border);
                double backgroundLuminance = Luminance(background);
                double variation = border.Count == 0 ? 0 : border.Average(delegate(Color color) { return Math.Abs(Luminance(color) - backgroundLuminance); });
                complexitySum += variation;
                double threshold = Math.Max(24, Math.Min(76, 24 + variation * 1.6));
                wordArea += word.Width * word.Height;

                for (int y = word.Top; y < word.Bottom; y++)
                {
                    for (int x = word.Left; x < word.Right; x++)
                    {
                        Color pixel = source.GetPixel(x, y);
                        double luminanceDifference = Math.Abs(Luminance(pixel) - backgroundLuminance);
                        double colorDifference = ColorDistance(pixel, background);
                        if (luminanceDifference >= threshold || colorDifference >= threshold * 1.55)
                        {
                            int localX = x - region.Left;
                            int localY = y - region.Top;
                            if (localX >= 0 && localY >= 0 && localX < region.Width && localY < region.Height)
                            {
                                mask[localX, localY] = true;
                                foregroundSamples.Add(pixel);
                                candidateCount++;
                            }
                        }
                    }
                }
            }

            double averageComplexity = words.Count == 0 ? 0 : complexitySum / words.Count;
            if (averageComplexity < 24 && lineBounds != null)
                ExpandSmoothLineMask(source, region, lineBounds, mask, foregroundSamples, backgroundSamples);
            Dilate(mask, region.Width, region.Height, averageComplexity < 24 ? 2 : 1);
            Color averageBackground = MedianColor(backgroundSamples);
            Color foreground = foregroundSamples.Count > 0
                ? MedianColor(foregroundSamples)
                : (Luminance(averageBackground) > 140 ? Color.FromArgb(28, 30, 34) : Color.White);
            double contrast = Math.Abs(Luminance(foreground) - Luminance(averageBackground));
            if (contrast < 70) foreground = Luminance(averageBackground) > 140 ? Color.FromArgb(25, 27, 31) : Color.White;

            return new TextStyle
            {
                Foreground = foreground,
                Complexity = averageComplexity,
                Bold = wordArea > 0 && (double)candidateCount / wordArea > 0.19
            };
        }

        private static List<Color> SampleBorder(Bitmap source, Rectangle rectangle, int thickness)
        {
            Rectangle outer = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), Rectangle.Inflate(rectangle, thickness, thickness));
            List<Color> colors = new List<Color>();
            int step = Math.Max(1, Math.Min(outer.Width, outer.Height) / 24);
            for (int x = outer.Left; x < outer.Right; x += step)
            {
                for (int offset = 0; offset < thickness; offset++)
                {
                    int top = Math.Min(outer.Bottom - 1, outer.Top + offset);
                    int bottom = Math.Max(outer.Top, outer.Bottom - 1 - offset);
                    colors.Add(source.GetPixel(x, top));
                    colors.Add(source.GetPixel(x, bottom));
                }
            }
            for (int y = outer.Top; y < outer.Bottom; y += step)
            {
                for (int offset = 0; offset < thickness; offset++)
                {
                    int left = Math.Min(outer.Right - 1, outer.Left + offset);
                    int right = Math.Max(outer.Left, outer.Right - 1 - offset);
                    colors.Add(source.GetPixel(left, y));
                    colors.Add(source.GetPixel(right, y));
                }
            }
            return colors;
        }

        private static void ExpandSmoothLineMask(Bitmap source, Rectangle region, List<Rectangle> lineBounds,
            bool[,] mask, List<Color> foregroundSamples, List<Color> backgroundSamples)
        {
            Rectangle imageBounds = new Rectangle(Point.Empty, source.Size);
            foreach (Rectangle rawLine in lineBounds)
            {
                int horizontalPadding = Math.Max(6, rawLine.Height);
                int verticalPadding = Math.Max(3, rawLine.Height / 4);
                Rectangle scan = Rectangle.Intersect(imageBounds, Rectangle.Inflate(rawLine, horizontalPadding, verticalPadding));
                scan = Rectangle.Intersect(scan, region);
                if (scan.Width < 2 || scan.Height < 2) continue;
                List<Color> border = SampleBorder(source, scan, 3);
                Color background = MedianColor(border);
                double backgroundLuminance = Luminance(background);
                double variation = border.Count == 0 ? 0 : border.Average(delegate(Color color) { return Math.Abs(Luminance(color) - backgroundLuminance); });
                if (variation > 28) continue;
                backgroundSamples.AddRange(border);
                double threshold = Math.Max(19, Math.Min(62, 20 + variation * 1.8));

                for (int y = scan.Top; y < scan.Bottom; y++)
                    for (int x = scan.Left; x < scan.Right; x++)
                    {
                        Color pixel = source.GetPixel(x, y);
                        double luminanceDifference = Math.Abs(Luminance(pixel) - backgroundLuminance);
                        double colorDifference = ColorDistance(pixel, background);
                        if (luminanceDifference >= threshold || colorDifference >= threshold * 1.45)
                        {
                            int localX = x - region.Left;
                            int localY = y - region.Top;
                            if (localX >= 0 && localY >= 0 && localX < region.Width && localY < region.Height)
                            {
                                mask[localX, localY] = true;
                                foregroundSamples.Add(pixel);
                            }
                        }
                    }
            }
        }

        private static void Dilate(bool[,] mask, int width, int height, int radius)
        {
            bool[,] original = (bool[,])mask.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!original[x, y]) continue;
                    for (int offsetY = -radius; offsetY <= radius; offsetY++)
                        for (int offsetX = -radius; offsetX <= radius; offsetX++)
                        {
                            int targetX = x + offsetX;
                            int targetY = y + offsetY;
                            if (targetX >= 0 && targetY >= 0 && targetX < width && targetY < height) mask[targetX, targetY] = true;
                        }
                }
            }
        }

        private static void RestoreTextPixels(Bitmap source, Bitmap result, Rectangle region, bool[,] mask)
        {
            for (int y = 0; y < region.Height; y++)
                for (int x = 0; x < region.Width; x++)
                    if (mask[x, y]) result.SetPixel(region.Left + x, region.Top + y, ReplacementColor(source, region, mask, x, y));
        }

        private static Color ReplacementColor(Bitmap source, Rectangle region, bool[,] mask, int localX, int localY)
        {
            int[,] directions = { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 }, { -1, -1 }, { 1, -1 }, { -1, 1 }, { 1, 1 } };
            double red = 0, green = 0, blue = 0, weightSum = 0;
            for (int direction = 0; direction < directions.GetLength(0); direction++)
            {
                for (int distance = 1; distance <= 18; distance++)
                {
                    int x = localX + directions[direction, 0] * distance;
                    int y = localY + directions[direction, 1] * distance;
                    if (x < 0 || y < 0 || x >= region.Width || y >= region.Height) break;
                    if (mask[x, y]) continue;
                    Color sample = source.GetPixel(region.Left + x, region.Top + y);
                    double weight = 1.0 / distance;
                    red += sample.R * weight;
                    green += sample.G * weight;
                    blue += sample.B * weight;
                    weightSum += weight;
                    break;
                }
            }
            if (weightSum <= 0) return source.GetPixel(region.Left + localX, region.Top + localY);
            return Color.FromArgb(255, ClampColor(red / weightSum), ClampColor(green / weightSum), ClampColor(blue / weightSum));
        }

        private static void DrawTranslation(Graphics graphics, Size imageSize, OcrLineInfo line, TextStyle style, Rectangle opaqueRegion)
        {
            Rectangle imageBounds = new Rectangle(Point.Empty, imageSize);
            int sourceLines = CountSourceLines(line.WordBounds, line.Bounds.Height);
            int sourceLineHeight = Math.Max(12, line.Bounds.Height / Math.Max(1, sourceLines));
            Rectangle textArea = Rectangle.Intersect(imageBounds,
                new Rectangle(Math.Max(0, line.Bounds.Left - 2), Math.Max(0, line.Bounds.Top - 2),
                    Math.Min(imageSize.Width - Math.Max(0, line.Bounds.Left - 2), line.Bounds.Width + 8),
                    Math.Min(imageSize.Height - Math.Max(0, line.Bounds.Top - 2), Math.Max(line.Bounds.Height + 6, (int)(sourceLineHeight * sourceLines * 1.15)))));
            Rectangle safeRegion = Rectangle.Inflate(opaqueRegion, -2, -2);
            textArea = Rectangle.Intersect(textArea, safeRegion);
            if (textArea.Width < 4 || textArea.Height < 4) return;

            string fontName = sourceLineHeight >= 27 ? "Georgia" : "Segoe UI";
            FontStyle fontStyle = style.Bold ? FontStyle.Bold : FontStyle.Regular;
            System.Drawing.Drawing2D.GraphicsState graphicsState = graphics.Save();
            graphics.SetClip(safeRegion);
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Near;
                format.Trimming = StringTrimming.EllipsisWord;
                format.FormatFlags = StringFormatFlags.LineLimit;
                float fontSize = FitFont(graphics, line.Translation, textArea.Size, fontName, fontStyle, Math.Min(64, sourceLineHeight * 1.05f), format);
                using (Font font = new Font(fontName, fontSize, fontStyle, GraphicsUnit.Pixel))
                {
                    if (style.Complexity > 18)
                    {
                        Color outlineColor = Luminance(style.Foreground) > 140 ? Color.FromArgb(155, 0, 0, 0) : Color.FromArgb(155, 255, 255, 255);
                        using (Brush outline = new SolidBrush(outlineColor))
                        {
                            graphics.DrawString(line.Translation, font, outline, new RectangleF(textArea.X - 1, textArea.Y, textArea.Width, textArea.Height), format);
                            graphics.DrawString(line.Translation, font, outline, new RectangleF(textArea.X + 1, textArea.Y, textArea.Width, textArea.Height), format);
                            graphics.DrawString(line.Translation, font, outline, new RectangleF(textArea.X, textArea.Y - 1, textArea.Width, textArea.Height), format);
                            graphics.DrawString(line.Translation, font, outline, new RectangleF(textArea.X, textArea.Y + 1, textArea.Width, textArea.Height), format);
                        }
                    }
                    using (Brush foreground = new SolidBrush(style.Foreground))
                        graphics.DrawString(line.Translation, font, foreground, textArea, format);
                }
            }
            graphics.Restore(graphicsState);
        }

        private static float FitFont(Graphics graphics, string text, Size area, string fontName, FontStyle style, float maximum, StringFormat format)
        {
            float low = 7f;
            float high = Math.Max(low, maximum);
            for (int iteration = 0; iteration < 9; iteration++)
            {
                float middle = (low + high) / 2f;
                using (Font font = new Font(fontName, middle, style, GraphicsUnit.Pixel))
                {
                    SizeF measured = graphics.MeasureString(text, font, Math.Max(4, area.Width), format);
                    if (measured.Width <= area.Width + 1 && measured.Height <= area.Height + 1) low = middle;
                    else high = middle;
                }
            }
            return Math.Max(7f, low);
        }

        private static int CountSourceLines(List<Rectangle> words, int fallbackHeight)
        {
            if (words == null || words.Count == 0) return 1;
            List<int> centers = words.Select(delegate(Rectangle word) { return word.Top + word.Height / 2; }).OrderBy(delegate(int value) { return value; }).ToList();
            int tolerance = Math.Max(4, words.Select(delegate(Rectangle word) { return word.Height; }).OrderBy(delegate(int value) { return value; }).ElementAt(words.Count / 2) / 2);
            int lines = 0;
            int lastCenter = Int32.MinValue;
            foreach (int center in centers)
            {
                if (lastCenter == Int32.MinValue || center - lastCenter > tolerance)
                {
                    lines++;
                    lastCenter = center;
                }
            }
            return Math.Max(1, lines);
        }

        private static Color MedianColor(List<Color> colors)
        {
            if (colors == null || colors.Count == 0) return Color.FromArgb(245, 245, 245);
            int[] reds = colors.Select(delegate(Color color) { return (int)color.R; }).OrderBy(delegate(int value) { return value; }).ToArray();
            int[] greens = colors.Select(delegate(Color color) { return (int)color.G; }).OrderBy(delegate(int value) { return value; }).ToArray();
            int[] blues = colors.Select(delegate(Color color) { return (int)color.B; }).OrderBy(delegate(int value) { return value; }).ToArray();
            int middle = colors.Count / 2;
            return Color.FromArgb(reds[middle], greens[middle], blues[middle]);
        }

        private static double Luminance(Color color)
        {
            return color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
        }

        private static double ColorDistance(Color first, Color second)
        {
            int red = first.R - second.R;
            int green = first.G - second.G;
            int blue = first.B - second.B;
            return Math.Sqrt(red * red + green * green + blue * blue);
        }

        private static int ClampColor(double value)
        {
            return Math.Max(0, Math.Min(255, (int)Math.Round(value)));
        }
    }

    internal sealed class OverlayForm : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private readonly Timer closeTimer;
        private readonly Bitmap renderedOverlay;

        public OverlayForm(Rectangle screenArea, IEnumerable<OcrLineInfo> lines, Bitmap capturedImage, int seconds)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = screenArea;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;

            renderedOverlay = OverlayRenderer.Render(capturedImage, lines);

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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (renderedOverlay != null) e.Graphics.DrawImageUnscaled(renderedOverlay, Point.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (closeTimer != null) closeTimer.Dispose();
                if (renderedOverlay != null) renderedOverlay.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
