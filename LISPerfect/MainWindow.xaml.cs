using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

// Aliases to disambiguate WPF types from Windows Forms equivalents
// (System.Windows.Forms is enabled for Screen and FolderBrowserDialog).
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using Media = System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace LISPerfect
{
    /// <summary>
    /// The main LISPerfect window. Coordinates the editor, REPL, project tree,
    /// tabs, SBCL subprocess, and all user-facing features.
    ///
    /// Sections:
    ///   1.  Fields, constants, and commands
    ///   2.  Construction and lifecycle
    ///   3.  Settings persistence
    ///   4.  SBCL subprocess management
    ///   5.  REPL communication (send, receive, filter)
    ///   6.  REPL introspection (query/response layer for autocomplete and jump)
    ///   7.  REPL input handling and history
    ///   8.  Tab management
    ///   9.  File operations (New/Open/Save/Recent Files)
    ///   10. Editor features (bracket matching, expression sending)
    ///   11. Run File
    ///   12. Autocomplete
    ///   13. Jump-to-definition
    ///   14. Project tree
    ///   15. Themes
    ///   16. Font zoom
    ///   17. Auto-save
    ///   18. Preferences and Tools menu
    ///   19. Quicklisp integration
    ///   20. Small helpers
    ///   21. Menu click handlers
    /// </summary>
    public partial class MainWindow : Window
    {
        // =====================================================================
        // 1. Fields, constants, and commands
        // =====================================================================

        // --- Core state ---
        private Process? _sbcl;
        private Settings _settings = new();
        private bool _settingsLoadedCleanly = true;
        private ParedItHandler? _paredItHandler;
        private bool _showingOwnDialog;

        // --- Tab state ---
        private readonly List<EditorTab> _tabs = new();
        private EditorTab? _currentTab;
        private bool _switchingTab;
        private EditorTab? _contextTab;

        // --- Autocomplete state ---
        private CompletionWindow? _completionWindow;

        // --- REPL history state ---
        private List<string> _replHistory = new();
        private int _replHistoryIndex = -1;

        // --- SBCL introspection state ---
        private int _nextQueryId = 1;
        private readonly Dictionary<int, TaskCompletionSource<string>> _pendingQueries = new();
        private int _capturingQueryId = -1;
        private readonly System.Text.StringBuilder _captureBuffer = new();

        // --- Project tree state ---
        private FileSystemWatcher? _projectWatcher;
        private readonly Dictionary<string, TreeViewItem> _treeItemsByPath =
            new(StringComparer.OrdinalIgnoreCase);

        // --- Auto-save state ---
        private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;

        // --- Font sizing constants ---
        private const double DefaultEditorFontSize = 14.0;
        private const double DefaultReplFontSize = 13.0;
        private const double MinFontSize = 8.0;
        private const double MaxFontSize = 40.0;

        // --- Persistent file paths ---
        private const int MaxRecentFiles = 10;
        private const int MaxReplHistory = 500;

        private static string RecentFilesPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LISPerfect", "recent-files.txt");

        private static string ReplHistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LISPerfect", "repl-history.txt");

        private static string QuicklispDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "quicklisp");

        private static string QuicklispSetup => Path.Combine(QuicklispDir, "setup.lisp");
        private static string QuicklispBootstrapUrl => "https://beta.quicklisp.org/quicklisp.lisp";

        private static string SkipQlInstallFlag => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LISPerfect", "skip-ql-install");

        // --- WPF commands (backing the keyboard shortcuts declared in XAML) ---
        public static readonly RoutedUICommand NewCommand = new("New", "New", typeof(MainWindow));
        public static readonly RoutedUICommand OpenCommand = new("Open", "Open", typeof(MainWindow));
        public static readonly RoutedUICommand SaveCommand = new("Save", "Save", typeof(MainWindow));
        public static readonly RoutedUICommand SaveAsCommand = new("SaveAs", "SaveAs", typeof(MainWindow));
        public static readonly RoutedUICommand ClearReplCommand = new("ClearRepl", "ClearRepl", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomInCommand = new("ZoomIn", "ZoomIn", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomOutCommand = new("ZoomOut", "ZoomOut", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomResetCommand = new("ZoomReset", "ZoomReset", typeof(MainWindow));
        public static readonly RoutedUICommand RunFileCommand = new("RunFile", "RunFile", typeof(MainWindow));
        public static readonly RoutedUICommand CompletionCommand = new("Completion", "Completion", typeof(MainWindow));
        public static readonly RoutedUICommand JumpToDefinitionCommand = new("JumpToDefinition", "JumpToDefinition", typeof(MainWindow));
        public static readonly RoutedUICommand NewTabCommand = new("NewTab", "NewTab", typeof(MainWindow));
        public static readonly RoutedUICommand CloseTabCommand = new("CloseTab", "CloseTab", typeof(MainWindow));
        public static readonly RoutedUICommand NextTabCommand = new("NextTab", "NextTab", typeof(MainWindow));
        public static readonly RoutedUICommand PrevTabCommand = new("PrevTab", "PrevTab", typeof(MainWindow));
        public static readonly RoutedUICommand GoToTabCommand = new("GoToTab", "GoToTab", typeof(MainWindow));


        // =====================================================================
        // 2. Construction and lifecycle
        // =====================================================================

        public MainWindow()
        {
            InitializeComponent();

            // Settings must load before any UI initialization that reads them.
            _settings = SettingsManager.Load(out _settingsLoadedCleanly);
            LoadReplHistory();
            ApplySettingsToWindow();

            // Window-level events
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            Deactivated += MainWindow_Deactivated;

            // Input handling
            ReplInput.PreviewKeyDown += ReplInput_KeyDown;
            Editor.PreviewKeyDown += Editor_PreviewKeyDown;

            // Editor features
            SetupBracketMatching();
            _paredItHandler = new ParedItHandler(Editor, _settings);
            ApplyTheme(_settings.Theme);

            // Tab dirty tracking. Guard against reacting during tab-switch text swaps.
            Editor.TextChanged += (s, e) =>
            {
                if (_switchingTab || _currentTab == null) return;
                if (!_currentTab.IsDirty)
                {
                    _currentTab.IsDirty = true;
                    UpdateTitle();
                    UpdateTabHeader(_currentTab);
                }
            };

            // Create the initial welcome tab.
            NewTab(";; Welcome to LISPerfect\n;; Write your Common Lisp code here\n\n(defun hello ()\n  (format t \"Hello, world!~%\"))\n");
            _currentTab!.IsDirty = false;
            UpdateTitle();
            UpdateTabHeader(_currentTab);

            RegisterCommandBindings();

            UpdateTitle();
            ReplInput.Focus();
        }

        private void RegisterCommandBindings()
        {
            CommandBindings.Add(new CommandBinding(NewCommand, (s, e) => DoNew()));
            CommandBindings.Add(new CommandBinding(OpenCommand, (s, e) => DoOpen()));
            CommandBindings.Add(new CommandBinding(SaveCommand, (s, e) => DoSave()));
            CommandBindings.Add(new CommandBinding(SaveAsCommand, (s, e) => DoSaveAs()));
            CommandBindings.Add(new CommandBinding(ClearReplCommand, (s, e) => ClearRepl()));
            CommandBindings.Add(new CommandBinding(ZoomInCommand, (s, e) => AdjustFontSize(+1)));
            CommandBindings.Add(new CommandBinding(ZoomOutCommand, (s, e) => AdjustFontSize(-1)));
            CommandBindings.Add(new CommandBinding(ZoomResetCommand, (s, e) => ResetFontSize()));
            CommandBindings.Add(new CommandBinding(RunFileCommand, (s, e) => RunFile()));
            CommandBindings.Add(new CommandBinding(CompletionCommand, (s, e) => ShowCompletion()));
            CommandBindings.Add(new CommandBinding(JumpToDefinitionCommand, (s, e) => JumpToDefinition()));

            CommandBindings.Add(new CommandBinding(NewTabCommand, (s, e) => NewTab()));
            CommandBindings.Add(new CommandBinding(CloseTabCommand, (s, e) => CloseCurrentTab()));
            CommandBindings.Add(new CommandBinding(NextTabCommand, (s, e) => CycleTab(+1)));
            CommandBindings.Add(new CommandBinding(PrevTabCommand, (s, e) => CycleTab(-1)));
            CommandBindings.Add(new CommandBinding(GoToTabCommand, (s, e) =>
            {
                if (e.Parameter is string s2 && int.TryParse(s2, out int idx)) GoToTab(idx);
            }));
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplySplitterPosition();
            StartSbcl();

            if (!string.IsNullOrEmpty(_settings.ProjectFolder) &&
                Directory.Exists(_settings.ProjectFolder))
            {
                LoadProjectFolder(_settings.ProjectFolder);
            }

            await Task.Delay(500);
            await MaybeOfferQuicklispInstall();
            ConfigureReplForQuicklisp();
            SetupAutoSaveTimer();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            foreach (var tab in _tabs)
            {
                if (!tab.IsDirty) continue;

                var item = FindTabItem(tab);
                if (item != null) TabBar.SelectedItem = item;

                _showingOwnDialog = true;
                MessageBoxResult result;
                try
                {
                    result = MessageBox.Show(
                        $"Save changes to {tab.DisplayName.TrimStart('*')} before closing?",
                        "LISPerfect",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning);
                }
                finally { _showingOwnDialog = false; }

                if (result == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (result == MessageBoxResult.Yes && !SaveTab(tab)) { e.Cancel = true; return; }
            }

            if (_settingsLoadedCleanly)
            {
                CaptureWindowState();
                SettingsManager.Save(_settings);
            }
            if (_projectWatcher != null)
            {
                _projectWatcher.EnableRaisingEvents = false;
                _projectWatcher.Dispose();
            }
            StopSbcl();
        }


        // =====================================================================
        // 3. Settings persistence
        // =====================================================================

        private void ApplySettingsToWindow()
        {
            Editor.FontSize = _settings.EditorFontSize;
            ReplOutput.FontSize = _settings.ReplFontSize;
            ReplInput.FontSize = _settings.ReplFontSize;

            if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue &&
                IsPositionOnScreen(_settings.WindowLeft.Value, _settings.WindowTop.Value))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft.Value;
                Top = _settings.WindowTop.Value;
            }

            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;

            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

            if (_settings.ProjectTreeWidth > 0)
                ProjectColumn.Width = new GridLength(_settings.ProjectTreeWidth);

            ShowOnlyLispMenuItem.IsChecked = _settings.ShowOnlyLispFiles;
        }

        private void CaptureWindowState()
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }

            _settings.EditorFontSize = Editor.FontSize;
            _settings.ReplFontSize = ReplOutput.FontSize;

            // Layout: [project 0][splitter 1][editor 2][splitter 3][repl 4]
            var mainGrid = (Grid)((Grid)Content).Children[1];
            double editorWidth = mainGrid.ColumnDefinitions[2].ActualWidth;
            double replWidth = mainGrid.ColumnDefinitions[4].ActualWidth;
            double total = editorWidth + replWidth;
            if (total > 0) _settings.EditorColumnFraction = editorWidth / total;

            _settings.ProjectTreeWidth = ProjectColumn.ActualWidth;
        }

        private void ApplySplitterPosition()
        {
            var mainGrid = (Grid)((Grid)Content).Children[1];
            double fraction = _settings.EditorColumnFraction;
            if (fraction < 0.1) fraction = 0.1;
            if (fraction > 0.9) fraction = 0.9;
            mainGrid.ColumnDefinitions[2].Width = new GridLength(fraction, GridUnitType.Star);
            mainGrid.ColumnDefinitions[4].Width = new GridLength(1 - fraction, GridUnitType.Star);
        }

        private static bool IsPositionOnScreen(double left, double top)
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var bounds = screen.Bounds;
                if (left >= bounds.Left - 100 && left <= bounds.Right - 100 &&
                    top >= bounds.Top && top <= bounds.Bottom - 100)
                {
                    return true;
                }
            }
            return false;
        }


        // =====================================================================
        // 4. SBCL subprocess management
        // =====================================================================

        private void StartSbcl()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ResolveSbclPath(),
                    Arguments = "--no-sysinit --no-userinit",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _sbcl = new Process { StartInfo = psi, EnableRaisingEvents = true };

                _sbcl.OutputDataReceived += (s, ev) =>
                {
                    if (ev.Data == null) return;
                    Dispatcher.Invoke(() =>
                    {
                        if (TryInterceptQueryOutput(ev.Data)) return;
                        string? filtered = FilterSbclOutput(ev.Data);
                        if (filtered != null) AppendToRepl(filtered + Environment.NewLine);
                    });
                };
                _sbcl.ErrorDataReceived += (s, ev) =>
                {
                    if (ev.Data != null) AppendToRepl(ev.Data + Environment.NewLine);
                };
                _sbcl.Exited += (s, ev) =>
                {
                    AppendToRepl(Environment.NewLine + ";; SBCL process exited." + Environment.NewLine);
                };

                _sbcl.Start();
                _sbcl.BeginOutputReadLine();
                _sbcl.BeginErrorReadLine();

                // Install a debugger hook so errors print and return to REPL rather
                // than opening SBCL's interactive debugger.
                const string errorHandler = @"
(progn
  (setf *debugger-hook*
        (lambda (condition hook)
          (declare (ignore hook))
          (format *error-output* ""~&;; Error: ~A~%"" condition)
          (abort)))
  (values))
";
                _sbcl.StandardInput.WriteLine(errorHandler);
                _sbcl.StandardInput.Flush();

                // Load sb-introspect for jump-to-definition support.
                _sbcl.StandardInput.WriteLine("(progn (require :sb-introspect) (values))");
                _sbcl.StandardInput.Flush();

                StatusText.Text = "SBCL running";
            }
            catch (Exception ex)
            {
                AppendToRepl($";; Failed to start SBCL: {ex.Message}{Environment.NewLine}");
                StatusText.Text = "SBCL failed to start";
            }
        }

        private void StopSbcl()
        {
            if (_sbcl == null) return;
            try
            {
                if (!_sbcl.HasExited)
                {
                    try
                    {
                        _sbcl.StandardInput.WriteLine("(quit)");
                        _sbcl.StandardInput.Flush();
                        _sbcl.StandardInput.Close();
                    }
                    catch { }

                    if (!_sbcl.WaitForExit(1000))
                    {
                        try { _sbcl.Kill(entireProcessTree: true); } catch { }
                        _sbcl.WaitForExit(2000);
                    }
                }
            }
            catch { }
            try { _sbcl.Dispose(); } catch { }
            _sbcl = null;
        }

        /// <summary>
        /// Prefer a bundled sbcl.exe next to our own exe. Falls back to system PATH.
        /// </summary>
        private static string ResolveSbclPath()
        {
            string? exeDir = Path.GetDirectoryName(
                Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location);
            if (exeDir != null)
            {
                string bundled = Path.Combine(exeDir, "sbcl", "sbcl.exe");
                if (File.Exists(bundled)) return bundled;
            }
            return "sbcl";
        }


        // =====================================================================
        // 5. REPL communication
        // =====================================================================

        private void AppendToRepl(string text)
        {
            Dispatcher.Invoke(() =>
            {
                ReplOutput.AppendText(text);
                ReplOutput.ScrollToEnd();
            });
        }

        private void SendToSbcl(string code)
        {
            if (_sbcl == null || _sbcl.HasExited)
            {
                AppendToRepl(";; SBCL is not running." + Environment.NewLine);
                return;
            }

            AppendToRepl("> " + code + Environment.NewLine);
            try
            {
                _sbcl.StandardInput.WriteLine(code);
                _sbcl.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                AppendToRepl($";; Send failed: {ex.Message}{Environment.NewLine}");
            }
        }

        /// <summary>
        /// Filters SBCL's stdout to hide interactive REPL prompts. Returns null to
        /// suppress a line entirely. Handles accumulated prompts like "* * 3".
        /// </summary>
        private static string? FilterSbclOutput(string line)
        {
            while (line.StartsWith("* ") || line == "*" || line == "* ")
            {
                if (line == "*" || line == "* ") return null;
                line = line.Substring(2);
            }
            if (string.IsNullOrEmpty(line)) return null;
            return line;
        }


        // =====================================================================
        // 6. REPL introspection
        // =====================================================================

        /// <summary>
        /// Ask SBCL a question and return its response, or null on timeout.
        /// The query should be a Lisp expression that produces one printable value.
        /// </summary>
        private async Task<string?> QuerySbcl(string lispExpression, int timeoutMs = 3000)
        {
            if (_sbcl == null || _sbcl.HasExited) return null;

            int queryId = _nextQueryId++;
            var tcs = new TaskCompletionSource<string>();
            _pendingQueries[queryId] = tcs;

            // Wrap the query so its answer is bracketed by our markers, with error handling
            // and compiler-warning suppression. handler-case ensures errors don't kill the
            // query mid-flight.
            string wrapped =
                $"(progn " +
                $"  (locally " +
                $"    (declare (sb-ext:muffle-conditions cl:warning cl:style-warning)) " +
                $"    (handler-case " +
                $"      (progn " +
                $"        (format t \"~%<<LISPERFECT-ANSWER {queryId} BEGIN>>~%\") " +
                $"        (format t \"~S\" {lispExpression}) " +
                $"        (format t \"~%<<LISPERFECT-ANSWER {queryId} END>>~%\")) " +
                $"      (error (c) " +
                $"        (declare (ignore c)) " +
                $"        (format t \"~%<<LISPERFECT-ANSWER {queryId} BEGIN>>~%NIL~%<<LISPERFECT-ANSWER {queryId} END>>~%\")))) " +
                $"  (values))";

            try
            {
                _sbcl.StandardInput.WriteLine(wrapped);
                _sbcl.StandardInput.Flush();
            }
            catch
            {
                _pendingQueries.Remove(queryId);
                return null;
            }

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            _pendingQueries.Remove(queryId);
            if (completedTask == tcs.Task) return await tcs.Task;
            return null;
        }

        /// <summary>
        /// Intercepts SBCL output lines for query markers. Returns true if the line
        /// was consumed and should not be displayed.
        /// </summary>
        private bool TryInterceptQueryOutput(string line)
        {
            string stripped = line;
            while (stripped.StartsWith("* ")) stripped = stripped.Substring(2);
            stripped = stripped.Trim();

            if (stripped.StartsWith("<<LISPERFECT-ANSWER ") && stripped.EndsWith(" BEGIN>>"))
            {
                string idPart = stripped.Substring(20);
                idPart = idPart.Substring(0, idPart.Length - 8);
                if (int.TryParse(idPart, out int id))
                {
                    _capturingQueryId = id;
                    _captureBuffer.Clear();
                    return true;
                }
            }

            if (stripped.StartsWith("<<LISPERFECT-ANSWER ") && stripped.EndsWith(" END>>"))
            {
                string idPart = stripped.Substring(20);
                idPart = idPart.Substring(0, idPart.Length - 6);
                if (int.TryParse(idPart, out int id))
                {
                    if (id == _capturingQueryId && _pendingQueries.TryGetValue(id, out var tcs))
                    {
                        string result = _captureBuffer.ToString().Trim();
                        tcs.TrySetResult(result);
                    }
                    _capturingQueryId = -1;
                    _captureBuffer.Clear();
                    return true;
                }
            }

            if (_capturingQueryId != -1)
            {
                _captureBuffer.AppendLine(stripped);
                return true;
            }

            return false;
        }


        // =====================================================================
        // 7. REPL input handling and history
        // =====================================================================

        private void ReplInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                var code = ReplInput.Text;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    AddToReplHistory(code);
                    SendToSbcl(code);
                    ReplInput.Clear();
                    _replHistoryIndex = -1;
                }
            }
            else if (e.Key == Key.Up && _replHistory.Count > 0)
            {
                e.Handled = true;
                if (_replHistoryIndex + 1 < _replHistory.Count)
                {
                    _replHistoryIndex++;
                    ReplInput.Text = _replHistory[_replHistoryIndex];
                    ReplInput.CaretIndex = ReplInput.Text.Length;
                }
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (_replHistoryIndex > 0)
                {
                    _replHistoryIndex--;
                    ReplInput.Text = _replHistory[_replHistoryIndex];
                    ReplInput.CaretIndex = ReplInput.Text.Length;
                }
                else if (_replHistoryIndex == 0)
                {
                    _replHistoryIndex = -1;
                    ReplInput.Clear();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _replHistoryIndex = -1;
                ReplInput.Clear();
            }
        }

        private void LoadReplHistory()
        {
            try
            {
                if (!File.Exists(ReplHistoryPath))
                {
                    _replHistory = new List<string>();
                    return;
                }
                _replHistory = File.ReadAllLines(ReplHistoryPath)
                    .Select(DecodeHistoryLine)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            catch
            {
                _replHistory = new List<string>();
            }
        }

        private void SaveReplHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReplHistoryPath)!);
                File.WriteAllLines(ReplHistoryPath,
                    _replHistory.Take(MaxReplHistory).Select(EncodeHistoryLine));
            }
            catch { }
        }

        // Multi-line history entries live on one file line with \n escaped.
        private static string EncodeHistoryLine(string entry) =>
            entry.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "");

        private static string DecodeHistoryLine(string line)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    char next = line[i + 1];
                    if (next == 'n') { sb.Append('\n'); i++; continue; }
                    if (next == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(line[i]);
            }
            return sb.ToString();
        }

        private void AddToReplHistory(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;
            if (_replHistory.Count > 0 && _replHistory[0] == entry) return;

            _replHistory.Insert(0, entry);
            if (_replHistory.Count > MaxReplHistory)
                _replHistory.RemoveRange(MaxReplHistory, _replHistory.Count - MaxReplHistory);

            SaveReplHistory();
        }


        // =====================================================================
        // 8. Tab management
        // =====================================================================

        private EditorTab NewTab(string initialText = "")
        {
            var tab = new EditorTab(initialText: initialText);
            _tabs.Add(tab);

            var tabItem = new TabItem
            {
                Header = BuildTabHeader(tab),
                Tag = tab
            };
            TabBar.Items.Add(tabItem);
            TabBar.SelectedItem = tabItem;
            UpdateEmptyState();
            return tab;
        }

        private StackPanel BuildTabHeader(EditorTab tab)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(new TextBlock
            {
                Text = tab.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            var closeButton = new Button
            {
                Content = "×",
                Width = 16,
                Height = 16,
                Padding = new Thickness(0),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Close tab",
                Tag = tab
            };
            closeButton.Click += TabCloseButton_Click;
            panel.Children.Add(closeButton);

            return panel;
        }

        private void TabCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is EditorTab tab)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        }

        private void SwitchToTab(EditorTab tab)
        {
            if (_currentTab == tab) return;

            if (_currentTab != null)
            {
                _currentTab.SavedCaretOffset = Editor.CaretOffset;
                _currentTab.SavedVerticalOffset = Editor.VerticalOffset;
            }

            _switchingTab = true;
            Editor.Document = tab.Document;
            _switchingTab = false;

            if (tab.SavedCaretOffset <= tab.Document.TextLength)
                Editor.CaretOffset = tab.SavedCaretOffset;
            Editor.ScrollToVerticalOffset(tab.SavedVerticalOffset);

            _currentTab = tab;

            Dispatcher.BeginInvoke(new Action(() => Editor.Focus()),
                System.Windows.Threading.DispatcherPriority.Loaded);

            UpdateTitle();
        }

        private void CloseCurrentTab()
        {
            if (_currentTab == null) return;
            CloseTab(_currentTab);
        }

        private void CloseTab(EditorTab tab)
        {
            if (tab.IsDirty)
            {
                var itemToShow = FindTabItem(tab);
                if (itemToShow != null) TabBar.SelectedItem = itemToShow;

                _showingOwnDialog = true;
                MessageBoxResult result;
                try
                {
                    result = MessageBox.Show(
                        $"Save changes to {tab.DisplayName.TrimStart('*')}?",
                        "LISPerfect",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning);
                }
                finally { _showingOwnDialog = false; }

                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes && !SaveTab(tab)) return;
            }

            int index = _tabs.IndexOf(tab);
            var tabItem = FindTabItem(tab);
            _tabs.Remove(tab);
            if (tabItem != null) TabBar.Items.Remove(tabItem);

            if (_tabs.Count == 0)
            {
                // Truly empty state — no current tab, overlay shown, editor hidden.
                _currentTab = null;
                UpdateTitle();
                UpdateEmptyState();
            }
            else
            {
                int newIndex = index >= _tabs.Count ? _tabs.Count - 1 : index;
                TabBar.SelectedIndex = newIndex;
            }
        }

        private TabItem? FindTabItem(EditorTab tab)
        {
            foreach (TabItem item in TabBar.Items)
                if (item.Tag == tab) return item;
            return null;
        }

        private void UpdateTabHeader(EditorTab tab)
        {
            var item = FindTabItem(tab);
            if (item != null) item.Header = BuildTabHeader(tab);
        }

        private void CycleTab(int direction)
        {
            if (_tabs.Count == 0) return;
            int current = TabBar.SelectedIndex;
            int next = (current + direction + _tabs.Count) % _tabs.Count;
            TabBar.SelectedIndex = next;
        }

        private void GoToTab(int index)
        {
            if (index >= 0 && index < _tabs.Count) TabBar.SelectedIndex = index;
        }

        private void TabBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabBar.SelectedItem is TabItem item && item.Tag is EditorTab tab)
                SwitchToTab(tab);
        }

        private void TabBar_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _contextTab = _currentTab;
            if (e.OriginalSource is DependencyObject src)
            {
                var item = FindAncestor<TabItem>(src);
                if (item?.Tag is EditorTab tab) _contextTab = tab;
            }
        }

        private void UpdateTitle()
        {
            Title = _currentTab == null
                ? "LISPerfect"
                : $"{_currentTab.DisplayName} — LISPerfect";
        }

        /// <summary>
        /// Shows the "No file open" overlay when no tabs are open; hides it otherwise.
        /// </summary>
        private void UpdateEmptyState()
        {
            if (_tabs.Count == 0)
            {
                EmptyStateOverlay.Visibility = Visibility.Visible;
                Editor.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyStateOverlay.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
            }
        }


        // =====================================================================
        // 9. File operations
        // =====================================================================

        private void DoNew()
        {
            NewTab();
            StatusText.Text = "New tab";
        }

        private void DoOpen()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Lisp files (*.lisp;*.lsp;*.cl;*.asd)|*.lisp;*.lsp;*.cl;*.asd|All files (*.*)|*.*",
                Title = "Open Lisp file"
            };
            if (dlg.ShowDialog() == true) OpenFileInTab(dlg.FileName);
        }

        private void OpenFileInTab(string path)
        {
            // Normalize the path so forward-slashes (Lisp) and backslashes (Windows)
            // compare equal — jump-to-definition passes forward-slashed paths.
            string normalizedPath;
            try { normalizedPath = Path.GetFullPath(path); }
            catch { normalizedPath = path; }

            foreach (var existing in _tabs)
            {
                if (existing.FilePath != null &&
                    string.Equals(Path.GetFullPath(existing.FilePath), normalizedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var item = FindTabItem(existing);
                    if (item != null) TabBar.SelectedItem = item;
                    StatusText.Text = $"Switched to {existing.FilePath}";
                    return;
                }
            }

            path = normalizedPath;

            EditorTab targetTab;
            if (_currentTab != null && _currentTab.FilePath == null && !_currentTab.IsDirty &&
                _currentTab.Document.TextLength == 0)
            {
                targetTab = _currentTab;
            }
            else
            {
                targetTab = NewTab();
            }

            try
            {
                string text = File.ReadAllText(path);
                _switchingTab = true;
                targetTab.Document.Text = text;
                _switchingTab = false;
                targetTab.FilePath = path;
                targetTab.IsDirty = false;
                UpdateTabHeader(targetTab);
                UpdateTitle();
                AddToRecentFiles(path);
                StatusText.Text = $"Opened {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool DoSave() => _currentTab != null && SaveTab(_currentTab);
        private bool DoSaveAs() => _currentTab != null && SaveTabAs(_currentTab);

        private bool SaveTab(EditorTab tab)
        {
            if (tab.FilePath == null) return SaveTabAs(tab);
            try
            {
                File.WriteAllText(tab.FilePath, tab.Document.Text);
                tab.IsDirty = false;
                UpdateTabHeader(tab);
                UpdateTitle();
                StatusText.Text = $"Saved {tab.FilePath}";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save file: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private bool SaveTabAs(EditorTab tab)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Lisp files (*.lisp)|*.lisp|All files (*.*)|*.*",
                Title = "Save Lisp file",
                DefaultExt = ".lisp",
                FileName = tab.FilePath != null ? Path.GetFileName(tab.FilePath) : "untitled.lisp"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, tab.Document.Text);
                    tab.FilePath = dlg.FileName;
                    tab.IsDirty = false;
                    UpdateTabHeader(tab);
                    UpdateTitle();
                    AddToRecentFiles(dlg.FileName);
                    StatusText.Text = $"Saved {dlg.FileName}";
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not save file: {ex.Message}", "LISPerfect",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return false;
        }

        // --- Recent Files ---

        private static List<string> LoadRecentFiles()
        {
            try
            {
                if (!File.Exists(RecentFilesPath)) return new List<string>();
                return File.ReadAllLines(RecentFilesPath)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Take(MaxRecentFiles)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        private static void SaveRecentFiles(IEnumerable<string> files)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecentFilesPath)!);
                File.WriteAllLines(RecentFilesPath, files.Take(MaxRecentFiles));
            }
            catch { }
        }

        private static void AddToRecentFiles(string path)
        {
            var recent = LoadRecentFiles();
            recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, path);
            SaveRecentFiles(recent);
        }

        private void RecentFilesMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            RecentFilesMenu.Items.Clear();
            var recent = LoadRecentFiles();

            if (recent.Count == 0)
            {
                RecentFilesMenu.Items.Add(new MenuItem { Header = "(no recent files)", IsEnabled = false });
                return;
            }

            int index = 1;
            foreach (var path in recent)
            {
                string display = path.Length > 60 ? $"...{path.Substring(path.Length - 57)}" : path;
                var item = new MenuItem
                {
                    Header = $"_{index} {display}",
                    Tag = path,
                    ToolTip = path
                };
                item.Click += RecentFile_Click;
                RecentFilesMenu.Items.Add(item);
                index++;
            }

            RecentFilesMenu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear Recent Files" };
            clear.Click += (s, ev) =>
            {
                SaveRecentFiles(new List<string>());
                StatusText.Text = "Recent files cleared";
            };
            RecentFilesMenu.Items.Add(clear);
        }

        private void RecentFile_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            if (item.Tag is not string path) return;

            if (!File.Exists(path))
            {
                var result = MessageBox.Show(
                    $"File no longer exists:\n{path}\n\nRemove from recent files?",
                    "LISPerfect", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    var recent = LoadRecentFiles();
                    recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    SaveRecentFiles(recent);
                }
                return;
            }

            OpenFileInTab(path);
        }


        // =====================================================================
        // 10. Editor features
        // =====================================================================

        private void SetupBracketMatching()
        {
            var renderer = new BracketRenderer(Editor);
            Editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
            Editor.TextArea.Caret.PositionChanged += (s, e) =>
                Editor.TextArea.TextView.InvalidateLayer(
                    ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                SendCurrentExpression();
            }
        }

        private void SendCurrentExpression()
        {
            if (_currentTab == null)
            {
                StatusText.Text = "No file open";
                return;
            }

            string text = Editor.Text;
            int caret = Editor.CaretOffset;

            var (start, end) = FindEnclosingTopLevel(text, caret);
            if (start < 0)
            {
                StatusText.Text = "No expression at cursor";
                return;
            }

            string code = text.Substring(start, end - start).Trim();
            if (code.Length == 0)
            {
                StatusText.Text = "Empty expression";
                return;
            }

            SendToSbcl(code);
            StatusText.Text = $"Sent {code.Length} chars to REPL";
        }

        /// <summary>
        /// Find the top-level form containing or nearest before the given offset.
        /// Correctly handles strings and line/block comments.
        /// </summary>
        private static (int start, int end) FindEnclosingTopLevel(string text, int offset)
        {
            if (string.IsNullOrEmpty(text)) return (-1, -1);
            if (offset > text.Length) offset = text.Length;

            var ranges = new List<(int s, int e)>();
            int depth = 0;
            int currentStart = -1;
            bool inString = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char prev = i > 0 ? text[i - 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '#' && prev == '|') inBlockComment = false;
                    continue;
                }
                if (inString)
                {
                    if (c == '"' && prev != '\\') inString = false;
                    continue;
                }
                if (c == ';') { inLineComment = true; continue; }
                if (c == '|' && prev == '#') { inBlockComment = true; continue; }
                if (c == '"') { inString = true; continue; }

                if (c == '(')
                {
                    if (depth == 0) currentStart = i;
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0 && currentStart >= 0)
                    {
                        ranges.Add((currentStart, i + 1));
                        currentStart = -1;
                    }
                }
            }

            foreach (var r in ranges)
                if (offset >= r.s && offset <= r.e) return r;

            (int s, int e) best = (-1, -1);
            foreach (var r in ranges)
                if (r.e <= offset && r.e > best.e) best = r;
            return best;
        }


        // =====================================================================
        // 11. Run File
        // =====================================================================

        private void RunFile()
        {
            if (_sbcl == null || _sbcl.HasExited)
            {
                StatusText.Text = "SBCL is not running";
                return;
            }

            if (_currentTab == null)
            {
                StatusText.Text = "No file open";
                return;
            }

            string text = Editor.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "Nothing to run — buffer is empty";
                return;
            }

            string pathToLoad;
            string labelForStatus;

            // If the tab has a saved file, load from the real path so SBCL records the
            // true source — makes F12 jump-to-definition land in the user's own file.
            if (_currentTab.FilePath != null && !_currentTab.IsDirty)
            {
                pathToLoad = _currentTab.FilePath;
                labelForStatus = Path.GetFileName(_currentTab.FilePath);
            }
            else if (_currentTab.FilePath != null && _currentTab.IsDirty)
            {
                // Save first so we can load from the real path.
                if (SaveTab(_currentTab))
                {
                    pathToLoad = _currentTab.FilePath;
                    labelForStatus = Path.GetFileName(_currentTab.FilePath);
                }
                else
                {
                    try { pathToLoad = WriteBufferToTempFile(text); }
                    catch (Exception ex)
                    {
                        AppendToRepl($";; Could not write buffer to temp file: {ex.Message}{Environment.NewLine}");
                        StatusText.Text = "Run failed";
                        return;
                    }
                    labelForStatus = "buffer (temp file)";
                }
            }
            else
            {
                // Untitled tab — temp file only.
                try { pathToLoad = WriteBufferToTempFile(text); }
                catch (Exception ex)
                {
                    AppendToRepl($";; Could not write buffer to temp file: {ex.Message}{Environment.NewLine}");
                    StatusText.Text = "Run failed";
                    return;
                }
                labelForStatus = "buffer (temp file)";
            }

            string lispPath = pathToLoad.Replace("\\", "/");
            string command = $"(load \"{lispPath}\")";

            AppendToRepl(";; --- Running file ---" + Environment.NewLine);
            SendToSbcl(command);
            StatusText.Text = $"Loaded {labelForStatus}";
        }

        private static string WriteBufferToTempFile(string text)
        {
            string dir = Path.Combine(SettingsManager.SettingsDir, "temp");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "run-buffer.lisp");
            File.WriteAllText(path, text);
            return path;
        }


        // =====================================================================
        // 12. Autocomplete
        // =====================================================================

        private async void ShowCompletion()
        {
            if (_completionWindow != null) return;

            var (prefix, startOffset) = GetCurrentSymbolPrefix();
            if (string.IsNullOrEmpty(prefix)) return;

            StatusText.Text = "Finding completions...";
            var suggestions = await GetCompletionsForPrefix(prefix);

            if (suggestions.Count == 0)
            {
                StatusText.Text = "No completions";
                return;
            }

            _completionWindow = new CompletionWindow(Editor.TextArea)
            {
                StartOffset = startOffset,
                Width = 300
            };
            _completionWindow.CompletionList.ListBox.HorizontalContentAlignment =
                HorizontalAlignment.Stretch;

            var data = _completionWindow.CompletionList.CompletionData;
            foreach (var suggestion in suggestions)
                data.Add(new SimpleCompletionData(suggestion, ""));

            _completionWindow.Show();
            _completionWindow.Closed += (s, e) =>
            {
                _completionWindow = null;
                Editor.PreviewMouseDown -= Editor_PreviewMouseDown_DismissCompletion;
            };
            Editor.PreviewMouseDown += Editor_PreviewMouseDown_DismissCompletion;

            StatusText.Text = $"{suggestions.Count} completions";
        }

        private void Editor_PreviewMouseDown_DismissCompletion(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _completionWindow?.Close();
        }

        /// <summary>
        /// Query SBCL for symbols matching a prefix. Returns up to 100 symbol names.
        /// Note: sort is destructive in CL; we must assign the result back to matches
        /// before measuring its length (a classic Lisp footgun).
        /// </summary>
        private async Task<List<string>> GetCompletionsForPrefix(string prefix)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(prefix)) return results;

            string upperPrefix = prefix.ToUpperInvariant().Replace("\"", "\\\"");
            string query =
                "(let ((matches '()) " +
                $"      (prefix \"{upperPrefix}\")) " +
                "  (do-external-symbols (sym (find-package :cl)) " +
                "    (let ((name (symbol-name sym))) " +
                "      (when (and (>= (length name) (length prefix)) " +
                "                 (string-equal (subseq name 0 (length prefix)) prefix)) " +
                "        (pushnew (string-downcase name) matches :test #'string=)))) " +
                "  (do-symbols (sym (find-package :cl-user)) " +
                "    (when (eql (symbol-package sym) (find-package :cl-user)) " +
                "      (let ((name (symbol-name sym))) " +
                "        (when (and (>= (length name) (length prefix)) " +
                "                   (string-equal (subseq name 0 (length prefix)) prefix)) " +
                "          (pushnew (string-downcase name) matches :test #'string=))))) " +
                "  (setf matches (sort matches #'string<)) " +
                "  (subseq matches 0 (min 100 (length matches))))";

            string? response = await QuerySbcl(query, timeoutMs: 2000);
            if (response == null) return results;

            return ParseLispStringList(response);
        }

        /// <summary>
        /// Parse a Lisp list of strings like ("foo" "bar" "baz") into a C# list.
        /// </summary>
        private static List<string> ParseLispStringList(string response)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(response)) return result;

            int i = 0;
            while (i < response.Length)
            {
                if (response[i] == '"')
                {
                    var sb = new System.Text.StringBuilder();
                    i++;
                    while (i < response.Length && response[i] != '"')
                    {
                        if (response[i] == '\\' && i + 1 < response.Length)
                        {
                            sb.Append(response[i + 1]);
                            i += 2;
                        }
                        else { sb.Append(response[i]); i++; }
                    }
                    result.Add(sb.ToString());
                    i++;
                }
                else i++;
            }
            return result;
        }

        /// <summary>
        /// Get the symbol prefix to the left of the cursor.
        /// </summary>
        private (string prefix, int startOffset) GetCurrentSymbolPrefix()
        {
            int caret = Editor.CaretOffset;
            int start = caret;
            while (start > 0)
            {
                char c = Editor.Document.GetCharAt(start - 1);
                if (IsSymbolChar(c)) start--;
                else break;
            }
            return (Editor.Document.GetText(start, caret - start), start);
        }


        // =====================================================================
        // 13. Jump-to-definition
        // =====================================================================

        private async void JumpToDefinition()
        {
            string? symbol = GetSymbolAtCursor();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                StatusText.Text = "No symbol at cursor";
                return;
            }

            StatusText.Text = $"Finding definition of {symbol}...";
            var location = await GetDefinitionLocation(symbol);
            if (location == null)
            {
                StatusText.Text = $"No definition found for {symbol}";
                MessageBox.Show(
                    $"Could not find a definition for '{symbol}'.\n\n" +
                    $"This can happen if the symbol is built into SBCL, defined in a package " +
                    $"not currently loaded, or has no source information available.",
                    "LISPerfect", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenFileAtForm(location.Value.filePath, location.Value.formIndex);
            StatusText.Text = $"Jumped to {symbol}";
        }

        /// <summary>
        /// Ask SBCL for the source location of a symbol. Handles package-qualified
        /// forms like "alexandria:iota" or "some-package::internal-name".
        /// </summary>
        private async Task<(string filePath, int formIndex)?> GetDefinitionLocation(string symbolName)
        {
            string packageName;
            string bareName;

            int doubleColon = symbolName.IndexOf("::", StringComparison.Ordinal);
            int singleColon = symbolName.IndexOf(':');

            if (doubleColon >= 0)
            {
                packageName = symbolName.Substring(0, doubleColon);
                bareName = symbolName.Substring(doubleColon + 2);
            }
            else if (singleColon >= 0)
            {
                packageName = symbolName.Substring(0, singleColon);
                bareName = symbolName.Substring(singleColon + 1);
            }
            else
            {
                packageName = "";
                bareName = symbolName;
            }

            string escapedName = bareName.ToUpperInvariant().Replace("\"", "\\\"");
            string escapedPkg = packageName.ToUpperInvariant().Replace("\"", "\\\"");

            string packageSearch = string.IsNullOrEmpty(packageName)
                ? $"(or (find-symbol \"{escapedName}\" (find-package :cl-user)) " +
                  $"    (find-symbol \"{escapedName}\" (find-package :cl)))"
                : $"(let ((pkg (find-package \"{escapedPkg}\"))) " +
                  $"  (when pkg (find-symbol \"{escapedName}\" pkg)))";

            string query =
                $"(let* ((sym {packageSearch})) " +
                "  (if sym " +
                "      (let ((sources (sb-introspect:find-definition-sources-by-name sym :function))) " +
                "        (if sources " +
                "            (let* ((s (first sources)) " +
                "                   (path (sb-introspect:definition-source-pathname s)) " +
                "                   (form-path (sb-introspect:definition-source-form-path s))) " +
                "              (if (and path form-path) " +
                "                  (list :file (namestring path) :form (first form-path)) " +
                "                  nil)) " +
                "            nil)) " +
                "      nil))";

            string? response = await QuerySbcl(query, timeoutMs: 3000);
            if (response == null || response.Trim() == "NIL") return null;

            string? filePath = ExtractLispStringAfter(response, ":FILE");
            int? formIndex = ExtractLispIntAfter(response, ":FORM");

            if (filePath == null || formIndex == null) return null;
            return (filePath, formIndex.Value);
        }

        private static string? ExtractLispStringAfter(string text, string tag)
        {
            int idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx = text.IndexOf('"', idx);
            if (idx < 0) return null;
            idx++;
            var sb = new System.Text.StringBuilder();
            while (idx < text.Length && text[idx] != '"')
            {
                if (text[idx] == '\\' && idx + 1 < text.Length)
                {
                    sb.Append(text[idx + 1]);
                    idx += 2;
                }
                else { sb.Append(text[idx]); idx++; }
            }
            return sb.ToString();
        }

        private static int? ExtractLispIntAfter(string text, string tag)
        {
            int idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += tag.Length;
            while (idx < text.Length && !char.IsDigit(text[idx]) && text[idx] != '-') idx++;
            int start = idx;
            if (start < text.Length && text[start] == '-') idx++;
            while (idx < text.Length && char.IsDigit(text[idx])) idx++;
            if (idx == start) return null;
            if (int.TryParse(text.Substring(start, idx - start), out int result)) return result;
            return null;
        }

        private void OpenFileAtForm(string filePath, int formIndex)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show(
                    $"SBCL reported the definition is at:\n\n{filePath}\n\n" +
                    $"...but that file doesn't exist on your system. This is common for " +
                    $"built-in Common Lisp symbols — SBCL's own source files aren't shipped " +
                    $"with the Windows binary distribution.",
                    "LISPerfect", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenFileInTab(filePath);

            string text = _currentTab?.Document.Text ?? "";
            int line = FindLineOfTopLevelForm(text, formIndex);
            if (line >= 0)
            {
                int targetLine = line + 1;
                Editor.ScrollToLine(targetLine);
                var lineInfo = Editor.Document.GetLineByNumber(targetLine);
                Editor.CaretOffset = lineInfo.Offset;
                Editor.Focus();
            }
        }

        private static int FindLineOfTopLevelForm(string text, int formIndex)
        {
            int depth = 0;
            int lineNum = 0;
            int formsSeenAtDepthZero = 0;
            bool inString = false;
            bool inLineComment = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char prev = i > 0 ? text[i - 1] : '\0';

                if (c == '\n') { lineNum++; inLineComment = false; continue; }
                if (inLineComment) continue;
                if (inString)
                {
                    if (c == '"' && prev != '\\') inString = false;
                    continue;
                }
                if (c == ';') { inLineComment = true; continue; }
                if (c == '"') { inString = true; continue; }

                if (c == '(')
                {
                    if (depth == 0)
                    {
                        if (formsSeenAtDepthZero == formIndex) return lineNum;
                        formsSeenAtDepthZero++;
                    }
                    depth++;
                }
                else if (c == ')') depth--;
            }
            return -1;
        }

        private string? GetSymbolAtCursor()
        {
            string text = Editor.Text;
            int caret = Editor.CaretOffset;
            if (string.IsNullOrEmpty(text)) return null;

            int start = caret;
            int end = caret;

            while (start > 0 && IsSymbolChar(text[start - 1])) start--;
            while (end < text.Length && IsSymbolChar(text[end])) end++;

            if (start == end) return null;
            return text.Substring(start, end - start);
        }

        private static bool IsSymbolChar(char c) =>
            char.IsLetterOrDigit(c) || c == '-' || c == '+' || c == '*' || c == '/' ||
            c == '?' || c == '!' || c == '<' || c == '>' || c == '=' || c == ':';


        // =====================================================================
        // 14. Project tree
        // =====================================================================

        private void LoadProjectFolder(string? folder)
        {
            if (_projectWatcher != null)
            {
                _projectWatcher.EnableRaisingEvents = false;
                _projectWatcher.Dispose();
                _projectWatcher = null;
            }

            ProjectTree.Items.Clear();
            _treeItemsByPath.Clear();

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                ProjectFolderLabel.Text = "No folder open";
                _settings.ProjectFolder = null;
                if (_settingsLoadedCleanly) SettingsManager.Save(_settings);
                return;
            }

            ProjectFolderLabel.Text = Path.GetFileName(folder);
            ProjectFolderLabel.ToolTip = folder;

            var root = CreateTreeItem(folder, isDirectory: true, isRoot: true);
            ProjectTree.Items.Add(root);
            // IsExpanded fires Expanded, which populates children via TreeItem_Expanded.
            root.IsExpanded = true;

            _settings.ProjectFolder = folder;
            if (_settingsLoadedCleanly) SettingsManager.Save(_settings);

            try
            {
                _projectWatcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite
                };
                _projectWatcher.Created += OnFileSystemChanged;
                _projectWatcher.Deleted += OnFileSystemChanged;
                _projectWatcher.Renamed += OnFileSystemChanged;
                _projectWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_settings.ProjectFolder != null) RefreshProjectTree();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshProjectTree()
        {
            if (_settings.ProjectFolder == null) return;
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpandedPaths(ProjectTree.Items, expanded);
            string? selectedPath = (ProjectTree.SelectedItem as TreeViewItem)?.Tag as string;

            LoadProjectFolder(_settings.ProjectFolder);

            foreach (var path in expanded)
                if (_treeItemsByPath.TryGetValue(path, out var item)) item.IsExpanded = true;

            if (selectedPath != null && _treeItemsByPath.TryGetValue(selectedPath, out var sel))
                sel.IsSelected = true;
        }

        private void CollectExpandedPaths(ItemCollection items, HashSet<string> into)
        {
            foreach (TreeViewItem item in items)
            {
                if (item.IsExpanded && item.Tag is string path) into.Add(path);
                CollectExpandedPaths(item.Items, into);
            }
        }

        private TreeViewItem CreateTreeItem(string path, bool isDirectory, bool isRoot = false)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = isDirectory ? "📁 " : (IsLispFile(path) ? "λ " : "📄 "),
                Margin = new Thickness(0, 0, 4, 0)
            });
            header.Children.Add(new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center
            });

            var item = new TreeViewItem { Header = header, Tag = path };
            _treeItemsByPath[path] = item;

            if (isDirectory)
            {
                // Dummy child so the expander arrow appears; real children populated on expand.
                item.Items.Add(new TreeViewItem { Header = "(loading...)" });
                item.Expanded += TreeItem_Expanded;
            }

            return item;
        }

        private void TreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem item) return;
            if (item.Tag is not string path) return;

            if (item.Items.Count == 1 &&
                item.Items[0] is TreeViewItem dummy &&
                (dummy.Header as string) == "(loading...)")
            {
                item.Items.Clear();
                PopulateChildren(item, path);
            }
        }

        private void PopulateChildren(TreeViewItem parent, string path)
        {
            try
            {
                var dirs = Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
                foreach (var dir in dirs)
                {
                    if (Path.GetFileName(dir).StartsWith(".")) continue;
                    parent.Items.Add(CreateTreeItem(dir, isDirectory: true));
                }

                var files = Directory.GetFiles(path)
                    .Where(f => !_settings.ShowOnlyLispFiles || IsLispFile(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (Path.GetFileName(file).StartsWith(".")) continue;
                    parent.Items.Add(CreateTreeItem(file, isDirectory: false));
                }
            }
            catch (UnauthorizedAccessException)
            {
                parent.Items.Add(new TreeViewItem { Header = "(access denied)" });
            }
            catch (Exception ex)
            {
                parent.Items.Add(new TreeViewItem { Header = $"(error: {ex.Message})" });
            }
        }

        private static bool IsLispFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".lisp" || ext == ".lsp" || ext == ".cl" || ext == ".asd";
        }

        private string? GetSelectedTreePath() =>
            (ProjectTree.SelectedItem as TreeViewItem)?.Tag as string;

        private void OpenSelectedTreeItem()
        {
            string? path = GetSelectedTreePath();
            if (path != null && File.Exists(path)) OpenFileInTab(path);
        }

        private string? GetContextDirectory()
        {
            string? path = GetSelectedTreePath();
            if (path == null) return _settings.ProjectFolder;
            if (Directory.Exists(path)) return path;
            if (File.Exists(path)) return Path.GetDirectoryName(path);
            return _settings.ProjectFolder;
        }

        // --- Tree click handlers ---

        private void ProjectTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => OpenSelectedTreeItem();

        private void ProjectTree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { OpenSelectedTreeItem(); e.Handled = true; }
            else if (e.Key == Key.Delete) { TreeDelete_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.F2) { TreeRename_Click(sender, e); e.Handled = true; }
        }

        private void ProjectTreeContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            string? path = GetSelectedTreePath();
            bool hasSelection = path != null;
            bool isFile = hasSelection && File.Exists(path);

            foreach (var item in ProjectTreeContextMenu.Items)
            {
                if (item is MenuItem mi)
                {
                    string h = mi.Header?.ToString() ?? "";
                    if (h.StartsWith("_Open")) mi.IsEnabled = isFile;
                    else if (h.Contains("Rename") || h.Contains("Delete") ||
                             h.Contains("Copy Path") || h.Contains("Show in"))
                        mi.IsEnabled = hasSelection;
                }
            }
        }

        private void TreeOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedTreeItem();

        private void TreeNewFile_Click(object sender, RoutedEventArgs e)
        {
            string? contextDir = GetContextDirectory();
            if (contextDir == null) return;

            string? name = PromptForString("New file", "File name:", "untitled.lisp");
            if (string.IsNullOrWhiteSpace(name)) return;

            string newPath = Path.Combine(contextDir, name);
            if (File.Exists(newPath))
            {
                MessageBox.Show("A file with that name already exists.", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                File.WriteAllText(newPath, "");
                OpenFileInTab(newPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not create file: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TreeNewFolder_Click(object sender, RoutedEventArgs e)
        {
            string? contextDir = GetContextDirectory();
            if (contextDir == null) return;

            string? name = PromptForString("New folder", "Folder name:", "new-folder");
            if (string.IsNullOrWhiteSpace(name)) return;

            string newPath = Path.Combine(contextDir, name);
            if (Directory.Exists(newPath))
            {
                MessageBox.Show("A folder with that name already exists.", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { Directory.CreateDirectory(newPath); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not create folder: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TreeRename_Click(object sender, RoutedEventArgs e)
        {
            string? path = GetSelectedTreePath();
            if (path == null) return;

            string oldName = Path.GetFileName(path);
            string? newName = PromptForString("Rename", "New name:", oldName);
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            string parent = Path.GetDirectoryName(path)!;
            string newPath = Path.Combine(parent, newName);
            try
            {
                if (File.Exists(path)) File.Move(path, newPath);
                else if (Directory.Exists(path)) Directory.Move(path, newPath);

                foreach (var tab in _tabs)
                {
                    if (tab.FilePath != null &&
                        string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        tab.FilePath = newPath;
                        UpdateTabHeader(tab);
                        if (tab == _currentTab) UpdateTitle();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not rename: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TreeDelete_Click(object sender, RoutedEventArgs e)
        {
            string? path = GetSelectedTreePath();
            if (path == null) return;

            string kind = File.Exists(path) ? "file" : "folder";
            var confirm = MessageBox.Show(
                $"Delete this {kind}?\n\n{path}\n\nThis moves it to the Recycle Bin.",
                "LISPerfect", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else if (Directory.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not delete: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TreeShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            string? path = GetSelectedTreePath();
            if (path == null) return;

            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open Explorer: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TreeCopyPath_Click(object sender, RoutedEventArgs e)
        {
            string? path = GetSelectedTreePath();
            if (path == null) return;
            try
            {
                Clipboard.SetText(path);
                StatusText.Text = "Path copied";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Clipboard error: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a project folder",
                UseDescriptionForTitle = true,
                SelectedPath = _settings.ProjectFolder ?? ""
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                LoadProjectFolder(dlg.SelectedPath);
        }

        private void CloseFolder_Click(object sender, RoutedEventArgs e) => LoadProjectFolder(null);

        private void ShowOnlyLisp_Click(object sender, RoutedEventArgs e)
        {
            _settings.ShowOnlyLispFiles = ShowOnlyLispMenuItem.IsChecked;
            if (_settingsLoadedCleanly) SettingsManager.Save(_settings);
            if (_settings.ProjectFolder != null) RefreshProjectTree();
        }

        private void RefreshProject_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.ProjectFolder != null) RefreshProjectTree();
        }

        private void ProjectMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }


        // =====================================================================
        // 15. Themes
        // =====================================================================

        private void ApplyTheme(string themeName)
        {
            _settings.Theme = themeName;
            if (_settingsLoadedCleanly) SettingsManager.Save(_settings);

            if (themeName == "dark")
            {
                Editor.Background = new Media.SolidColorBrush(Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
                Editor.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xDC, 0xDC, 0xDC));
                Editor.LineNumbersForeground = new Media.SolidColorBrush(Media.Color.FromRgb(0x85, 0x85, 0x85));
                Editor.TextArea.SelectionBrush = new Media.SolidColorBrush(Media.Color.FromArgb(120, 0x2E, 0x4A, 0x7D));
                Editor.TextArea.SelectionForeground = null;

                ReplOutput.Background = new Media.SolidColorBrush(Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
                ReplOutput.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xDC, 0xDC, 0xDC));
                ReplInput.Background = new Media.SolidColorBrush(Media.Color.FromRgb(0x25, 0x25, 0x25));
                ReplInput.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xDC, 0xDC, 0xDC));
                ReplInput.CaretBrush = new Media.SolidColorBrush(Media.Colors.White);

                RootGrid.Background = new Media.SolidColorBrush(Media.Color.FromRgb(0x25, 0x25, 0x25));

                LoadHighlightingDefinition("LISPerfect.CommonLisp-Dark.xshd");
            }
            else
            {
                Editor.Background = Media.Brushes.White;
                Editor.Foreground = Media.Brushes.Black;
                Editor.LineNumbersForeground = new Media.SolidColorBrush(Media.Color.FromRgb(0x80, 0x80, 0x80));
                Editor.TextArea.SelectionBrush = null;
                Editor.TextArea.SelectionForeground = null;

                ReplOutput.Background = new Media.SolidColorBrush(Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
                ReplOutput.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xDC, 0xDC, 0xDC));
                ReplInput.Background = Media.Brushes.White;
                ReplInput.Foreground = Media.Brushes.Black;
                ReplInput.CaretBrush = Media.Brushes.Black;

                RootGrid.Background = Media.Brushes.White;

                LoadHighlightingDefinition("LISPerfect.CommonLisp.xshd");
            }

            UpdateThemeCheckmarks();
        }

        private void LoadHighlightingDefinition(string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) return;
                using var reader = new XmlTextReader(stream);
                var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                Editor.SyntaxHighlighting = def;
            }
            catch { }
        }

        private void UpdateThemeCheckmarks()
        {
            ThemeDefaultMenuItem.IsChecked = _settings.Theme != "dark";
            ThemeDarkMenuItem.IsChecked = _settings.Theme == "dark";
        }


        // =====================================================================
        // 16. Font zoom
        // =====================================================================

        private void AdjustFontSize(double delta)
        {
            double newEditor = Clamp(Editor.FontSize + delta, MinFontSize, MaxFontSize);
            double newRepl = Clamp(ReplOutput.FontSize + delta, MinFontSize, MaxFontSize);
            Editor.FontSize = newEditor;
            ReplOutput.FontSize = newRepl;
            ReplInput.FontSize = newRepl;
            _settings.EditorFontSize = newEditor;
            _settings.ReplFontSize = newRepl;
            if (_settingsLoadedCleanly) SettingsManager.Save(_settings);
            StatusText.Text = $"Font size: editor {newEditor:0}, repl {newRepl:0}";
        }

        private void ResetFontSize()
        {
            Editor.FontSize = DefaultEditorFontSize;
            ReplOutput.FontSize = DefaultReplFontSize;
            ReplInput.FontSize = DefaultReplFontSize;
            _settings.EditorFontSize = DefaultEditorFontSize;
            _settings.ReplFontSize = DefaultReplFontSize;
            if (_settingsLoadedCleanly) SettingsManager.Save(_settings);
            StatusText.Text = "Font size reset";
        }

        private static double Clamp(double v, double lo, double hi) =>
            v < lo ? lo : v > hi ? hi : v;


        // =====================================================================
        // 17. Auto-save
        // =====================================================================

        private void AutoSaveDirtyTabs(string reason)
        {
            int savedCount = 0;
            int failedCount = 0;

            foreach (var tab in _tabs)
            {
                if (!tab.IsDirty) continue;
                if (tab.FilePath == null) continue; // never auto-save untitled

                try
                {
                    File.WriteAllText(tab.FilePath, tab.Document.Text);
                    tab.IsDirty = false;
                    UpdateTabHeader(tab);
                    savedCount++;
                }
                catch { failedCount++; }
            }

            if (savedCount > 0 || failedCount > 0)
            {
                UpdateTitle();
                StatusText.Text = failedCount > 0
                    ? $"Auto-save ({reason}): {savedCount} saved, {failedCount} failed"
                    : $"Auto-save ({reason}): {savedCount} saved";
            }
        }

        private void SetupAutoSaveTimer()
        {
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer = null;
            }

            if (!_settings.AutoSaveOnInterval) return;
            int seconds = Math.Max(5, _settings.AutoSaveIntervalSeconds);

            _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            _autoSaveTimer.Tick += (s, e) => AutoSaveDirtyTabs("interval");
            _autoSaveTimer.Start();
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            if (_showingOwnDialog) return;
            if (_settings.AutoSaveOnFocusLoss) AutoSaveDirtyTabs("focus loss");
        }


        // =====================================================================
        // 18. Preferences and Tools menu
        // =====================================================================

        private void Preferences_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new PreferencesWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                if (_settingsLoadedCleanly) SettingsManager.Save(_settings);
                SetupAutoSaveTimer(); // apply new interval settings immediately
                StatusText.Text = "Preferences saved";
            }
        }

        private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(SettingsManager.SettingsDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = SettingsManager.SettingsDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open settings folder: {ex.Message}", "LISPerfect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // =====================================================================
        // 19. Quicklisp integration
        // =====================================================================

        private static bool IsQuicklispInstalled() => File.Exists(QuicklispSetup);

        private async Task MaybeOfferQuicklispInstall()
        {
            if (IsQuicklispInstalled()) return;
            if (File.Exists(SkipQlInstallFlag)) return;

            var result = MessageBox.Show(
                "Quicklisp is the standard package manager for Common Lisp, " +
                "and most Lisp libraries expect it to be available.\n\n" +
                "Would you like LISPerfect to install Quicklisp now? " +
                "(This requires an internet connection.)\n\n" +
                "You can install it later from the Quicklisp menu.",
                "Install Quicklisp?",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await InstallQuicklispAsync();
            }
            else if (result == MessageBoxResult.No)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SkipQlInstallFlag)!);
                    File.WriteAllText(SkipQlInstallFlag,
                        "Delete this file to be prompted about Quicklisp again.");
                }
                catch { }
            }
        }

        private async Task InstallQuicklispAsync()
        {
            StatusText.Text = "Installing Quicklisp...";
            AppendToRepl(";; --- Installing Quicklisp ---" + Environment.NewLine);

            string tempFile = Path.Combine(Path.GetTempPath(), "quicklisp-bootstrap.lisp");

            try
            {
                using (var http = new HttpClient())
                {
                    AppendToRepl(";; Downloading quicklisp.lisp..." + Environment.NewLine);
                    var bytes = await http.GetByteArrayAsync(QuicklispBootstrapUrl);
                    await File.WriteAllBytesAsync(tempFile, bytes);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not download Quicklisp: {ex.Message}",
                    "LISPerfect", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Quicklisp install failed";
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ResolveSbclPath(),
                Arguments = "--noinform --no-sysinit --no-userinit --non-interactive " +
                            $"--load \"{tempFile}\" " +
                            "--eval \"(quicklisp-quickstart:install)\" " +
                            "--eval \"(ql-util:without-prompting (ql:add-to-init-file))\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var proc = Process.Start(psi)!;
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode == 0 && IsQuicklispInstalled())
                {
                    AppendToRepl(";; Quicklisp installed successfully." + Environment.NewLine);
                    AppendToRepl(";; Restart LISPerfect to activate it, or use " +
                                 "Quicklisp menu to load a package now." + Environment.NewLine);
                    StatusText.Text = "Quicklisp installed";
                    ConfigureReplForQuicklisp();
                }
                else
                {
                    AppendToRepl(";; Quicklisp install may have failed. Details:" + Environment.NewLine);
                    AppendToRepl(stdout + Environment.NewLine);
                    if (!string.IsNullOrWhiteSpace(stderr))
                        AppendToRepl(";; stderr: " + stderr + Environment.NewLine);
                    StatusText.Text = "Quicklisp install had issues";
                }
            }
            catch (Exception ex)
            {
                AppendToRepl($";; Install error: {ex.Message}" + Environment.NewLine);
                StatusText.Text = "Quicklisp install failed";
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        private void ConfigureReplForQuicklisp()
        {
            if (!IsQuicklispInstalled()) return;
            if (_sbcl == null || _sbcl.HasExited) return;

            // Forward slashes work in Lisp pathnames even on Windows and sidestep escaping.
            string path = QuicklispSetup.Replace("\\", "/");
            string code = $"(load \"{path}\")";
            try
            {
                _sbcl.StandardInput.WriteLine(code);
                _sbcl.StandardInput.Flush();
            }
            catch { }
        }


        // =====================================================================
        // 20. Small helpers
        // =====================================================================

        private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            var current = start;
            while (current != null)
            {
                if (current is T t) return t;
                current = Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>
        /// Modal text-input dialog. WPF ships file dialogs and message boxes but
        /// no plain "type a string" input, so we build one on the fly.
        /// </summary>
        private static string? PromptForString(string title, string prompt, string defaultValue)
        {
            var win = new Window
            {
                Title = title,
                Width = 400,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var box = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            win.Content = grid;

            string? result = null;
            ok.Click += (s, e) => { result = box.Text; win.DialogResult = true; };
            box.Focus();
            box.SelectAll();

            return win.ShowDialog() == true ? result : null;
        }


        // =====================================================================
        // 21. Menu click handlers (mostly one-liners)
        // =====================================================================

        // --- File menu ---
        private void NewFile_Click(object sender, RoutedEventArgs e) => DoNew();
        private void OpenFile_Click(object sender, RoutedEventArgs e) => DoOpen();
        private void SaveFile_Click(object sender, RoutedEventArgs e) => DoSave();
        private void SaveFileAs_Click(object sender, RoutedEventArgs e) => DoSaveAs();
        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        // --- Lisp menu ---
        private void SendExprMenuItem_Click(object sender, RoutedEventArgs e) => SendCurrentExpression();
        private void RunFile_Click(object sender, RoutedEventArgs e) => RunFile();
        private void ClearRepl_Click(object sender, RoutedEventArgs e) => ClearRepl();
        private void RestartSbcl_Click(object sender, RoutedEventArgs e)
        {
            AppendToRepl(Environment.NewLine + ";; --- Restarting SBCL ---" + Environment.NewLine);
            StopSbcl();
            StartSbcl();
            ConfigureReplForQuicklisp();
            StatusText.Text = "SBCL restarted";
        }

        private void ClearRepl()
        {
            ReplOutput.Clear();
            StatusText.Text = "REPL cleared";
        }

        // --- View menu ---
        private void ZoomIn_Click(object sender, RoutedEventArgs e) => AdjustFontSize(+1);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => AdjustFontSize(-1);
        private void ZoomReset_Click(object sender, RoutedEventArgs e) => ResetFontSize();
        private void ThemeDefault_Click(object sender, RoutedEventArgs e) => ApplyTheme("default");
        private void ThemeDark_Click(object sender, RoutedEventArgs e) => ApplyTheme("dark");

        // --- Tools menu ---
        private void CompleteSymbol_Click(object sender, RoutedEventArgs e) => ShowCompletion();
        private void JumpToDefinition_Click(object sender, RoutedEventArgs e) => JumpToDefinition();

        // --- Quicklisp menu ---
        private void QlInstallPackage_Click(object sender, RoutedEventArgs e)
        {
            if (!IsQuicklispInstalled())
            {
                MessageBox.Show(
                    "Quicklisp is not installed yet. Use " +
                    "Quicklisp → Reinstall Quicklisp to set it up.",
                    "LISPerfect", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? pkg = PromptForString(
                "Install package",
                "Enter the Quicklisp package name to install:",
                "alexandria");
            if (string.IsNullOrWhiteSpace(pkg)) return;

            pkg = pkg.Trim().Replace("\"", "");
            SendToSbcl($"(ql:quickload \"{pkg}\")");
            StatusText.Text = $"Loading {pkg}...";
        }

        private async void QlReinstall_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will download and install Quicklisp into your home directory. " +
                "If Quicklisp is already installed, this may reinstall it.\n\nContinue?",
                "Reinstall Quicklisp?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try { File.Delete(SkipQlInstallFlag); } catch { }
            await InstallQuicklispAsync();
        }

        // --- Tab actions (from context menu and Ctrl+W) ---
        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            var tab = _contextTab ?? _currentTab;
            if (tab != null) CloseTab(tab);
            _contextTab = null;
        }

        private void TabCloseOthers_Click(object sender, RoutedEventArgs e)
        {
            var keep = _contextTab ?? _currentTab;
            if (keep == null) return;
            foreach (var t in _tabs.ToArray())
                if (t != keep) CloseTab(t);
            _contextTab = null;
        }

        private void TabCloseAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in _tabs.ToArray()) CloseTab(t);
            _contextTab = null;
        }

        private void NewTabButton_Click(object sender, RoutedEventArgs e) => NewTab();
    }
}