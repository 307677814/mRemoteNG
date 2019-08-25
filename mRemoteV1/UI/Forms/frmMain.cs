﻿using Microsoft.Win32;
using mRemoteNG.App;
using mRemoteNG.App.Info;
using mRemoteNG.App.Initialization;
using mRemoteNG.Config;
using mRemoteNG.Config.Connections;
using mRemoteNG.Config.DataProviders;
using mRemoteNG.Config.Putty;
using mRemoteNG.Config.Settings;
using mRemoteNG.Connection;
using mRemoteNG.Messages;
using mRemoteNG.Messages.MessageWriters;
using mRemoteNG.Themes;
using mRemoteNG.Tools;
using mRemoteNG.UI.Menu;
using mRemoteNG.UI.Tabs;
using mRemoteNG.UI.TaskDialog;
using mRemoteNG.UI.Window;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.UI.Panels;
using WeifenLuo.WinFormsUI.Docking;
using Message = System.Windows.Forms.Message;

// ReSharper disable MemberCanBePrivate.Global

namespace mRemoteNG.UI.Forms
{
    public partial class FrmMain
    {
        public static FrmMain Default { get; } = new FrmMain();

        private static ClipboardchangeEventHandler _clipboardChangedEvent;
        private bool _inSizeMove;
        private bool _inMouseActivate;
        private IntPtr _fpChainedWindowHandle;
        private bool _usingSqlServer;
        private string _connectionsFileName;
        private bool _showFullPathInTitle;
        private readonly ScreenSelectionSystemMenu _screenSystemMenu;
        private ConnectionInfo _selectedConnection;
        private readonly IList<IMessageWriter> _messageWriters = new List<IMessageWriter>();
        private readonly ThemeManager _themeManager;
        private readonly FileBackupPruner _backupPruner = new FileBackupPruner();
        private readonly IConnectionInitiator _connectionInitiator = new ConnectionInitiator();
        private readonly SystemKeyboardHook _keyboardHook;
        private bool _childProcessHeldLastFocus;
        private bool _currentlyFixingAltTab;

        /// <summary>
        /// TRUE if any part of mrng has focus - the main window or child processes
        /// </summary>
        private bool _mrngFocused;

        internal FullscreenHandler Fullscreen { get; set; }

        //Added theming support
        private readonly ToolStripRenderer _toolStripProfessionalRenderer = new ToolStripProfessionalRenderer();

        private FrmMain()
        {
            _showFullPathInTitle = Settings.Default.ShowCompleteConsPathInTitle;
            InitializeComponent();
            Fullscreen = new FullscreenHandler(this);

            //Theming support
            _themeManager = ThemeManager.getInstance();
            vsToolStripExtender.DefaultRenderer = _toolStripProfessionalRenderer;
            ApplyTheme();

            _screenSystemMenu = new ScreenSelectionSystemMenu(this);
            _keyboardHook = new SystemKeyboardHook(KeyboardHookCallback);
        }

        #region Properties

        public FormWindowState PreviousWindowState { get; set; }

        public bool IsClosing { get; private set; }

        public bool AreWeUsingSqlServerForSavingConnections
        {
            get => _usingSqlServer;
            set
            {
                if (_usingSqlServer == value)
                {
                    return;
                }

                _usingSqlServer = value;
                UpdateWindowTitle();
            }
        }

        public string ConnectionsFileName
        {
            get => _connectionsFileName;
            set
            {
                if (_connectionsFileName == value)
                {
                    return;
                }

                _connectionsFileName = value;
                UpdateWindowTitle();
            }
        }

        public bool ShowFullPathInTitle
        {
            get => _showFullPathInTitle;
            set
            {
                if (_showFullPathInTitle == value)
                {
                    return;
                }

                _showFullPathInTitle = value;
                UpdateWindowTitle();
            }
        }

        public ConnectionInfo SelectedConnection
        {
            get => _selectedConnection;
            set
            {
                if (_selectedConnection == value)
                {
                    return;
                }

                _selectedConnection = value;
                UpdateWindowTitle();
            }
        }

        #endregion

        #region Startup & Shutdown

        private void frmMain_Load(object sender, EventArgs e)
        {
            var messageCollector = Runtime.MessageCollector;
            MessageCollectorSetup.SetupMessageCollector(messageCollector, _messageWriters);
            MessageCollectorSetup.BuildMessageWritersFromSettings(_messageWriters);

            Windows.ConnectionInitiator = _connectionInitiator;
            Startup.Instance.InitializeProgram(messageCollector);

            msMain.Location = Point.Empty;
            var settingsLoader = new SettingsLoader(this, _connectionInitiator, messageCollector, _quickConnectToolStrip,
                                                    _externalToolsToolStrip, _multiSshToolStrip, msMain);
            settingsLoader.LoadSettings();

            SetMenuDependencies();

            var uiLoader = new DockPanelLayoutLoader(this, messageCollector);
            uiLoader.LoadPanelsFromXml();

            LockToolbarPositions(Settings.Default.LockToolbars);
            Settings.Default.PropertyChanged += OnApplicationSettingChanged;

            _themeManager.ThemeChanged += ApplyTheme;

            _fpChainedWindowHandle = NativeMethods.SetClipboardViewer(Handle);

            Runtime.WindowList = new WindowList();

            if (Settings.Default.ResetPanels)
                SetDefaultLayout();

            Runtime.ConnectionsService.ConnectionsLoaded += ConnectionsServiceOnConnectionsLoaded;
            Runtime.ConnectionsService.ConnectionsSaved += ConnectionsServiceOnConnectionsSaved;
            var credsAndConsSetup = new CredsAndConsSetup();
            credsAndConsSetup.LoadCredsAndCons();

            Windows.TreeForm.Focus();

            PuttySessionsManager.Instance.StartWatcher();
            if (Settings.Default.StartupComponentsCheck)
                Windows.Show(WindowType.ComponentsCheck);

            Startup.Instance.CreateConnectionsProvider(messageCollector);

            _screenSystemMenu.BuildScreenList();
            SystemEvents.DisplaySettingsChanged += _screenSystemMenu.OnDisplayChanged;
            ApplyLanguage();

            Opacity = 1;
            //Fix MagicRemove , revision on panel strategy for mdi

            pnlDock.ShowDocumentIcon = true;

            FrmSplashScreen.getInstance().Close();

            if (Settings.Default.CreateEmptyPanelOnStartUp)
            {
                var panelName = !string.IsNullOrEmpty(Settings.Default.StartUpPanelName)
                    ? Settings.Default.StartUpPanelName
                    : Language.strNewPanel;

                var panelAdder = new PanelAdder(_connectionInitiator);
                if (!panelAdder.DoesPanelExist(panelName))
                    panelAdder.AddPanel(panelName);
            }

            TabHelper.Instance.ActiveConnectionTabChanged += OnActiveConnectionTabChanged;
            TabHelper.Instance.TabClicked += OnTabClicked;
        }

        private void OnTabClicked(object sender, EventArgs e)
        {
            ActivateConnection();
        }

        private void OnActiveConnectionTabChanged(object sender, EventArgs e)
        {
            ActivateConnection();
        }

        private void ApplyLanguage()
        {
            fileMenu.ApplyLanguage();
            viewMenu.ApplyLanguage();
            toolsMenu.ApplyLanguage();
            helpMenu.ApplyLanguage();
        }

        private void OnApplicationSettingChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            if (propertyChangedEventArgs.PropertyName != nameof(Settings.LockToolbars))
                return;

            LockToolbarPositions(Settings.Default.LockToolbars);
        }

        private void LockToolbarPositions(bool shouldBeLocked)
        {
            var toolbars = new ToolStrip[]
                {_quickConnectToolStrip, _multiSshToolStrip, _externalToolsToolStrip, msMain};
            foreach (var toolbar in toolbars)
            {
                toolbar.GripStyle = shouldBeLocked
                    ? ToolStripGripStyle.Hidden
                    : ToolStripGripStyle.Visible;
            }
        }

        private void ConnectionsServiceOnConnectionsLoaded(object sender,
                                                           ConnectionsLoadedEventArgs connectionsLoadedEventArgs)
        {
            UpdateWindowTitle();
        }

        private void ConnectionsServiceOnConnectionsSaved(object sender,
                                                          ConnectionsSavedEventArgs connectionsSavedEventArgs)
        {
            if (connectionsSavedEventArgs.UsingDatabase)
                return;

            _backupPruner.PruneBackupFiles(connectionsSavedEventArgs.ConnectionFileName,
                                           Settings.Default.BackupFileKeepCount);
        }

        private void SetMenuDependencies()
        {
            fileMenu.TreeWindow = Windows.TreeForm;
            fileMenu.ConnectionInitiator = _connectionInitiator;

            viewMenu.TsExternalTools = _externalToolsToolStrip;
            viewMenu.TsQuickConnect = _quickConnectToolStrip;
            viewMenu.TsMultiSsh = _multiSshToolStrip;
            viewMenu.FullscreenHandler = Fullscreen;
            viewMenu.MainForm = this;
            viewMenu.ConnectionInitiator = _connectionInitiator;

            toolsMenu.MainForm = this;
            toolsMenu.CredentialProviderCatalog = Runtime.CredentialProviderCatalog;

            _quickConnectToolStrip.ConnectionInitiator = _connectionInitiator;
        }

        //Theming support
        private void ApplyTheme()
        {
            if (!_themeManager.ThemingActive)
            {
                pnlDock.Theme = _themeManager.DefaultTheme.Theme;
                return;
            }

            try
            {
                // this will always throw when turning themes on from
                // the options menu.
                pnlDock.Theme = _themeManager.ActiveTheme.Theme;
            }
            catch (Exception)
            {
                // intentionally ignore exception
            }

            // Persist settings when rebuilding UI
            try
            {
                vsToolStripExtender.SetStyle(msMain, _themeManager.ActiveTheme.Version,
                                             _themeManager.ActiveTheme.Theme);
                vsToolStripExtender.SetStyle(_quickConnectToolStrip, _themeManager.ActiveTheme.Version,
                                             _themeManager.ActiveTheme.Theme);
                vsToolStripExtender.SetStyle(_externalToolsToolStrip, _themeManager.ActiveTheme.Version,
                                             _themeManager.ActiveTheme.Theme);
                vsToolStripExtender.SetStyle(_multiSshToolStrip, _themeManager.ActiveTheme.Version,
                                             _themeManager.ActiveTheme.Theme);

                if (!_themeManager.ActiveAndExtended) return;
                tsContainer.TopToolStripPanel.BackColor =
                    _themeManager.ActiveTheme.ExtendedPalette.getColor("CommandBarMenuDefault_Background");
                BackColor = _themeManager.ActiveTheme.ExtendedPalette.getColor("Dialog_Background");
                ForeColor = _themeManager.ActiveTheme.ExtendedPalette.getColor("Dialog_Foreground");
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("Error applying theme", ex, MessageClass.WarningMsg);
            }
        }

        private void frmMain_Shown(object sender, EventArgs e)
        {
            PromptForUpdatesPreference();
            CheckForUpdates();
        }

        private void PromptForUpdatesPreference()
        {
            if (Settings.Default.CheckForUpdatesAsked) return;
            string[] commandButtons =
            {
                Language.strAskUpdatesCommandRecommended,
                Language.strAskUpdatesCommandCustom,
                Language.strAskUpdatesCommandAskLater
            };

            CTaskDialog.ShowTaskDialogBox(this, GeneralAppInfo.ProductName, Language.strAskUpdatesMainInstruction,
                                          string.Format(Language.strAskUpdatesContent, GeneralAppInfo.ProductName),
                                          "", "", "", "", string.Join(" | ", commandButtons), ETaskDialogButtons.None,
                                          ESysIcons.Question,
                                          ESysIcons.Question);

            if (CTaskDialog.CommandButtonResult == 0 | CTaskDialog.CommandButtonResult == 1)
            {
                Settings.Default.CheckForUpdatesAsked = true;
            }

            if (CTaskDialog.CommandButtonResult != 1) return;

            using (var optionsForm = new FrmOptions(Language.strTabUpdates, _connectionInitiator))
            {
                optionsForm.ShowDialog(this);
            }
        }

        private void CheckForUpdates()
        {
            if (!Settings.Default.CheckForUpdatesOnStartup) return;

            var updateFrequencyInDays = Convert.ToDouble(Settings.Default.CheckForUpdatesFrequencyDays);
            var nextUpdateCheck = Settings.Default.CheckForUpdatesLastCheck.AddDays(updateFrequencyInDays);

            if (!Settings.Default.UpdatePending && DateTime.UtcNow <= nextUpdateCheck) return;
            if (!IsHandleCreated)
                CreateHandle(); // Make sure the handle is created so that InvokeRequired returns the correct result

            Startup.Instance.CheckForUpdate();
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            _keyboardHook?.Dispose();

            if (!(Runtime.WindowList == null || Runtime.WindowList.Count == 0))
            {
                var openConnections = 0;
                if (pnlDock.Contents.Count > 0)
                {
                    foreach (var dc in pnlDock.Contents)
                    {
                        if (!(dc is ConnectionWindow cw)) continue;
                        if (cw.Controls.Count < 1) continue;
                        if (!(cw.Controls[0] is DockPanel dp)) continue;
                        if (dp.Contents.Count > 0)
                            openConnections += dp.Contents.Count;
                    }
                }

                if (openConnections > 0 &&
                    (Settings.Default.ConfirmCloseConnection == (int)ConfirmCloseEnum.All |
                     (Settings.Default.ConfirmCloseConnection == (int)ConfirmCloseEnum.Multiple &
                      openConnections > 1) || Settings.Default.ConfirmCloseConnection == (int)ConfirmCloseEnum.Exit))
                {
                    var result = CTaskDialog.MessageBox(this, Application.ProductName,
                                                        Language.strConfirmExitMainInstruction, "", "", "",
                                                        Language.strCheckboxDoNotShowThisMessageAgain,
                                                        ETaskDialogButtons.YesNo, ESysIcons.Question,
                                                        ESysIcons.Question);
                    if (CTaskDialog.VerificationChecked)
                    {
                        Settings.Default.ConfirmCloseConnection--;
                    }

                    if (result == DialogResult.No)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }

            Shutdown.Cleanup(_quickConnectToolStrip, _externalToolsToolStrip, _multiSshToolStrip, this);

            IsClosing = true;

            if (Runtime.WindowList != null)
            {
                foreach (BaseWindow window in Runtime.WindowList)
                {
                    window.Close();
                }
            }

            Shutdown.StartUpdate();

            Debug.Print("[END] - " + Convert.ToString(DateTime.Now, CultureInfo.InvariantCulture));
        }

        #endregion

        #region Timer

        private void tmrAutoSave_Tick(object sender, EventArgs e)
        {
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "Doing AutoSave");
            Runtime.ConnectionsService.SaveConnectionsAsync();
        }

        #endregion

        #region Window Overrides and DockPanel Stuff

        private void frmMain_ResizeBegin(object sender, EventArgs e)
        {
            _inSizeMove = true;
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "Begin app window move/resize");
        }

        private void frmMain_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                if (!Settings.Default.MinimizeToTray) return;
                if (Runtime.NotificationAreaIcon == null)
                {
                    Runtime.NotificationAreaIcon = new NotificationAreaIcon(_connectionInitiator);
                }

                Hide();
            }
            else
            {
                PreviousWindowState = WindowState;
            }
        }

        private void frmMain_ResizeEnd(object sender, EventArgs e)
        {
            _inSizeMove = false;
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "End app window move/resize");
            // This handles activations from clicks that started a size/move operation
            ActivateConnection();
        }


        // Maybe after starting putty, remove its ability to show up in alt-tab?
        // SetWindowLong(this.Handle, GWL_EXSTYLE, (GetWindowLong(this.Handle,GWL_EXSTYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        protected override void WndProc(ref Message m)
        {
            // Listen for and handle operating system messages
            try
            {
                // ReSharper disable once SwitchStatementMissingSomeCases
                switch (m.Msg)
                {
                    case NativeMethods.WM_MOUSEACTIVATE:
                        _inMouseActivate = true;
                        Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"_inMouseActivate = {_inMouseActivate}");

                        var controlThatWasClicked2 = FromChildHandle(NativeMethods.WindowFromPoint(MousePosition))
                                                    ?? GetChildAtPoint(MousePosition);

                        if (controlThatWasClicked2 == null)
                            break;

                        Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"Clicked control: {controlThatWasClicked2}");
                        break;
                    case NativeMethods.WM_ACTIVATEAPP:
                        if (m.WParam.ToInt32() == 0) // mRemoteNG is being deactivated
                        {
                            var threadWhichIsActivating = m.LParam.ToInt32();
                            _childProcessHeldLastFocus = _connectionInitiator
                                .ActiveConnections
                                .OfType<ExternalProcessProtocolBase>()
                                .Any(proc => proc.ThreadId == threadWhichIsActivating);

                            _inMouseActivate = false;
                            _mrngFocused = _childProcessHeldLastFocus;
                            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"mRemoteNG main window lost focus (_childProcessHeldLastFocus={_childProcessHeldLastFocus})");
                            break;
                        }

                        if (_childProcessHeldLastFocus && !_mrngFocused)
                        {
                            ActivateConnection();
                        }

                        _childProcessHeldLastFocus = false;
                        _mrngFocused = true;
                        Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"mRemoteNG main window received focus (_childProcessHeldLastFocus={_childProcessHeldLastFocus})");

                        //var candidateTabToFocus = FromChildHandle(NativeMethods.WindowFromPoint(MousePosition))
                        //                       ?? GetChildAtPoint(MousePosition);
                        //if (candidateTabToFocus is InterfaceControl)
                        //{
                        //    candidateTabToFocus.Parent.Focus();
                        //}

                        break;
                    case NativeMethods.WM_ACTIVATE:
                        if (NativeMethods.LOWORD(m.WParam) == NativeMethods.WA_ACTIVE)
                        {
                            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "KB ACTIVATE");
                        }

                        // Only handle this msg if it was triggered by a click
                        if (NativeMethods.LOWORD(m.WParam) != NativeMethods.WA_CLICKACTIVE)
                            return;

                        var controlThatWasClicked = FromChildHandle(NativeMethods.WindowFromPoint(MousePosition))
                                                    ?? GetChildAtPoint(MousePosition);

                        if (controlThatWasClicked == null)
                            break;

                        Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"Click activate: {controlThatWasClicked}");

                        if (controlThatWasClicked is TreeView ||
                            controlThatWasClicked is ComboBox ||
                            controlThatWasClicked is TextBox ||
                            controlThatWasClicked is FrmMain ||
                            controlThatWasClicked is AutoHideStripBase)
                        {
                            controlThatWasClicked.Focus();
                        }
                        else if (controlThatWasClicked.CanSelect ||
                                 controlThatWasClicked is MenuStrip ||
                                 controlThatWasClicked is ToolStrip)
                        {
                            // Simulate a mouse event since one wasn't generated by Windows
                            SimulateClick(controlThatWasClicked);
                            controlThatWasClicked.Focus();
                        }
                        //else
                        //{
                        //    // This handles activations from clicks that did not start a size/move operation
                        //    ActivateConnection();
                        //}

                        break;
                    case NativeMethods.WM_NCACTIVATE:
                        //if (m.WParam.ToInt32() == 1)
                        //    break;

                        //// Never allow the mRemoteNG window to display itself as inactive. By doing this,
                        //// we ensure focus events can propagate to child connection windows
                        NativeMethods.DefWindowProc(Handle, Convert.ToUInt32(m.Msg), (IntPtr)1, m.LParam);
                        m.Result = (IntPtr)1;
                        Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "Fixed main app NCACTIVATE");
                        return;
                    case NativeMethods.WM_WINDOWPOSCHANGED:
                        // Ignore this message if the window wasn't activated
                        if (!_inMouseActivate)
                            break;

                        var windowPos = (NativeMethods.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.WINDOWPOS));
                        if ((windowPos.flags & NativeMethods.SWP_NOACTIVATE) == 0)
                        {
                            if (!_inMouseActivate && !_inSizeMove)
                            {
                                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "WM_WINDOWPOSCHANGED DONE");
                                ActivateConnection();
                            }
                        }

                        break;
                    case NativeMethods.WM_SYSCOMMAND:
                        var screen = _screenSystemMenu.GetScreenById(m.WParam.ToInt32());
                        if (screen != null)
                            Screens.SendFormToScreen(screen);
                        break;
                    case NativeMethods.WM_DRAWCLIPBOARD:
                        NativeMethods.SendMessage(_fpChainedWindowHandle, m.Msg, m.LParam, m.WParam);
                        _clipboardChangedEvent?.Invoke();
                        break;
                    case NativeMethods.WM_CHANGECBCHAIN:
                        //Send to the next window
                        NativeMethods.SendMessage(_fpChainedWindowHandle, m.Msg, m.LParam, m.WParam);
                        _fpChainedWindowHandle = m.LParam;
                        break;
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("frmMain WndProc failed", ex);
            }

            base.WndProc(ref m);
        }

        public int KeyboardHookCallback(int msg, NativeMethods.KBDLLHOOKSTRUCT kbd)
        {
            var key = (Keys) kbd.vkCode;
            if (key.HasFlag(Keys.Tab) && kbd.flags.HasFlag(NativeMethods.KBDLLHOOKSTRUCTFlags.LLKHF_ALTDOWN))
            {
                if (msg == NativeMethods.WM_SYSKEYDOWN || msg == NativeMethods.WM_KEYDOWN)
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"ALT-TAB PRESSED (CPF={_childProcessHeldLastFocus}, MRNGF={_mrngFocused}, IMA={_inMouseActivate}, CFAT={_currentlyFixingAltTab})");
                    if (_childProcessHeldLastFocus && _mrngFocused && !_inMouseActivate && !_currentlyFixingAltTab)
                    {
                        Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "FIXING ALT-TAB FOR EXTAPP");
                        _currentlyFixingAltTab = true;

                        // simulate an extra TAB key press. This skips focus of the mrng main window.
                        NativeMethods.keybd_event((byte)Keys.Tab, 0, (uint)NativeMethods.KEYEVENTF.KEYUP, 0);
                        NativeMethods.keybd_event((byte)Keys.Tab, 0, 0, 0);

                        // WndProc will never get an event when we switch from a child proc to a completely different program since the main mrng window never had focus to begin with.
                        // Assume mrng as a whole will lose focus, even though the user could choose to retain focus on us. When Alt-tab completes, the mrng main window will
                        // receive the focus event and we will handle the child process focusing as necessary.
                        _mrngFocused = false;
                        _currentlyFixingAltTab = false;
                    }
                }
            }

            // alt + right-shift
            if (key.HasFlag(Keys.RShiftKey) && kbd.flags.HasFlag(NativeMethods.KBDLLHOOKSTRUCTFlags.LLKHF_ALTDOWN))
            {
                if (msg != NativeMethods.WM_SYSKEYUP && msg != NativeMethods.WM_KEYUP)
                    return 0;

                if (!_mrngFocused)
                    return 0;

                if (_childProcessHeldLastFocus)
                {
                    Focus(); // focus main window
                    return 1;
                }

                // focus connection
                ActivateConnection();
                return 1;
            }

            return 0;
        }

        private void SimulateClick(Control control)
        {
            var clientMousePosition = control.PointToClient(MousePosition);
            var temp_wLow = clientMousePosition.X;
            var temp_wHigh = clientMousePosition.Y;
            NativeMethods.SendMessage(control.Handle, NativeMethods.WM_LBUTTONDOWN, (IntPtr)NativeMethods.MK_LBUTTON,
                                      (IntPtr)NativeMethods.MAKELPARAM(ref temp_wLow, ref temp_wHigh));
            clientMousePosition.X = temp_wLow;
            clientMousePosition.Y = temp_wHigh;
        }

        private void ActivateConnection()
        {
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "Performing special connection focus logic");
            //var cw = pnlDock.ActiveDocument as ConnectionWindow;
            //var dp = cw?.ActiveControl as DockPane;

            //if (!(dp?.ActiveContent is ConnectionTab tab))
            //{
            //    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, "Active content is not a tab. We won't focus a specific connection.");
            //    return;
            //}

            //var ifc = InterfaceControl.FindInterfaceControl(tab);
            var tab = TabHelper.Instance.CurrentTab;
            if (tab == null)
                return;

            var ifc = tab.InterfaceControl;
            if (ifc == null)
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"InterfaceControl for tab '{tab.Name}' was not found. We won't focus that connection.");
                return;
            }

            ifc.Protocol.Focus();
            var conFormWindow = ifc.FindForm();
            ((ConnectionTab)conFormWindow)?.RefreshInterfaceController();
        }

        private void pnlDock_ActiveDocumentChanged(object sender, EventArgs e)
        {
            //ActivateConnection();
        }

        internal void UpdateWindowTitle()
        {
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(UpdateWindowTitle));
                return;
            }

            var titleBuilder = new StringBuilder(Application.ProductName);
            const string separator = " - ";

            if (Runtime.ConnectionsService.IsConnectionsFileLoaded)
            {
                if (Runtime.ConnectionsService.UsingDatabase)
                {
                    titleBuilder.Append(separator);
                    titleBuilder.Append(Language.strSQLServer.TrimEnd(':'));
                }
                else
                {
                    if (!string.IsNullOrEmpty(Runtime.ConnectionsService.ConnectionFileName))
                    {
                        titleBuilder.Append(separator);
                        titleBuilder.Append(Settings.Default.ShowCompleteConsPathInTitle
                                                ? Runtime.ConnectionsService.ConnectionFileName
                                                : Path.GetFileName(Runtime.ConnectionsService.ConnectionFileName));
                    }
                }
            }

            if (!string.IsNullOrEmpty(SelectedConnection?.Name))
            {
                titleBuilder.Append(separator);
                titleBuilder.Append(SelectedConnection.Name);

                if (Settings.Default.TrackActiveConnectionInConnectionTree)
                    Windows.TreeForm.JumpToNode(SelectedConnection);
            }

            Text = titleBuilder.ToString();
        }

        public void ShowHidePanelTabs(DockContent closingDocument = null)
        {
            DocumentStyle newDocumentStyle;

            if (Settings.Default.AlwaysShowPanelTabs)
            {
                newDocumentStyle = DocumentStyle.DockingWindow; // Show the panel tabs
            }
            else
            {
                var nonConnectionPanelCount = 0;
                foreach (var dockContent in pnlDock.Documents)
                {
                    var document = (DockContent)dockContent;
                    if ((closingDocument == null || document != closingDocument) && !(document is ConnectionWindow))
                    {
                        nonConnectionPanelCount++;
                    }
                }

                newDocumentStyle = nonConnectionPanelCount == 0
                    ? DocumentStyle.DockingSdi
                    : DocumentStyle.DockingWindow;
            }

            // TODO: See if we can get this to work with DPS
#if false
            foreach (var dockContent in pnlDock.Documents)
			{
				var document = (DockContent)dockContent;
				if (document is ConnectionWindow)
				{
					var connectionWindow = (ConnectionWindow)document;
					if (Settings.Default.AlwaysShowConnectionTabs == false)
					{
						connectionWindow.TabController.HideTabsMode = TabControl.HideTabsModes.HideAlways;
					}
					else
					{
						connectionWindow.TabController.HideTabsMode = TabControl.HideTabsModes.ShowAlways;
					}
				}
			}
#endif

            if (pnlDock.DocumentStyle == newDocumentStyle) return;
            pnlDock.DocumentStyle = newDocumentStyle;
            pnlDock.Size = new Size(1, 1);
        }

        #endregion

        #region Screen Stuff

        public void SetDefaultLayout()
        {
            pnlDock.Visible = false;

            pnlDock.DockLeftPortion = pnlDock.Width * 0.2;
            pnlDock.DockRightPortion = pnlDock.Width * 0.2;
            pnlDock.DockTopPortion = pnlDock.Height * 0.25;
            pnlDock.DockBottomPortion = pnlDock.Height * 0.25;

            Windows.TreeForm.Show(pnlDock, DockState.DockLeft);
            Windows.ConfigForm.Show(pnlDock);
            Windows.ConfigForm.DockTo(Windows.TreeForm.Pane, DockStyle.Bottom, -1);
            Windows.ErrorsForm.Show(pnlDock, DockState.DockBottomAutoHide);
            Windows.ScreenshotForm.Hide();

            pnlDock.Visible = true;
        }

        #endregion

        #region Events

        public delegate void ClipboardchangeEventHandler();

        public static event ClipboardchangeEventHandler ClipboardChanged
        {
            add =>
                _clipboardChangedEvent =
                    (ClipboardchangeEventHandler)Delegate.Combine(_clipboardChangedEvent, value);
            remove =>
                _clipboardChangedEvent =
                    (ClipboardchangeEventHandler)Delegate.Remove(_clipboardChangedEvent, value);
        }

        #endregion

        private void ViewMenu_Opening(object sender, EventArgs e)
        {
            viewMenu.mMenView_DropDownOpening(sender, e);
        }

        private void mainFileMenu1_DropDownOpening(object sender, EventArgs e)
        {
            fileMenu.mMenFile_DropDownOpening(sender, e);
        }
    }
}