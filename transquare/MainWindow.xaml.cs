using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;

namespace ScreenFrame
{
    // 設定保存用モデル
    public class AppSettings
    {
        public double Left { get; set; } = 100;
        public double Top { get; set; } = 100;
        public double Width { get; set; } = 1920;
        public double Height { get; set; } = 1080;
        public string ColorName { get; set; } = "Red";
        public double Thickness { get; set; } = 4.0;
        public double Opacity { get; set; } = 1.0;
        public bool IsMoveLocked { get; set; } = true;
        public bool IsSizeLocked { get; set; } = true;
        public bool Topmost { get; set; } = true;
        public bool ShowInTaskbar { get; set; } = true;
        public double GridSize { get; set; } = 20.0;
        public string Language { get; set; } = "ja";
        public PresetItem?[] Presets { get; set; } = new PresetItem?[4];
    }

    public class PresetItem
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string ColorName { get; set; } = "Red";
        public double Thickness { get; set; } = 4.0;
        public double Opacity { get; set; } = 1.0;
    }

    public partial class MainWindow : Window
    {
        // Win32 API: 画面全体の絶対カーソル座標を取得
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        // Win32 API: 現在のモニター情報取得（マルチモニター対応）
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // 状態変数
        private bool isMoveLocked = true;
        private bool isSizeLocked = true;
        private double gridSize = 20.0;

        // 確定済みのスタイルプロパティ（ホバープレビューのキャンセル復元用）
        private double committedOpacity = 1.0;
        private Brush committedBrush = Brushes.Red;
        private string committedColorName = "Red";
        private Thickness committedThickness = new Thickness(4);

        // ドラッグ移動用の保持変数
        private bool isDraggingMove = false;
        private Point initialCursorPos;
        private Point initialWindowPos;

        // HUD自動非表示タイマー
        private DispatcherTimer? hudTimer;

        // タスクトレイ常駐用 NotifyIcon
        private WinForms.NotifyIcon? notifyIcon;

        // 現在の言語 ("ja" または "en")
        private string currentLang = "ja";

        // プリセットデータ
        private PresetItem?[] userPresets = new PresetItem?[4];

        // 設定ファイルパス (%AppData%\Transquare\settings.json)
        private readonly string settingsFilePath;

        public MainWindow()
        {
            InitializeComponent();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "Transquare");
            Directory.CreateDirectory(folder);
            settingsFilePath = Path.Combine(folder, "settings.json");

            // ウィンドウ・タスクバー用アイコンの明示的適用
            try
            {
                var iconUri = new Uri("pack://application:,,,/andrsrxn-image-square.ico");
                this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
            catch { }

            // OSの言語設定から初期言語を判定
            string osLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
            currentLang = (osLang == "ja") ? "ja" : "en";

            InitNotifyIcon();
            LoadSettings(); // 前回の設定を復元
            ApplyLanguage(currentLang);
            UpdateUIState();
        }

        #region 設定の自動保存・読み込み・リセット

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        // ウィンドウ位置・サイズ復元
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        if (s.Width >= 100) Width = s.Width;
                        if (s.Height >= 100) Height = s.Height;
                        Left = s.Left;
                        Top = s.Top;

                        // ロック設定
                        isMoveLocked = s.IsMoveLocked;
                        isSizeLocked = s.IsSizeLocked;
                        MenuMoveLock.IsChecked = isMoveLocked;
                        MenuSizeLock.IsChecked = isSizeLocked;

                        // オプション
                        Topmost = s.Topmost;
                        MenuTopmost.IsChecked = Topmost;

                        ShowInTaskbar = s.ShowInTaskbar;
                        MenuShowInTaskbar.IsChecked = ShowInTaskbar;

                        gridSize = s.GridSize > 0 ? s.GridSize : 20.0;
                        currentLang = !string.IsNullOrEmpty(s.Language) ? s.Language : currentLang;

                        // 色・太さ・不透明度
                        committedColorName = s.ColorName ?? "Red";
                        var brush = (Brush)new BrushConverter().ConvertFromString(committedColorName)!;
                        committedBrush = brush;
                        FrameBorder.BorderBrush = brush;

                        committedThickness = new Thickness(s.Thickness > 0 ? s.Thickness : 4.0);
                        FrameBorder.BorderThickness = committedThickness;

                        committedOpacity = (s.Opacity > 0 && s.Opacity <= 1.0) ? s.Opacity : 1.0;
                        FrameBorder.Opacity = committedOpacity;

                        // プリセット
                        if (s.Presets != null && s.Presets.Length == 4)
                        {
                            userPresets = s.Presets;
                        }
                    }
                }
                else
                {
                    // 初回起動時は画面中央に配置
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var s = new AppSettings
                {
                    Left = Left,
                    Top = Top,
                    Width = Width,
                    Height = Height,
                    ColorName = committedColorName,
                    Thickness = committedThickness.Left,
                    Opacity = committedOpacity,
                    IsMoveLocked = isMoveLocked,
                    IsSizeLocked = isSizeLocked,
                    Topmost = Topmost,
                    ShowInTaskbar = ShowInTaskbar,
                    GridSize = gridSize,
                    Language = currentLang,
                    Presets = userPresets
                };

                string json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsFilePath, json);
            }
            catch { }
        }

        // 設定のリセット（確認ダイアログ付き）
        private void MenuResetSettings_Click(object sender, RoutedEventArgs e)
        {
            bool isJa = (currentLang == "ja");
            string message = isJa
                ? "すべての設定とプリセットを初期状態にリセットしますか？"
                : "Are you sure you want to reset all settings and presets to default?";
            string caption = isJa ? "設定のリセット" : "Reset Settings";

            var result = MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                ResetSettingsToDefault();
            }
        }

        private void ResetSettingsToDefault()
        {
            // デフォルト値に戻す
            Width = 1920;
            Height = 1080;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // 画面中央に配置
            AlignWindowToScreen("Center");

            isMoveLocked = true;
            isSizeLocked = true;
            MenuMoveLock.IsChecked = true;
            MenuSizeLock.IsChecked = true;

            Topmost = true;
            MenuTopmost.IsChecked = true;

            ShowInTaskbar = true;
            MenuShowInTaskbar.IsChecked = true;

            gridSize = 20.0;

            committedColorName = "Red";
            committedBrush = Brushes.Red;
            FrameBorder.BorderBrush = Brushes.Red;

            committedThickness = new Thickness(4.0);
            FrameBorder.BorderThickness = committedThickness;

            committedOpacity = 1.0;
            FrameBorder.Opacity = committedOpacity;

            userPresets = new PresetItem?[4];

            SaveSettings();
            ApplyLanguage(currentLang);
            UpdateUIState();

            ShowBriefHud("設定をリセットしました", "Settings Reset", "初期状態に戻しました");
        }

        #endregion

        #region ユーザープリセット (1〜4)

        // プリセット適用
        private void MenuApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int index) && index >= 0 && index < 4)
            {
                var p = userPresets[index];
                if (p == null)
                {
                    string emptyMsg = (currentLang == "ja")
                        ? $"プリセット {index + 1} はまだ保存されていません"
                        : $"Preset {index + 1} is empty";
                    ShowBriefHud("プリセット未保存", "Preset Empty", emptyMsg);
                    return;
                }

                // プリセット適用
                Width = p.Width;
                Height = p.Height;
                Left = p.Left;
                Top = p.Top;

                committedColorName = p.ColorName;
                committedBrush = (Brush)new BrushConverter().ConvertFromString(p.ColorName)!;
                FrameBorder.BorderBrush = committedBrush;

                committedThickness = new Thickness(p.Thickness);
                FrameBorder.BorderThickness = committedThickness;

                committedOpacity = p.Opacity;
                FrameBorder.Opacity = committedOpacity;

                SaveSettings();

                string applyMsg = (currentLang == "ja")
                    ? $"プリセット {index + 1} を適用しました"
                    : $"Preset {index + 1} Applied";
                ShowBriefHud(applyMsg, applyMsg, $"{(int)Width} × {(int)Height}  [X: {(int)Left}, Y: {(int)Top}]");
            }
        }

        // プリセット保存
        private void MenuSavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int index) && index >= 0 && index < 4)
            {
                userPresets[index] = new PresetItem
                {
                    Left = Left,
                    Top = Top,
                    Width = Width,
                    Height = Height,
                    ColorName = committedColorName,
                    Thickness = committedThickness.Left,
                    Opacity = committedOpacity
                };

                SaveSettings();
                UpdatePresetMenuTexts();

                string saveMsg = (currentLang == "ja")
                    ? $"プリセット {index + 1} に保存しました"
                    : $"Saved to Preset {index + 1}";
                ShowBriefHud(saveMsg, saveMsg, $"{(int)Width} × {(int)Height}  [X: {(int)Left}, Y: {(int)Top}]");
            }
        }

        private void UpdatePresetMenuTexts()
        {
            bool isJa = (currentLang == "ja");
            var applyItems = new[] { MenuApplyPreset1, MenuApplyPreset2, MenuApplyPreset3, MenuApplyPreset4 };
            var saveItems = new[] { MenuSavePreset1, MenuSavePreset2, MenuSavePreset3, MenuSavePreset4 };

            bool hasAnyPreset = false;
            for (int i = 0; i < 4; i++)
            {
                var p = userPresets[i];
                if (p != null)
                {
                    hasAnyPreset = true;
                    string info = $"({(int)p.Width}×{(int)p.Height})";
                    applyItems[i].Header = isJa ? $"プリセット {i + 1} {info}" : $"Preset {i + 1} {info}";
                    applyItems[i].IsEnabled = true;
                }
                else
                {
                    applyItems[i].Header = isJa ? $"プリセット {i + 1} (空)" : $"Preset {i + 1} (Empty)";
                    applyItems[i].IsEnabled = false;
                }

                saveItems[i].Header = isJa ? $"プリセット {i + 1} に保存" : $"Save to Preset {i + 1}";
            }

            // 保存されているプリセットが1つでもある場合のみクリア項目を表示
            if (MenuClearPresets != null)
            {
                MenuClearPresets.Visibility = hasAnyPreset ? Visibility.Visible : Visibility.Collapsed;
                MenuClearPresets.Header = isJa ? "プリセットをすべてクリア..." : "Clear All Presets...";
            }
        }

        // プリセット全クリア（確認ダイアログ付き）
        private void MenuClearPresets_Click(object sender, RoutedEventArgs e)
        {
            bool isJa = (currentLang == "ja");
            string message = isJa
                ? "保存されているすべてのプリセットをクリアしますか？"
                : "Are you sure you want to clear all saved presets?";
            string caption = isJa ? "プリセットのクリア" : "Clear Presets";

            if (MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                userPresets = new PresetItem?[4];
                SaveSettings();
                UpdatePresetMenuTexts();

                ShowBriefHud("プリセットをクリアしました", "Presets Cleared", "すべてのプリセットを消去しました");
            }
        }

        #endregion

        #region タスクトレイ（NotifyIcon）常駐 & 切り替え

        private void InitNotifyIcon()
        {
            notifyIcon = new WinForms.NotifyIcon
            {
                Visible = true,
                Text = "Transquare"
            };

            // 設定されたアイコンをリソースから読み込んでタスクトレイにも適用 (16x16の適切なサイズを選択)
            try
            {
                var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/andrsrxn-image-square.ico"));
                if (streamInfo != null)
                {
                    using var stream = streamInfo.Stream;
                    notifyIcon.Icon = new Drawing.Icon(stream, 16, 16);
                }
                else
                {
                    notifyIcon.Icon = Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                notifyIcon.Icon = Drawing.SystemIcons.Application;
            }

            // トレイアイコンのマウスクリックイベント
            notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    // 左クリックで「枠の表示 / 非表示」をトグル切り替え
                    ToggleVisibility();
                }
                else if (e.Button == WinForms.MouseButtons.Right)
                {
                    // 右クリックで設定メニューをトレイ付近にポップアップ
                    OpenContextMenuFromTray();
                }
            };
        }

        // 枠の表示・非表示トグル切り替え
        public void ToggleVisibility()
        {
            if (Visibility == Visibility.Visible)
            {
                Hide();
            }
            else
            {
                Show();
                Activate();
                Topmost = MenuTopmost.IsChecked;
            }
            UpdateVisibilityMenuText();
        }

        private void MenuToggleVisible_Click(object sender, RoutedEventArgs e)
        {
            ToggleVisibility();
        }

        // タスクバー表示 ↔ タスクトレイ格納の切り替え
        private void MenuShowInTaskbar_Click(object sender, RoutedEventArgs e)
        {
            ShowInTaskbar = MenuShowInTaskbar.IsChecked;
            SaveSettings();
        }

        // トレイアイコン右クリック時にメニューをカーソル位置にポップアップ
        private void OpenContextMenuFromTray()
        {
            if (GetCursorPos(out POINT p))
            {
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                MainMenu.Placement = PlacementMode.AbsolutePoint;
                MainMenu.HorizontalOffset = p.X / dpiX;
                MainMenu.VerticalOffset = p.Y / dpiY;
                MainMenu.IsOpen = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveSettings();

            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                notifyIcon = null;
            }
            base.OnClosed(e);
        }

        #endregion

        #region ドラッグ移動 & 大きな位置表示HUD

        // 枠線上を左クリックで移動開始
        private void FrameBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (isMoveLocked) return;

            if (e.ButtonState == MouseButtonState.Pressed && GetCursorPos(out POINT p))
            {
                hudTimer?.Stop();
                isDraggingMove = true;
                initialCursorPos = new Point(p.X, p.Y);
                initialWindowPos = new Point(Left, Top);
                FrameBorder.CaptureMouse();

                ShowMoveHud(Left, Top, false);
            }
        }

        // 移動中のマウス移動処理
        private void FrameBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDraggingMove) return;

            if (GetCursorPos(out POINT p))
            {
                // DPIスケーリング（100%, 125%, 150% など）の考慮
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                double deltaX = (p.X - initialCursorPos.X) / dpiX;
                double deltaY = (p.Y - initialCursorPos.Y) / dpiY;

                double targetLeft = initialWindowPos.X + deltaX;
                double targetTop = initialWindowPos.Y + deltaY;

                // Alt キーが押されているときはグリッド吸着
                bool isAlt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
                if (isAlt)
                {
                    targetLeft = Math.Round(targetLeft / gridSize) * gridSize;
                    targetTop = Math.Round(targetTop / gridSize) * gridSize;
                }

                Left = targetLeft;
                Top = targetTop;

                ShowMoveHud(targetLeft, targetTop, isAlt);
            }
        }

        // マウスボタンを離したら移動終了
        private void FrameBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isDraggingMove)
            {
                isDraggingMove = false;
                FrameBorder.ReleaseMouseCapture();
                HudOverlay.Visibility = Visibility.Collapsed;
                SaveSettings();
            }
        }

        private void ShowMoveHud(double left, double top, bool isAlt)
        {
            HudOverlay.Visibility = Visibility.Visible;
            if (currentLang == "ja")
            {
                HudTitle.Text = "位置（ディスプレイ左上からの距離）";
                HudMainText.Text = $"X (左): {(int)left} px    Y (上): {(int)top} px";
                HudSubText.Text = $"⚡ Altグリッド吸着中 ({gridSize}px)";
            }
            else
            {
                HudTitle.Text = "Position (from top-left of display)";
                HudMainText.Text = $"X (Left): {(int)left} px    Y (Top): {(int)top} px";
                HudSubText.Text = $"⚡ Alt Grid Snapping ({gridSize}px)";
            }

            HudSubText.Visibility = isAlt ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowBriefHud(string titleJa, string titleEn, string mainText)
        {
            hudTimer?.Stop();
            HudOverlay.Visibility = Visibility.Visible;
            HudTitle.Text = (currentLang == "ja") ? titleJa : titleEn;
            HudMainText.Text = mainText;
            HudSubText.Visibility = Visibility.Collapsed;

            hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            hudTimer.Tick += (s, e) =>
            {
                HudOverlay.Visibility = Visibility.Collapsed;
                hudTimer.Stop();
            };
            hudTimer.Start();
        }

        #endregion

        #region リサイズ & 大きなサイズ表示HUD

        private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (!isSizeLocked)
            {
                hudTimer?.Stop();
                ShowResizeHud(Width, Height, false);
            }
        }

        // 右下の小さい四角（Thumb）をドラッグしてサイズ変更
        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (isSizeLocked) return;

            double newWidth = Width + e.HorizontalChange;
            double newHeight = Height + e.VerticalChange;

            // Alt キーが押されているときはサイズもグリッド吸着
            bool isAlt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
            if (isAlt)
            {
                newWidth = Math.Round(newWidth / gridSize) * gridSize;
                newHeight = Math.Round(newHeight / gridSize) * gridSize;
            }

            // 最小サイズ 100x100
            if (newWidth >= 100) Width = newWidth;
            if (newHeight >= 100) Height = newHeight;

            ShowResizeHud(Width, Height, isAlt);
        }

        private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            HudOverlay.Visibility = Visibility.Collapsed;
            SaveSettings();
        }

        private void ShowResizeHud(double width, double height, bool isAlt)
        {
            HudOverlay.Visibility = Visibility.Visible;
            if (currentLang == "ja")
            {
                HudTitle.Text = "現在のサイズ";
                HudMainText.Text = $"幅: {(int)width} px  ×  高さ: {(int)height} px";
                HudSubText.Text = $"⚡ Altグリッド吸着中 ({gridSize}px)";
            }
            else
            {
                HudTitle.Text = "Current Size";
                HudMainText.Text = $"Width: {(int)width} px  ×  Height: {(int)height} px";
                HudSubText.Text = $"⚡ Alt Grid Snapping ({gridSize}px)";
            }

            HudSubText.Visibility = isAlt ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 枠位置（一括移動）

        // 角・中央への一括配置
        private void MenuPosition_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string pos)
            {
                AlignWindowToScreen(pos);
            }
        }

        private void AlignWindowToScreen(string position)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(hMonitor, ref mi)) return;

            // DPI スケーリング考慮
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double sLeft = mi.rcMonitor.Left / dpiX;
            double sTop = mi.rcMonitor.Top / dpiY;
            double sWidth = (mi.rcMonitor.Right - mi.rcMonitor.Left) / dpiX;
            double sHeight = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpiY;

            double targetLeft = Left;
            double targetTop = Top;

            switch (position)
            {
                // 角
                case "TopLeft":
                    targetLeft = sLeft;
                    targetTop = sTop;
                    break;
                case "TopRight":
                    targetLeft = sLeft + sWidth - Width;
                    targetTop = sTop;
                    break;
                case "BottomLeft":
                    targetLeft = sLeft;
                    targetTop = sTop + sHeight - Height;
                    break;
                case "BottomRight":
                    targetLeft = sLeft + sWidth - Width;
                    targetTop = sTop + sHeight - Height;
                    break;

                // 中央 (上下左右の順)
                case "Center":
                    targetLeft = sLeft + (sWidth - Width) / 2.0;
                    targetTop = sTop + (sHeight - Height) / 2.0;
                    break;
                case "TopCenter":
                    targetLeft = sLeft + (sWidth - Width) / 2.0;
                    targetTop = sTop;
                    break;
                case "BottomCenter":
                    targetLeft = sLeft + (sWidth - Width) / 2.0;
                    targetTop = sTop + sHeight - Height;
                    break;
                case "LeftCenter":
                    targetLeft = sLeft;
                    targetTop = sTop + (sHeight - Height) / 2.0;
                    break;
                case "RightCenter":
                    targetLeft = sLeft + sWidth - Width;
                    targetTop = sTop + (sHeight - Height) / 2.0;
                    break;
            }

            Left = targetLeft;
            Top = targetTop;
            SaveSettings();

            ShowBriefHud("枠位置を設定しました", "Frame Position Set", $"X (左): {(int)Left} px    Y (上): {(int)Top} px");
        }

        #endregion

        #region メニュー & 設定

        // ⚙ボタンを左クリックしたとき
        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenContextMenu(SettingsBtn);
        }

        // ⚙ボタンを右クリックしたとき
        private void SettingsBtn_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            OpenContextMenu(SettingsBtn);
            e.Handled = true;
        }

        private void OpenContextMenu(FrameworkElement target)
        {
            MainMenu.PlacementTarget = target;
            MainMenu.Placement = PlacementMode.Top;
            MainMenu.HorizontalOffset = 0;
            MainMenu.VerticalOffset = 0;
            MainMenu.IsOpen = true;
        }

        // メニューが閉じたときの復元（未決定で閉じた場合は確定済みの値に戻す）
        private void MainMenu_Closed(object sender, RoutedEventArgs e)
        {
            FrameBorder.Opacity = committedOpacity;
            FrameBorder.BorderBrush = committedBrush;
            FrameBorder.BorderThickness = committedThickness;
        }

        // 移動ロック 切り替え
        private void MenuMoveLock_Click(object sender, RoutedEventArgs e)
        {
            isMoveLocked = MenuMoveLock.IsChecked;
            UpdateUIState();
            SaveSettings();
        }

        // サイズ変更ロック 切り替え
        private void MenuSizeLock_Click(object sender, RoutedEventArgs e)
        {
            isSizeLocked = MenuSizeLock.IsChecked;
            UpdateUIState();
            SaveSettings();
        }

        // 常に最前面 切り替え
        private void MenuTopmost_Click(object sender, RoutedEventArgs e)
        {
            Topmost = MenuTopmost.IsChecked;
            SaveSettings();
        }

        // グリッド吸着サイズ 変更
        private void MenuGrid_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && double.TryParse(fe.Tag?.ToString(), out double size))
            {
                gridSize = size;
                SaveSettings();
            }
        }

        // 枠の太さ: ホバー時プレビュー
        private void MenuThickness_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && double.TryParse(fe.Tag?.ToString(), out double thickness))
            {
                FrameBorder.BorderThickness = new Thickness(thickness);
            }
        }

        // 枠の太さ: クリック確定
        private void MenuThickness_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && double.TryParse(fe.Tag?.ToString(), out double thickness))
            {
                committedThickness = new Thickness(thickness);
                FrameBorder.BorderThickness = committedThickness;
                SaveSettings();
            }
        }

        // 枠線透明度: ホバー時プレビュー
        private void MenuOpacity_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && double.TryParse(fe.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity))
            {
                FrameBorder.Opacity = opacity;
            }
        }

        // 枠線透明度: クリック確定
        private void MenuOpacity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && double.TryParse(fe.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity))
            {
                committedOpacity = opacity;
                FrameBorder.Opacity = committedOpacity;
                SaveSettings();
            }
        }

        // 枠の色: ホバー時プレビュー
        private void MenuColor_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string colorName)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(colorName)!;
                FrameBorder.BorderBrush = brush;
            }
        }

        // 枠の色: クリック確定
        private void MenuColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string colorName)
            {
                committedColorName = colorName;
                var brush = (Brush)new BrushConverter().ConvertFromString(colorName)!;
                committedBrush = brush;
                FrameBorder.BorderBrush = committedBrush;
                SaveSettings();
            }
        }

        // プリセット解像度への一発リサイズ
        private void MenuPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                var parts = tag.Split('x');
                if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h))
                {
                    Width = w;
                    Height = h;
                    SaveSettings();
                    ShowBriefHud("サイズを設定しました", "Size Set", $"幅: {(int)Width} px  ×  高さ: {(int)Height} px");
                }
            }
        }

        // 言語切り替え
        private void MenuLang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string lang)
            {
                ApplyLanguage(lang);
                SaveSettings();
            }
        }

        // アプリ終了
        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void UpdateVisibilityMenuText()
        {
            bool isJa = (currentLang == "ja");
            if (Visibility == Visibility.Visible)
            {
                MenuToggleVisible.Header = isJa ? "枠を隠す (非表示)" : "Hide Frame";
            }
            else
            {
                MenuToggleVisible.Header = isJa ? "枠を表示する" : "Show Frame";
            }
        }

        // 言語リソースの適用
        private void ApplyLanguage(string lang)
        {
            currentLang = lang;
            bool isJa = (lang == "ja");

            // メニュー項目
            MenuMoveLock.Header = isJa ? "移動をロック" : "Lock Movement";
            MenuSizeLock.Header = isJa ? "サイズ変更をロック" : "Lock Resize";
            MenuTopmost.Header = isJa ? "常に最前面" : "Always on Top";
            MenuShowInTaskbar.Header = isJa ? "タスクバーに表示" : "Show in Taskbar";
            UpdateVisibilityMenuText();

            // 枠位置
            MenuGroupPosition.Header = isJa ? "枠位置" : "Frame Position";
            MenuGroupCorners.Header = isJa ? "角" : "Corners";
            MenuPosTopLeft.Header = isJa ? "左上" : "Top-Left";
            MenuPosTopRight.Header = isJa ? "右上" : "Top-Right";
            MenuPosBottomLeft.Header = isJa ? "左下" : "Bottom-Left";
            MenuPosBottomRight.Header = isJa ? "右下" : "Bottom-Right";

            MenuGroupCenters.Header = isJa ? "中央" : "Center / Align";
            MenuPosCenter.Header = isJa ? "画面中央" : "Screen Center";
            MenuPosTopCenter.Header = isJa ? "上・左右中央" : "Top Center";
            MenuPosBottomCenter.Header = isJa ? "下・左右中央" : "Bottom Center";
            MenuPosLeftCenter.Header = isJa ? "左・上下中央" : "Left Center";
            MenuPosRightCenter.Header = isJa ? "右・上下中央" : "Right Center";

            // プリセット
            MenuGroupPresets.Header = isJa ? "プリセット" : "Presets";
            MenuGroupApplyPreset.Header = isJa ? "プリセットを適用" : "Apply Preset";
            MenuGroupSavePreset.Header = isJa ? "現在の状態を保存" : "Save Current to Preset";
            UpdatePresetMenuTexts();

            MenuGroupPreset.Header = isJa ? "既定の解像度 (OBS用)" : "Preset Resolutions (for OBS)";
            MenuPresetSquare.Header = isJa ? "1080 × 1080 (1:1 正方形)" : "1080 × 1080 (1:1 Square)";

            MenuGroupSnap.Header = isJa ? "Alt吸着グリッドサイズ" : "Alt Snap Grid Size";
            MenuSnap20.Header = isJa ? "20 px (標準)" : "20 px (Default)";

            MenuGroupThickness.Header = isJa ? "枠の太さ" : "Border Thickness";

            MenuGroupOpacity.Header = isJa ? "枠線の不透明度" : "Border Opacity";
            MenuOpacity100.Header = isJa ? "100% (標準)" : "100% (Default)";

            MenuGroupColor.Header = isJa ? "枠の色" : "Border Color";
            MenuColorRed.Header = isJa ? "赤 (Red)" : "Red";
            MenuColorCyan.Header = isJa ? "青 (Cyan)" : "Cyan";
            MenuColorLime.Header = isJa ? "緑 (Lime)" : "Lime";
            MenuColorYellow.Header = isJa ? "黄 (Yellow)" : "Yellow";
            MenuColorWhite.Header = isJa ? "白 (White)" : "White";

            MenuGroupLanguage.Header = isJa ? "言語 (Language)" : "Language (言語)";
            MenuResetSettings.Header = isJa ? "設定をリセット..." : "Reset Settings...";
            MenuExit.Header = isJa ? "終了" : "Exit";

            // 言語選択のチェックマーク状態
            MenuLangJa.IsChecked = isJa;
            MenuLangEn.IsChecked = !isJa;

            // ツールチップなどの更新
            SettingsBtn.ToolTip = isJa
                ? "設定メニュー (クリックまたは右クリック)"
                : "Settings menu (Click or Right-click)";

            if (notifyIcon != null)
            {
                notifyIcon.Text = isJa
                    ? "Transquare (左クリックで表示/非表示)"
                    : "Transquare (Left-click to toggle)";
            }

            UpdateUIState();
        }

        // UI状態（カーソル形状・ロック時のグリップ表示・ツールチップ）の更新
        private void UpdateUIState()
        {
            // 移動可能なら十字カーソル、ロック時は通常矢印
            FrameBorder.Cursor = isMoveLocked ? Cursors.Arrow : Cursors.SizeAll;

            // サイズ変更ロック時は四角を半透明＆操作無効に
            if (ResizeGrip != null)
            {
                bool isJa = (currentLang == "ja");
                ResizeGrip.Opacity = isSizeLocked ? 0.35 : 1.0;
                ResizeGrip.Cursor = isSizeLocked ? Cursors.No : Cursors.SizeNWSE;
                ResizeGrip.ToolTip = isSizeLocked
                    ? (isJa ? "サイズロック中（設定メニューから解除できます）" : "Size locked (Unlock from settings menu)")
                    : (isJa ? "ドラッグしてサイズ変更（Altキーでグリッド吸着）" : "Drag to resize (Hold Alt for grid snap)");
            }
        }

        #endregion
    }
}
