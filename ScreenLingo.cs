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
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

[assembly: System.Reflection.AssemblyTitle("ScreenLingo")]
[assembly: System.Reflection.AssemblyDescription("Screen area OCR translator for Windows")]
[assembly: System.Reflection.AssemblyProduct("ScreenLingo")]
[assembly: System.Reflection.AssemblyVersion("0.8.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.8.3.0")]

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
                    lines[0].Translation = "–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏";
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
                    string parsed = TranslationService.ParseResponse("[[[\"–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏\",\"Open settings\",null,null,10]],null,\"en\"]");
                    log.AppendLine("TRANSLATION_PARSER=" + parsed);
                    if (parsed != "–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏") throw new InvalidOperationException("Self-test translation parser failed.");
                    string deepLParsed = TranslationService.ParseDeepLResponse("{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏\"}]}");
                    string libreParsed = TranslationService.ParseLibreResponse("{\"translatedText\":\"–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏\"}");
                    log.AppendLine("DEEPL_PARSER=" + deepLParsed);
                    log.AppendLine("LIBRE_PARSER=" + libreParsed);
                    if (deepLParsed != "–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏" || libreParsed != "–û—Ç–∫—Ä—ã—Ç—å –Ω–∞—Å—Ç—Ä–æ–π–∫–∏") throw new InvalidOperationException("Self-test provider parser failed.");
                    uint hotkeyModifiers;
                    Keys hotkeyKey;
                    bool hotkeyParsed = HotkeyUtility.TryParse("Ctrl+Alt+F8", out hotkeyModifiers, out hotkeyKey);
                    log.AppendLine("HOTKEY_PARSER=" + hotkeyParsed + "; KEY=" + hotkeyKey);
                    if (!hotkeyParsed || hotkeyKey != Keys.F8) throw new InvalidOperationException("Self-test hotkey parser failed.");
                    bool protectedSettings = AppSettings.TestProtectionRoundTrip("screenlingo-test-key");
                    log.AppendLine("SETTINGS_DPAPI=" + protectedSettings);
                    if (!protectedSettings) throw new InvalidOperationException("Self-test settings encryption failed.");
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

    internal sealed class AppSettings
    {
        public string SourceLanguage = "en-US";
        public string TargetLanguage = "ru";
        public string TranslationProvider = "Google";
        public string TranslationApiKey = String.Empty;
        public string TranslationEndpoint = "https://libretranslate.com";
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
                "# –Ø–∑—ã–∫ OCR –¥–æ–ª–∂–µ–Ω –±—ã—Ç—å —É—Å—Ç–∞–Ω–æ–≤–ª–µ–Ω –≤ –ø–∞—Ä–∞–º–µ—Ç—Ä–∞—Ö —è–∑—ã–∫–∞ Windows." + Environment.NewLine +
                "SourceLanguage=" + SourceLanguage + Environment.NewLine +
                "TargetLanguage=" + TargetLanguage + Environment.NewLine + Environment.NewLine +
                "# –°–µ—Ä–≤–∏—Å –ø–µ—Ä–µ–≤–æ–¥–∞: Google, DeepL –∏–ª–∏ LibreTranslate." + Environment.NewLine +
                "TranslationProvider=" + TranslationProvider + Environment.NewLine +
                "TranslationEndpoint=" + TranslationEndpoint + Environment.NewLine +
                "TranslationApiKeyProtected=" + Protect(TranslationApiKey) + Environment.NewLine + Environment.NewLine +
                "# –ß–µ—Ä–µ–∑ —Å–∫–æ–ª—å–∫–æ —Å–µ–∫—É–Ω–¥ —Å–∫—Ä—ã–≤–∞—Ç—å –ø–µ—Ä–µ–≤–æ–¥." + Environment.NewLine +
                "OverlaySeconds=" + OverlaySeconds + Environment.NewLine + Environment.NewLine +
                "# –ò–Ω—Ç–µ—Ä–≤–∞–ª –ø—Ä–æ–≤–µ—Ä–∫–∏ –æ–±–ª–∞—Å—Ç–∏ –≤ —Ä–µ–∂–∏–º–µ –Ω–∞–±–ª—é–¥–µ–Ω–∏—è, –≤ –º–∏–ª–ª–∏—Å–µ–∫—É–Ω–¥–∞—Ö." + Environment.NewLine +
                "WatchIntervalMs=" + WatchIntervalMs + Environment.NewLine + Environment.NewLine +
                "# –ì–ª–æ–±–∞–ª—å–Ω—ã–µ –≥–æ—Ä—è—á–∏–µ –∫–ª–∞–≤–∏—à–∏." + Environment.NewLine +
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
            if (String.IsNullOrW€nº⁄⁄$z{-ÆÈ‹j◊ù"Üf˜&Vw&˜VÊE6◊∆W2ê¢¢Ñ«V÷ñÊÊ6RÜfW&vT&6∂w&˜VÊBí‚CÚ6ˆ∆˜"‰g&ˆ‘&v"É#Ç¬3¬3Bí¢6ˆ∆˜"ÂvÜóFRì∞¢F˜V&∆R6ˆÁG&7B“÷FÇ‰'2Ñ«V÷ñÊÊ6RÜf˜&Vw&˜VÊBí“«V÷ñÊÊ6RÜfW&vT&6∂w&˜VÊBíì∞¢ñbÜ6ˆÁG&7B¬síf˜&Vw&˜VÊB“«V÷ñÊÊ6RÜfW&vT&6∂w&˜VÊBí‚CÚ6ˆ∆˜"‰g&ˆ‘&v"É#R¬#r¬3í¢6ˆ∆˜"ÂvÜóFS∞†¢&WGW&‚ÊWrFWáE7Gñ∆P¢∞¢f˜&Vw&˜VÊB“f˜&Vw&˜VÊB¿¢6ˆ◊∆WÜóGí“fW&vT6ˆ◊∆WÜóGí¿¢&ˆ∆B“v˜&D&V‚bbÜF˜V&∆Rñ6ÊFñFFT6˜VÁBÚv˜&D&V‚„ê¢”∞¢–†¢&ófFR7FFñ2∆ó7Cƒ6ˆ∆˜#‚6◊∆T&˜&FW"Ñ&óF÷6˜W&6R¬&V7FÊv∆R&V7FÊv∆R¬ñÁBFÜñ6∂ÊW72ê¢∞¢&V7FÊv∆R˜WFW"“&V7FÊv∆R‰ñÁFW'6V7BÜÊWr&V7FÊv∆RÖˆñÁB‰V◊Gí¬6˜W&6RÂ6ó¶Rí¬&V7FÊv∆R‰ñÊf∆FRá&V7FÊv∆R¬FÜñ6∂ÊW72¬FÜñ6∂ÊW72íì∞¢∆ó7Cƒ6ˆ∆˜#‚6ˆ∆˜'2“ÊWr∆ó7Cƒ6ˆ∆˜#‚Çì∞¢ñÁB7FW“÷FÇ‰÷ÇÉ¬÷FÇ‰÷ñ‚Ü˜WFW"ÂvñGFÇ¬˜WFW"‰ÜVñváBíÚ#Bì∞¢f˜"ÜñÁBÇ“˜WFW"‰∆VgC≤Ç¬˜WFW"Â&ñváC≤Ç≥“7FWê¢∞¢f˜"ÜñÁBˆfg6WB“≤ˆfg6WB¬FÜñ6∂ÊW73≤ˆfg6WB≤≤ê¢∞¢ñÁBF˜“÷FÇ‰÷ñ‚Ü˜WFW"‰&˜GFˆ““¬˜WFW"ÂF˜≤ˆfg6WBì∞¢ñÁB&˜GFˆ““÷FÇ‰÷ÇÜ˜WFW"ÂF˜¬˜WFW"‰&˜GFˆ“““ˆfg6WBì∞¢6ˆ∆˜'2‰FBá6˜W&6R‰vWEóÜV¬áÇ¬F˜íì∞¢6ˆ∆˜'2‰FBá6˜W&6R‰vWEóÜV¬áÇ¬&˜GFˆ“íì∞¢–¢–¢f˜"ÜñÁBí“˜WFW"ÂF˜≤í¬˜WFW"‰&˜GFˆ”≤í≥“7FWê¢∞¢f˜"ÜñÁBˆfg6WB“≤ˆfg6WB¬FÜñ6∂ÊW73≤ˆfg6WB≤≤ê¢∞¢ñÁB∆VgB“÷FÇ‰÷ñ‚Ü˜WFW"Â&ñváB“¬˜WFW"‰∆VgB≤ˆfg6WBì∞¢ñÁB&ñváB“÷FÇ‰÷ÇÜ˜WFW"‰∆VgB¬˜WFW"Â&ñváB““ˆfg6WBì∞¢6ˆ∆˜'2‰FBá6˜W&6R‰vWEóÜV¬Ü∆VgB¬ííì∞¢6ˆ∆˜'2‰FBá6˜W&6R‰vWEóÜV¬á&ñváB¬ííì∞¢–¢–¢&WGW&‚6ˆ∆˜'3∞¢–†¢&ófFR7FFñ2fˆñBWáÊE6÷ˆ˜FÑ∆ñÊT÷6≤Ñ&óF÷6˜W&6R¬&V7FÊv∆R&Vvñˆ‚¬∆ó7C≈&V7FÊv∆S‚∆ñÊT&˜VÊG2¿¢&ˆˆ≈≤≈“÷6≤¬∆ó7Cƒ6ˆ∆˜#‚f˜&Vw&˜VÊE6◊∆W2¬∆ó7Cƒ6ˆ∆˜#‚&6∂w&˜VÊE6◊∆W2ê¢∞¢&V7FÊv∆Rñ÷vT&˜VÊG2“ÊWr&V7FÊv∆RÖˆñÁB‰V◊Gí¬6˜W&6RÂ6ó¶Rì∞¢f˜&V6ÇÖ&V7FÊv∆R&t∆ñÊRñ‚∆ñÊT&˜VÊG2ê¢∞¢ñÁBÜ˜&ó¶ˆÁF≈FFñÊr“÷FÇ‰÷ÇÉb¬&t∆ñÊR‰ÜVñváBì∞¢ñÁBfW'Fñ6≈FFñÊr“÷FÇ‰÷ÇÉ2¬&t∆ñÊR‰ÜVñváBÚBì∞¢&V7FÊv∆R66‚“&V7FÊv∆R‰ñÁFW'6V7BÜñ÷vT&˜VÊG2¬&V7FÊv∆R‰ñÊf∆FRá&t∆ñÊR¬Ü˜&ó¶ˆÁF≈FFñÊr¬fW'Fñ6≈FFñÊríì∞¢66‚“&V7FÊv∆R‰ñÁFW'6V7Bá66‚¬&Vvñˆ‚ì∞¢ñbá66‚ÂvñGFÇ¬"«¬66‚‰ÜVñváB¬"í6ˆÁFñÁVS∞¢∆ó7Cƒ6ˆ∆˜#‚&˜&FW"“6◊∆T&˜&FW"á6˜W&6R¬66‚¬2ì∞¢6ˆ∆˜"&6∂w&˜VÊB“÷VFñ‰6ˆ∆˜"Ü&˜&FW"ì∞¢F˜V&∆R&6∂w&˜VÊD«V÷ñÊÊ6R“«V÷ñÊÊ6RÜ&6∂w&˜VÊBì∞¢F˜V&∆Rf&ñFñˆ‚“&˜&FW"‰6˜VÁB”“Ú¢&˜&FW"‰fW&vRÜFV∆VvFRÑ6ˆ∆˜"6ˆ∆˜"í≤&WGW&‚÷FÇ‰'2Ñ«V÷ñÊÊ6RÜ6ˆ∆˜"í“&6∂w&˜VÊD«V÷ñÊÊ6Rì≤“ì∞¢ñbáf&ñFñˆ‚‚#Çí6ˆÁFñÁVS∞¢&6∂w&˜VÊE6◊∆W2‰FE&ÊvRÜ&˜&FW"ì∞¢F˜V&∆RFá&W6Üˆ∆B“÷FÇ‰÷ÇÉí¬÷FÇ‰÷ñ‚Éc"¬#≤f&ñFñˆ‚¢„Çíì∞†¢f˜"ÜñÁBí“66‚ÂF˜≤í¬66‚‰&˜GFˆ”≤í≤≤ê¢f˜"ÜñÁBÇ“66‚‰∆VgC≤Ç¬66‚Â&ñváC≤Ç≤≤ê¢∞¢6ˆ∆˜"óÜV¬“6˜W&6R‰vWEóÜV¬áÇ¬íì∞¢F˜V&∆R«V÷ñÊÊ6TFñffW&VÊ6R“÷FÇ‰'2Ñ«V÷ñÊÊ6RáóÜV¬í“&6∂w&˜VÊD«V÷ñÊÊ6Rì∞¢F˜V&∆R6ˆ∆˜$FñffW&VÊ6R“6ˆ∆˜$Fó7FÊ6RáóÜV¬¬&6∂w&˜VÊBì∞¢ñbÜ«V÷ñÊÊ6TFñffW&VÊ6R„“Fá&W6Üˆ∆B«¬6ˆ∆˜$FñffW&VÊ6R„“Fá&W6Üˆ∆B¢„CRê¢∞¢ñÁB∆ˆ6≈Ç“Ç“&Vvñˆ‚‰∆VgC∞¢ñÁB∆ˆ6≈í“í“&Vvñˆ‚ÂF˜∞¢ñbÜ∆ˆ6≈Ç„“bb∆ˆ6≈í„“bb∆ˆ6≈Ç¬&Vvñˆ‚ÂvñGFÇbb∆ˆ6≈í¬&Vvñˆ‚‰ÜVñváBê¢∞¢÷6µ∂∆ˆ6≈Ç¬∆ˆ6≈ï““G'VS∞¢f˜&Vw&˜VÊE6◊∆W2‰FBáóÜV¬ì∞¢–¢–¢–¢–¢–†¢&ófFR7FFñ2fˆñBFñ∆FRÜ&ˆˆ≈≤≈“÷6≤¬ñÁBvñGFÇ¬ñÁBÜVñváB¬ñÁB&FóW2ê¢∞¢&ˆˆ≈≤≈“˜&ñvñÊ¬“Ü&ˆˆ≈≤≈“ñ÷6≤‰6∆ˆÊRÇì∞¢f˜"ÜñÁBí“≤í¬ÜVñváC≤í≤≤ê¢∞¢f˜"ÜñÁBÇ“≤Ç¬vñGFÉ≤Ç≤≤ê¢∞¢ñbÇ˜&ñvñÊ≈∑Ç¬ï“í6ˆÁFñÁVS∞¢f˜"ÜñÁBˆfg6WEí“◊&FóW3≤ˆfg6WEí√“&FóW3≤ˆfg6WEí≤≤ê¢f˜"ÜñÁBˆfg6WEÇ“◊&FóW3≤ˆfg6WEÇ√“&FóW3≤ˆfg6WEÇ≤≤ê¢∞¢ñÁBF&vWEÇ“Ç≤ˆfg6WEÉ∞¢ñÁBF&vWEí“í≤ˆfg6WEì∞¢ñbáF&vWEÇ„“bbF&vWEí„“bbF&vWEÇ¬vñGFÇbbF&vWEí¬ÜVñváBí÷6µ∑F&vWEÇ¬F&vWEï““G'VS∞¢–¢–¢–¢–†¢&ófFR7FFñ2fˆñB&W7F˜&UFWáEóÜV«2Ñ&óF÷6˜W&6R¬&óF÷&W7V«B¬&V7FÊv∆R&Vvñˆ‚¬&ˆˆ≈≤≈“÷6≤ê¢∞¢f˜"ÜñÁBí“≤í¬&Vvñˆ‚‰ÜVñváC≤í≤≤ê¢f˜"ÜñÁBÇ“≤Ç¬&Vvñˆ‚ÂvñGFÉ≤Ç≤≤ê¢ñbÜ÷6µ∑Ç¬ï“í&W7V«BÂ6WEóÜV¬á&Vvñˆ‚‰∆VgB≤Ç¬&Vvñˆ‚ÂF˜≤í¬&W∆6V÷VÁD6ˆ∆˜"á6˜W&6R¬&Vvñˆ‚¬÷6≤¬Ç¬ííì∞¢–†¢&ófFR7FFñ26ˆ∆˜"&W∆6V÷VÁD6ˆ∆˜"Ñ&óF÷6˜W&6R¬&V7FÊv∆R&Vvñˆ‚¬&ˆˆ≈≤≈“÷6≤¬ñÁB∆ˆ6≈Ç¬ñÁB∆ˆ6≈íê¢∞¢ñÁE≤≈“Fó&V7FñˆÁ2“≤≤”¬“¬≤¬“¬≤¬”“¬≤¬“¬≤”¬”“¬≤¬”“¬≤”¬“¬≤¬“”∞¢F˜V&∆R&VB“¬w&VV‚“¬&«VR“¬vVñváE7V““∞¢f˜"ÜñÁBFó&V7Fñˆ‚“≤Fó&V7Fñˆ‚¬Fó&V7FñˆÁ2‰vWD∆VÊwFÇÉì≤Fó&V7Fñˆ‚≤≤ê¢∞¢f˜"ÜñÁBFó7FÊ6R“≤Fó7FÊ6R√“É≤Fó7FÊ6R≤≤ê¢∞¢ñÁBÇ“∆ˆ6≈Ç≤Fó&V7FñˆÁ5∂Fó&V7Fñˆ‚¬“¢Fó7FÊ6S∞¢ñÁBí“∆ˆ6≈í≤Fó&V7FñˆÁ5∂Fó&V7Fñˆ‚¬“¢Fó7FÊ6S∞¢ñbáÇ¬«¬í¬«¬Ç„“&Vvñˆ‚ÂvñGFÇ«¬í„“&Vvñˆ‚‰ÜVñváBí'&V≥∞¢ñbÜ÷6µ∑Ç¬ï“í6ˆÁFñÁVS∞¢6ˆ∆˜"6◊∆R“6˜W&6R‰vWEóÜV¬á&Vvñˆ‚‰∆VgB≤Ç¬&Vvñˆ‚ÂF˜≤íì∞¢F˜V&∆RvVñváB“„ÚFó7FÊ6S∞¢&VB≥“6◊∆RÂ"¢vVñváC∞¢w&VV‚≥“6◊∆R‰r¢vVñváC∞¢&«VR≥“6◊∆R‰"¢vVñváC∞¢vVñváE7V“≥“vVñváC∞¢'&V≥∞¢–¢–¢ñbávVñváE7V“√“í&WGW&‚6˜W&6R‰vWEóÜV¬á&Vvñˆ‚‰∆VgB≤∆ˆ6≈Ç¬&Vvñˆ‚ÂF˜≤∆ˆ6≈íì∞¢&WGW&‚6ˆ∆˜"‰g&ˆ‘&v"É#SR¬6∆◊6ˆ∆˜"á&VBÚvVñváE7V“í¬6∆◊6ˆ∆˜"Üw&VV‚ÚvVñváE7V“í¬6∆◊6ˆ∆˜"Ü&«VRÚvVñváE7V“íì∞¢–†¢&ófFR7FFñ2fˆñBG&uG&Á6∆Fñˆ‚Ñw&Üñ72w&Üñ72¬6ó¶Rñ÷vU6ó¶R¬ˆ7$∆ñÊTñÊfÚ∆ñÊR¬FWáE7Gñ∆R7Gñ∆R¬&V7FÊv∆R˜VU&Vvñˆ‚ê¢∞¢&V7FÊv∆Rñ÷vT&˜VÊG2“ÊWr&V7FÊv∆RÖˆñÁB‰V◊Gí¬ñ÷vU6ó¶Rì∞¢ñÁB6˜W&6T∆ñÊW2“6˜VÁE6˜W&6T∆ñÊW2Ü∆ñÊRÂv˜&D&˜VÊG2¬∆ñÊR‰&˜VÊG2‰ÜVñváBì∞¢ñÁB6˜W&6T∆ñÊTÜVñváB“÷FÇ‰÷ÇÉ"¬∆ñÊR‰&˜VÊG2‰ÜVñváBÚ÷FÇ‰÷ÇÉ¬6˜W&6T∆ñÊW2íì∞¢&V7FÊv∆RFWáD&V“&V7FÊv∆R‰ñÁFW'6V7BÜñ÷vT&˜VÊG2¿¢ÊWr&V7FÊv∆RÑ÷FÇ‰÷ÇÉ¬∆ñÊR‰&˜VÊG2‰∆VgB“"í¬÷FÇ‰÷ÇÉ¬∆ñÊR‰&˜VÊG2ÂF˜“"í¿¢÷FÇ‰÷ñ‚Üñ÷vU6ó¶RÂvñGFÇ“÷FÇ‰÷ÇÉ¬∆ñÊR‰&˜VÊG2‰∆VgB“"í¬∆ñÊR‰&˜VÊG2ÂvñGFÇ≤Çí¿¢÷FÇ‰÷ñ‚Üñ÷vU6ó¶R‰ÜVñváB“÷FÇ‰÷ÇÉ¬∆ñÊR‰&˜VÊG2ÂF˜“"í¬÷FÇ‰÷ÇÜ∆ñÊR‰&˜VÊG2‰ÜVñváB≤b¬ÜñÁBíá6˜W&6T∆ñÊTÜVñváB¢6˜W&6T∆ñÊW2¢„Rííííì∞¢&V7FÊv∆R6fU&Vvñˆ‚“&V7FÊv∆R‰ñÊf∆FRÜ˜VU&Vvñˆ‚¬”"¬”"ì∞¢FWáD&V“&V7FÊv∆R‰ñÁFW'6V7BáFWáD&V¬6fU&Vvñˆ‚ì∞¢ñbáFWáD&VÂvñGFÇ¬B«¬FWáD&V‰ÜVñváB¬Bí&WGW&„∞†¢7G&ñÊrfˆÁDÊ÷R“6˜W&6T∆ñÊTÜVñváB„“#rÚ$vV˜&vñ"¢%6VvˆRTí#∞¢fˆÁE7Gñ∆RfˆÁE7Gñ∆R“7Gñ∆R‰&ˆ∆BÚfˆÁE7Gñ∆R‰&ˆ∆B¢fˆÁE7Gñ∆RÂ&VwV∆#∞¢7ó7FV“‰G&vñÊr‰G&vñÊs$B‰w&Üñ757FFRw&Üñ757FFR“w&Üñ72Â6fRÇì∞¢w&Üñ72Â6WD6∆óá6fU&Vvñˆ‚ì∞¢W6ñÊrÖ7G&ñÊtf˜&÷Bf˜&÷B“ÊWr7G&ñÊtf˜&÷BÇíê¢∞¢f˜&÷B‰∆ñvÊ÷VÁB“7G&ñÊt∆ñvÊ÷VÁB‰ÊV#∞¢f˜&÷B‰∆ñÊT∆ñvÊ÷VÁB“7G&ñÊt∆ñvÊ÷VÁB‰ÊV#∞¢f˜&÷BÂG&ñ÷÷ñÊr“7G&ñÊuG&ñ÷÷ñÊr‰V∆∆ó6ó5v˜&C∞¢f˜&÷B‰f˜&÷Df∆w2“7G&ñÊtf˜&÷Df∆w2‰∆ñÊT∆ñ÷óC∞¢f∆ˆBfˆÁE6ó¶R“fóDfˆÁBÜw&Üñ72¬∆ñÊRÂG&Á6∆Fñˆ‚¬FWáD&VÂ6ó¶R¬fˆÁDÊ÷R¬fˆÁE7Gñ∆R¬÷FÇ‰÷ñ‚ÉcB¬6˜W&6T∆ñÊTÜVñváB¢„Vbí¬f˜&÷Bì∞¢W6ñÊrÑfˆÁBfˆÁB“ÊWrfˆÁBÜfˆÁDÊ÷R¬fˆÁE6ó¶R¬fˆÁE7Gñ∆R¬w&Üñ75VÊóBÂóÜV¬íê¢∞¢ñbá7Gñ∆R‰6ˆ◊∆WÜóGí‚Çê¢∞¢6ˆ∆˜"˜WF∆ñÊT6ˆ∆˜"“«V÷ñÊÊ6Rá7Gñ∆R‰f˜&Vw&˜VÊBí‚CÚ6ˆ∆˜"‰g&ˆ‘&v"ÉSR¬¬¬í¢6ˆ∆˜"‰g&ˆ‘&v"ÉSR¬#SR¬#SR¬#SRì∞¢W6ñÊrÑ''W6Ç˜WF∆ñÊR“ÊWr6ˆ∆ñD''W6ÇÜ˜WF∆ñÊT6ˆ∆˜"íê¢∞¢w&Üñ72‰G&u7G&ñÊrÜ∆ñÊRÂG&Á6∆Fñˆ‚¬fˆÁB¬˜WF∆ñÊR¬ÊWr&V7FÊv∆TbáFWáD&VÂÇ“¬FWáD&VÂí¬FWáD&VÂvñGFÇ¬FWáD&V‰ÜVñváBí¬f˜&÷Bì∞¢w&Üñ72‰G&u7G&ñÊrÜ∆ñÊRÂG&Á6∆Fñˆ‚¬fˆÁB¬˜WF∆ñÊR¬ÊWr&V7FÊv∆TbáFWáD&VÂÇ≤¬FWáD&VÂí¬FWáD&VÂvñGFÇ¬FWáD&V‰ÜVñváBí¬f˜&÷Bì∞¢w&Üñ72‰G&u7G&ñÊrÜ∆ñÊRÂG&Á6∆Fñˆ‚¬fˆÁB¬˜WF∆ñÊR¬ÊWr&V7FÊv∆TbáFWáD&VÂÇ¬FWáD&VÂí“¬FWáD&VÂvñGFÇ¬FWáD&V‰ÜVñváBí¬f˜&÷Bì∞¢w&Üñ72‰G&u7G&ñÊrÜ∆ñÊRÂG&Á6∆Fñˆ‚¬fˆÁB¬˜WF∆ñÊR¬ÊWr&V7FÊv∆TbáFWáD&VÂÇ¬FWáD&VÂí≤¬FWáD&VÂvñGFÇ¬FWáD&V‰ÜVñváBí¬f˜&÷Bì∞¢–¢–¢W6ñÊrÑ''W6Çf˜&Vw&˜VÊB“ÊWr6ˆ∆ñD''W6Çá7Gñ∆R‰f˜&Vw&˜VÊBíê¢w&Üñ72‰G&u7G&ñÊrÜ∆ñÊRÂG&Á6∆Fñˆ‚¬fˆÁB¬f˜&Vw&˜VÊB¬FWáD&V¬f˜&÷Bì∞¢–¢–¢w&Üñ72Â&W7F˜&RÜw&Üñ757FFRì∞¢–†¢&ófFR7FFñ2f∆ˆBfóDfˆÁBÑw&Üñ72w&Üñ72¬7G&ñÊrFWáB¬6ó¶R&V¬7G&ñÊrfˆÁDÊ÷R¬fˆÁE7Gñ∆R7Gñ∆R¬f∆ˆB÷Üñ◊V“¬7G&ñÊtf˜&÷Bf˜&÷Bê¢∞¢f∆ˆB∆˜r“vc∞¢f∆ˆBÜñvÇ“÷FÇ‰÷ÇÜ∆˜r¬÷Üñ◊V“ì∞¢f˜"ÜñÁBóFW&Fñˆ‚“≤óFW&Fñˆ‚¬ì≤óFW&Fñˆ‚≤≤ê¢∞¢f∆ˆB÷ñFF∆R“Ü∆˜r≤ÜñvÇíÚ&c∞¢W6ñÊrÑfˆÁBfˆÁB“ÊWrfˆÁBÜfˆÁDÊ÷R¬÷ñFF∆R¬7Gñ∆R¬w&Üñ75VÊóBÂóÜV¬íê¢∞¢6ó¶Tb÷V7W&VB“w&Üñ72‰÷V7W&U7G&ñÊráFWáB¬fˆÁB¬÷FÇ‰÷ÇÉB¬&VÂvñGFÇí¬f˜&÷Bì∞¢ñbÜ÷V7W&VBÂvñGFÇ√“&VÂvñGFÇ≤bb÷V7W&VB‰ÜVñváB√“&V‰ÜVñváB≤í∆˜r“÷ñFF∆S∞¢V«6RÜñvÇ“÷ñFF∆S∞¢–¢–¢&WGW&‚÷FÇ‰÷ÇÉvb¬∆˜rì∞¢–†¢&ófFR7FFñ2ñÁB6˜VÁE6˜W&6T∆ñÊW2Ñ∆ó7C≈&V7FÊv∆S‚v˜&G2¬ñÁBf∆∆&6¥ÜVñváBê¢∞¢ñbáv˜&G2”“ÁV∆¬«¬v˜&G2‰6˜VÁB”“í&WGW&‚∞¢∆ó7C∆ñÁC‚6VÁFW'2“v˜&G2Â6V∆V7BÜFV∆VvFRÖ&V7FÊv∆Rv˜&Bí≤&WGW&‚v˜&BÂF˜≤v˜&B‰ÜVñváBÚ#≤“í‰˜&FW$'íÜFV∆VvFRÜñÁBf«VRí≤&WGW&‚f«VS≤“íÂFÙ∆ó7BÇì∞¢ñÁBFˆ∆W&Ê6R“÷FÇ‰÷ÇÉB¬v˜&G2Â6V∆V7BÜFV∆VvFRÖ&V7FÊv∆Rv˜&Bí≤&WGW&‚v˜&B‰ÜVñváC≤“í‰˜&FW$'íÜFV∆VvFRÜñÁBf«VRí≤&WGW&‚f«VS≤“í‰V∆V÷VÁDBáv˜&G2‰6˜VÁBÚ"íÚ"ì∞¢ñÁB∆ñÊW2“∞¢ñÁB∆7D6VÁFW"“ñÁC3"‰÷ñÂf«VS∞¢f˜&V6ÇÜñÁB6VÁFW"ñ‚6VÁFW'2ê¢∞¢ñbÜ∆7D6VÁFW"”“ñÁC3"‰÷ñÂf«VR«¬6VÁFW"“∆7D6VÁFW"‚Fˆ∆W&Ê6Rê¢∞¢∆ñÊW2≤≥∞¢∆7D6VÁFW"“6VÁFW#∞¢–¢–¢&WGW&‚÷FÇ‰÷ÇÉ¬∆ñÊW2ì∞¢–†¢&ófFR7FFñ26ˆ∆˜"÷VFñ‰6ˆ∆˜"Ñ∆ó7Cƒ6ˆ∆˜#‚6ˆ∆˜'2ê¢∞¢ñbÜ6ˆ∆˜'2”“ÁV∆¬«¬6ˆ∆˜'2‰6˜VÁB”“í&WGW&‚6ˆ∆˜"‰g&ˆ‘&v"É#CR¬#CR¬#CRì∞¢ñÁEµ“&VG2“6ˆ∆˜'2Â6V∆V7BÜFV∆VvFRÑ6ˆ∆˜"6ˆ∆˜"í≤&WGW&‚ÜñÁBñ6ˆ∆˜"Â#≤“í‰˜&FW$'íÜFV∆VvFRÜñÁBf«VRí≤&WGW&‚f«VS≤“íÂFÙ'&íÇì∞¢ñÁEµ“w&VVÁ2“6ˆ∆˜'2Â6V∆V7BÜFV∆VvFRÑ6ˆ∆˜"6ˆ∆˜"í≤&WGW&‚ÜñÁBñ6ˆ∆˜"‰s≤“í‰˜&FW$'íÜFV∆VvFRÜñÁBf«VRí≤&WGW&‚f«VS≤“íÂFÙ'&íÇì∞¢ñÁEµ“&«VW2“6ˆ∆˜'2Â6V∆V7BÜFV∆VvFRÑ6ˆ∆˜"6ˆ∆˜"í≤&WGW&‚ÜñÁBñ6ˆ∆˜"‰#≤“í‰˜&FW$'íÜFV∆VvFRÜñÁBf«VRí≤&WGW&‚f«VS≤“íÂFÙ'&íÇì∞¢ñÁB÷ñFF∆R“6ˆ∆˜'2‰6˜VÁBÚ#∞¢&WGW&‚6ˆ∆˜"‰g&ˆ‘&v"á&VG5∂÷ñFF∆U“¬w&VVÁ5∂÷ñFF∆U“¬&«VW5∂÷ñFF∆U“ì∞¢–†¢&ófFR7FFñ2F˜V&∆R«V÷ñÊÊ6RÑ6ˆ∆˜"6ˆ∆˜"ê¢∞¢&WGW&‚6ˆ∆˜"Â"¢„#ìí≤6ˆ∆˜"‰r¢„SÉr≤6ˆ∆˜"‰"¢„C∞¢–†¢&ófFR7FFñ2F˜V&∆R6ˆ∆˜$Fó7FÊ6RÑ6ˆ∆˜"fó'7B¬6ˆ∆˜"6V6ˆÊBê¢∞¢ñÁB&VB“fó'7BÂ"“6V6ˆÊBÂ#∞¢ñÁBw&VV‚“fó'7B‰r“6V6ˆÊB‰s∞¢ñÁB&«VR“fó'7B‰"“6V6ˆÊB‰#∞¢&WGW&‚÷FÇÂ7'Bá&VB¢&VB≤w&VV‚¢w&VV‚≤&«VR¢&«VRì∞¢–†¢&ófFR7FFñ2ñÁB6∆◊6ˆ∆˜"ÜF˜V&∆Rf«VRê¢∞¢&WGW&‚÷FÇ‰÷ÇÉ¬÷FÇ‰÷ñ‚É#SR¬ÜñÁBî÷FÇÂ&˜VÊBáf«VRííì∞¢–¢–†¢ñÁFW&Ê¬6V∆VB6∆72˜fW&∆îf˜&“¢f˜&–¢∞¢&ófFR6ˆÁ7BñÁBw4WÖG&Á7&VÁB“É#∞¢&ófFR6ˆÁ7BñÁBw4WÖFˆˆ≈vñÊF˜r“ÉÉ∞¢&ófFR6ˆÁ7BñÁBw4WÑÊÙ7FófFR“ÉÉ∞¢&ófFR&VFˆÊ«íFñ÷W"6∆˜6UFñ÷W#∞¢&ófFR&VFˆÊ«í&óF÷&VÊFW&VD˜fW&∆ì∞†¢V&∆ñ2˜fW&∆îf˜&“Ö&V7FÊv∆R67&VV‰&V¬îVÁV÷W&&∆Sƒˆ7$∆ñÊTñÊfÛ‚∆ñÊW2¬&óF÷6GW&VDñ÷vR¬ñÁB6V6ˆÊG2ê¢∞¢7F'E˜6óFñˆ‚“f˜&’7F'E˜6óFñˆ‚‰÷ÁV√∞¢&˜VÊG2“67&VV‰&V∞¢f˜&‘&˜&FW%7Gñ∆R“f˜&‘&˜&FW%7Gñ∆R‰ÊˆÊS∞¢6Ü˜tñÂF6∂&"“f«6S∞¢F˜÷˜7B“G'VS∞¢&6¥6ˆ∆˜"“6ˆ∆˜"‰÷vVÁF∞¢G&Á7&VÊ7î∂Wí“6ˆ∆˜"‰÷vVÁF∞¢F˜V&∆T'VffW&VB“G'VS∞†¢&VÊFW&VD˜fW&∆í“˜fW&∆ï&VÊFW&W"Â&VÊFW"Ü6GW&VDñ÷vR¬∆ñÊW2ì∞†¢6∆˜6UFñ÷W"“ÊWrFñ÷W"Çì∞¢6∆˜6UFñ÷W"‰ñÁFW'f¬“6V6ˆÊG2¢∞¢6∆˜6UFñ÷W"ÂFñ6≤≥“FV∆VvFR≤6∆˜6RÇì≤”∞¢6∆˜6UFñ÷W"Â7F'BÇì∞¢–†¢&˜FV7FVB˜fW'&ñFR&ˆˆ¬6Ü˜uvóFÜ˜WD7FófFñˆ‚≤vWB≤&WGW&‚G'VS≤“–†¢&˜FV7FVB˜fW'&ñFR7&VFU&◊27&VFU&◊0¢∞¢vW@¢∞¢7&VFU&◊2&÷WFW'2“&6R‰7&VFU&◊3∞¢&÷WFW'2‰WÖ7Gñ∆R√“w4WÖG&Á7&VÁB¬w4WÖFˆˆ≈vñÊF˜r¬w4WÑÊÙ7FófFS∞¢&WGW&‚&÷WFW'3∞¢–¢–†¢&˜FV7FVB˜fW'&ñFRfˆñBˆÂñÁBÖñÁDWfVÁD&w2Rê¢∞¢&6R‰ˆÂñÁBÜRì∞¢ñbá&VÊFW&VD˜fW&∆í“ÁV∆¬íR‰w&Üñ72‰G&tñ÷vUVÁ66∆VBá&VÊFW&VD˜fW&∆í¬ˆñÁB‰V◊Gíì∞¢–†¢&˜FV7FVB˜fW'&ñFRfˆñBFó7˜6RÜ&ˆˆ¬Fó7˜6ñÊrê¢∞¢ñbÜFó7˜6ñÊrê¢∞¢ñbÜ6∆˜6UFñ÷W"“ÁV∆¬í6∆˜6UFñ÷W"‰Fó7˜6RÇì∞¢ñbá&VÊFW&VD˜fW&∆í“ÁV∆¬í&VÊFW&VD˜fW&∆í‰Fó7˜6RÇì∞¢–¢&6R‰Fó7˜6RÜFó7˜6ñÊrì∞¢–¢–ß–