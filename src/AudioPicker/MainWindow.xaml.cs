using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioRouter.Core;
using NAudio.CoreAudioApi;

namespace AudioPicker
{
    public class SourceItem
    {
        public string Label { get; set; }
        public string SearchKey { get; set; }
        public bool IsLive { get; set; }
        public AudioSource Source { get; set; }
        public bool IsSystem => Source is AudioSource.System;
    }

    public class LiveItem
    {
        public string Name { get; set; }
        public string Display => Name.Length > 26 ? Name[..26] : Name;
        public float Level { get; set; }
    }

    public partial class MainWindow : Window
    {
        private readonly MMDeviceEnumerator _devices = new();
        private readonly DispatcherTimer _uiTimer;
        private MultiSourceMixer _mixer;
        private OutputPlayer _player;
        private bool _starting;

        private static readonly HashSet<string> SkipProcs = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Idle", "Memory Compression", "csrss", "wininit", "winlogon",
            "services", "lsass", "smss", "svchost", "explorer", "dwm", "conhost", "rundll32",
            "dllhost", "sihost", "taskhostw", "fontdrvhost", "audiodg", "python", "py",
            "SearchIndexer", "SecurityHealthService", "AudioPicker", "SmokeTest"
        };

        public MainWindow()
        {
            InitializeComponent();
            RefreshOutputs();
            RefreshSources("");
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _uiTimer.Tick += UiTick;
            _uiTimer.Start();
        }

        // ---------- lists ----------

        private void RefreshOutputs()
        {
            var names = _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => d.FriendlyName).ToList();
            OutCombo.ItemsSource = names;
            var cable = names.FindIndex(n => n.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            OutCombo.SelectedIndex = cable >= 0 ? cable : 0;
        }

        private void RefreshSources(string search)
        {
            var keep = SourceList.SelectedItems.Cast<SourceItem>().Select(s => s.Label).ToHashSet();
            var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var s in SessionMonitor.ReadPeaks())
                    if (s.Peak > 0.01f) liveNames.Add(s.Name);
            }
            catch { }

            var items = new ObservableCollection<SourceItem>
            {
                new()
                {
                    Label = "вся система (кроме этой программы)",
                    SearchKey = "вся система system",
                    IsLive = liveNames.Count > 0,
                    Source = new AudioSource.System()
                }
            };
            foreach (var p in ProcessResolver.EnumerateCandidates(SkipProcs, search))
            {
                items.Add(new SourceItem
                {
                    Label = p.WindowTitle is null ? p.ExeName : $"{p.ExeName} — {p.WindowTitle}",
                    SearchKey = $"{p.ExeName} {p.WindowTitle}",
                    IsLive = liveNames.Contains(p.ExeName),
                    Source = new AudioSource.Process(p.Pid)
                });
            }

            if (!string.IsNullOrEmpty(search))
                items = new ObservableCollection<SourceItem>(items.Where(i =>
                    i.SearchKey.Contains(search, StringComparison.OrdinalIgnoreCase)));

            SourceList.ItemsSource = items;
            // keep previous selection where possible
            if (keep.Count > 0)
            {
                for (int i = 0; i < items.Count; i++)
                    if (keep.Contains(items[i].Label))
                        SourceList.SelectedItems.Add(items[i]);
            }
            if (SourceList.SelectedItems.Count == 0 && items.Count > 0)
                SourceList.SelectedItems.Add(items[0]);
        }

        // ---------- events ----------

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => RefreshSources(SearchBox.Text.Trim());

        private void Refresh_Click(object sender, RoutedEventArgs e)
            => RefreshSources(SearchBox.Text.Trim());

        private void OutCombo_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // mid-stream output change requires a player restart
            if (_player != null)
            {
                StopStream();
                SetStatus("вывод сменился — жми старт", false);
            }
        }

        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainLabel != null)
            {
                GainLabel.Text = $"x{GainSlider.Value:0.00}";
                if (_mixer != null) _mixer.Gain = (float)GainSlider.Value;
            }
        }

        private void Monitor_Changed(object sender, RoutedEventArgs e)
        {
            if (_player == null) return;
            if (MonitorCheck.IsChecked == true) _player.EnableMonitor();
            else _player.DisableMonitor();
        }

        private void SourceList_DoubleClick(object sender, MouseButtonEventArgs e) => StartStream();

        private void LiveCell_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var name = (string)fe.Tag;
            // select the matching row in the source list
            SourceList.SelectedItems.Clear();
            if (SourceList.ItemsSource is IEnumerable<SourceItem> items)
            {
                foreach (var it in items)
                    if (it.Label.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        SourceList.SelectedItems.Add(it);
                        SourceList.ScrollIntoView(it);
                        break;
                    }
            }
        }

        // ---------- stream control ----------

        private async void StartStream()
        {
            if (_mixer != null || _starting) { StopStream(); return; }
            _starting = true;
            try
            {
                var selected = SourceList.SelectedItems.Cast<SourceItem>().ToList();
                if (selected.Count == 0)
                {
                    SetStatus("источник не выбран", true);
                    return;
                }

                var sources = new List<AudioSource>();
                var labels = new List<string>();
                var quiet = new List<bool>();
                var loudNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var s in SessionMonitor.ReadPeaks())
                        if (s.Peak > 0.01f) loudNames.Add(s.Name);
                }
                catch { }

                foreach (var it in selected)
                {
                    if (it.Source is AudioSource.Process pr)
                    {
                        var exe = it.Label.Split(" — ")[0].Trim();
                        var resolved = ProcessResolver.ResolvePid(exe);
                        if (resolved is uint pid) pr = pr with { Pid = pid };
                        quiet.Add(!loudNames.Contains(exe));
                        labels.Add($"{exe} → pid {pr.Pid}");
                        sources.Add(pr);
                    }
                    else
                    {
                        quiet.Add(false);
                        labels.Add("вся система");
                        sources.Add(it.Source);
                    }
                }

                var outName = (string)OutCombo.SelectedItem;
                var monitor = MonitorCheck.IsChecked == true;

                _player = new OutputPlayer(outName, monitor);
                _mixer = new MultiSourceMixer(sources, _player)
                {
                    Gain = (float)GainSlider.Value
                };
                _mixer.Died += () => Dispatcher.BeginInvoke(() =>
                    SetStatus("источник умер — выбери другого и жми старт", true));

                // give activation a moment, then flip the UI
                await Task.Delay(250);
                if (_mixer == null) return;

                StartBtn.Style = (System.Windows.Style)FindResource("StopButton");
                StartBtn.Content = "■  СТОП";
                StateBadge.Text = "LIVE";
                StateBadge.Foreground = FindResource("GreenBrush") as System.Windows.Media.Brush;
                PulseState(true);

                var allQuiet = quiet.All(q => q);
                SetStatus($"→ {outName}   [{string.Join(", ", labels)}]"
                          + (allQuiet ? "   ⚠ все источники пока молчат" : ""), allQuiet);
            }
            catch (Exception ex)
            {
                SetStatus($"ошибка старта: {ex.Message}", true);
                StopStream();
            }
            finally
            {
                _starting = false;
            }
        }

        private void StopStream()
        {
            var m = _mixer; _mixer = null;
            var p = _player; _player = null;
            try { m?.Dispose(); } catch { }
            try { p?.Dispose(); } catch { }

            StartBtn.Style = (System.Windows.Style)FindResource("BigStartButton");
            StartBtn.Content = "▶  СТАРТ";
            StateBadge.Text = "idle";
            StateBadge.Foreground = FindResource("SubTextBrush") as System.Windows.Media.Brush;
            PulseState(false);
            SetStatus("остановлено", false);
        }

        private void Start_Click(object sender, RoutedEventArgs e) => StartStream();

        // ---------- ui ticker ----------

        private float _lastPeak;
        private DateTime _silentSince = DateTime.MinValue;

        private void UiTick(object sender, EventArgs e)
        {
            // live "who plays now" panel
            List<SessionInfo> levels = null;
            try { levels = SessionMonitor.ReadPeaks(); } catch { }
            if (levels != null)
            {
                var top = levels.Take(8)
                    .Where(s => s.Peak > 0.004f)
                    .Select(s => new LiveItem { Name = s.Name, Level = s.Peak })
                    .ToList();
                LivePanel.ItemsSource = top;
                // tint live rows in the source list
                if (SourceList.ItemsSource is ObservableCollection<SourceItem> srcs)
                {
                    var liveSet = levels.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in srcs)
                        if (!it.IsSystem)
                            it.IsLive = liveSet.Contains(it.Label.Split(" — ")[0].Split(" (pid")[0]);
                    SourceList.Items.Refresh();
                }
            }

            if (_mixer != null)
            {
                var lv = _mixer.Level;
                OutMeter.Value = Math.Min(lv, 1f);
                if (lv > 0.01f)
                {
                    _lastPeak = lv;
                    _silentSince = DateTime.MinValue;
                }
                else if (_silentSince == DateTime.MinValue)
                {
                    _silentSince = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - _silentSince).TotalSeconds > 3)
                {
                    SetStatus("в кабеле тишина — источник молчит, включи звук в нём "
                              + "или выбери другого", true);
                    _silentSince = DateTime.UtcNow; // avoid re-firing every tick
                }
            }
        }

        private void SetStatus(string text, bool warn)
        {
            StatusText.Text = text;
            StatusText.Foreground = warn
                ? FindResource("WarnBrush") as System.Windows.Media.Brush
                : FindResource("SubTextBrush") as System.Windows.Media.Brush;
        }

        // ---------- animations ----------

        private Storyboard _pulse;

        private void PulseState(bool on)
        {
            if (_pulse != null)
            {
                _pulse.Remove(StateBadge);
                _pulse = null;
            }
            if (!on) return;

            var anim = new DoubleAnimation
            {
                From = 0.4,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            _pulse = new Storyboard();
            _pulse.Children.Add(anim);
            Storyboard.SetTarget(anim, StateBadge);
            Storyboard.SetTargetProperty(anim, new PropertyPath("Opacity"));
            _pulse.Begin(StateBadge, true);
        }
    }
}
