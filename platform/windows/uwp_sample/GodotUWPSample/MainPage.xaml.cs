// MainPage.xaml.cs
// Hosts an in-process Godot engine inside a XAML SwapChainPanel. The engine
// runs on a dedicated thread owned by GodotEngineHost; this page only forwards
// input and panel sizing onto that thread. No HWND is involved: the engine
// binds a composition swap chain to GodotPanel and all input is injected from
// the XAML pointer/key events below.

using System;
using System.Runtime.InteropServices;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Godot.Uwp.Embedding
{
    public sealed partial class MainPage : Page
    {
        private readonly GodotEngineHost _host = new GodotEngineHost();

        // Generic host<->engine JSON message bus. The sample only logs incoming
        // messages; real apps answer them and/or push messages with _sender.
        private readonly EngineMessageReceiver _receiver;
        private readonly EngineMessageSender _sender;

        private double _lastX, _lastY;
        private bool _engineStarted;

        public MainPage()
        {
            this.InitializeComponent();

            _receiver = new EngineMessageReceiver();
            _sender = new EngineMessageSender(_host);

            // Key events arrive on the CoreWindow in UWP.
            Window.Current.CoreWindow.KeyDown += OnCoreKeyDown;
            Window.Current.CoreWindow.KeyUp += OnCoreKeyUp;

            // PLM terminates suspended apps that keep presenting — pause the
            // engine loop across suspend/resume.
            Application.Current.Suspending += (s, e2) => _host.Pause();
            Application.Current.Resuming += (s, e2) => _host.Resume();

            _host.Log += OnGodotLog;
            _host.Stopped += OnEngineStopped;
        }

        // -------------------------------------------------------------------
        // Engine startup
        // -------------------------------------------------------------------

        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            if (_engineStarted)
            {
                return;
            }
            _engineStarted = true;

            // Load a bundled "project.pck" if present, else the loose
            // GodotProject folder shipped in the package.
            string installed = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            string pckPath = System.IO.Path.Combine(installed, "Assets", "project.pck");
            string projectPath = System.IO.File.Exists(pckPath)
                ? pckPath
                : System.IO.Path.Combine(installed, "GodotProject");

            _host.ProjectPath = projectPath;

            // The embedded display server is D3D12 / RenderingDevice-only, so
            // force a RenderingDevice method in case the project defaults to
            // the gl_compatibility renderer.
            _host.ExtraArgs = new string[] { "--rendering-method", "forward_plus" };

            // Wire the message bus before Start so messages emitted during
            // GDScript _ready are not dropped. The sample just logs them.
            _receiver.OnMessage += OnBusMessage;
            _receiver.Initialize();

            float scaleX = GodotPanel.CompositionScaleX;
            float scaleY = GodotPanel.CompositionScaleY;
            int widthPx = Math.Max(64, (int)(GodotPanel.ActualWidth * scaleX));
            int heightPx = Math.Max(64, (int)(GodotPanel.ActualHeight * scaleY));

            // IUnknown* of the panel; the engine QIs ISwapChainPanelNative
            // internally (UWP and WinUI3 panel IIDs are both supported).
            IntPtr panelUnknown = Marshal.GetIUnknownForObject(GodotPanel);

            StatusText.Text = "Starting Godot engine...\nLogs: " +
                ApplicationData.Current.LocalFolder.Path + @"\Logs";

            _host.Start(panelUnknown, widthPx, heightPx, scaleX, scaleY, Dispatcher);
        }

        // -------------------------------------------------------------------
        // Engine -> host messages (raised on the UI thread by the receiver)
        // -------------------------------------------------------------------

        private void OnBusMessage(object sender, EngineMessageEventArgs e)
        {
            // Keep the original generic log line for every message.
            string preview = e.ArgsJson != null && e.ArgsJson.Length > 200
                ? e.ArgsJson.Substring(0, 200) + "..."
                : e.ArgsJson;
            GodotEngineHost.LogLine("Bus", "<- " + e.Method + " " + preview);

            // Raised on the UI thread by the receiver, so touching XAML is safe.
            switch (e.Method)
            {
                case "cubes_ready":
                    BuildCubeButtons(e.ArgsJson);
                    break;
                case "cube_color_changed":
                    AppendColorChange(e.ArgsJson);
                    break;
            }
        }

        // Engine -> host: build one button per cube reported by GDScript _ready.
        // argsJson = [{ "index": 0, "name": "Cube0", "color": "rrggbb" }, ...].
        private void BuildCubeButtons(string argsJson)
        {
            try
            {
                ButtonHost.Children.Clear();
                JsonArray cubes = JsonArray.Parse(argsJson);
                foreach (IJsonValue value in cubes)
                {
                    JsonObject cube = value.GetObject();
                    int index = (int)cube.GetNamedNumber("index");
                    string name = cube.GetNamedString("name");
                    Color color = HexToColor(cube.GetNamedString("color"));

                    var button = new Button
                    {
                        Content = name,
                        Tag = index,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 0, 6),
                        Background = new SolidColorBrush(color),
                        Foreground = new SolidColorBrush(ContrastColor(color)),
                    };
                    button.Click += OnCubeButtonClick;
                    ButtonHost.Children.Add(button);
                }
            }
            catch (Exception ex)
            {
                GodotEngineHost.LogLine("Bus", "cubes_ready parse failed: " + ex.Message);
            }
        }

        // Host -> engine: ask GDScript to recolor this cube. Fire-and-forget; the
        // resulting color comes back as a "cube_color_changed" message below.
        private void OnCubeButtonClick(object sender, RoutedEventArgs e)
        {
            int index = (int)((Button)sender).Tag;
            _sender.Post("change_cube_color", "[" + index + "]");
        }

        // Engine -> host: a cube changed color. Show it in the message box and
        // retint the matching button. argsJson = [index, name, "rrggbb"].
        private void AppendColorChange(string argsJson)
        {
            try
            {
                JsonArray args = JsonArray.Parse(argsJson);
                int index = (int)args.GetNumberAt(0);
                string name = args.GetStringAt(1);
                string hex = args.GetStringAt(2);
                Color color = HexToColor(hex);

                foreach (UIElement child in ButtonHost.Children)
                {
                    if (child is Button b && b.Tag is int tag && tag == index)
                    {
                        b.Background = new SolidColorBrush(color);
                        b.Foreground = new SolidColorBrush(ContrastColor(color));
                        break;
                    }
                }

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                row.Children.Add(new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(0, 0, 8, 0),
                });
                row.Children.Add(new TextBlock
                {
                    Text = DateTime.Now.ToString("HH:mm:ss") + "  " + name + "  #" + hex,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 13,
                });
                MessageLog.Children.Add(row);

                // Keep the newest line in view.
                MessageScroll.UpdateLayout();
                MessageScroll.ChangeView(null, MessageScroll.ScrollableHeight, null);
            }
            catch (Exception ex)
            {
                GodotEngineHost.LogLine("Bus", "cube_color_changed parse failed: " + ex.Message);
            }
        }

        // "rrggbb" (Godot Color.to_html(false)) -> opaque Windows.UI.Color.
        private static Color HexToColor(string hex)
        {
            if (!string.IsNullOrEmpty(hex))
            {
                hex = hex.TrimStart('#');
            }
            if (string.IsNullOrEmpty(hex) || hex.Length < 6)
            {
                return Colors.Gray;
            }
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            catch
            {
                return Colors.Gray;
            }
        }

        // Readable text color (black/white) for a tinted button.
        private static Color ContrastColor(Color c)
        {
            double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return luminance > 0.55 ? Colors.Black : Colors.White;
        }

        private void OnEngineStopped(bool engineRequestedQuit)
        {
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = engineRequestedQuit
                    ? "Godot engine exited."
                    : "Godot engine stopped (see logs in LocalState\\Logs).";
            });
        }

        private int _overlayHidden; // 0 = visible, 1 = hide already queued

        private void OnGodotLog(string message, GodotLogLevel level)
        {
            // Called on the ENGINE thread — never touch XAML here directly.
            // Hide the status overlay once, after the engine is running.
            if (_host.IsRunning && System.Threading.Interlocked.Exchange(ref _overlayHidden, 1) == 0)
            {
                var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    StatusText.Visibility = Visibility.Collapsed;
                });
            }
        }

        // -------------------------------------------------------------------
        // Panel sizing / DPI
        // -------------------------------------------------------------------

        private void ConfigurePanel()
        {
            float scaleX = GodotPanel.CompositionScaleX;
            float scaleY = GodotPanel.CompositionScaleY;
            double width = GodotPanel.ActualWidth * scaleX;
            double height = GodotPanel.ActualHeight * scaleY;
            _host.ConfigurePanel(width, height, scaleX, scaleY);
        }

        private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ConfigurePanel();
        }

        private void OnPanelCompositionScaleChanged(SwapChainPanel sender, object args)
        {
            ConfigurePanel();
        }

        // -------------------------------------------------------------------
        // Input forwarding (physical pixels = DIP * CompositionScale)
        // -------------------------------------------------------------------

        private float DpiScale
        {
            get { return GodotPanel.CompositionScaleX; }
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(GodotPanel);
            float scale = DpiScale;
            float x = (float)(pt.Position.X * scale);
            float y = (float)(pt.Position.Y * scale);
            if (pt.Properties.IsLeftButtonPressed) _host.InjectMouseButton(GodotMouseButton.Left, true, x, y);
            if (pt.Properties.IsRightButtonPressed) _host.InjectMouseButton(GodotMouseButton.Right, true, x, y);
            if (pt.Properties.IsMiddleButtonPressed) _host.InjectMouseButton(GodotMouseButton.Middle, true, x, y);
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(GodotPanel);
            float scale = DpiScale;
            float x = (float)(pt.Position.X * scale);
            float y = (float)(pt.Position.Y * scale);
            if (!pt.Properties.IsLeftButtonPressed) _host.InjectMouseButton(GodotMouseButton.Left, false, x, y);
            if (!pt.Properties.IsRightButtonPressed) _host.InjectMouseButton(GodotMouseButton.Right, false, x, y);
            if (!pt.Properties.IsMiddleButtonPressed) _host.InjectMouseButton(GodotMouseButton.Middle, false, x, y);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(GodotPanel);
            float scale = DpiScale;
            float px = (float)(pt.Position.X * scale);
            float py = (float)(pt.Position.Y * scale);
            _host.InjectMouseMotion(px, py,
                (float)((pt.Position.X - _lastX) * scale),
                (float)((pt.Position.Y - _lastY) * scale));
            _lastX = pt.Position.X;
            _lastY = pt.Position.Y;
            e.Handled = true;
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(GodotPanel);
            float scale = DpiScale;
            float x = (float)(pt.Position.X * scale);
            float y = (float)(pt.Position.Y * scale);
            float notches = pt.Properties.MouseWheelDelta / 120.0f;
            if (pt.Properties.IsHorizontalMouseWheel)
            {
                _host.InjectMouseWheel(x, y, notches, 0f);
            }
            else
            {
                _host.InjectMouseWheel(x, y, 0f, notches);
            }
            e.Handled = true;
        }

        private void OnCoreKeyDown(CoreWindow sender, KeyEventArgs e)
        {
            _host.InjectKey((uint)e.VirtualKey, true, e.KeyStatus.WasKeyDown, 0);
        }

        private void OnCoreKeyUp(CoreWindow sender, KeyEventArgs e)
        {
            _host.InjectKey((uint)e.VirtualKey, false, false, 0);
        }
    }
}
