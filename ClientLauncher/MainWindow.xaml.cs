﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Web;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using RestSharp;
using Newtonsoft.Json;
using ClientLauncher.Core.XNative;
using ClientLauncher.Dtos;
using ClientLauncher.Core;
#if EOS_SUPPORTED
using ClientLauncher.Core.EOS;
#endif
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Media;

namespace ClientLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Global constants

#if DEBUG
        private static readonly string BroadcastServer = "http://localhost:8030/";
#else
        private static readonly string BroadcastServer = "https://nv-mp.com/";
#endif

        private static readonly int TimerIntervalQueryServers = 60 * 1000 * 2;

        // Services and instances for client auth
#if !NEXUS_CANDIDATE
        public GithubPatchService PatchService;
#endif 
        public ProgramVersioning ProgramVersion;
        public LocalStorage StorageService;

#if EOS_SUPPORTED
        public IEOSManager EOSManager;
#endif

        public GameActivityMonitor GameActivityMonitor;

        // Keep in sync with gamefalloutnv.h 
        static public string[] VanillaMods = new string[]
        {
            "FalloutNV.esm",
            "DeadMoney.esm",
            "HonestHearts.esm",
            "OldWorldBlues.esm",
            "LonesomeRoad.esm",
            "GunRunnersArsenal.esm",
            "CaravanPack.esm",
            "ClassicPack.esm",
            "MercenaryPack.esm",
            "TribalPack.esm",
            "Fallout3.esm",
            "Anchorage.esm",
            "ThePitt.esm",
            "BrokenSteel.esm",
            "PointLookout.esm",
            "Zeta.esm",
        };

        private class DynamicBackground
        {
            public string Filename { get; set; }
            public string Author { get; set; }
        }

        static private DynamicBackground[] DynamicBackgrounds = new DynamicBackground[]
        {
            new DynamicBackground { Filename = "granda1.png", Author = "granda1" },
            new DynamicBackground { Filename = "misterdjd.png", Author = "MISTERDJD" },
            new DynamicBackground { Filename = "raccoon.png", Author = "A WHOLE Lotta Raccoons" },
            new DynamicBackground { Filename = "rusty shackleford.png", Author = "Rusty Shackleford" }
        };

        // Data
        private bool HasGamePatched;
        private Windows.About AboutWindowInstance;
        private Windows.JoiningServerDisplay JoiningWindowInstance;
        private Windows.ManuallyJoinServerDisplay ManuallyJoinServerWindowInstance;

        private System.Timers.Timer QueryTimer;

#if EOS_SUPPORTED
        private System.Timers.Timer EOSUpdateTimer;
#endif

        private bool IsQuerying;
        private int BlurLevel;

        public string GamePathOverride;

        private ObservableCollection<DtoGameServer> ServerListCollection;

        public bool IsServerAvailable
        {
            get
            {
                if (StorageService == null)
                    return false;

                var falloutDir = FalloutFinder.GameDir(StorageService);
                if (falloutDir != null)
                {
                    return File.Exists($"{falloutDir}\\{XNativeConfig.Exe_PrivateServer}");
                }

                return false;
            }
        }

        public MainWindow()
        {
            HasGamePatched = false;
            IsQuerying = false;
            AboutWindowInstance = null;
            BlurLevel = 0;

#if EOS_SUPPORTED
            EOSManager = new EOSManager();
            EOSManager.UserUpdated += EOSManager_UserUpdated;
#endif

            string exeName = "nvmp_launcher";
            var nvmpProcesses = Process.GetProcessesByName(exeName);
            if (nvmpProcesses.Length > 1)
            {
                foreach (var process in nvmpProcesses)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch { }
                }
            }

            ENet.Managed.ManagedENet.Startup();

            // Needed for communication to HTTPS supporting websites. This could either be the version checker, or the patch service.
            // Regardless this needs to stay enabled even if we do a Nexus submission
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.Expect100Continue = true;

            StorageService = new LocalStorage();
            StorageService.TryLoadSavedData();

            string falloutDir = FalloutFinder.GameDir(StorageService);
            if (falloutDir == null)
            {
                MessageBox.Show("No fallout installation was found on this system. NV:MP can't start up without a valid game directory",
                    "New Vegas: Multiplayer", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            // Start the viewer up.
            InitializeComponent();

            // overrides
            try
            {
                if (!LoadCustomBackground())
                {
                    LoadDynamicBackground();
                }
            }
            catch { }

            var rng = new Random();
            //if ((rng.Next() % 7) == 0)
            //{
            //    SupportMsg.Visibility = Visibility.Visible;
            //}

            ServerListCollection = new ObservableCollection<DtoGameServer>();
            ServerList.ItemsSource = ServerListCollection;

#if !NEXUS_CANDIDATE
            // If this launcher is not the launcher inside the fallout dir, then we need to switch to it to 
            // ensure that any patching that happens returns back to the original launcher.
            if (Environment.GetEnvironmentVariable(XNativeConfig.Patching_ForkingVariable) == null)
            {
                VerifyOrRunOtherGameLauncher(falloutDir, copyIfMissing: true);
            }
#endif

            // Show the fallout folder name in the root window
            Title = $"{Title} - {Path.GetFileName(falloutDir)}";

            GameActivityMonitor = new GameActivityMonitor(this);

            ProgramVersion = new ProgramVersioning(falloutDir);

            // Need to mark the dependency here
            VersionLabel.DataContext = ProgramVersion;

#if !NEXUS_CANDIDATE
            // Patch before initialising components of the main window.
            PatchService = new GithubPatchService(this, falloutDir);
#else
#if !DEBUG
            // If we are out of sync with the Nexus candidate, throw up a warning overlay and prevent the player from 
            // connecting to servers. They will be incompatible. 
            if (ProgramVersion.IsOutOfDate)
            {
                MessageBox.Show($"NV:MP ({ProgramVersion.CurrentVersion}) is out of date. Please update NV:MP via the Nexus mod page to version {ProgramVersion.LatestRelease.tag_name}, or through your mod manager."
                    , "New Vegas: Multiplayer", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                Close();
                return;
            }
#endif
#endif

            CustomToken.Password = StorageService.CustomToken;

            // Patch if token is here.
            if (!HasGamePatched)
            {
#if !DEBUG && !NEXUS_CANDIDATE
                if (!Debugger.IsAttached)
                {
                    PatchService.Patch();
                }
#endif
                HasGamePatched = true;

#if !DEBUG
                QueryTimer = new System.Timers.Timer {
                    Interval = TimerIntervalQueryServers
                };
                QueryTimer.Elapsed += (_, __) => QueryServers();
                QueryTimer.Start();

                // run it async so it doesn't block the UI
                Task.Run(() =>
                {
                    QueryServers();
                });
#endif

                new Thread(delegate ()
                {
#if DEBUG
                    Dispatcher.Invoke(() =>
                    {
                        var dbgServerList = new List<DtoGameServer>();

                        Func<string> randomIPGen = () =>
                        {
                            var items = new string[] { "google.com", "nv-mp.com", "domain.com", "10.0.0.2", "128.0.0.1", "74.3.2.5", "1.2.3.4" };
                            return items[rng.Next(items.Length - 1)];
                        };

                        Func<ushort> randomPortGen = () =>
                        {
                            return (ushort)rng.Next(1000, ushort.MaxValue);
                        };

                        Func<string> randomNameGen = () =>
                        {
                            var items = new string[] { "A New Vegas Multiplayer server", "My Server", "Cool Server Dot Com", "Battle of the Brutes", "BIG WEINERS ONLY", "DOG",
                        "Point Lookout Deathmatch | 64 Tick" };
                            return items[rng.Next(items.Length - 1)];
                        };

                        Func<int> randomMaxPlayersGen = () =>
                        {
                            return rng.Next(0, 64);
                        };

                        // add a good fair few low pop servers
                        for (int i = 0; i < 32; ++i)
                        {
                            var maxPlayers = randomMaxPlayersGen();
                            var playerDensity = (float)rng.NextDouble();

                            var server = new DtoGameServer
                            {
                                IP = randomIPGen(),
                                Port = randomPortGen(),
                                Name = randomNameGen(),
                                MaxPlayers = maxPlayers,
                                NumPlayers = playerDensity >= 0.25 ? (int)((float)maxPlayers * playerDensity) : 0
                            };

                            dbgServerList.Add(server);
                        }

                        // add some high pop servers
                        for (int i = 0; i < 5; ++i)
                        {
                            var maxPlayers = 64;
                            var playerDensity = (float)rng.NextDouble();

                            var server = new DtoGameServer
                            {
                                IP = randomIPGen(),
                                Port = randomPortGen(),
                                Name = randomNameGen(),
                                MaxPlayers = maxPlayers,
                                NumPlayers = 45
                            };

                            dbgServerList.Add(server);
                        }

                        // add the main server
                        var main = new DtoGameServer
                        {
                            IP = "eden.nv-mp.com",
                            Port = 27017,
                            Name = "[OFFICIAL] Freeroam Server",
                            MaxPlayers = 45,
                            NumPlayers = 2
                        };

                        dbgServerList.Add(main);

                        ProcessRemoteServerList(dbgServerList);
                        NoServersMessage.Visibility = Visibility.Hidden;
                        LoadingServersMessage.Visibility = Visibility.Hidden;
                    });
#endif
                    try
                    {
                        GetAndVerifyInstallation();
                    } catch (Exception e)
                    {
                        Trace.Write(e.Message);
                        Trace.Write(e.StackTrace);
                        Dispatcher.Invoke(() =>
                        {
                            ShowError(e.Message);
                        });
                    }
                }).Start();
            }


            Thread.Sleep(500);


#if EOS_SUPPORTED
            bool b_EOSInitialized = false;

            try
            {
                b_EOSInitialized = EOSManager.Initialize();
            }
            catch (Exception e)
            {
                Trace.WriteLine(e.ToString());
            }

            if (!b_EOSInitialized)
            {
                MessageBox.Show($"Epic Online Services platform failed to initialize due to a configuration or installation error. ", "New Vegas: Multiplayer");
                Close();
                return;
            }

            EOSUpdateTimer = new System.Timers.Timer
            {
                Interval = 10
            };
            EOSUpdateTimer.Elapsed += (_, __) => Dispatcher.Invoke(() => { try { EOSManager.Tick(); } catch { } }, System.Windows.Threading.DispatcherPriority.Render);
            EOSUpdateTimer.Start();

            if (EOSManager.User == null)
            {
                // Show the authentication window, if the window closes and the authentication still is not met - then close the program as an implicit
                // closure of the program.
                PushWindowBlur();

                var eosAuthenticationWindow = new Windows.EOSAuthenticate(this);
                eosAuthenticationWindow.ShowDialog();

                if (!eosAuthenticationWindow.Succeeded)
                {
                    Close();
                    PopWindowBlur();
                    return;
                }

                // Check the user's sanctioning. If they are sanctioned, display it and only offer to exit the application. Don't pop the blur until this
                // query is complete, but we can render the window
                if (EOSManager.User != null)
                {
                    if (EOSManager.User.Sanctions != null)
                    {
                        var gameBan = EOSManager.User.Sanctions.Where(sanction => sanction.Type == EOSUserSanctionType.GameBan).FirstOrDefault();
                        if (gameBan != null)
                        {
                            // Game ban found, throw up the suprise message and then quit.
                            var gameBannedModal = new Windows.Modals.ModalGameBanned(gameBan);
                            gameBannedModal.ShowDialog();
                            Close();
                            PopWindowBlur();
                            return;
                        }
                    }
                }

                PopWindowBlur();
            }
#endif

            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;  // important
            Topmost = false; // important
            Focus();         // important
        }

#if EOS_SUPPORTED
        private void EOSManager_UserUpdated(IEOSCurrentUser user)
        {
            if (user == null)
            {
                AuthBar_Name.Content = "OFFLINE MODE";
                AuthBar_Name.Foreground = new SolidColorBrush(Colors.Red);
                ServerScroller.Visibility = Visibility.Hidden;
            }
            else if (user.DisplayName == null)
            {
                AuthBar_Name.Content = $"Authorizing...";
                AuthBar_Name.Foreground = new SolidColorBrush(Colors.Yellow);
                ServerScroller.Visibility = Visibility.Hidden;
            }
            else
            {
                AuthBar_Name.Content = $"{user.DisplayName}";
                AuthBar_Name.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 255, 0));
                ServerScroller.Visibility = Visibility.Visible;
            }
        }
#endif 

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            EOSUpdateTimer.Stop();
            QueryTimer?.Stop();
            
            EOSUpdateTimer.Dispose();
            QueryTimer?.Dispose();

#if EOS_SUPPORTED
            EOSManager?.Dispose();
#endif
            GameActivityMonitor?.Shutdown();
        }

        public void VerifyOrRunOtherGameLauncher(string falloutDir, bool copyIfMissing = false)
        {
            if (!Debugger.IsAttached)
            {
                var expectedFolder = falloutDir;
                var currentExeFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
                var currentExeName = "nvmp_launcher.exe";
                if (expectedFolder != currentExeFolder)
                {
                    // If there is a launcher in there with the same name as ours, then start that one up instead
                    var requiredLauncherName = $"{expectedFolder}\\{currentExeName}";

                    if (copyIfMissing)
                    {
                        if (!File.Exists(requiredLauncherName))
                        {
                            try
                            {
                                File.Copy(System.Reflection.Assembly.GetEntryAssembly().Location, requiredLauncherName);
                            } catch (Exception) { }
                        }
                    }

                    if (File.Exists(requiredLauncherName))
                    {
                        try
                        {
                            using (Process fork = new Process())
                            {
                                fork.StartInfo.FileName = requiredLauncherName;
                                fork.StartInfo.UseShellExecute = false;
                                fork.StartInfo.CreateNoWindow = true;
                                fork.StartInfo.WorkingDirectory = expectedFolder;
                                fork.Start();
                            }

                            Process.GetCurrentProcess().Kill();
                        }
                        catch (Exception)
                        {
                            // Purposeful fall-through if the above fails
                        }
                    }
                }
            }

        }

        /// <summary>
        /// Reads the data folder for a custom background image
        /// and if it exists, applies it to the browser window.
        /// </summary>
        public bool LoadCustomBackground()
        {
            string GameDir = FalloutFinder.GameDir(StorageService);

            if (GameDir == null)
                return false;

            if (!Directory.Exists(GameDir + "\\nvmp\\res"))
                return false;

            if (File.Exists(GameDir + "\\nvmp\\res\\LauncherBackground.png"))
            {
                try {
                    BackgroundPanel.ImageSource = new BitmapImage(new Uri(GameDir + "\\nvmp\\res\\LauncherBackground.png"));
                    BackgroundAuthor.Content = $"Custom Background";
                    return true;
                } catch (Exception e)
                {
                    MessageBox.Show("Failed to load custom background, exception thrown. " + e.ToString());
                }
            }

            return false;
        }

        public void LoadDynamicBackground()
        {
            string GameDir = FalloutFinder.GameDir(StorageService);

            if (GameDir == null)
                return;

            if (!Directory.Exists(GameDir + "\\nvmp\\res"))
                return;

            int totalDaysEver = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000 / 60 / 60 / 24);
            int randomseed = (totalDaysEver / 7) - 1; /* -1 for the launch to use default bg */
            var rng = new Random(randomseed);
            var bg = DynamicBackgrounds[rng.Next(0, DynamicBackgrounds.Length - 1)];

            var uri = new Uri($"pack://application:,,,/ClientLauncher;component/Res/StaticBackgrounds/{bg.Filename}", UriKind.Absolute);

            BackgroundAuthor.Content = $"Background by {bg.Author}";
            BackgroundPanel.ImageSource = new BitmapImage(uri);
        }

        private void TryToInstallMSVC(string GameDir)
        {
            if (!Directory.Exists(GameDir + "\\nvmp\\redist"))
            {
                Trace.WriteLine("Redist dir not found");
                return;
            }

            string InstallerExe = GameDir + "\\nvmp\\redist\\vc_redist.x86.exe";
            if (!File.Exists(InstallerExe))
            {
                Trace.WriteLine("Redist file not found");
                return;
            }

            Process installer = new Process();
            installer.StartInfo.FileName         = InstallerExe;
            installer.StartInfo.UseShellExecute  = true;
            installer.StartInfo.WorkingDirectory = GameDir + "\\nvmp\\redist";
            installer.StartInfo.Verb             = "runas";
            installer.StartInfo.Arguments        = "/q"; // Silent installation
            installer.EnableRaisingEvents        = true;
            installer.Start();
            installer.WaitForExit(); // Make sure this process is completed before starting NV:MP
        }

        private void SortServerList()
        {
            Dispatcher.Invoke(() =>
            {
                // Sort the servers
                Trace.WriteLine("Sorting...");

                var sortedCollection = ServerListCollection.ToList();
                sortedCollection.Sort(delegate (DtoGameServer a, DtoGameServer b)
                {
                    if (a.IsStarred)
                    {
                        return -1;
                    }
                    if (b.IsStarred)
                        return 1;

                    return b.NumPlayers.CompareTo(a.NumPlayers);
                });

                ServerListCollection = new ObservableCollection<DtoGameServer>(sortedCollection);
                ServerList.ItemsSource = ServerListCollection;
                ServerList.Items.Refresh();
            });
        }

        private void ProcessRemoteServerList(List<DtoGameServer> servers)
        {
            if (servers.Count == 0)
            {
                NoServersMessage.Visibility = Visibility.Visible;
            }
            else
            {
                int SoftReturnPosition = ServerList.SelectedIndex;

                NoServersMessage.Visibility = Visibility.Hidden;

                int pendingPings = 0;
                foreach (var server in servers)
                {
                    ++pendingPings;

                    // Evaluate if the server is starred
                    if (StorageService.StarredServers.Contains($"{server.IP}:{server.Port}"))
                        server.IsStarred = true;

                    // Evaluate the ping
                    Task.Run(() =>
                    {
                        PingServer(server);
                        {
                            Dispatcher.Invoke(() =>
                            {
                                ServerListCollection.Add(server);
                            });
                        }

                        --pendingPings;

                        if (pendingPings == 0)
                        {
                            SortServerList();
                        }
                    });
                }

                ServerList.SelectedIndex = SoftReturnPosition;
            }
        }

        /// <summary>
        /// A synchronous ping of a server object. Will update the supplied object with status updates
        /// </summary>
        /// <param name="server"></param>
        internal bool PingServer(DtoGameServer server)
        {
            Trace.WriteLine($"Pinging {server.Name} {server.IP}");

            PingReply reply;
            try
            {
                var pingInstance = new Ping();
                reply = pingInstance.Send(server.IP, 1000);
                switch (reply.Status)
                {
                    case IPStatus.Success:
                        {
                            server.DisplayPing = reply.RoundtripTime.ToString() + " ms";
                            break;
                        }
                    default:
                        {
                            server.DisplayPing = "TMOUT";
                            break;
                        }
                }

                return reply.Status == IPStatus.Success;
            }
            catch (Exception)
            {
                server.DisplayPing = "ERR";
            }

            return false;
        }

        private async void QueryServers()
        {
            if (IsQuerying)
            {
                return;
            }

            IsQuerying = true;
            try
            {
                var client = new RestClient(BroadcastServer);
                var request = new RestRequest($"/serverlist", Method.GET);

                IRestResponse response = await client.ExecuteAsync(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var servers = JsonConvert.DeserializeObject<List<DtoGameServer>>(response.Content);
                    if (servers != null && servers.Count > 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (ServerListCollection != null)
                            {
                                ServerListCollection.Clear();
                            }
                            ProcessRemoteServerList(servers);
                            NoServersMessage.Visibility = Visibility.Hidden;
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (ServerListCollection != null)
                            {
                                ServerListCollection.Clear();
                            }
                            NoServersMessage.Visibility = Visibility.Visible;
                        });
                    }
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (ServerListCollection != null)
                        {
                            ServerListCollection.Clear();
                        }

                        NoServersMessage.Visibility = Visibility.Visible;
                        ShowError("Server Update HTTP Exception: " + response.ErrorMessage);
                        Trace.WriteLine(response.ErrorMessage);
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ShowError("Server Update Exception: " + ex.Message);

                    if (ServerListCollection != null)
                    {
                        ServerListCollection.Clear();
                    }
                    NoServersMessage.Visibility = Visibility.Visible;
                });
            }

            Dispatcher.Invoke(() =>
            {
                LoadingServersMessage.Visibility = Visibility.Hidden;
            });

            IsQuerying = false;
        }

        public void PushWindowBlur()
        {
            if (BlurLevel == 0)
            {
                Dispatcher.Invoke(() =>
                {
                    var effect = new BlurEffect();
                    effect.KernelType = KernelType.Gaussian;
                    effect.Radius = 10;
                    effect.RenderingBias = RenderingBias.Performance;

                    Effect = effect;
                });
            }
            ++BlurLevel;
        }

        public void ClearWindowBlur()
        {
            Dispatcher.Invoke(() =>
            {
                Effect = null;
            });

            BlurLevel = 0;
        }

        public void PopWindowBlur()
        {
            if (BlurLevel > 0)
            {
                --BlurLevel;

                if (BlurLevel == 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Effect = null;
                    });
                }
            }
        }

        public void Refresh_ServerList_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                QueryServers();
            });
        }

#if !NEXUS_CANDIDATE
        /// <summary>
        /// Click event that should start a forceful resync of NV:MP binaries from GitHub
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void Repair_Click(object sender, RoutedEventArgs e)
        {
            string GameDirectory;

            try
            {
                GameDirectory = FalloutFinder.GameDir(StorageService);

                if (GameDirectory == null)
                    throw new Exception("FO:NV or Steam is not installed.");

                TryToInstallMSVC(GameDirectory);
            }
            catch (Exception)
            {
            }

            // Do the patching.
            PatchService.Patch( true );
        }
#endif

        public void ContextMenu_ShowLauncher(object sender, RoutedEventArgs e)
        {
            ShowInTaskbar = true;
            Show();
            NotifyIcon.Visibility = System.Windows.Visibility.Collapsed;
        }

        public void ContextMenu_Quit(object sender, RoutedEventArgs e)
        {
            ShowInTaskbar = true;
            Show();
            NotifyIcon.Visibility = System.Windows.Visibility.Collapsed;

            Close();
        }

        /// <summary>
        /// Repairs via Steam (temp hack)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void SteamRepair_Click(object sender, RoutedEventArgs e)
        {
            Process proc =  Process.Start("steam://validate/22380");
            if (proc != null)
            {
                MessageBox.Show("Please check your Steam client to check if Fallout has been validated");
            }
            else
            {
                MessageBox.Show("Could not start the Steam client");
            }
        }

        /// <summary>
        /// Click event that should open the about page and show license/attribution
        /// information.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void About_Click(object sender, RoutedEventArgs e)
        {
            if (AboutWindowInstance == null)
            {
                // Apply blurred effect whilst about window is open.
                PushWindowBlur();

                AboutWindowInstance = new Windows.About();
                AboutWindowInstance.Closed += DialogBoxClosed;
                AboutWindowInstance.ShowDialog();
            }
        }

        public void Patreon_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://www.patreon.com/newvegasmp");
        }

        public void KoFi_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://ko-fi.com/newvegasmp");
        }

        public void Discord_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://nv-mp.com/discord");
        }

        public void Wiki_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://wiki.nv-mp.com/");
        }

        private void DialogBoxClosed(object sender, EventArgs e)
        {
            PopWindowBlur();
            AboutWindowInstance = null;
            ManuallyJoinServerWindowInstance = null;
        }

        public void CustomToken_Changed(object sender, EventArgs e)
        {
            StorageService.CustomToken = CustomToken.Password;
        }

        public void CopyToken_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(CustomToken.Password.ToString());
        }

        public void OverrideGameDir_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = null;

            ShowError(null);

            try
            {
                currentPath = GetAndVerifyInstallation().GameDirectory;
            } catch (Exception)
            {
            }

            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                if (currentPath != null)
                {
                    dialog.SelectedPath = currentPath;
                }

                dialog.ShowDialog();
                if (dialog.SelectedPath != null)
                {
                    StorageService.GamePathOverride = dialog.SelectedPath;

                    try
                    {
                        GetAndVerifyInstallation();

                        // We may need to swap over to that installation's launcher if it exists, but if it doesn't, then copy our launcher
                        // into it and then it should automatically update
                        VerifyOrRunOtherGameLauncher(StorageService.GamePathOverride, copyIfMissing: true);
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex.Message);
                        StorageService.GamePathOverride = null;
                    }
                }
            }
        }

        protected class StartupInfo
        {
            public string GameDirectory { get; set; }
            public string GameExe { get; set; }
            public int GameID { get; set; }
            public string NVMPExe { get; set; }
        }

        protected StartupInfo GetAndVerifyInstallation()
        {
            StartupInfo result = new StartupInfo();

            if (result.GameDirectory == null)
            {
                result.GameDirectory = FalloutFinder.GameDir(StorageService);
            }

            if (result.GameDirectory == null)
            {
                throw new Exception("FO:NV or Steam is not installed");
            }

            result.GameExe = result.GameDirectory + "\\FalloutNV.exe";

            // Only require steam if the folder has a steam api present
            if (File.Exists(result.GameDirectory + "\\steam_api.dll"))
            {
                Process[] SteamInstance = Process.GetProcessesByName("Steam");
                if (SteamInstance.Length == 0)
                {
                    throw new Exception("Steam is not running, please ensure it is running");
                }
            }

            // Validate the Fallout: New Vegas game exists.
            if (!File.Exists(result.GameExe))
            {
                throw new Exception("Fallout: New Vegas is not installed");
            }

            // Validate the Fallout: New Vegas game files are the right
            // checksums (to prevent "no gore" edition being ran).

            string ValidityError = FalloutChecksum.IsGameCorrectVersion(result.GameDirectory);
            if (ValidityError != null)
            {
                throw new Exception("FO:NV copy invalid (" + ValidityError + ")");
            }

            // Validate the NVMP executable exists.
            result.NVMPExe = result.GameDirectory + "\\nvmp_start.exe";
            if (!File.Exists(result.NVMPExe))
            {
                throw new Exception("NV:MP binaries missing, please try to repair");
            }

            result.GameID = FalloutChecksum.LookupGameID(result.GameDirectory);
            if (result.GameID == 0)
            {
                throw new Exception("GameID cannot be calculated");
            }

            return result;
        }

        public void JoinServer(DtoGameServer server)
        {
            if (JoiningWindowInstance != null)
            {
                Dispatcher.Invoke(() =>
                {
                    JoiningWindowInstance.Close();
                    JoiningWindowInstance = null;
                });
            }

            var installation = GetAndVerifyInstallation();

            string modsList = "*";
            if (server.Mods != null)
            {
                modsList = String.Join(",", server.Mods.Select(mod => mod.Name));
            }

            // If the mods list ins't correctly populated, try to PROBE the server to see if we can grab this information
            // directly. We don't do this if we have the mods list already, as this can be delivered elsewhere in the launcher (server list, maybe URL?)
            //if (modsList == "*")
            {
                //
                // Try to probe
                // 
                try
                {
                    using (var probe = new ServerProbe(server))
                    {
                        var result = probe.Connect();

                        if (result.State != ServerProbe.ProbeStatus.ProbeState.ReplyOK)
                        {
                            var msg = "Unknown";
                            switch (result.State)
                            {
                                case ServerProbe.ProbeStatus.ProbeState.AwaitingReply:
                                    msg = "Awaiting Reply";
                                    break;
                                case ServerProbe.ProbeStatus.ProbeState.ReplyMalformed:
                                    msg = "Reply Malformed";
                                    break;
                                case ServerProbe.ProbeStatus.ProbeState.Unreachable:
                                    msg = "Unreachable";
                                    break;
                                default: break;
                            }

                            MessageBox.Show($"Could not connect to {server.IP}:{server.Port} [{msg}]", "New Vegas: Multiplayer", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                            return;
                        }

                        foreach (var accountType in result.Result.AccountTypesRequired)
                        {
                            switch (accountType)
                            {
                                case NetProbe.Types.OSExternalAccountType.EpicGames:
                                    break;
                                case NetProbe.Types.OSExternalAccountType.Discord:
                                    if (EOSManager.User == null)
                                        return;

                                    if (!EOSManager.User.LinkedAuths.Contains(EOSLoginType.Discord))
                                    {
                                        // Request it through the modal. 
                                        var linkageModal = new Windows.Modals.ModalEOSLinkDiscord(EOSManager);
                                        linkageModal.ShowDialog();

                                        if (!EOSManager.User.LinkedAuths.Contains(EOSLoginType.Discord))
                                        {
                                            ShowError("Discord Authorization failed");
                                            return;
                                        }
                                    }
                                    break;
                                default: break;
                            }
                        }

                        // The first entries of the result CSV are the mods available. If modsList doesnt exist (from a public server entry), then use the probe result
                        // as this allows for internal reporting to always be a reliable fallback.
                        if (result.Result.Mods.Count != 0)
                        {
                            modsList = String.Join(",", result.Result.Mods);
                        }
                    }
                }
                catch (Exception e)
                {
                    ShowError(e.Message);
                    return;
                }
            }

            SortModFiles(modsList, installation.GameDirectory);

            //
            // Start the game
            //
            GameActivityMonitor.ShutdownCurrentActivity();

            // Get the EOS token and Product ID
            string jwtToken = "invalid_jwt_token";
            string productId = "invalid_product_id";

            if (EOSManager.User != null)
            {
                productId = EOSManager.User.ProductId;
                jwtToken = EOSManager.GetProductAuthToken();
                
                if (jwtToken == null)
                {
                    // Rome has fallen! Re-authenticate.
                    Hide();

                    // Logs out
                    EOSManager.LogoutFromPersistent((IEOSLoginResult result) =>
                    {
                        // Show the authentication window, if the window closes and the authentication still is not met - then close the program as an implicit
                        // closure of the program.
                        var eosAuthenticationWindow = new Windows.EOSAuthenticate(this);
                        eosAuthenticationWindow.ShowDialog();

                        if (EOSManager.User == null)
                        {
                            Close();
                            return;
                        }

                        Show();
                    });
                    return;
                }
            }

            var game = new Process();
            game.StartInfo.FileName             = installation.NVMPExe;
            game.StartInfo.Arguments            = $"{server.IP} {server.Port} {installation.GameID} {HttpUtility.UrlEncode(jwtToken)} {HttpUtility.UrlEncode(productId)} \"{modsList}\"";
            game.StartInfo.UseShellExecute      = true;
            game.StartInfo.WorkingDirectory     = installation.GameDirectory;
            game.EnableRaisingEvents            = true;
            game.Exited += delegate (object sender, EventArgs e)
            {
                // See if it spawned the game child
                var procs = Process.GetProcessesByName("FalloutNV");
                if (procs.Length == 0)
                {
                    GameActivityMonitor.ShutdownCurrentActivity();
                    return;
                }

                var falloutproc = procs.FirstOrDefault();
                GameActivityMonitor.TrackNewInstance(falloutproc, server);

                Dispatcher.Invoke(() =>
                {
                    Play_Control.IsEnabled = true;
#if !NEXUS_CANDIDATE
                    Repair_Control.IsEnabled = true;
#endif
                    RepairSteam_Control.IsEnabled = true;
                });
            };

            game.Start();

            Dispatcher.Invoke(() =>
            {
                Play_Control.IsEnabled = false;
#if !NEXUS_CANDIDATE
                Repair_Control.IsEnabled = false;
#endif
                RepairSteam_Control.IsEnabled = false;
            });
        }

        protected void SortModFiles(string modsList, string gameDirectory)
        {
            //
            // Sort Available Mods - Always put default mods first
            //
            var modFileListPaths = new List<string>();
            if (modsList == "*")
            {
                // Query for all availalbe mods types
                var files = Directory
                    .EnumerateFiles($"{gameDirectory}\\Data", "*.*", SearchOption.AllDirectories)
                    .Where(s => new string[] { "esp", "esm" }.Contains(Path.GetExtension(s).TrimStart('.').ToLowerInvariant()));

                modFileListPaths = files.ToList();

                // var mods = Directory
                //     .EnumerateFiles($"{installation.GameDirectory}\\Data", "*.*", SearchOption.AllDirectories)
                //     .Where(s => new string[] { "esp", "esm" }.Contains(Path.GetExtension(s).TrimStart('.').ToLowerInvariant()))
                //     .Select(s => Path.GetFileName(s));
                // 
                // modsList = "FalloutNV.esm," + String.Join(",", mods);
            }
            else
            {
                modFileListPaths = modsList.Split(',').Select(mod => $"{gameDirectory}\\Data\\{mod}").ToList();
            }

            //
            // Sort the mod file paths to ensure vanilla content is first
            //
            //var vanillaContent = modFileListPaths
            //    .Where(c => VanillaMods.Contains(Path.GetFileName(c)))
            //    .OrderBy(x => Array.FindIndex(VanillaMods, v => v == Path.GetFileName(x)))
            //    .ToArray();

            //var nonVanillaContent = modFileListPaths
            //    //.Where(c => !VanillaMods.Contains(Path.GetFileName(c)))
            //    .OrderBy(x => Array.FindIndex(modFileListPaths.ToArray(), v => v == Path.GetFileName(x)))
            //    .ToArray();
            //
            //modFileListPaths = /*vanillaContent.Concat(nonVanillaContent)*/ nonVanillaContent.ToList();

            int index = 0;
            foreach (var modFilePath in modFileListPaths)
            {
                try
                {
                    File.SetLastWriteTime(modFilePath, new DateTime(2000, (index / 29) + 1, index + 1));
                    ++index;
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                }
            }
        }

        protected void CancelJoiningServer(object sender, EventArgs e)
        {
            JoiningWindowInstance = null;
        }

        public void ServerItem_Selected(object sender, EventArgs e)
        {
            if (ServerList.SelectedIndex == -1)
            {
                Play_Control.IsEnabled = false;
                return;
            }

            Play_Control.IsEnabled = true;
            Play_Control.Content = "Authenticate";
            CustomToken.IsEnabled = false;
        }

        public void Play_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ServerList.SelectedIndex == -1)
                {
                    return;
                }

                // Do a key flush firstly, so that we don't try to hit the server with a key that is now expired
                StorageService.TryFlushExpiredKeys();

                DtoGameServer server = ServerListCollection[ ServerList.SelectedIndex ];

                //
                // Acquire Mods
                //
                if (server.Mods != null && server.Mods.Count != 0)
                {
                    var downloadModsWindow = new Windows.DownloadModsDisplay
                    {
                        StorageService = StorageService,
                        ServerMods = server.Mods,
                        DownloadResourceURL = server.ModsDownloadURL
                    };

                    downloadModsWindow.UpdateModStates();

                    if (!downloadModsWindow.IsClosed)
                    {
                        downloadModsWindow.ShowDialog();
                    }

                    if (!downloadModsWindow.DependenciesResolved)
                    {
                        ShowError("Failed to download server dependencies");
                        return;
                    }
                }

                JoinServer(server);
                ShowError(null);
            }
            catch (Exception exc)
            {
                ShowError(exc.Message + "\n" + exc.StackTrace);
            }
        }
        
        public void ShowError(string errorMsg)
        {
            try
            {
                if (errorMsg != null)
                {
                    File.WriteAllText("nvmp_launcher_last_error.log", $"message: {errorMsg}\ncallstack:\n{Environment.StackTrace}");
                }
            } catch (Exception)
            {
            }

            Dispatcher.Invoke(() =>
            {
                if (errorMsg == null)
                {
                    MessageBorder.Visibility = Visibility.Hidden;
                }
                else
                {
                    MessageBorder.Visibility = Visibility.Visible;
                    MessageText_Control.Text = errorMsg;
                }
            });
        }

        private void Start_StoryServer_Click(object sender, RoutedEventArgs e)
        {
            if (!IsServerAvailable)
            {
                return;
            }

            string falloutDir = FalloutFinder.GameDir(StorageService);
            if (falloutDir == null)
            {
                return;
            }

            // First do a soft mod load order sort of all mods on disk. 
            // SortModFiles("*", falloutDir);

            var game = new Process();
            game.StartInfo.FileName = $"{falloutDir}\\{XNativeConfig.Exe_PrivateServer}";
            game.StartInfo.WorkingDirectory = falloutDir;
            game.StartInfo.UseShellExecute = true;

            if (EOSManager.User == null)
            {
                // if we are in offline mode, start the server in offline mode too!
                game.StartInfo.Arguments += "-eos_disable ";
            }

            game.Start();
        }

        private void ManualJoin_Click(object sender, RoutedEventArgs e)
        {
            if (ManuallyJoinServerWindowInstance == null)
            {
                // Apply blurred effect whilst about window is open.
                PushWindowBlur();

                ManuallyJoinServerWindowInstance = new Windows.ManuallyJoinServerDisplay(this);
                ManuallyJoinServerWindowInstance.Closed += DialogBoxClosed;
                ManuallyJoinServerWindowInstance.ShowDialog();
            }
        }

        private void AuthBar_Name_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Don't allow offline mode to relog unless they do a full restart
            if (EOSManager.User == null)
            {
                return;
            }

            Hide();

            // Logs out
            EOSManager.LogoutFromPersistent((IEOSLoginResult result) =>
            {
                // Show the authentication window, if the window closes and the authentication still is not met - then close the program as an implicit
                // closure of the program.
                var eosAuthenticationWindow = new Windows.EOSAuthenticate(this);
                eosAuthenticationWindow.ShowDialog();

                if (EOSManager.User == null)
                {
                    Close();
                    return;
                }

                Show();
            });
        }

        private void AuthBar_Name_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Try to copy the account and product token
            if (EOSManager.User != null)
            {
                Clipboard.SetText($"account_id: {EOSManager.User.AccountId}\nproduct_id: {EOSManager.User.ProductId}");
            }
        }
    }
}
