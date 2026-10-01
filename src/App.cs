using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("Classic Forever Launcher")]
[assembly: AssemblyProduct("Classic Forever Launcher")]
[assembly: AssemblyDescription("Launcher del servidor Classic Forever (codigo abierto)")]
[assembly: AssemblyVersion(ForeverLauncher.App.Version + ".0")]
[assembly: AssemblyFileVersion(ForeverLauncher.App.Version + ".0")]
[assembly: AssemblyInformationalVersion(ForeverLauncher.App.Version)]

namespace ForeverLauncher
{
    public static class App
    {
        public const string Version = "1.2.1";
        public const string ReleasesUrl = "https://github.com/defexnicolas/wow-classic-launcher/releases/latest";

        [STAThread]
        public static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "ClassicForeverLauncher-single", out first))
            {
                if (!first)
                {
                    L.Load();
                    MessageBox.Show(L.Get("app.already"), "Classic Forever");
                    return;
                }
                L.Load();
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Crash(e.Exception); e.Handled = true; app.Shutdown(); };
                var ui = new LauncherWindow(app);
                app.Run(ui.Window);
            }
        }

        public static string CrashFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicForeverLauncher", "crash.txt"); }
        }

        static void Crash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashFile));
                File.AppendAllText(CrashFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " v" + Version + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
                MessageBox.Show(L.Get("app.crash", CrashFile), "Classic Forever", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }

    public sealed class LauncherWindow
    {
        public readonly Window Window;
        readonly Application app;
        readonly Dispatcher ui;

        Button playButton, folderButton, logButton, cacheButton, updateLink, langEs, langEn, navNews, navPatch, navSettings;
        TextBlock statusText, detailText, pingText, messageText, stepText, clientText, folderText, versionText, updateText;
        TextBlock subtitleText, newsHeader, loginLabel, worldLabel, newsPlaceholder, playOverlayText;
        Ellipse statusDot, statusHalo, loginDot, worldDot;
        StackPanel newsList;
        WrapPanel linksPanel;
        Border updateBanner, playOverlay;
        FrameworkElement feedView, settingsView;
        Image playImg;

        string gameDir;
        Patcher patcher;
        bool gameRunning;
        WinForms.NotifyIcon tray;
        string updateUrl = App.ReleasesUrl;
        ServerStatus lastStatus;       // ultimo estado recibido (se repinta al cambiar de idioma)
        Msg lastStep;                  // ultimo mensaje de la barra inferior
        string clientVersion;
        bool clientOk;
        Tab tab = Tab.News;
        CheckBox addonsCheck;
        TextBlock addonsText;
        CheckBox fovCheck;
        Slider fovSlider;
        TextBlock fovValue;
        bool addonsBusy;
        string addonsDoneFor;          // carpeta del juego ya sincronizada en esta sesion
        bool mustUpdate;               // status.json launcher.min > esta version: el boton grande pasa a ACTUALIZAR

        enum Tab { News, Patch, Settings }

        static readonly Color Green = Color.FromRgb(0x5C, 0xD6, 0x7A), Red = Color.FromRgb(0xE0, 0x5A, 0x4E),
                              Amber = Color.FromRgb(0xF0, 0xB2, 0x3E), Grey = Color.FromRgb(0x8A, 0x8A, 0x8A);

        public LauncherWindow(Application app)
        {
            this.app = app;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ForeverLauncher.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);
            ui = Window.Dispatcher;
            try { Window.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(AppIcon().Handle, Int32Rect.Empty, null); } catch { }

            playButton = Find<Button>("PlayButton"); folderButton = Find<Button>("FolderButton"); logButton = Find<Button>("LogButton");
            cacheButton = Find<Button>("CacheButton"); updateLink = Find<Button>("UpdateLink");
            statusText = Find<TextBlock>("StatusText"); detailText = Find<TextBlock>("DetailText"); pingText = Find<TextBlock>("PingText");
            messageText = Find<TextBlock>("MessageText"); stepText = Find<TextBlock>("StepText"); clientText = Find<TextBlock>("ClientText");
            folderText = Find<TextBlock>("FolderText"); versionText = Find<TextBlock>("VersionText"); updateText = Find<TextBlock>("UpdateText");
            statusDot = Find<Ellipse>("StatusDot"); statusHalo = Find<Ellipse>("StatusHalo");
            loginDot = Find<Ellipse>("LoginDot"); worldDot = Find<Ellipse>("WorldDot");
            newsList = Find<StackPanel>("NewsList"); linksPanel = Find<WrapPanel>("LinksPanel");
            updateBanner = Find<Border>("UpdateBanner"); playOverlay = Find<Border>("PlayOverlay");
            subtitleText = Find<TextBlock>("SubtitleText"); newsHeader = Find<TextBlock>("NewsHeader");
            loginLabel = Find<TextBlock>("LoginLabel"); worldLabel = Find<TextBlock>("WorldLabel"); newsPlaceholder = Find<TextBlock>("NewsPlaceholder");
            playOverlayText = Find<TextBlock>("PlayOverlayText"); playImg = Find<Image>("PlayImg");
            feedView = Find<FrameworkElement>("FeedView"); settingsView = Find<FrameworkElement>("SettingsView");
            navNews = Find<Button>("BtnNewsNav"); navPatch = Find<Button>("BtnPatchNav"); navSettings = Find<Button>("BtnSettingsNav");
            langEs = Find<Button>("LangEs"); langEn = Find<Button>("LangEn");
            addonsCheck = Find<CheckBox>("AddonsCheck"); addonsText = Find<TextBlock>("AddonsText");
            addonsCheck.IsChecked = Settings.AddonsEnabled;
            addonsCheck.Click += (s, e) => { Settings.AddonsEnabled = addonsCheck.IsChecked == true; addonsDoneFor = null; SyncAddons(); };

            // Campo de vision: la casilla y el deslizador guardan al momento; FovPatcher (si el juego esta abierto) los lee cada segundo.
            fovCheck = Find<CheckBox>("FovCheck"); fovSlider = Find<Slider>("FovSlider"); fovValue = Find<TextBlock>("FovValue");
            fovCheck.IsChecked = Settings.FovEnabled;
            fovSlider.Value = Settings.FovDegrees;
            fovSlider.IsEnabled = Settings.FovEnabled;
            fovValue.Text = Settings.FovDegrees + "°";
            fovCheck.Click += (s, e) => { Settings.FovEnabled = fovCheck.IsChecked == true; fovSlider.IsEnabled = Settings.FovEnabled; };
            fovSlider.ValueChanged += (s, e) => { int d = (int)Math.Round(e.NewValue); Settings.FovDegrees = d; fovValue.Text = d + "°"; };

            Find<Image>("BgImage").Source = Img("panel.png");
            Find<Image>("LogoImage").Source = Img("logo.png");
            Find<Image>("CloseImg").Source = Img("close.png");
            Find<Image>("NewsImg").Source = Img("nav_news.png");
            Find<Image>("PatchImg").Source = Img("nav_patch.png");
            Find<Image>("SettingsImg").Source = Img("nav_settings.png");
            Find<Image>("DiscordImg").Source = Img("discord.png");
            Find<Button>("DiscordButton").Click += (s, e) => { if (lastStatus != null) OpenUrl(lastStatus.DiscordUrl); };

            langEs.Click += (s, e) => SetLanguage("es");
            langEn.Click += (s, e) => SetLanguage("en");
            navNews.Click += (s, e) => ShowTab(Tab.News);
            navPatch.Click += (s, e) => ShowTab(Tab.Patch);
            navSettings.Click += (s, e) => ShowTab(Tab.Settings);

            versionText.Text = "Launcher v" + App.Version;
            // Toda la ventana se arrastra (los botones y las barras de desplazamiento se quedan el clic antes).
            Window.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove(); };
            Find<Button>("MinButton").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            Find<Button>("CloseButton").Click += (s, e) => OnCloseClicked();
            playButton.Click += (s, e) => { if (mustUpdate) OpenUrl(updateUrl); else Play(); };
            folderButton.Click += (s, e) => PickFolder();
            logButton.Click += (s, e) => OpenLog();
            cacheButton.Click += (s, e) => ClearCache();
            updateLink.Click += (s, e) => OpenUrl(updateUrl);
            Window.Closing += (s, e) => { if (gameRunning) { e.Cancel = true; HideToTray(); } };
            Window.Closed += (s, e) => Shutdown();

            PulseHalo();
            ApplyLanguage();
            SetGameDir(Settings.LoadGameDir() ?? GameLocator.Find(), false);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            timer.Tick += (s, e) => RefreshStatus();
            // Tras Loaded: ahi ya existe el contexto de sincronizacion de WPF y los await vuelven al hilo de la ventana.
            Window.Loaded += (s, e) => { timer.Start(); RefreshStatus(); };
        }

        T Find<T>(string name) where T : class { return (T)Window.FindName(name); }

        // Imagenes incrustadas con build.cmd (/resource:src\img\X,ForeverLauncher.img.X).
        static ImageSource Img(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ForeverLauncher.img." + name))
            {
                if (s == null) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = s;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }

        // ------------------------------------------------------------------ idioma
        void SetLanguage(string lang)
        {
            if (L.Lang == lang) return;
            L.Lang = lang;
            L.Save();
            ApplyLanguage();
        }

        void ApplyLanguage()
        {
            var gold = (Brush)Window.FindResource("Gold");
            var off = new SolidColorBrush(Color.FromRgb(0x8F, 0x87, 0x73));
            langEs.Foreground = L.Lang == "es" ? gold : off;
            langEn.Foreground = L.Lang == "en" ? gold : off;

            subtitleText.Text = L.Get("ui.subtitle");
            loginLabel.Text = L.Get("ui.login");
            worldLabel.Text = L.Get("ui.world");
            Find<TextBlock>("TxtNews").Text = L.Get("nav.news");
            Find<TextBlock>("TxtPatch").Text = L.Get("nav.patch");
            Find<TextBlock>("TxtSettings").Text = L.Get("nav.settings");
            Find<TextBlock>("RealmHeader").Text = L.Get("ui.realm");
            Find<TextBlock>("LinksHeader").Text = L.Get("ui.links");
            Find<TextBlock>("SettingsHeader").Text = L.Get("set.header");
            Find<TextBlock>("FolderLabel").Text = L.Get("set.folder");
            Find<TextBlock>("CacheLabel").Text = L.Get("set.cache");
            Find<TextBlock>("CacheHint").Text = L.Get("set.cachehint");
            Find<TextBlock>("AddonsLabel").Text = L.Get("set.addons");
            Find<TextBlock>("AddonsCheckText").Text = L.Get("set.addonsauto");
            PaintAddons();
            Find<TextBlock>("FovLabel").Text = L.Get("set.fov");
            Find<TextBlock>("FovCheckText").Text = L.Get("set.fovcheck");
            Find<TextBlock>("FovHint").Text = L.Get("set.fovhint");
            Find<Button>("MinButton").ToolTip = L.Get("ui.minimize");
            Find<Button>("CloseButton").ToolTip = L.Get("ui.close");
            Find<Button>("DiscordButton").ToolTip = L.Get("ui.discord");
            Find<TextBlock>("DiscordLabel").Text = L.Get("ui.discord");
            logButton.Content = L.Get("ui.viewlog");
            cacheButton.Content = L.Get("set.cachebtn");
            updateLink.Content = L.Get("ui.download");
            folderButton.Content = L.Get(gameDir == null ? "ui.pickfolder" : "ui.changefolder");
            clientText.Text = clientVersion == null ? "" : L.Get("ui.client", clientVersion) + (clientOk ? "  ✓" : "");
            if (lastStatus == null) statusText.Text = L.Get("status.checking");
            else RenderStatus(lastStatus);
            RenderFeed();
            PaintPlayButton();
            PaintNav();
            if (lastStep != null) Step(lastStep);
            if (tray != null) BuildTrayMenu();
        }

        static System.Drawing.Icon AppIcon()
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        }

        // ------------------------------------------------------------------ pestanas
        void ShowTab(Tab t)
        {
            tab = t;
            feedView.Visibility = t == Tab.Settings ? Visibility.Collapsed : Visibility.Visible;
            settingsView.Visibility = t == Tab.Settings ? Visibility.Visible : Visibility.Collapsed;
            if (t == Tab.Settings) PaintAddons();
            RenderFeed();
            PaintNav();
        }

        void PaintNav()
        {
            var gold = (Brush)Window.FindResource("Gold");
            var dim = new SolidColorBrush(Color.FromRgb(0x9C, 0x90, 0x76));
            foreach (var p in new[] { Tuple.Create(navNews, "TxtNews", Tab.News), Tuple.Create(navPatch, "TxtPatch", Tab.Patch),
                                      Tuple.Create(navSettings, "TxtSettings", Tab.Settings) })
            {
                bool on = p.Item3 == tab;
                p.Item1.Opacity = on ? 1.0 : 0.62;
                Find<TextBlock>(p.Item2).Foreground = on ? gold : dim;
            }
        }

        // Boton grande: JUGAR (imagen por idioma), EN JUEGO (atenuado) o ACTUALIZAR (launcher obligatorio).
        void PaintPlayButton()
        {
            if (mustUpdate)
            {
                playImg.Source = Img("update.png");
                playImg.Opacity = 1;
                playOverlayText.Text = L.Get("ui.updatebtn");
                playOverlay.Visibility = Visibility.Visible;
                playButton.IsEnabled = true;
                playButton.ToolTip = L.Get("ui.mustupdate", lastStatus != null ? lastStatus.MinLauncher : "");
                return;
            }
            playImg.Source = Img(L.Lang == "es" ? "play_es.png" : "play_en.png");
            playImg.Opacity = gameRunning ? 0.45 : 1;
            playOverlayText.Text = L.Get("ui.ingame");
            playOverlay.Visibility = gameRunning ? Visibility.Visible : Visibility.Collapsed;
            playButton.IsEnabled = !gameRunning;
            playButton.ToolTip = null;
        }

        // ------------------------------------------------------------------ escena
        void PulseHalo()
        {
            var st = new ScaleTransform(1, 1);
            statusHalo.RenderTransform = st;
            var grow = new DoubleAnimation(1, 2.1, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var fade = new DoubleAnimation(0.5, 0, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
            statusHalo.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // ------------------------------------------------------------------ estado del servidor
        async void RefreshStatus()
        {
            ServerStatus s;
            try { s = await StatusClient.FetchAsync(); } catch { return; }
            if (s.FeedOk || lastStatus == null || !lastStatus.FeedOk) lastStatus = s;
            else
            {
                // Fallo puntual al leer status.json: se conservan las novedades anteriores y se actualiza la sonda.
                s.FeedOk = true; s.Root = lastStatus.Root; s.News = lastStatus.News; s.Links = lastStatus.Links;
                s.PatchNotes = lastStatus.PatchNotes;
                s.LatestLauncher = lastStatus.LatestLauncher; s.LauncherUrl = lastStatus.LauncherUrl; s.MinLauncher = lastStatus.MinLauncher;
                s.DiscordUrl = lastStatus.DiscordUrl; s.Addons = lastStatus.Addons;
                lastStatus = s;
            }
            RenderStatus(s);
            RenderFeed();
            // La build del cliente se comprobo al arrancar, antes de conocer los clientBuilds del status.json: se repite.
            if (!clientOk && !gameRunning && gameDir != null) SetGameDir(gameDir, false);
            SyncAddons();
        }

        // ------------------------------------------------------------------ addons del servidor (Addons.cs)
        async void SyncAddons()
        {
            if (addonsBusy || !Settings.AddonsEnabled || !clientOk || gameRunning || gameDir == null || addonsDoneFor == gameDir
                || lastStatus == null || !lastStatus.FeedOk || lastStatus.Addons.Count == 0)
            {
                PaintAddons();
                return;
            }
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Patcher.ExeName)).Length > 0)
                return;   // con el juego abierto no se tocan sus addons; se reintenta en el siguiente refresco
            addonsBusy = true;
            string dir = gameDir;
            try
            {
                foreach (Msg m in await AddonInstaller.SyncAsync(dir, lastStatus.Addons))
                    Step(m);
                addonsDoneFor = dir;
            }
            finally { addonsBusy = false; PaintAddons(); }
        }

        void PaintAddons()
        {
            if (addonsText == null) return;
            var parts = new System.Collections.Generic.List<string>();
            if (gameDir != null && lastStatus != null)
                foreach (var a in lastStatus.Addons)
                {
                    string v = AddonInstaller.InstalledVersion(gameDir, a.Name);
                    if (v != null) parts.Add(a.Name + " " + v);
                }
            addonsText.Text = parts.Count > 0 ? string.Join("  ·  ", parts) : L.Get("set.addonsnone");
        }

        void RenderStatus(ServerStatus s)
        {
            Color c; string key;
            if (s.LoginUp && s.WorldUp) { c = Green; key = "status.online"; }
            else if (s.LoginUp) { c = Amber; key = "status.worlddown"; }
            else if (s.FeedFresh && s.FeedLoginUp) { c = Amber; key = "status.unreachable"; }
            else if (s.Maintenance) { c = Amber; key = "status.maintenance"; }
            else { c = Red; key = "status.offline"; }
            SetDot(statusDot, c); SetDot(statusHalo, c);
            statusText.Text = L.Get(key);
            SetDot(loginDot, s.LoginUp ? Green : Red);
            SetDot(worldDot, s.WorldUp ? Green : Red);
            pingText.Text = s.LoginMs >= 0 ? L.Get("ui.ping", s.LoginMs) : "";
            detailText.Text = !s.LoginUp && !s.WorldUp ? L.Get("status.noresponse") : "";
            detailText.Visibility = detailText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            string message = s.Root.Get("message") ?? "";
            messageText.Text = message;
            messageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

            if (s.FeedOk) FillLinks(s);
            // con Discord, el emblema ocupa la parte de arriba de la caja y los enlaces van debajo
            var discordVis = s.DiscordUrl != null ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("DiscordButton").Visibility = discordVis;
            Find<TextBlock>("DiscordLabel").Visibility = discordVis;
            var rb = Find<ScrollViewer>("RightBottom");
            Canvas.SetTop(rb, s.DiscordUrl != null ? 424 : 316);
            rb.Height = s.DiscordUrl != null ? 40 : 146;

            if (!string.IsNullOrEmpty(s.LauncherUrl)) updateUrl = s.LauncherUrl;
            bool newer = StatusClient.IsNewer(s.LatestLauncher, App.Version);
            bool must = StatusClient.IsNewer(s.MinLauncher, App.Version);
            updateText.Text = L.Get("ui.update", s.LatestLauncher);
            updateBanner.Visibility = newer && !must ? Visibility.Visible : Visibility.Collapsed;
            if (must != mustUpdate)
            {
                mustUpdate = must;
                if (must && !gameRunning) Step(new Msg(MsgKind.Warn, "ui.mustupdate", s.MinLauncher));
                else if (!must && !gameRunning && clientOk) Step(new Msg(MsgKind.Info, "ui.ready"));
            }
            PaintPlayButton();
        }

        static void SetDot(Shape e, Color c)
        {
            e.Fill = new SolidColorBrush(c);
        }

        // Noticias y notas del parche comparten lista: mismas entradas del status.json (date, title, text, url, *_en).
        void RenderFeed()
        {
            if (tab == Tab.Settings) return;
            bool patch = tab == Tab.Patch;
            newsHeader.Text = L.Get(patch ? "ui.patchnotes" : "ui.news");
            newsList.Children.Clear();
            var dim = (Brush)Window.FindResource("Dim");
            if (lastStatus == null || !lastStatus.FeedOk)
            {
                newsPlaceholder.Text = L.Get(lastStatus == null ? "ui.loading" : "ui.newsfail");
                newsList.Children.Add(newsPlaceholder);
                return;
            }
            var items = patch ? lastStatus.PatchNotes : lastStatus.News;
            if (items.Count == 0)
            {
                newsList.Children.Add(new TextBlock { Text = L.Get(patch ? "ui.nopatchnotes" : "ui.nonews"), Foreground = dim, FontSize = 13, TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var n in items)
            {
                string date = n.Get("date"), text = n.Get("text"), link = n.Get("url");
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                if (!string.IsNullOrEmpty(date))
                    item.Children.Add(new TextBlock { Text = date, FontSize = 11, Foreground = dim });
                var title = new TextBlock { Text = n.Get("title") ?? "", FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                if (!string.IsNullOrEmpty(link))
                {
                    string url = link;
                    title.Cursor = Cursors.Hand;
                    title.Foreground = (Brush)Window.FindResource("Gold");
                    title.MouseLeftButtonUp += (o, e) => OpenUrl(url);
                }
                item.Children.Add(title);
                if (!string.IsNullOrEmpty(text))
                    item.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)Window.FindResource("Body"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 18 });
                newsList.Children.Add(item);
            }
        }

        void FillLinks(ServerStatus s)
        {
            linksPanel.Children.Clear();
            foreach (var l in s.Links)
            {
                string url = l.Get("url");
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                var b = new Button { Content = l.Get("label"), Style = (Style)Window.FindResource("PillButton") };
                b.Click += (o, e) => OpenUrl(url);
                linksPanel.Children.Add(b);
            }
            Find<TextBlock>("LinksHeader").Visibility = linksPanel.Children.Count > 0 && s.DiscordUrl == null ? Visibility.Visible : Visibility.Collapsed;
        }

        static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------------ carpeta del juego
        void SetGameDir(string dir, bool save)
        {
            gameDir = dir;
            string version;
            Msg err = Patcher.CheckGameDir(dir, out version);
            folderText.Text = dir ?? "";
            folderText.ToolTip = dir;
            clientVersion = version;
            clientOk = err == null;
            clientText.Text = version == null ? "" : L.Get("ui.client", version) + (clientOk ? "  ✓" : "");
            folderButton.Content = L.Get(dir == null ? "ui.pickfolder" : "ui.changefolder");
            cacheButton.IsEnabled = clientOk && !gameRunning;
            addonsDoneFor = null;
            if (err != null) Step(err);
            else
            {
                Step(mustUpdate ? new Msg(MsgKind.Warn, "ui.mustupdate", lastStatus.MinLauncher) : new Msg(MsgKind.Info, "ui.ready"));
                if (save) Settings.SaveGameDir(dir);
            }
        }

        void PickFolder()
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = L.Get("ui.folderdlg");
                dlg.ShowNewFolderButton = false;
                if (gameDir != null && Directory.Exists(gameDir)) dlg.SelectedPath = gameDir;
                if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                // Si eligio la carpeta "World of Warcraft", se baja a _classic_beta_.
                string sub = Path.Combine(dir, "_classic_beta_");
                if (!File.Exists(Path.Combine(dir, Patcher.ExeName)) && File.Exists(Path.Combine(sub, Patcher.ExeName))) dir = sub;
                SetGameDir(dir, true);
            }
        }

        void OpenLog()
        {
            if (gameDir == null) return;
            string log = Path.Combine(gameDir, "Logs", "launcher.log");
            if (File.Exists(log)) try { Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); } catch { }
            else Step(new Msg(MsgKind.Dim, "ui.nolog"));
        }

        // Borra _classic_beta_\Cache (el cliente guarda ahi respuestas del servidor y a veces se quedan rotas; se rehace al entrar).
        // Solo en una carpeta con WowB.exe y con el juego cerrado: nunca borra nada fuera de <carpeta del juego>\Cache.
        void ClearCache()
        {
            if (!clientOk || gameDir == null || !File.Exists(Path.Combine(gameDir, Patcher.ExeName))) { Step(new Msg(MsgKind.Error, "dir.choose")); return; }
            if (gameRunning || Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Patcher.ExeName)).Length > 0)
            {
                Step(new Msg(MsgKind.Warn, "cache.running"));
                return;
            }
            string cache = Path.Combine(gameDir, "Cache");
            if (!Directory.Exists(cache)) { Step(new Msg(MsgKind.Info, "cache.empty")); return; }
            if (MessageBox.Show(L.Get("cache.confirm", cache), "Classic Forever", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                Directory.Delete(cache, true);
                Step(new Msg(MsgKind.Good, "cache.done"));
            }
            catch (Exception ex) { Step(new Msg(MsgKind.Error, "p.error", ex.Message)); }
        }

        // ------------------------------------------------------------------ jugar
        void Play()
        {
            string version;
            Msg err = Patcher.CheckGameDir(gameDir, out version);
            if (err != null) { Step(new Msg(MsgKind.Error, err.Key, err.Args)); if (!File.Exists(Path.Combine(gameDir ?? "", Patcher.ExeName))) PickFolder(); return; }
            Settings.SaveGameDir(gameDir);

            gameRunning = true;
            PaintPlayButton();
            folderButton.IsEnabled = false;
            cacheButton.IsEnabled = false;

            patcher = new Patcher(gameDir);
            patcher.Message += m => ui.BeginInvoke(new Action(() => Step(m)));
            patcher.PhaseChanged += p => ui.BeginInvoke(new Action(() => OnPhase(p)));
            var t = new Thread(patcher.Run) { IsBackground = true, Name = "patcher" };
            t.Start();
        }

        void OnPhase(PatchPhase p)
        {
            switch (p)
            {
                case PatchPhase.WaitingLogin:
                    ShowTray();
                    Window.WindowState = WindowState.Minimized;
                    break;
                case PatchPhase.Ready:
                    System.Media.SystemSounds.Asterisk.Play();
                    if (tray != null)
                        tray.ShowBalloonTip(6000, L.Get("tray.readytitle"), L.Get("tray.readytext"), WinForms.ToolTipIcon.Info);
                    break;
                case PatchPhase.Closed:
                case PatchPhase.Failed:
                    gameRunning = false;
                    patcher = null;
                    PaintPlayButton();
                    folderButton.IsEnabled = true;
                    cacheButton.IsEnabled = clientOk;
                    SyncAddons();
                    HideTray();
                    RestoreWindow();
                    break;
            }
        }

        void Step(Msg m)
        {
            lastStep = m;
            string text = m.ToString();
            stepText.Text = text;
            Color c;
            switch (m.Kind)
            {
                case MsgKind.Good: c = Green; break;
                case MsgKind.Warn: c = Amber; break;
                case MsgKind.Error: c = Red; break;
                case MsgKind.Dim: c = Color.FromRgb(0xB0, 0xA8, 0x94); break;
                default: c = Color.FromRgb(0xED, 0xE6, 0xD6); break;
            }
            stepText.Foreground = new SolidColorBrush(c);
            if (tray != null) tray.Text = Truncate("Classic Forever - " + text, 63);
        }

        static string Truncate(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        // ------------------------------------------------------------------ bandeja y cierre
        void ShowTray()
        {
            if (tray != null) return;
            tray = new WinForms.NotifyIcon { Icon = AppIcon(), Text = "Classic Forever", Visible = true };
            BuildTrayMenu();
            tray.DoubleClick += (s, e) => RestoreWindow();
        }

        void BuildTrayMenu()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add(L.Get("tray.open"), null, (s, e) => RestoreWindow());
            menu.Items.Add(L.Get("tray.exit"), null, (s, e) => ConfirmExit());
            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        void HideTray()
        {
            if (tray == null) return;
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }

        void HideToTray()
        {
            ShowTray();
            Window.Hide();
            tray.ShowBalloonTip(4000, "Classic Forever", L.Get("tray.hidden"), WinForms.ToolTipIcon.None);
        }

        void RestoreWindow()
        {
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
        }

        void OnCloseClicked()
        {
            if (gameRunning) HideToTray();
            else Window.Close();
        }

        void ConfirmExit()
        {
            if (gameRunning)
            {
                var r = MessageBox.Show(L.Get("exit.confirm"),
                    "Classic Forever", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            Shutdown();
        }

        void Shutdown()
        {
            if (patcher != null) patcher.Stop();
            HideTray();
            app.Shutdown();
        }
    }

    static class Settings
    {
        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassicForeverLauncher", "gamedir.txt"); }
        }

        public static string LoadGameDir()
        {
            try
            {
                string d = File.ReadAllText(FilePath).Trim();
                return File.Exists(Path.Combine(d, Patcher.ExeName)) ? d : null;
            }
            catch { return null; }
        }

        public static void SaveGameDir(string dir)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, dir); } catch { }
        }

        // addons del servidor: activado salvo que exista addons_off.txt
        static string AddonsOffFile
        {
            get { return Path.Combine(Path.GetDirectoryName(FilePath), "addons_off.txt"); }
        }

        public static bool AddonsEnabled
        {
            get { return !File.Exists(AddonsOffFile); }
            set
            {
                try
                {
                    if (value) File.Delete(AddonsOffFile);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(AddonsOffFile)); File.WriteAllText(AddonsOffFile, "1"); }
                }
                catch { }
            }
        }

        // campo de vision: fov.txt con "on|off <grados>" (desactivado si no existe). Lo lee FovPatcher cada segundo.
        static string FovFile
        {
            get { return Path.Combine(Path.GetDirectoryName(FilePath), "fov.txt"); }
        }
        static readonly object fovLock = new object();

        static void ReadFov(out bool on, out int degrees)
        {
            on = false; degrees = FovPatcher.DefaultDegrees;
            try
            {
                string[] parts = File.ReadAllText(FovFile).Trim().Split(' ');
                on = parts[0] == "on";
                int d;
                if (parts.Length > 1 && int.TryParse(parts[1], out d)) degrees = Math.Max(FovPatcher.MinDegrees, Math.Min(FovPatcher.MaxDegrees, d));
            }
            catch { }
        }

        static void WriteFov(bool on, int degrees)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FovFile));
                File.WriteAllText(FovFile, (on ? "on " : "off ") + degrees);
            }
            catch { }
        }

        public static bool FovEnabled
        {
            get { lock (fovLock) { bool on; int d; ReadFov(out on, out d); return on; } }
            set { lock (fovLock) { bool on; int d; ReadFov(out on, out d); WriteFov(value, d); } }
        }

        public static int FovDegrees
        {
            get { lock (fovLock) { bool on; int d; ReadFov(out on, out d); return d; } }
            set { lock (fovLock) { bool on; int d; ReadFov(out on, out d); WriteFov(on, Math.Max(FovPatcher.MinDegrees, Math.Min(FovPatcher.MaxDegrees, value))); } }
        }
    }

    static class GameLocator
    {
        // Busca _classic_beta_\WowB.exe: junto al launcher, en el registro de Blizzard y en las rutas habituales.
        public static string Find()
        {
            var candidates = new System.Collections.Generic.List<string>();
            string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            candidates.Add(here);
            candidates.Add(Path.Combine(here, "_classic_beta_"));
            candidates.Add(Path.Combine(Path.GetDirectoryName(here) ?? here, "_classic_beta_"));
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (string v in new[] { "InstallPath", "GamePath" })
                        {
                            string p = k.GetValue(v) as string;
                            if (string.IsNullOrEmpty(p)) continue;
                            if (File.Exists(p)) p = Path.GetDirectoryName(p);
                            p = p.TrimEnd('\\');
                            string parent = Path.GetDirectoryName(p);
                            if (parent != null) candidates.Add(Path.Combine(parent, "_classic_beta_"));
                            candidates.Add(Path.Combine(p, "_classic_beta_"));
                        }
                    }
                }
                catch { }
            }
            foreach (var drive in DriveInfo.GetDrives().Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } }))
            {
                string r = drive.RootDirectory.FullName;
                foreach (string rel in new[] { "World of Warcraft", @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft",
                                               @"Games\World of Warcraft", @"Juegos\World of Warcraft", @"Battle.net\World of Warcraft",
                                               @"Blizzard\World of Warcraft", @"Program Files (x86)\Battle.net\World of Warcraft" })
                    candidates.Add(Path.Combine(r, rel, "_classic_beta_"));
            }
            foreach (string c in candidates)
            {
                try { if (File.Exists(Path.Combine(c, Patcher.ExeName))) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }
    }
}
