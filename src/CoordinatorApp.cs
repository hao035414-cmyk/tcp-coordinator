// ============================================================================
//  TcpCoordinatorApp.cs —— TCP 协调助手（桌面版）
//
//  双击即用：既可以“启动服务”当主机，也可以连别人的协调服务。
//  界面：在线成员 / 消息收发 / 租约锁 / 共享板，全部实时刷新。
//
//  编译（Windows 自带 .NET Framework 4.x 编译器，零安装）：
//      csc /target:winexe /main:TcpCoordinatorApp.Program ^
//          /out:TcpCoordinatorApp.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
//          TcpCoordinatorApp.cs TcpCoordinator.cs
//
//  注：服务端实现复用同目录的 TcpCoordinator.cs（已通过 43 项协议测试）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TcpCoordinator;

namespace TcpCoordinatorApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 先声明 DPI 感知，否则在 125%/150% 缩放屏上整个窗口会被系统拉伸而发虚
            EnableDpiAwareness();

            // 桌面上没有控制台，内嵌服务端的日志直接丢弃，避免写入无效句柄
            try
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
            }
            catch (Exception)
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                SetProcessDPIAware();
            }
            catch (Exception)
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    // ------------------------------------------------------------------------
    //  设计系统
    // ------------------------------------------------------------------------
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(13, 16, 21);
        public static readonly Color Card = Color.FromArgb(21, 26, 34);
        public static readonly Color CardBorder = Color.FromArgb(35, 42, 53);
        public static readonly Color Input = Color.FromArgb(15, 20, 27);
        public static readonly Color InputBorder = Color.FromArgb(43, 51, 63);
        public static readonly Color Chat = Color.FromArgb(17, 22, 29);
        public static readonly Color Text = Color.FromArgb(231, 236, 243);
        public static readonly Color Muted = Color.FromArgb(139, 149, 167);
        public static readonly Color Faint = Color.FromArgb(92, 102, 117);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color AccentHover = Color.FromArgb(104, 160, 255);
        public static readonly Color AccentSoft = Color.FromArgb(29, 43, 68);
        public static readonly Color Good = Color.FromArgb(59, 214, 138);
        public static readonly Color Warn = Color.FromArgb(255, 176, 32);
        public static readonly Color Bad = Color.FromArgb(255, 94, 94);
        public static readonly Color Self = Color.FromArgb(122, 209, 160);

        public static readonly Font Ui;
        public static readonly Font UiBold;
        public static readonly Font Small;
        public static readonly Font SmallBold;
        public static readonly Font Title;
        public static readonly Font CardTitle;

        static Theme()
        {
            string family = PickFamily();
            Ui = new Font(family, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            UiBold = new Font(family, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            Small = new Font(family, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            SmallBold = new Font(family, 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            Title = new Font(family, 15f, FontStyle.Bold, GraphicsUnit.Point);
            CardTitle = new Font(family, 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        }

        private static string PickFamily()
        {
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" };
            foreach (string name in candidates)
            {
                try
                {
                    using (FontFamily probe = new FontFamily(name))
                    {
                        return name;
                    }
                }
                catch (Exception)
                {
                }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        public static Color AvatarColor(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Muted;
            }
            int hash = 0;
            foreach (char ch in text)
            {
                hash = (hash * 31 + ch) & 0x7fffffff;
            }
            Color[] palette =
            {
                Color.FromArgb(76, 141, 255), Color.FromArgb(59, 190, 160),
                Color.FromArgb(217, 142, 74), Color.FromArgb(166, 120, 240),
                Color.FromArgb(226, 108, 138), Color.FromArgb(90, 176, 220)
            };
            return palette[hash % palette.Length];
        }
    }

    internal static class Gfx
    {
        public static GraphicsPath Round(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > rect.Width)
            {
                d = rect.Width;
            }
            if (d > rect.Height)
            {
                d = rect.Height;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ------------------------------------------------------------------------
    //  卡片：圆角面板 + 标题 + 细分隔线
    // ------------------------------------------------------------------------
    internal sealed class Card : Panel
    {
        public string Title = string.Empty;
        public string Caption = string.Empty;
        public int Radius = 14;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath path = Gfx.Round(r, Radius))
            using (SolidBrush fill = new SolidBrush(Theme.Card))
            using (Pen border = new Pen(Theme.CardBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            if (Title.Length > 0)
            {
                TextRenderer.DrawText(g, Title, Theme.CardTitle, new Point(18, 14), Theme.Text,
                                      TextFormatFlags.NoPadding);
            }
            if (Caption.Length > 0)
            {
                TextRenderer.DrawText(g, Caption, Theme.Small, new Point(18, 44), Theme.Faint,
                                      TextFormatFlags.NoPadding);
            }

            using (Pen line = new Pen(Color.FromArgb(31, 38, 48)))
            {
                g.DrawLine(line, 18, 72, Width - 18, 72);
            }
        }
    }

    // ------------------------------------------------------------------------
    //  按钮
    // ------------------------------------------------------------------------
    internal sealed class PillButton : Control
    {
        public enum Kind { Primary, Secondary, Ghost }

        private bool _hover;
        private bool _down;
        private Kind _kind = Kind.Secondary;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.UiBold;
            Cursor = Cursors.Hand;
        }

        public Kind Style
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        public bool EnabledVisual
        {
            get { return Enabled; }
            set { Enabled = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _down = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color fill;
            Color text;
            Color border = Color.Empty;

            if (!Enabled)
            {
                fill = _kind == Kind.Ghost ? Theme.Card : Color.FromArgb(28, 34, 44);
                text = Theme.Faint;
            }
            else if (_kind == Kind.Primary)
            {
                fill = _down ? Color.FromArgb(58, 118, 226) : (_hover ? Theme.AccentHover : Theme.Accent);
                text = Color.White;
            }
            else if (_kind == Kind.Ghost)
            {
                fill = _hover ? Color.FromArgb(31, 38, 48) : Theme.Card;
                text = Theme.Muted;
            }
            else
            {
                fill = _hover ? Color.FromArgb(32, 39, 50) : Color.FromArgb(26, 32, 41);
                text = Theme.Text;
                border = Theme.InputBorder;
            }

            using (GraphicsPath path = Gfx.Round(r, 9))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
                if (border != Color.Empty)
                {
                    using (Pen pen = new Pen(border))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            TextRenderer.DrawText(g, Text, Font, r, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------------
    //  输入框：圆角背景 + 无边框 TextBox + 提示文字
    // ------------------------------------------------------------------------
    internal sealed class Field : Panel
    {
        private const int EM_SETCUEBANNER = 0x1501;
        private string _cue = string.Empty;

        public readonly TextBox Box;

        public Field()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Input;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Ui;
            Box.HandleCreated += delegate { ApplyCue(); };
            Controls.Add(Box);
        }

        public string Cue
        {
            get { return _cue; }
            set
            {
                _cue = value ?? string.Empty;
                ApplyCue();
            }
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        private bool _enabledVisual = true;

        public bool EnabledVisual
        {
            get { return _enabledVisual; }
            set
            {
                _enabledVisual = value;
                Box.Enabled = value;
                Box.ForeColor = value ? Theme.Text : Theme.Faint;
                Invalidate();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCue();
        }

        private void ApplyCue()
        {
            try
            {
                if (Box.IsHandleCreated)
                {
                    SendMessage(Box.Handle, EM_SETCUEBANNER, (IntPtr)1, _cue);
                }
            }
            catch (Exception)
            {
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fillColor = _enabledVisual ? Theme.Input : Color.FromArgb(19, 24, 31);
            Color lineColor = !_enabledVisual ? Theme.CardBorder : (Box.Focused ? Theme.Accent : Theme.InputBorder);
            using (GraphicsPath path = Gfx.Round(r, 9))
            using (SolidBrush fill = new SolidBrush(fillColor))
            using (Pen pen = new Pen(lineColor))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Box.SetBounds(11, (Height - Box.Height) / 2, Math.Max(10, Width - 22), Box.Height);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            Box.SetBounds(11, (Height - Box.Height) / 2, Math.Max(10, Width - 22), Box.Height);
        }
    }

    // ------------------------------------------------------------------------
    //  连接状态胶囊
    // ------------------------------------------------------------------------
    internal sealed class StatusPill : Control
    {
        private string _label = "未连接";
        private Color _dot = Theme.Faint;

        public StatusPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Font = Theme.SmallBold;
        }

        public void Set(string label, Color dot)
        {
            _label = label;
            _dot = dot;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath path = Gfx.Round(r, Height / 2))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(24, 30, 39)))
            using (Pen pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            int cy = Height / 2;
            using (SolidBrush dot = new SolidBrush(_dot))
            {
                g.FillEllipse(dot, 14, cy - 4, 8, 8);
            }
            TextRenderer.DrawText(g, _label, Font, new Rectangle(30, 0, Width - 38, Height), Theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ------------------------------------------------------------------------
    //  网络客户端
    // ------------------------------------------------------------------------
    internal sealed class CoordClient : IDisposable
    {
        private TcpClient _tcp;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _thread;
        private volatile bool _running;

        public event Action<string> LineReceived;
        public event Action<string> Disconnected;

        public bool Connected
        {
            get { return _running; }
        }

        public void Connect(string host, int port, string name, string role)
        {
            Disconnect();

            TcpClient tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Connect(host, port);

            _tcp = tcp;
            NetworkStream stream = tcp.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096);
            _writer.NewLine = "\n";
            _writer.AutoFlush = true;

            _running = true;
            Send("HELLO " + name + " " + role);

            _thread = new Thread(RxLoop);
            _thread.IsBackground = true;
            _thread.Name = "coord-rx";
            _thread.Start();
        }

        private void RxLoop()
        {
            string reason = "连接已关闭";
            try
            {
                while (_running)
                {
                    string line = _reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }
                    Action<string> handler = LineReceived;
                    if (handler != null)
                    {
                        handler(line);
                    }
                }
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            _running = false;
            Action<string> closed = Disconnected;
            if (closed != null)
            {
                closed(reason);
            }
        }

        public void Send(string line)
        {
            try
            {
                StreamWriter writer = _writer;
                if (writer != null)
                {
                    writer.WriteLine(line);
                }
            }
            catch (Exception)
            {
            }
        }

        public void Disconnect()
        {
            _running = false;
            try
            {
                if (_tcp != null)
                {
                    _tcp.Close();
                }
            }
            catch (Exception)
            {
            }
            _tcp = null;
            _writer = null;
            _reader = null;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    // ------------------------------------------------------------------------
    //  分段选择器：主机 / 加入 / 仅服务端
    // ------------------------------------------------------------------------
    internal sealed class Segmented : Control
    {
        private string[] _items = new string[0];
        private int _selected;

        public event EventHandler SelectionChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            Font = Theme.SmallBold;
        }

        public void SetItems(params string[] items)
        {
            _items = items ?? new string[0];
            _selected = 0;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (_items.Length == 0)
                {
                    return;
                }
                int next = Math.Max(0, Math.Min(_items.Length - 1, value));
                if (next == _selected)
                {
                    return;
                }
                _selected = next;
                Invalidate();
                EventHandler handler = SelectionChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_items.Length > 0)
            {
                int width = Math.Max(1, Width / _items.Length);
                SelectedIndex = e.X / width;
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath path = Gfx.Round(r, 9))
            using (SolidBrush fill = new SolidBrush(Theme.Input))
            using (Pen pen = new Pen(Theme.InputBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            if (_items.Length == 0)
            {
                return;
            }

            int segWidth = Width / _items.Length;
            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle seg = new Rectangle(i * segWidth + 3, 3, segWidth - 6, Height - 6);
                if (i == _selected)
                {
                    using (GraphicsPath p = Gfx.Round(seg, 7))
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                    {
                        g.FillPath(b, p);
                    }
                }
                Color color = i == _selected ? Color.White : Theme.Muted;
                TextRenderer.DrawText(g, _items[i], Font, seg, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // ------------------------------------------------------------------------
    //  主窗口
    // ------------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private float _scale = 1f;

        // 状态
        private readonly List<MemberRow> _members = new List<MemberRow>();
        private readonly List<string> _targetValues = new List<string>();
        private readonly List<LockRow> _locks = new List<LockRow>();
        private readonly List<KeyValuePair<string, string>> _board = new List<KeyValuePair<string, string>>();

        private CoordClient _client;
        private Coordinator _server;
        private CancellationTokenSource _serverCts;
        private Thread _serverThread;
        private int _serverPort;
        private string _selfName = string.Empty;
        private string _selfRole = string.Empty;
        private bool _connected;

        private readonly List<LockRow> _pendingLocks = new List<LockRow>();
        private readonly List<KeyValuePair<string, string>> _pendingBoard = new List<KeyValuePair<string, string>>();

        // 控件
        private Label _title;
        private Label _subtitle;
        private StatusPill _status;

        private Card _connectCard;
        private Field _fHost;
        private Field _fPort;
        private Field _fName;
        private Field _fRole;
        private Segmented _mode;
        private PillButton _btnAction;
        private Label _hint;
        private Label _serverState;

        private Card _membersCard;
        private ListBox _memberList;
        private Label _emptyMembers;

        private Card _chatCard;
        private RichTextBox _chat;
        private Card _composerCard;
        private ComboBox _target;
        private Field _input;
        private PillButton _btnSend;

        private Card _locksCard;
        private ListBox _lockList;
        private PillButton _btnLocks;

        private Card _boardCard;
        private ListBox _boardList;
        private PillButton _btnBoard;

        private Label _statusText;
        private System.Windows.Forms.Timer _refresh;

        public MainForm()
        {
            _scale = DetectScale();

            Text = "TCP 协调助手";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            MinimumSize = new Size(S(1120), S(640));

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int width = Math.Min(S(1180), Math.Max(S(860), work.Width - S(80)));
            int height = Math.Min(S(760), Math.Max(S(560), work.Height - S(80)));
            ClientSize = new Size(width, height);

            Icon = BuildIcon();

            BuildUi();
            HookEvents();

            _refresh = new System.Windows.Forms.Timer();
            _refresh.Interval = 2000;
            _refresh.Tick += delegate { RefreshFromServer(); };
        }

        private static float DetectScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float scale = g.DpiX / 96f;
                    return scale < 1f ? 1f : scale;
                }
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 注意：句柄创建阶段 DeviceDpi 可能还没有切换到当前显示器，统一用屏幕 DPI，
            // 避免把构造函数里算好的缩放比覆盖成 1.0。
            _scale = DetectScale();

            if (_memberList != null)
            {
                _memberList.ItemHeight = S(54);
                _lockList.ItemHeight = S(46);
                _boardList.ItemHeight = S(46);
            }

            TryDarkTitleBar();
            LayoutAll();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            TryDarkTitleBar();
        }

        private void TryDarkTitleBar()
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
            }
            catch (Exception)
            {
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static Icon BuildIcon()
        {
            try
            {
                Bitmap bmp = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath path = Gfx.Round(new Rectangle(1, 1, 30, 30), 8))
                    using (SolidBrush fill = new SolidBrush(Theme.Accent))
                    {
                        g.FillPath(fill, path);
                    }
                    using (Font f = new Font("Segoe UI", 13f, FontStyle.Bold))
                    {
                        TextRenderer.DrawText(g, "TC", f, new Rectangle(0, 1, 32, 30), Color.White,
                                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- 构建界面

        private void BuildUi()
        {
            _title = new Label();
            _title.Text = "TCP 协调助手";
            _title.Font = Theme.Title;
            _title.ForeColor = Theme.Text;
            _title.AutoSize = true;
            _title.BackColor = Theme.Bg;
            Controls.Add(_title);

            _subtitle = new Label();
            _subtitle.Text = "多进程 · 多脚本的会合、广播与互斥";
            _subtitle.Font = Theme.Small;
            _subtitle.ForeColor = Theme.Faint;
            _subtitle.AutoSize = true;
            _subtitle.BackColor = Theme.Bg;
            Controls.Add(_subtitle);

            _status = new StatusPill();
            Controls.Add(_status);

            // ---- 连接卡片
            _connectCard = new Card();
            _connectCard.Title = "连接";
            Controls.Add(_connectCard);

            _mode = new Segmented();
            _mode.SetItems("主机", "加入", "仅服务端");
            _connectCard.Controls.Add(_mode);

            _fHost = NewField("127.0.0.1", "地址");
            _fPort = NewField("9000", "端口");
            _fName = NewField("alice", "名字");
            _fRole = NewField("worker", "角色");
            _connectCard.Controls.Add(_fHost);
            _connectCard.Controls.Add(_fPort);
            _connectCard.Controls.Add(_fName);
            _connectCard.Controls.Add(_fRole);

            _btnAction = new PillButton();
            _btnAction.Text = "启动并连接";
            _btnAction.Style = PillButton.Kind.Primary;
            _connectCard.Controls.Add(_btnAction);

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Text = "启动服务后会自动连接，也可以填别人的 IP。";
            _hint.Font = Theme.Small;
            _hint.ForeColor = Theme.Faint;
            _hint.BackColor = Theme.Card;
            _connectCard.Controls.Add(_hint);

            _serverState = new Label();
            _serverState.Text = "服务未启动";
            _serverState.Font = Theme.Small;
            _serverState.ForeColor = Theme.Faint;
            _serverState.BackColor = Theme.Card;
            _connectCard.Controls.Add(_serverState);

            // ---- 成员卡片
            _membersCard = new Card();
            _membersCard.Title = "在线成员";
            Controls.Add(_membersCard);

            _memberList = NewListBox();
            _memberList.DrawMode = DrawMode.OwnerDrawFixed;
            _memberList.ItemHeight = S(54);
            _memberList.DrawItem += DrawMember;
            _membersCard.Controls.Add(_memberList);

            _emptyMembers = new Label();
            _emptyMembers.Text = "还没有成员加入";
            _emptyMembers.Font = Theme.Small;
            _emptyMembers.ForeColor = Theme.Faint;
            _emptyMembers.BackColor = Theme.Card;
            _emptyMembers.TextAlign = ContentAlignment.MiddleCenter;
            _membersCard.Controls.Add(_emptyMembers);

            // ---- 聊天卡片
            _chatCard = new Card();
            _chatCard.Title = "消息";
            Controls.Add(_chatCard);

            _chat = new RichTextBox();
            _chat.BorderStyle = BorderStyle.None;
            _chat.BackColor = Theme.Chat;
            _chat.ForeColor = Theme.Text;
            _chat.Font = Theme.Ui;
            _chat.ReadOnly = true;
            _chat.DetectUrls = false;
            _chat.WordWrap = true;
            _chat.ScrollBars = RichTextBoxScrollBars.Vertical;
            _chat.HideSelection = false;
            _chatCard.Controls.Add(_chat);

            // ---- 输入卡片
            _composerCard = new Card();
            Controls.Add(_composerCard);

            _target = new ComboBox();
            _target.DropDownStyle = ComboBoxStyle.DropDownList;
            _target.FlatStyle = FlatStyle.Flat;
            _target.BackColor = Theme.Input;
            _target.ForeColor = Theme.Text;
            _target.Font = Theme.Ui;
            _target.DrawMode = DrawMode.OwnerDrawFixed;
            _target.ItemHeight = 22;
            _target.DrawItem += DrawTarget;
            _composerCard.Controls.Add(_target);

            _input = NewField(string.Empty, "说点什么，回车发送");
            _composerCard.Controls.Add(_input);

            _btnSend = new PillButton();
            _btnSend.Text = "发送";
            _btnSend.Style = PillButton.Kind.Primary;
            _composerCard.Controls.Add(_btnSend);

            // ---- 锁卡片
            _locksCard = new Card();
            _locksCard.Title = "租约锁";
            Controls.Add(_locksCard);

            _lockList = NewListBox();
            _lockList.DrawMode = DrawMode.OwnerDrawFixed;
            _lockList.ItemHeight = S(46);
            _lockList.DrawItem += DrawLock;
            _locksCard.Controls.Add(_lockList);

            _btnLocks = new PillButton();
            _btnLocks.Text = "刷新";
            _btnLocks.Style = PillButton.Kind.Ghost;
            _locksCard.Controls.Add(_btnLocks);

            // ---- 共享板卡片
            _boardCard = new Card();
            _boardCard.Title = "共享板";
            Controls.Add(_boardCard);

            _boardList = NewListBox();
            _boardList.DrawMode = DrawMode.OwnerDrawFixed;
            _boardList.ItemHeight = S(46);
            _boardList.DrawItem += DrawBoard;
            _boardCard.Controls.Add(_boardList);

            _btnBoard = new PillButton();
            _btnBoard.Text = "刷新";
            _btnBoard.Style = PillButton.Kind.Ghost;
            _boardCard.Controls.Add(_btnBoard);

            _statusText = new Label();
            _statusText.Text = "就绪";
            _statusText.Font = Theme.Small;
            _statusText.ForeColor = Theme.Faint;
            _statusText.BackColor = Theme.Bg;
            Controls.Add(_statusText);

            ApplyMode();
            RebuildTargets();
        }

        private Field NewField(string text, string cue)
        {
            Field field = new Field();
            field.Cue = cue;
            field.Text = text;
            return field;
        }

        private ListBox NewListBox()
        {
            ListBox list = new ListBox();
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Card;
            list.ForeColor = Theme.Text;
            list.Font = Theme.Ui;
            list.IntegralHeight = false;
            return list;
        }

        private void HookEvents()
        {
            _btnAction.Click += delegate { OnActionClicked(); };
            _mode.SelectionChanged += delegate { OnModeChanged(); };
            _btnSend.Click += delegate { SendMessageNow(); };
            _btnLocks.Click += delegate { SendLine("LOCKS"); };
            _btnBoard.Click += delegate { SendLine("BOARD"); };

            _input.Box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendMessageNow();
                }
            };

            _lockList.DoubleClick += delegate { ReleaseSelectedLock(); };
            Resize += delegate { LayoutAll(); };
            FormClosing += delegate { ShutdownAll(); };
        }

        // ---------------------------------------------------------------- 布局

        private void LayoutAll()
        {
            if (_title == null)
            {
                return;
            }

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int pad = S(20);
            int leftW = S(300);
            int rightW = S(300);
            int gap = S(16);
            int bodyTop = S(84);
            int barH = S(44);
            int contentBottom = h - barH;

            _title.Location = new Point(pad, S(12));
            _subtitle.Location = new Point(pad + S(2), S(52));
            _status.SetBounds(w - pad - S(170), S(24), S(170), S(32));

            int leftX = pad;
            int rightX = w - pad - rightW;
            int centerX = leftX + leftW + gap;
            int centerW = rightX - gap - centerX;

            int connectH = S(300);
            _connectCard.SetBounds(leftX, bodyTop, leftW, connectH);

            int cw = leftW;
            _mode.SetBounds(S(18), S(46), cw - S(36), S(36));
            _fHost.SetBounds(S(18), S(94), S(160), S(36));
            _fPort.SetBounds(S(186), S(94), cw - S(186 + 18), S(36));
            _fName.SetBounds(S(18), S(138), S(160), S(36));
            _fRole.SetBounds(S(186), S(138), cw - S(186 + 18), S(36));
            _btnAction.SetBounds(S(18), S(186), cw - S(36), S(40));
            _hint.SetBounds(S(18), S(236), cw - S(36), S(24));
            _serverState.SetBounds(S(18), S(262), cw - S(36), S(24));

            int membersTop = bodyTop + connectH + gap;
            _membersCard.SetBounds(leftX, membersTop, leftW, Math.Max(S(160), contentBottom - membersTop));
            _memberList.SetBounds(S(10), S(82), leftW - S(20), _membersCard.Height - S(90));
            _emptyMembers.SetBounds(S(12), S(82), leftW - S(24), _membersCard.Height - S(94));

            int chatH = contentBottom - bodyTop - S(72) - gap;
            _chatCard.SetBounds(centerX, bodyTop, centerW, Math.Max(S(200), chatH));
            _chat.SetBounds(S(14), S(82), centerW - S(28), _chatCard.Height - S(94));

            _composerCard.SetBounds(centerX, bodyTop + _chatCard.Height + gap, centerW, S(72));
            int sendW = S(96);
            _target.SetBounds(S(14), S(20), S(132), S(32));
            _input.SetBounds(S(158), S(18), Math.Max(S(90), centerW - S(158 + 14) - sendW - S(12)), S(36));
            _btnSend.SetBounds(centerW - S(14) - sendW, S(18), sendW, S(36));

            int lockH = (contentBottom - bodyTop - gap) / 2;
            _locksCard.SetBounds(rightX, bodyTop, rightW, lockH);
            _lockList.SetBounds(S(10), S(82), rightW - S(20), lockH - S(90));
            _btnLocks.SetBounds(rightW - S(14) - S(64), S(14), S(64), S(26));

            int boardTop = bodyTop + lockH + gap;
            _boardCard.SetBounds(rightX, boardTop, rightW, Math.Max(S(160), contentBottom - boardTop));
            _boardList.SetBounds(S(10), S(82), rightW - S(20), _boardCard.Height - S(90));
            _btnBoard.SetBounds(rightW - S(14) - S(64), S(14), S(64), S(26));

            _statusText.SetBounds(pad, h - S(34), w - pad * 2, S(24));
        }

        // ---------------------------------------------------------------- 服务端

        private void StartServer(bool autoConnect)
        {
            if (_server != null)
            {
                StopServer();
                return;
            }

            int port;
            if (!int.TryParse(_fPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                SetStatus("端口需要 1~65535 之间的数字", Theme.Warn);
                return;
            }

            Options options;
            string error;
            string[] args = { "--port", port.ToString(CultureInfo.InvariantCulture) };
            if (!Options.TryParse(args, out options, out error))
            {
                SetStatus("启动失败：" + error, Theme.Bad);
                return;
            }

            _serverCts = new CancellationTokenSource();
            try
            {
                _server = new Coordinator(options, _serverCts.Token);
            }
            catch (Exception ex)
            {
                _serverCts.Dispose();
                _serverCts = null;
                SetStatus("启动失败：" + ex.Message, Theme.Bad);
                return;
            }

            _serverPort = port;
            _fPort.Text = port.ToString(CultureInfo.InvariantCulture);
            _serverThread = new Thread(delegate()
            {
                try
                {
                    _server.Run();
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)delegate
                    {
                        SetStatus("服务异常：" + ex.Message, Theme.Bad);
                    });
                }
            });
            _serverThread.IsBackground = true;
            _serverThread.Name = "coord-server";
            _serverThread.Start();

            RefreshFieldStates();
            UpdateActionButton();
            _serverState.Text = "服务运行中 · 端口 " + port;
            _serverState.ForeColor = Theme.Good;
            SetStatus(autoConnect ? "服务已启动，正在连接本机…" : "服务已启动，等待客户端接入", Theme.Accent);

            if (autoConnect)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(350);
                    BeginInvoke((Action)delegate
                    {
                        _fHost.Text = "127.0.0.1";
                        if (!_connected)
                        {
                            DoConnect();
                        }
                    });
                });
            }
        }

        private void StopServer()
        {
            try
            {
                if (_serverCts != null)
                {
                    _serverCts.Cancel();
                }
            }
            catch (Exception)
            {
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(400);
                try
                {
                    if (_serverThread != null)
                    {
                        _serverThread.Join(1500);
                    }
                    if (_server != null)
                    {
                        _server.Dispose();
                    }
                }
                catch (Exception)
                {
                }

                BeginInvoke((Action)delegate
                {
                    _server = null;
                    _serverThread = null;
                    if (_serverCts != null)
                    {
                        _serverCts.Dispose();
                        _serverCts = null;
                    }
                    _serverState.Text = "服务未启动";
                    _serverState.ForeColor = Theme.Faint;
                    RefreshFieldStates();
                    UpdateActionButton();
                    SetStatus("服务已停止", Theme.Muted);
                });
            });
        }

        // ---------------------------------------------------------------- 客户端

        private string ModeName()
        {
            switch (_mode.SelectedIndex)
            {
                case 0: return "主机";
                case 1: return "加入";
                default: return "仅服务端";
            }
        }

        private void OnModeChanged()
        {
            if (_server != null)
            {
                StopServer();
            }
            if (_connected)
            {
                DoDisconnect(null);
            }
            ApplyMode();
            SetStatus("已切换到「" + ModeName() + "」模式", Theme.Muted);
        }

        private void RefreshFieldStates()
        {
            int mode = _mode.SelectedIndex;
            _fHost.EnabledVisual = mode == 1;
            _fName.EnabledVisual = mode != 2;
            _fRole.EnabledVisual = mode != 2;
            _fPort.EnabledVisual = _server == null;
        }

        private void ApplyMode()
        {
            int mode = _mode.SelectedIndex;
            RefreshFieldStates();

            if (mode == 0)
            {
                _fHost.Text = "127.0.0.1";
                _hint.Text = "本机开服务并自动上线，别人填你的 IP 连进来。";
            }
            else if (mode == 1)
            {
                _hint.Text = "填对方的 IP 和端口，加入它。";
            }
            else
            {
                _hint.Text = "只监听端口，不以成员身份上线。";
            }

            UpdateActionButton();
        }

        private void UpdateActionButton()
        {
            int mode = _mode.SelectedIndex;

            if (mode == 2)
            {
                _btnAction.Text = _server != null ? "停止服务" : "启动服务";
            }
            else if (mode == 0)
            {
                _btnAction.Text = _server != null ? "停止" : "启动并连接";
            }
            else
            {
                _btnAction.Text = _connected ? "断开" : "连接";
            }

            _btnAction.Style = (_server != null || _connected) ? PillButton.Kind.Secondary : PillButton.Kind.Primary;
        }

        private void OnActionClicked()
        {
            int mode = _mode.SelectedIndex;

            if (mode == 2)
            {
                if (_server != null)
                {
                    StopServer();
                }
                else
                {
                    StartServer(false);
                }
                return;
            }

            if (mode == 0)
            {
                if (_server != null)
                {
                    StopServer();
                    if (_connected)
                    {
                        DoDisconnect("已停止");
                    }
                }
                else
                {
                    _fHost.Text = "127.0.0.1";
                    StartServer(true);
                }
                return;
            }

            if (_connected)
            {
                DoDisconnect("已断开");
            }
            else
            {
                DoConnect();
            }
        }

        private void DoConnect()
        {
            string host = _fHost.Text.Trim();
            int port;
            string name = _fName.Text.Trim();
            string role = _fRole.Text.Trim();

            if (host.Length == 0)
            {
                SetStatus("请填写服务地址", Theme.Warn);
                return;
            }
            if (!int.TryParse(_fPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                SetStatus("端口需要 1~65535 之间的数字", Theme.Warn);
                return;
            }
            if (name.Length == 0)
            {
                SetStatus("请填写名字", Theme.Warn);
                return;
            }
            if (role.Length == 0)
            {
                role = "peer";
            }

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            CoordClient client = new CoordClient();
            client.LineReceived += delegate(string line)
            {
                BeginInvoke((Action)delegate { HandleServerLine(line); });
            };
            client.Disconnected += delegate(string reason)
            {
                BeginInvoke((Action)delegate
                {
                    if (_connected || _client == client)
                    {
                        DoDisconnect("连接断开：" + reason);
                    }
                });
            };

            try
            {
                client.Connect(host, port, name, role);
            }
            catch (Exception ex)
            {
                client.Dispose();
                SetStatus("连接失败：" + ex.Message + "（先确认对方已点「启动并连接」，且端口一致）", Theme.Bad);
                return;
            }

            _client = client;
            _selfName = name;
            _selfRole = role;
            _connected = true;
            UpdateActionButton();
            _status.Set("已连接", Theme.Good);
            AppendSystem("已连接到 " + host + ":" + port + "，身份 " + name + "（" + role + "）");
            SetStatus("已连接 " + host + ":" + port, Theme.Good);

            _refresh.Start();
            RefreshFromServer();
        }

        private void DoDisconnect(string message)
        {
            _refresh.Stop();
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
            _connected = false;
            UpdateActionButton();
            _status.Set("未连接", Theme.Faint);
            _members.Clear();
            _memberList.Items.Clear();
            _locks.Clear();
            _lockList.Items.Clear();
            _board.Clear();
            _boardList.Items.Clear();
            if (_emptyMembers != null)
            {
                _emptyMembers.Visible = true;
            }
            RebuildTargets();
            if (!string.IsNullOrEmpty(message))
            {
                AppendSystem(message);
                SetStatus(message, Theme.Muted);
            }
        }

        private void ShutdownAll()
        {
            try
            {
                _refresh.Stop();
            }
            catch (Exception)
            {
            }
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
            try
            {
                if (_serverCts != null)
                {
                    _serverCts.Cancel();
                }
                if (_serverThread != null)
                {
                    _serverThread.Join(800);
                }
                if (_server != null)
                {
                    _server.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        private void SendLine(string line)
        {
            CoordClient client = _client;
            if (client != null && _connected)
            {
                client.Send(line);
            }
        }

        private void RefreshFromServer()
        {
            if (!_connected)
            {
                return;
            }
            SendLine("LIST");
            SendLine("LOCKS");
            SendLine("BOARD");
        }

        // ---------------------------------------------------------------- 协议处理

        private void HandleServerLine(string line)
        {
            if (!_connected)
            {
                return;
            }

            try
            {
                if (line.StartsWith("WELCOME ", StringComparison.Ordinal))
                {
                    return;
                }
                if (line.StartsWith("PING", StringComparison.Ordinal))
                {
                    SendLine("PONG");
                    return;
                }
                if (line.StartsWith("PONG", StringComparison.Ordinal))
                {
                    return;
                }
                if (line.StartsWith("PEERS ", StringComparison.Ordinal))
                {
                    ApplyPeers(line);
                    return;
                }
                if (line.StartsWith("PRESENCE ", StringComparison.Ordinal))
                {
                    SendLine("LIST");
                    return;
                }
                if (line.StartsWith("MSG ", StringComparison.Ordinal))
                {
                    ApplyMessage(line);
                    return;
                }
                if (line.StartsWith("ERR ", StringComparison.Ordinal))
                {
                    AppendError(line.Substring(4));
                    return;
                }
                if (line == "LOCKS" || line.StartsWith("LOCKS ", StringComparison.Ordinal))
                {
                    _pendingLocks.Clear();
                    return;
                }
                if (line.StartsWith("LK ", StringComparison.Ordinal))
                {
                    ApplyLock(line);
                    return;
                }
                if (line.StartsWith("BOARD", StringComparison.Ordinal))
                {
                    _pendingBoard.Clear();
                    return;
                }
                if (line.StartsWith("KV ", StringComparison.Ordinal))
                {
                    ApplyBoard(line);
                    return;
                }
                if (line == "END")
                {
                    FlushCollections();
                    return;
                }
                if (line.StartsWith("BYE", StringComparison.Ordinal))
                {
                    DoDisconnect("服务端断开：" + line);
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendError("界面处理出错：" + ex.Message);
            }
        }

        private void ApplyPeers(string line)
        {
            _members.Clear();
            string[] parts = line.Split(' ');
            for (int i = 2; i < parts.Length; i++)
            {
                string item = parts[i];
                if (item.Length == 0)
                {
                    continue;
                }
                string name = item;
                string role = "peer";
                int colon = item.IndexOf(':');
                if (colon > 0)
                {
                    name = item.Substring(0, colon);
                    role = item.Substring(colon + 1);
                }
                MemberRow row = new MemberRow();
                row.Name = name;
                row.Role = role;
                row.Self = string.Equals(name, _selfName, StringComparison.Ordinal);
                row.Color = Theme.AvatarColor(name);
                row.Initial = name.Substring(0, 1).ToUpperInvariant();
                _members.Add(row);
            }

            _memberList.Items.Clear();
            foreach (MemberRow row in _members)
            {
                _memberList.Items.Add(row);
            }
            _membersCard.Caption = _members.Count.ToString(CultureInfo.InvariantCulture) + " 人在线";
            _membersCard.Invalidate();
            _emptyMembers.Visible = _members.Count == 0;
            RebuildTargets();
        }

        private void ApplyMessage(string line)
        {
            string[] parts = line.Split(new char[] { ' ' }, 5);
            if (parts.Length < 5)
            {
                return;
            }
            string from = parts[1];
            string role = parts[2];
            string target = parts[3];
            string text = parts[4];
            bool self = string.Equals(from, _selfName, StringComparison.Ordinal);
            AppendChat(from, role, target, text, self);
        }

        private void ApplyLock(string line)
        {
            string[] parts = line.Split(' ');
            if (parts.Length < 4)
            {
                return;
            }
            LockRow row = new LockRow();
            row.Key = parts[1];
            row.Owner = parts[2];
            int ttl;
            row.Ttl = int.TryParse(parts[3], out ttl) ? ttl : 0;
            row.Mine = string.Equals(row.Owner, _selfName, StringComparison.Ordinal);
            _pendingLocks.Add(row);
        }

        private void ApplyBoard(string line)
        {
            int space = line.IndexOf(' ');
            if (space < 0)
            {
                return;
            }
            string rest = line.Substring(space + 1);
            int second = rest.IndexOf(' ');
            string key = second < 0 ? rest : rest.Substring(0, second);
            string value = second < 0 ? string.Empty : rest.Substring(second + 1);
            _pendingBoard.Add(new KeyValuePair<string, string>(key, value));
        }

        private void FlushCollections()
        {
            if (_pendingLocks.Count > 0 || _locks.Count > 0)
            {
                _locks.Clear();
                _locks.AddRange(_pendingLocks);
                _lockList.Items.Clear();
                foreach (LockRow row in _locks)
                {
                    _lockList.Items.Add(row);
                }
                _locksCard.Caption = _locks.Count.ToString(CultureInfo.InvariantCulture) + " 个租约 · 双击可释放";
                _locksCard.Invalidate();
            }

            _board.Clear();
            _board.AddRange(_pendingBoard);
            _boardList.Items.Clear();
            foreach (KeyValuePair<string, string> pair in _board)
            {
                _boardList.Items.Add(pair);
            }
            _boardCard.Caption = _board.Count.ToString(CultureInfo.InvariantCulture) + " 条共享记录";
            _boardCard.Invalidate();
        }

        private void ReleaseSelectedLock()
        {
            if (_lockList.SelectedIndex < 0 || _lockList.SelectedIndex >= _locks.Count)
            {
                return;
            }
            LockRow row = _locks[_lockList.SelectedIndex];
            if (!row.Mine)
            {
                SetStatus("这把锁属于 " + row.Owner + "，不能释放", Theme.Warn);
                return;
            }
            SendLine("UNLOCK " + row.Key);
            SetStatus("已请求释放 " + row.Key, Theme.Muted);
        }

        // ---------------------------------------------------------------- 发送

        private void SendMessageNow()
        {
            if (!_connected)
            {
                SetStatus("还没有连接", Theme.Warn);
                return;
            }

            string text = _input.Text.Trim();
            if (text.Length == 0)
            {
                return;
            }

            string target = "*";
            int index = _target.SelectedIndex;
            if (index >= 0 && index < _targetValues.Count)
            {
                target = _targetValues[index];
            }

            SendLine("SAY " + target + " " + text);
            AppendChat(_selfName, _selfRole, target, text, true);
            _input.Text = string.Empty;
            _input.Box.Focus();
        }

        private void RebuildTargets()
        {
            string previous = null;
            int index = _target.SelectedIndex;
            if (index >= 0 && index < _targetValues.Count)
            {
                previous = _targetValues[index];
            }

            _targetValues.Clear();
            _targetValues.Add("*");

            List<string> labels = new List<string>();
            labels.Add("所有人");

            List<string> roles = new List<string>();
            foreach (MemberRow row in _members)
            {
                if (string.Equals(row.Name, _selfName, StringComparison.Ordinal))
                {
                    continue;
                }
                _targetValues.Add(row.Name);
                labels.Add(row.Name);
                if (!roles.Contains(row.Role))
                {
                    roles.Add(row.Role);
                }
            }
            foreach (string role in roles)
            {
                _targetValues.Add("#" + role);
                labels.Add("#" + role + " 角色");
            }

            _target.BeginUpdate();
            _target.Items.Clear();
            foreach (string label in labels)
            {
                _target.Items.Add(label);
            }
            _target.EndUpdate();

            int restore = 0;
            if (previous != null)
            {
                int found = _targetValues.IndexOf(previous);
                if (found >= 0)
                {
                    restore = found;
                }
            }
            if (_target.Items.Count > 0)
            {
                _target.SelectedIndex = restore;
            }
        }

        // ---------------------------------------------------------------- 绘制

        private void DrawMember(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _memberList.Items.Count)
            {
                return;
            }
            MemberRow row = (MemberRow)_memberList.Items[e.Index];
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = e.Bounds;
            using (SolidBrush clear = new SolidBrush(Theme.Card))
            {
                g.FillRectangle(clear, bounds);
            }
            if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
            {
                Rectangle sel = new Rectangle(bounds.X + 4, bounds.Y + 2, bounds.Width - 8, bounds.Height - 4);
                using (GraphicsPath path = Gfx.Round(sel, 9))
                using (SolidBrush brush = new SolidBrush(Theme.AccentSoft))
                {
                    g.FillPath(brush, path);
                }
            }

            int avatar = S(30);
            int ax = bounds.X + S(10);
            int ay = bounds.Y + (bounds.Height - avatar) / 2;
            using (SolidBrush brush = new SolidBrush(row.Color))
            {
                g.FillEllipse(brush, ax, ay, avatar, avatar);
            }
            TextRenderer.DrawText(g, row.Initial, Theme.SmallBold,
                                  new Rectangle(ax, ay + S(2), avatar, avatar), Color.White,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int textX = ax + avatar + S(10);
            TextRenderer.DrawText(g, row.Name, row.Self ? Theme.UiBold : Theme.Ui,
                                  new Point(textX, bounds.Y + S(9)), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, row.Role, Theme.Small,
                                  new Point(textX, bounds.Y + S(28)), Theme.Muted, TextFormatFlags.NoPadding);

            if (row.Self)
            {
                Rectangle badge = new Rectangle(bounds.Right - S(52), bounds.Y + S(16), S(40), S(20));
                using (GraphicsPath path = Gfx.Round(badge, 8))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(32, 52, 44)))
                {
                    g.FillPath(brush, path);
                }
                TextRenderer.DrawText(g, "自己", Theme.SmallBold, badge, Theme.Self,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawLock(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _lockList.Items.Count)
            {
                return;
            }
            LockRow row = (LockRow)_lockList.Items[e.Index];
            Graphics g = e.Graphics;
            Rectangle bounds = e.Bounds;
            using (SolidBrush clear = new SolidBrush(Theme.Card))
            {
                g.FillRectangle(clear, bounds);
            }
            if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
            {
                using (SolidBrush brush = new SolidBrush(Theme.AccentSoft))
                {
                    g.FillRectangle(brush, bounds);
                }
            }

            TextRenderer.DrawText(g, row.Key, Theme.UiBold, new Point(bounds.X + S(12), bounds.Y + S(7)),
                                  row.Mine ? Theme.Good : Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, row.Owner, Theme.Small, new Point(bounds.X + S(12), bounds.Y + S(26)),
                                  Theme.Muted, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, row.Ttl + "s", Theme.SmallBold,
                                  new Rectangle(bounds.Right - S(60), bounds.Y, S(48), bounds.Height),
                                  row.Ttl <= 10 ? Theme.Warn : Theme.Faint,
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawBoard(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _boardList.Items.Count)
            {
                return;
            }
            KeyValuePair<string, string> pair = (KeyValuePair<string, string>)_boardList.Items[e.Index];
            Graphics g = e.Graphics;
            Rectangle bounds = e.Bounds;
            using (SolidBrush clear = new SolidBrush(Theme.Card))
            {
                g.FillRectangle(clear, bounds);
            }

            TextRenderer.DrawText(g, pair.Key, Theme.SmallBold, new Point(bounds.X + S(12), bounds.Y + S(6)),
                                  Theme.Accent, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, pair.Value, Theme.Ui,
                                  new Rectangle(bounds.X + S(12), bounds.Y + S(23), bounds.Width - S(24), S(20)),
                                  Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawTarget(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _target.Items.Count)
            {
                return;
            }
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush brush = new SolidBrush(selected ? Theme.AccentSoft : Theme.Input))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            TextRenderer.DrawText(e.Graphics, _target.Items[e.Index].ToString(), Theme.Ui,
                                  new Rectangle(e.Bounds.X + S(6), e.Bounds.Y, e.Bounds.Width - S(8), e.Bounds.Height),
                                  Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---------------------------------------------------------------- 输出

        private void AppendChat(string from, string role, string target, string text, bool self)
        {
            AppendRun(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  ", Theme.Small, Theme.Faint);
            AppendRun(from, Theme.UiBold, self ? Theme.Self : Theme.Accent);
            AppendRun("  " + target + "  ", Theme.Small, Theme.Faint);
            AppendRun(text, Theme.Ui, Theme.Text);
            AppendRun("\n", Theme.Ui, Theme.Text);
            ScrollChat();
        }

        private void AppendSystem(string text)
        {
            AppendRun(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  ", Theme.Small, Theme.Faint);
            AppendRun("· " + text + "\n", Theme.Small, Theme.Faint);
            ScrollChat();
        }

        private void AppendError(string text)
        {
            AppendRun(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  ", Theme.Small, Theme.Faint);
            AppendRun("! " + text + "\n", Theme.Small, Theme.Warn);
            ScrollChat();
        }

        private void AppendRun(string text, Font font, Color color)
        {
            int start = _chat.TextLength;
            _chat.SelectionStart = start;
            _chat.SelectionLength = 0;
            _chat.SelectionFont = font;
            _chat.SelectionColor = color;
            _chat.AppendText(text);
        }

        private void ScrollChat()
        {
            _chat.SelectionStart = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.ScrollToCaret();
        }

        private void SetStatus(string text, Color color)
        {
            _statusText.Text = text;
            _statusText.ForeColor = color;
        }

        // ---------------------------------------------------------------- 数据模型

        private sealed class MemberRow
        {
            public string Name;
            public string Role;
            public bool Self;
            public Color Color;
            public string Initial;
        }

        private sealed class LockRow
        {
            public string Key;
            public string Owner;
            public int Ttl;
            public bool Mine;
        }
    }
}
