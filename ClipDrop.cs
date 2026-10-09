using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Layout;
using Microsoft.Win32;

namespace ClipDrop
{
    internal static class Program
    {
        internal const string AppTitle = "ClipDrop";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); }
            catch { }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "ClipDrop_SingleInstance_5C2F", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("ClipDrop 已经在运行了，请看任务栏窗口或右下角托盘图标。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                MainForm form = new MainForm(GetArg(args, "--dir"), GetArg(args, "--crop"), GetArg(args, "--merge"), args != null && Array.IndexOf(args, "--shortcuts") >= 0);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs te)
                {
                    Config.LogError("UI", te.Exception);
                    try
                    {
                        MessageBox.Show(form, "出错了：" + te.Exception.Message + "\n\n详情已写入日志：\n" + Config.LogPath(), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch { }
                };

                Application.Run(form);
            }
        }

        private static string GetArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return (i + 1 < args.Length) ? args[i + 1] : null;
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(name.Length + 1);
            }
            return null;
        }

        internal static Font UiFont()
        {
            string[] wanted = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" };
            try
            {
                HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FontFamily family in FontFamily.Families) installed.Add(family.Name);
                foreach (string name in wanted)
                    if (installed.Contains(name))
                        return new Font(name, 9f, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch { }
            return SystemFonts.MessageBoxFont;
        }

        internal static Icon LoadAppIcon(int size)
        {
            try
            {
                using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream("ClipDropIcon.ico"))
                {
                    if (stream != null) return new Icon(stream, new Size(size, size));
                }
            }
            catch { }
            return null;
        }

        internal static Image LoadLogoImage(int size)
        {
            Icon icon = LoadAppIcon(size);
            if (icon == null) return null;
            try { return icon.ToBitmap(); }
            catch { return null; }
        }

        internal static Icon CreateAppIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (SolidBrush back = new SolidBrush(Color.FromArgb(255, 47, 116, 240)))
                            g.FillEllipse(back, 1, 1, 30, 30);
                        using (Pen p = new Pen(Color.White, 2.6f))
                        {
                            p.StartCap = LineCap.Round;
                            p.EndCap = LineCap.Round;
                            g.DrawLine(p, 16, 8, 16, 19);
                            g.DrawLine(p, 10, 14, 16, 20);
                            g.DrawLine(p, 22, 14, 16, 20);
                            g.DrawLine(p, 9, 25, 23, 25);
                        }
                    }
                    IntPtr handle = bmp.GetHicon();
                    using (Icon tmp = Icon.FromHandle(handle))
                        return (Icon)tmp.Clone();
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }
    }

    internal sealed class Config
    {
        public string RootDir;
        public string Prefix = "Paste";
        public bool AutoCapture = true;
        public bool TopMost = true;
        public bool AutoStart;
        public int Opacity = 100;
        public int BallX = -1;
        public int BallY = -1;
        public bool RootSet;
        public string KeyDelete = "X";
        public string KeyFavorite = "F";
        public string KeyMerge = "E";
        public string KeyCrop = "T";
        public string KeyShot = "3";
        public int ShotMod = 3;

        public static string ModName(int index)
        {
            if (index == 1) return "Ctrl + Shift";
            if (index == 2) return "Alt + Shift";
            if (index == 3) return "Alt";
            return "Ctrl + Alt";
        }
        public string LastDir = string.Empty;

        public static Keys ParseKey(string value, Keys fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string t = value.Trim();
            if (t.Length == 0) return fallback;
            char c = char.ToUpperInvariant(t[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return (Keys)(int)c;
            return fallback;
        }
        public string Recent = string.Empty;
        public int WinX = -1;
        public int WinY = -1;
        public int WinW;
        public int WinH;

        public readonly Dictionary<string, List<string>> Favorites =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static string FavKey(string dir)
        {
            try
            {
                string norm = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant();
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(norm));
                return "fav_" + b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FavDirFromKey(string key)
        {
            try
            {
                string b64 = key.Substring(4).Replace('-', '+').Replace('_', '/');
                while (b64.Length % 4 != 0) b64 += "=";
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                return string.Empty;
            }
        }

        public List<string> GetFavorites(string dir)
        {
            List<string> list;
            if (Favorites.TryGetValue(FavKey(dir), out list)) return list;
            return new List<string>();
        }

        public bool IsFavorite(string dir, string fileName)
        {
            List<string> list = GetFavorites(dir);
            foreach (string n in list)
                if (string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool ToggleFavorite(string dir, string fileName)
        {
            string key = FavKey(dir);
            if (key.Length == 0) return false;
            List<string> list;
            if (!Favorites.TryGetValue(key, out list))
            {
                list = new List<string>();
                Favorites[key] = list;
            }
            bool added = false;
            int found = -1;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], fileName, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            if (found >= 0) list.RemoveAt(found);
            else { list.Add(fileName); added = true; }
            if (list.Count == 0) Favorites.Remove(key);
            return added;
        }

        public void RemoveFavorite(string dir, string fileName)
        {
            string key = FavKey(dir);
            List<string> list;
            if (!Favorites.TryGetValue(key, out list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (string.Equals(list[i], fileName, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
            if (list.Count == 0) Favorites.Remove(key);
        }

        public void ClearFavorites(string dir)
        {
            string key = FavKey(dir);
            if (key.Length > 0) Favorites.Remove(key);
        }

        public List<string> RecentList()
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(Recent)) return list;
            foreach (string s in Recent.Split(';'))
            {
                string t = s.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public void PushRecent(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            List<string> list = RecentList();
            for (int i = list.Count - 1; i >= 0; i--)
                if (string.Equals(list[i].TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
            list.Insert(0, dir);
            while (list.Count > 8) list.RemoveAt(list.Count - 1);
            Recent = string.Join(";", list.ToArray());
            LastDir = dir;
        }
        public Color BgColor = Color.FromArgb(255, 30, 30, 34);

        public static string DefaultRoot()
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pics))
                pics = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(pics, "ClipDrop");
        }

        private static string IniPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipDrop");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.ini");
        }

        public static string LogPath()
        {
            return Path.Combine(Path.GetDirectoryName(IniPath()), "error.log");
        }

        public static void LogError(string where, Exception ex)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "] ====");
                Exception e = ex;
                int depth = 0;
                while (e != null && depth < 6)
                {
                    sb.AppendLine(e.GetType().FullName + ": " + e.Message);
                    sb.AppendLine(e.StackTrace);
                    if (e.InnerException != null) sb.AppendLine("--- inner ---");
                    e = e.InnerException;
                    depth++;
                }
                File.AppendAllText(LogPath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static Config Load()
        {
            Config c = new Config();
            c.RootDir = DefaultRoot();
            try
            {
                string path = IniPath();
                if (!File.Exists(path)) return c;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if ((key.Equals("root", StringComparison.OrdinalIgnoreCase) || key.Equals("dir", StringComparison.OrdinalIgnoreCase))
                        && value.Length > 0)
                    {
                        c.RootDir = value;
                        c.RootSet = true;
                    }
                    else if (key.Equals("rootset", StringComparison.OrdinalIgnoreCase)) c.RootSet = (value == "1");
                    else if (key.Equals("prefix", StringComparison.OrdinalIgnoreCase) && value.Length > 0) c.Prefix = value;
                    else if (key.Equals("auto", StringComparison.OrdinalIgnoreCase)) c.AutoCapture = (value == "1");
                    else if (key.Equals("topmost", StringComparison.OrdinalIgnoreCase)) c.TopMost = (value == "1");
                    else if (key.Equals("autostart", StringComparison.OrdinalIgnoreCase)) c.AutoStart = (value == "1");
                    else if (key.Equals("ballx", StringComparison.OrdinalIgnoreCase))
                    {
                        int bx;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bx)) c.BallX = bx;
                    }
                    else if (key.Equals("bally", StringComparison.OrdinalIgnoreCase))
                    {
                        int by;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out by)) c.BallY = by;
                    }
                    else if (key.Equals("keydelete", StringComparison.OrdinalIgnoreCase)) c.KeyDelete = value;
                    else if (key.Equals("keyfav", StringComparison.OrdinalIgnoreCase)) c.KeyFavorite = value;
                    else if (key.Equals("keymerge", StringComparison.OrdinalIgnoreCase)) c.KeyMerge = value;
                    else if (key.Equals("keycrop", StringComparison.OrdinalIgnoreCase)) c.KeyCrop = value;
                    else if (key.Equals("keyshot", StringComparison.OrdinalIgnoreCase)) c.KeyShot = value;
                    else if (key.Equals("shotmod", StringComparison.OrdinalIgnoreCase))
                    {
                        int sm;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sm)) c.ShotMod = sm;
                    }
                    else if (key.Equals("lastdir", StringComparison.OrdinalIgnoreCase)) c.LastDir = value;
                    else if (key.Equals("recent", StringComparison.OrdinalIgnoreCase)) c.Recent = value;
                    else if (key.StartsWith("fav_", StringComparison.OrdinalIgnoreCase))
                    {
                        string dir = FavDirFromKey(key);
                        if (dir.Length > 0)
                        {
                            List<string> names = new List<string>();
                            foreach (string n in value.Split(';'))
                            {
                                string t = n.Trim();
                                if (t.Length > 0) names.Add(t);
                            }
                            c.Favorites[key] = names;
                        }
                    }
                    else if (key.Equals("winx", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) c.WinX = v;
                    }
                    else if (key.Equals("winy", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) c.WinY = v;
                    }
                    else if (key.Equals("winw", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) c.WinW = v;
                    }
                    else if (key.Equals("winh", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) c.WinH = v;
                    }
                    else if (key.Equals("opacity", StringComparison.OrdinalIgnoreCase))
                    {
                        int op;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out op))
                        {
                            if (op < 10) op = 10;
                            if (op > 100) op = 100;
                            c.Opacity = op;
                        }
                    }
                    else if (key.Equals("bg", StringComparison.OrdinalIgnoreCase) && value.Length == 6)
                    {
                        int rgb;
                        if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                            c.BgColor = Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                string bg = BgColor.R.ToString("X2") + BgColor.G.ToString("X2") + BgColor.B.ToString("X2");
                List<string> lines = new List<string>();
                lines.Add("root=" + RootDir);
                lines.Add("rootset=" + (RootSet ? "1" : "0"));
                lines.Add("prefix=" + Prefix);
                lines.Add("auto=" + (AutoCapture ? "1" : "0"));
                lines.Add("topmost=" + (TopMost ? "1" : "0"));
                lines.Add("autostart=" + (AutoStart ? "1" : "0"));
                lines.Add("opacity=" + Opacity.ToString(CultureInfo.InvariantCulture));
                lines.Add("keydelete=" + KeyDelete);
                lines.Add("keyfav=" + KeyFavorite);
                lines.Add("keymerge=" + KeyMerge);
                lines.Add("keycrop=" + KeyCrop);
                lines.Add("keyshot=" + KeyShot);
                lines.Add("shotmod=" + ShotMod.ToString(CultureInfo.InvariantCulture));
                lines.Add("lastdir=" + LastDir);
                lines.Add("recent=" + Recent);
                lines.Add("winx=" + WinX.ToString(CultureInfo.InvariantCulture));
                lines.Add("winy=" + WinY.ToString(CultureInfo.InvariantCulture));
                lines.Add("winw=" + WinW.ToString(CultureInfo.InvariantCulture));
                lines.Add("winh=" + WinH.ToString(CultureInfo.InvariantCulture));
                lines.Add("bg=" + bg);
                foreach (KeyValuePair<string, List<string>> kv in Favorites)
                {
                    if (kv.Value.Count == 0) continue;
                    lines.Add(kv.Key + "=" + string.Join(";", kv.Value.ToArray()));
                }
                File.WriteAllLines(IniPath(), lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(255, 43, 43, 49);
        private static readonly Color Hover = Color.FromArgb(255, 62, 62, 72);
        private static readonly Color Border = Color.FromArgb(255, 76, 76, 86);

        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return Border; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Back; } }
        public override Color ToolStripBorder { get { return Border; } }
        public override Color CheckBackground { get { return Hover; } }
        public override Color CheckSelectedBackground { get { return Hover; } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(255, 235, 235, 242) : Color.FromArgb(255, 122, 122, 132);
            base.OnRenderItemText(e);

            ToolStripMenuItem item = e.Item as ToolStripMenuItem;
            if (item == null || !(item.Tag is string) || (string)item.Tag != "rightcheck") return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = 14;
            Rectangle box = new Rectangle(item.Width - item.Padding.Right - size - 2, (item.Height - size) / 2, size, size);
            if (item.Checked)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 59, 130, 246)))
                    g.FillRectangle(b, box);
                using (Pen p = new Pen(Color.White, 2f))
                {
                    g.DrawLine(p, box.Left + 3, box.Top + 7.5f, box.Left + 6, box.Top + 10.5f);
                    g.DrawLine(p, box.Left + 6, box.Top + 10.5f, box.Left + 11.5f, box.Top + 4);
                }
            }
            else
            {
                using (Pen p = new Pen(Color.FromArgb(150, 200, 200, 215), 1.4f))
                    g.DrawRectangle(p, box);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(255, 235, 235, 242);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 59, 130, 246)))
                e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(Color.White, 1.8f))
            {
                e.Graphics.DrawLine(p, r.Left + 3, r.Top + r.Height / 2, r.Left + r.Width / 2 - 1, r.Bottom - 4);
                e.Graphics.DrawLine(p, r.Left + r.Width / 2 - 1, r.Bottom - 4, r.Right - 3, r.Top + 3);
            }
        }
    }

    internal sealed class ThumbTile : Control
    {
        private Image _thumb;
        private readonly bool _ownsThumb;
        private readonly int _imgSize;
        private readonly int _badge;
        private static readonly Color StarGold = Color.FromArgb(255, 250, 190, 40);
        private const int Pad = 4;

        private bool _hoverTile;
        private bool _hoverBadge;
        private bool _hoverStar;
        private bool _selected;
        private bool _favorited;
        private Color _textColor = Color.FromArgb(255, 235, 235, 242);
        private Color _accent = Color.FromArgb(255, 59, 130, 246);

        public readonly string FilePath;
        public bool HiddenByFilter;

        public ThumbTile(string path, Image thumb, int imageSize, bool ownsThumb)
        {
            FilePath = path;
            _thumb = thumb;
            _ownsThumb = ownsThumb;
            _imgSize = imageSize;
            _badge = Math.Max(18, imageSize / 5);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Size = new Size(_imgSize + Pad * 2, _imgSize + Pad * 2);
            Cursor = Cursors.SizeAll;
            AllowDrop = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        public void SetThumb(Image img)
        {
            if (img == null || _thumb == img) return;
            _thumb = img;
            Invalidate();
        }

        public bool HoverTile
        {
            set { if (_hoverTile != value) { _hoverTile = value; Invalidate(); } }
        }

        public bool HoverBadge
        {
            set { if (_hoverBadge != value) { _hoverBadge = value; Invalidate(); } }
        }

        public bool HoverStar
        {
            set { if (_hoverStar != value) { _hoverStar = value; Invalidate(); } }
        }

        public bool Favorited
        {
            get { return _favorited; }
            set { if (_favorited != value) { _favorited = value; Invalidate(); } }
        }

        public Rectangle ImageRect { get { return new Rectangle(Pad, Pad, _imgSize, _imgSize); } }

        public Rectangle BadgeRect
        {
            get
            {
                Rectangle r = ImageRect;
                return new Rectangle(r.Right - _badge - 2, r.Top + 2, _badge, _badge);
            }
        }

        public Rectangle StarRect
        {
            get
            {
                Rectangle r = ImageRect;
                return new Rectangle(r.Right - _badge - 2, r.Bottom - _badge - 2, _badge, _badge);
            }
        }

        public void ApplyColors(Color back, Color text, Color accent)
        {
            BackColor = back;
            _textColor = text;
            _accent = accent;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle img = ImageRect;

            if (_thumb != null) g.DrawImage(_thumb, img);

            Color borderColor;
            float borderWidth;
            if (_selected)
            {
                borderColor = _accent;
                borderWidth = 2f;
            }
            else if (_hoverTile)
            {
                borderColor = Color.FromArgb(150, _accent);
                borderWidth = 1.6f;
            }
            else
            {
                borderColor = Color.FromArgb(56, _textColor);
                borderWidth = 1f;
            }
            using (Pen p = new Pen(borderColor, borderWidth))
                g.DrawRectangle(p, img.Left - 1, img.Top - 1, img.Width + 1, img.Height + 1);

            if (_hoverBadge)
            {
                Rectangle badge = BadgeRect;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 224, 58, 58)))
                    g.FillRectangle(b, badge);
                using (Pen p = new Pen(Color.FromArgb(245, 255, 255, 255), 2f))
                {
                    int inset = _badge / 3;
                    g.DrawLine(p, badge.Left + inset, badge.Top + inset, badge.Right - inset, badge.Bottom - inset);
                    g.DrawLine(p, badge.Right - inset, badge.Top + inset, badge.Left + inset, badge.Bottom - inset);
                }
            }

            if (_favorited || _hoverStar)
            {
                Rectangle star = StarRect;
                float scx = star.Left + star.Width / 2f;
                float scy = star.Top + star.Height / 2f + 0.5f;
                float radius = _badge / 2f - 2.5f;
                PointF[] pts = MainForm.StarPoints(scx, scy, radius, radius * 0.45f);
                using (SolidBrush b = new SolidBrush(_favorited
                    ? (_hoverStar ? Color.FromArgb(255, 255, 216, 96) : StarGold)
                    : Color.FromArgb(242, 255, 255, 255)))
                    g.FillPolygon(b, pts);
                using (Pen p = new Pen(Color.FromArgb(140, 25, 25, 30), 1f))
                    g.DrawPolygon(p, pts);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsThumb && _thumb != null) _thumb.Dispose();
            base.Dispose(disposing);
        }
    }

    internal static class BorderHitTest
    {
        public static bool Handle(Control control, ref Message m)
        {
            if (m.Msg != 0x0084) return false;
            MainForm form = control.FindForm() as MainForm;
            if (form == null) return false;
            if (!form.IsOnWindowBorder(m.LParam)) return false;
            m.Result = (IntPtr)(-1);
            return true;
        }
    }

    internal sealed class DarkPanel : Panel
    {
        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class ChromePanel : Panel
    {
        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class ChromeLabel : Label
    {
        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class ChromeFlow : FlowLayoutPanel
    {
        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class NoLayoutEngine : LayoutEngine
    {
        public override bool Layout(object container, LayoutEventArgs layoutEventArgs)
        {
            return false;
        }
    }

    internal sealed class TilePanel : Panel
    {
        public int Gap = 6;
        public int Pad = 8;
        private int _scrollY;
        private int _page;
        private int _pageCount = 1;
        private int _tileH;
        private bool _suspend;

        public event EventHandler PageChanged;

        public int PageIndex { get { return _page; } }
        public int PageCount { get { return _pageCount; } }
        public int TileHeight { get { return _tileH; } }

        public void NextPage() { GoToPage(_page + 1); }
        public void PrevPage() { GoToPage(_page - 1); }

        public void GoToPage(int page)
        {
            if (page < 0) page = 0;
            if (page > _pageCount - 1) page = _pageCount - 1;
            if (page == _page)
            {
                Arrange();
                return;
            }
            _page = page;
            Arrange();
        }

        private int VisibleRows(int tileH, int gap)
        {
            if (tileH <= 0) return 1;
            return Math.Max(1, (ClientSize.Height - gap * 2 + gap) / (tileH + gap));
        }
        private const int WM_SETREDRAW = 0x000B;
        private static readonly LayoutEngine NoLayout = new NoLayoutEngine();

        public override LayoutEngine LayoutEngine
        {
            get { return NoLayout; }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool LockWindowUpdate(IntPtr hWndLock);

        public void Freeze(bool freeze)
        {
            IntPtr flag = freeze ? IntPtr.Zero : (IntPtr)1;
            try
            {
                if (IsHandleCreated) SendMessage(Handle, WM_SETREDRAW, flag, IntPtr.Zero);
                foreach (Control c in Controls)
                    if (c.Visible && c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, flag, IntPtr.Zero);
            }
            catch { }
        }

        public TilePanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public int ScrollY { get { return _scrollY; } }

        public void SuspendArrange()
        {
            _suspend = true;
            SuspendLayout();
        }

        public void ResumeArrange()
        {
            _suspend = false;
            ResumeLayout();
            Arrange();
        }

        public void ResetScroll()
        {
            _scrollY = 0;
            Arrange();
        }

        public void Arrange()
        {
            if (_suspend) return;
            int count = 0;
            int tileW = 0;
            int tileH = 0;
            foreach (Control c in Controls)
            {
                ThumbTile t = c as ThumbTile;
                if (t != null && t.HiddenByFilter) continue;
                count++;
                if (tileW == 0)
                {
                    tileW = c.Width;
                    tileH = c.Height;
                }
            }
            if (count == 0 || tileW <= 0 || tileH <= 0)
            {
                _scrollY = 0;
                return;
            }

            int avail = ClientSize.Width - Pad * 2;
            if (avail < tileW) avail = tileW;
            int cols = Math.Max(1, (avail + Gap) / (tileW + Gap));
            int gap = Gap;
            int x0;
            if (cols > 1)
            {
                int content = cols * tileW + (cols - 1) * gap;
                x0 = Math.Max(Pad, (ClientSize.Width - content) / 2);
            }
            else
            {
                x0 = Math.Max(Pad, (ClientSize.Width - tileW) / 2);
            }

            _tileH = tileH;
            int rows = (count + cols - 1) / cols;
            int step = tileH + gap;
            int rowsPerPage = Math.Max(1, (ClientSize.Height - gap * 2 - tileH) / step + 1);
            _pageCount = Math.Max(1, (rows + rowsPerPage - 1) / rowsPerPage);
            if (_page > _pageCount - 1) _page = _pageCount - 1;
            if (_page < 0) _page = 0;
            int firstRow = _page * rowsPerPage;
            int lastRow = firstRow + rowsPerPage - 1;
            _scrollY = gap + firstRow * step;

            SuspendLayout();
            LockWindowUpdate(Handle);
            try
            {
                int index = 0;
                foreach (Control c in Controls)
                {
                    ThumbTile tile = c as ThumbTile;
                    if (tile != null && tile.HiddenByFilter)
                    {
                        if (c.Visible) c.Visible = false;
                        continue;
                    }
                    int col = index % cols;
                    int row = index / cols;
                    index++;
                    if (row < firstRow || row > lastRow)
                    {
                        if (c.Visible) c.Visible = false;
                        continue;
                    }
                    int nx = x0 + col * (tileW + gap);
                    int ny = gap + (row - firstRow) * step;
                    if (!c.Visible) c.Visible = true;
                    if (c.Left != nx || c.Top != ny || c.Width != tileW || c.Height != tileH)
                        c.SetBounds(nx, ny, tileW, tileH);
                }
            }
            finally
            {
                ResumeLayout(false);
                LockWindowUpdate(IntPtr.Zero);
                Invalidate();
                foreach (Control c in Controls)
                {
                    if (!c.Visible) continue;
                    int top = c.Top;
                    if (top + c.Height < 0 || top > ClientSize.Height) continue;
                    c.Invalidate();
                }
            }
            if (PageChanged != null) PageChanged(this, EventArgs.Empty);
        }

        public void ScrollTo(Control target)
        {
            if (target == null) return;
            int contentTop = target.Top + _scrollY;
            if (contentTop < _scrollY) _scrollY = contentTop;
            else if (contentTop + target.Height > _scrollY + ClientSize.Height)
                _scrollY = contentTop + target.Height - ClientSize.Height;
            Arrange();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta > 0) PrevPage();
            else NextPage();
            base.OnMouseWheel(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int firstRow = _tileH > 0 ? _page * VisibleRows(_tileH, Gap) : 0;
            Arrange();
            if (_tileH > 0)
            {
                int visRows = Math.Max(1, VisibleRows(_tileH, Gap));
                _page = Math.Max(0, Math.Min(_pageCount - 1, firstRow / visRows));
                Arrange();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
        }

        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class ChromeButton : Button
    {
        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class RootTable : TableLayoutPanel
    {
        protected override void WndProc(ref Message m)
        {
            if (BorderHitTest.Handle(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class MergeForm : Form
    {
        private sealed class Piece
        {
            public Image Image;
            public RectangleF Rect;
            public string Name;
        }

        private readonly List<Piece> _pieces = new List<Piece>();
        private readonly Func<Bitmap, bool> _export;
        private readonly Color _back;
        private readonly Color _text;
        private readonly Color _sub;
        private readonly Color _accent = Color.FromArgb(255, 59, 130, 246);
        private readonly Button _bgButton;
        private readonly int _barHeight = 46;
        private readonly List<float> _guidesV = new List<float>();
        private readonly List<float> _guidesH = new List<float>();

        private float _zoom = 1f;
        private PointF _offset;
        private Piece _drag;
        private Piece _active;
        private int _resizeHandle = -1;
        private Piece _resizePiece;
        private RectangleF _resizeStartRect;
        private PointF _resizeAnchor;
        private PointF _resizeStartMouse;
        private PointF _dragStart;
        private RectangleF _dragStartRect;
        private bool _panning;
        private Point _panStart;
        private PointF _offsetStart;
        private bool _blackBackground;

        public MergeForm(List<Image> images, Color back, Color text, Color sub, Font font, Func<Bitmap, bool> export)
        {
            _export = export;
            _back = back;
            _text = text;
            _sub = sub;
            Text = "合并图片";
            Font = font;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(520, 380);
            ClientSize = new Size(980, 720);
            BackColor = back;
            ShowInTaskbar = false;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

            float x = 0;
            int index = 0;
            foreach (Image img in images)
            {
                Piece p = new Piece();
                p.Image = img;
                p.Name = "图" + (++index);
                p.Rect = new RectangleF(x, 0, img.Width, img.Height);
                x += img.Width;
                _pieces.Add(p);
            }

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = _barHeight;
            bottom.BackColor = back;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0);
            buttons.Controls.Add(MakeButton("顶部对齐", delegate { AlignTop(); }));
            buttons.Controls.Add(MakeButton("底部对齐", delegate { AlignBottom(); }));
            buttons.Controls.Add(MakeButton("左对齐", delegate { AlignLeft(); }));
            buttons.Controls.Add(MakeButton("右对齐", delegate { AlignRight(); }));
            _bgButton = MakeButton("透明底", delegate { _blackBackground = !_blackBackground; UpdateInfo(); Invalidate(); });
            buttons.Controls.Add(_bgButton);
            buttons.Controls.Add(MakeButton("合并", delegate { DoMerge(); }));
            buttons.Controls.Add(MakeButton("取消", delegate { Close(); }));

            bottom.Controls.Add(buttons);
            bottom.Resize += delegate
            {
                buttons.Left = Math.Max(0, (bottom.ClientSize.Width - buttons.Width) / 2);
                buttons.Top = Math.Max(0, (bottom.ClientSize.Height - buttons.Height) / 2);
            };
            Controls.Add(bottom);
        }

        private Button MakeButton(string caption, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = caption;
            b.Size = new Size(74, 30);
            b.Margin = new Padding(4, 0, 0, 0);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(95, _text);
            b.BackColor = _back;
            b.ForeColor = _text;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(255,
                Math.Min(255, _back.R + 22), Math.Min(255, _back.G + 22), Math.Min(255, _back.B + 22));
            b.TabStop = false;
            b.Click += onClick;
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            CenterPieces();
            ComputeFit();
            UpdateInfo();
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            foreach (Piece p in _pieces)
                if (p.Image != null) p.Image.Dispose();
        }

        private Rectangle Canvas
        {
            get
            {
                Rectangle r = ClientRectangle;
                r.Height -= _barHeight;
                return r;
            }
        }

        private void CenterPieces()
        {
            if (_pieces.Count == 0) return;
            RectangleF bbox = _pieces[0].Rect;
            foreach (Piece p in _pieces) bbox = RectangleF.Union(bbox, p.Rect);
            float dx = -bbox.X;
            float dy = -bbox.Y;
            foreach (Piece p in _pieces) p.Rect = new RectangleF(p.Rect.X + dx, p.Rect.Y + dy, p.Rect.Width, p.Rect.Height);
        }

        private RectangleF BoundingBox()
        {
            if (_pieces.Count == 0) return RectangleF.Empty;
            RectangleF bbox = _pieces[0].Rect;
            foreach (Piece p in _pieces) bbox = RectangleF.Union(bbox, p.Rect);
            return bbox;
        }

        private void ComputeFit()
        {
            Rectangle area = Canvas;
            RectangleF bbox = BoundingBox();
            if (area.Width <= 20 || area.Height <= 20 || bbox.Width <= 0 || bbox.Height <= 0) return;
            float fit = Math.Min((float)(area.Width - 40) / bbox.Width, (float)(area.Height - 40) / bbox.Height);
            if (fit > 1f) fit = 1f;
            if (fit <= 0.01f) fit = 0.01f;
            _zoom = fit;
            _offset = new PointF(
                area.Left + (area.Width - bbox.Width * _zoom) / 2f - bbox.X * _zoom,
                area.Top + (area.Height - bbox.Height * _zoom) / 2f - bbox.Y * _zoom);
        }

        private PointF ToCanvas(Point p)
        {
            if (_zoom <= 0) return PointF.Empty;
            return new PointF((p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom);
        }

        private RectangleF ToClient(RectangleF r)
        {
            return new RectangleF(_offset.X + r.X * _zoom, _offset.Y + r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);
        }

        private Piece HitTest(Point p)
        {
            PointF c = ToCanvas(p);
            for (int i = _pieces.Count - 1; i >= 0; i--)
                if (_pieces[i].Rect.Contains(c)) return _pieces[i];
            return null;
        }

        private Piece FindCorner(Point client, out int handle)
        {
            handle = -1;
            for (int i = _pieces.Count - 1; i >= 0; i--)
            {
                Piece p = _pieces[i];
                RectangleF r = ToClient(p.Rect);
                PointF[] corners = new PointF[]
                {
                    new PointF(r.Left, r.Top),
                    new PointF(r.Right, r.Top),
                    new PointF(r.Left, r.Bottom),
                    new PointF(r.Right, r.Bottom)
                };
                for (int c = 0; c < corners.Length; c++)
                {
                    if (Math.Abs(client.X - corners[c].X) <= 10 && Math.Abs(client.Y - corners[c].Y) <= 10)
                    {
                        handle = c;
                        return p;
                    }
                }
            }
            return null;
        }

        private void BeginResize(Piece p, int handle, Point client)
        {
            _resizePiece = p;
            _resizeHandle = handle;
            _resizeStartRect = p.Rect;
            _resizeStartMouse = ToCanvas(client);
            RectangleF r = p.Rect;
            switch (handle)
            {
                case 0: _resizeAnchor = new PointF(r.Right, r.Bottom); break;
                case 1: _resizeAnchor = new PointF(r.Left, r.Bottom); break;
                case 2: _resizeAnchor = new PointF(r.Right, r.Top); break;
                default: _resizeAnchor = new PointF(r.Left, r.Top); break;
            }
        }

        private void ContinueResize(Point client)
        {
            if (_resizePiece == null) return;
            PointF now = ToCanvas(client);
            float dx = now.X - _resizeStartMouse.X;
            float dy = now.Y - _resizeStartMouse.Y;
            float sx = (_resizeHandle == 0 || _resizeHandle == 2) ? -1f : 1f;
            float sy = (_resizeHandle == 0 || _resizeHandle == 1) ? -1f : 1f;
            float rawW = _resizeStartRect.Width + sx * dx;
            float rawH = _resizeStartRect.Height + sy * dy;
            if (rawW < 8f) rawW = 8f;
            if (rawH < 8f) rawH = 8f;
            float rawScale = Math.Max(rawW / _resizeStartRect.Width, rawH / _resizeStartRect.Height);

            float cornerX = (_resizeHandle == 0 || _resizeHandle == 2) ? _resizeAnchor.X - rawW : _resizeAnchor.X + rawW;
            float cornerY = (_resizeHandle == 0 || _resizeHandle == 1) ? _resizeAnchor.Y - rawH : _resizeAnchor.Y + rawH;

            float threshold = 9f / Math.Max(0.05f, _zoom);
            float bestDX = threshold + 1f;
            float bestDY = threshold + 1f;
            float snapX = 0f;
            float snapY = 0f;
            foreach (Piece o in _pieces)
            {
                if (o == _resizePiece) continue;
                float[] xs = new float[] { o.Rect.Left, o.Rect.Right };
                foreach (float line in xs)
                {
                    float d = line - cornerX;
                    if (Math.Abs(d) < Math.Abs(bestDX))
                    {
                        bestDX = d;
                        snapX = line;
                    }
                }
                float[] ys = new float[] { o.Rect.Top, o.Rect.Bottom };
                foreach (float line in ys)
                {
                    float d = line - cornerY;
                    if (Math.Abs(d) < Math.Abs(bestDY))
                    {
                        bestDY = d;
                        snapY = line;
                    }
                }
            }

            bool snappedX = Math.Abs(bestDX) <= threshold;
            bool snappedY = Math.Abs(bestDY) <= threshold;
            float scaleX = snappedX ? Math.Abs(snapX - _resizeAnchor.X) / _resizeStartRect.Width : 0f;
            float scaleY = snappedY ? Math.Abs(snapY - _resizeAnchor.Y) / _resizeStartRect.Height : 0f;

            float scale = rawScale;
            bool useX = false;
            bool useY = false;
            if (snappedX && snappedY)
            {
                float corrX = Math.Abs(scaleX - rawScale);
                float corrY = Math.Abs(scaleY - rawScale);
                if (corrX <= corrY) useX = true;
                else useY = true;
            }
            else if (snappedX) useX = true;
            else if (snappedY) useY = true;

            if (useX) scale = scaleX;
            else if (useY) scale = scaleY;

            if (scale < 0.03f) scale = 0.03f;
            if (scale > 30f) scale = 30f;

            _guidesV.Clear();
            _guidesH.Clear();
            if (useX) _guidesV.Add(snapX);
            if (useY) _guidesH.Add(snapY);

            float nw = (float)Math.Round(_resizeStartRect.Width * scale);
            float nh = (float)Math.Round(_resizeStartRect.Height * scale);
            if (nw < 8f) nw = 8f;
            if (nh < 8f) nh = 8f;
            float x = (_resizeHandle == 0 || _resizeHandle == 2) ? _resizeAnchor.X - nw : _resizeAnchor.X;
            float y = (_resizeHandle == 0 || _resizeHandle == 1) ? _resizeAnchor.Y - nh : _resizeAnchor.Y;
            _resizePiece.Rect = new RectangleF((float)Math.Round(x), (float)Math.Round(y), nw, nh);
            UpdateInfo();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int corner;
                Piece cornerPiece = FindCorner(e.Location, out corner);
                Piece hit = cornerPiece != null ? cornerPiece : HitTest(e.Location);
                _active = hit;
                if (cornerPiece != null)
                {
                    BeginResize(cornerPiece, corner, e.Location);
                    _pieces.Remove(cornerPiece);
                    _pieces.Add(cornerPiece);
                }
                else if (hit != null)
                {
                    _drag = hit;
                    _dragStart = ToCanvas(e.Location);
                    _dragStartRect = hit.Rect;
                    _pieces.Remove(hit);
                    _pieces.Add(hit);
                }
                Capture = true;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _panning = true;
                _panStart = e.Location;
                _offsetStart = _offset;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_resizeHandle >= 0)
            {
                ContinueResize(e.Location);
            }
            else if (_drag != null)
            {
                PointF now = ToCanvas(e.Location);
                float x = (float)Math.Round(_dragStartRect.X + (now.X - _dragStart.X));
                float y = (float)Math.Round(_dragStartRect.Y + (now.Y - _dragStart.Y));
                _drag.Rect = new RectangleF(x, y, _dragStartRect.Width, _dragStartRect.Height);
                ApplySnap(_drag);
                UpdateInfo();
                Invalidate();
            }
            else if (_panning)
            {
                _offset = new PointF(_offsetStart.X + (_panStart.X - e.Location.X) * -1, _offsetStart.Y + (e.Location.Y - _panStart.Y));
                Invalidate();
            }
            else
            {
                int corner;
                Piece cornerPiece = FindCorner(e.Location, out corner);
                if (cornerPiece != null)
                {
                    if (corner == 0 || corner == 3) Cursor = Cursors.SizeNWSE;
                    else Cursor = Cursors.SizeNESW;
                }
                else
                {
                    Cursor = HitTest(e.Location) != null ? Cursors.SizeAll : Cursors.Default;
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = null;
            _panning = false;
            _resizeHandle = -1;
            _resizePiece = null;
            Capture = false;
            _guidesV.Clear();
            _guidesH.Clear();
            UpdateInfo();
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Rectangle area = Canvas;
            if (area.Contains(e.Location))
            {
                PointF before = ToCanvas(e.Location);
                float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
                float next = _zoom * factor;
                if (next < 0.05f) next = 0.05f;
                if (next > 8f) next = 8f;
                _zoom = next;
                PointF after = ToCanvas(e.Location);
                _offset = new PointF(_offset.X + (after.X - before.X) * _zoom, _offset.Y + (after.Y - before.Y) * _zoom);
                UpdateInfo();
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            if (keyData == Keys.Delete && _active != null)
            {
                _pieces.Remove(_active);
                if (_active.Image != null) _active.Image.Dispose();
                _active = null;
                UpdateInfo();
                Invalidate();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ApplySnap(Piece p)
        {
            _guidesV.Clear();
            _guidesH.Clear();
            const float threshold = 7f;
            float bestDx = threshold + 1f;
            float bestDy = threshold + 1f;
            float snapX = 0f;
            float snapY = 0f;

            foreach (Piece o in _pieces)
            {
                if (o == p) continue;
                float[] cx = new float[]
                {
                    o.Rect.Right - p.Rect.Left,
                    o.Rect.Left - p.Rect.Right,
                    o.Rect.Left - p.Rect.Left,
                    o.Rect.Right - p.Rect.Right
                };
                foreach (float d in cx)
                {
                    if (Math.Abs(d) < Math.Abs(bestDx))
                    {
                        bestDx = d;
                        snapX = d;
                    }
                }
                float[] cy = new float[]
                {
                    o.Rect.Bottom - p.Rect.Top,
                    o.Rect.Top - p.Rect.Bottom,
                    o.Rect.Top - p.Rect.Top,
                    o.Rect.Bottom - p.Rect.Bottom
                };
                foreach (float d in cy)
                {
                    if (Math.Abs(d) < Math.Abs(bestDy))
                    {
                        bestDy = d;
                        snapY = d;
                    }
                }
            }

            if (Math.Abs(snapX) <= threshold)
            {
                p.Rect = new RectangleF(p.Rect.X + snapX, p.Rect.Y, p.Rect.Width, p.Rect.Height);
                _guidesV.Add(p.Rect.Left);
                _guidesV.Add(p.Rect.Right);
            }
            if (Math.Abs(snapY) <= threshold)
            {
                p.Rect = new RectangleF(p.Rect.X, p.Rect.Y + snapY, p.Rect.Width, p.Rect.Height);
                _guidesH.Add(p.Rect.Top);
                _guidesH.Add(p.Rect.Bottom);
            }
        }

        private void AlignTop()
        {
            PackRow(true);
        }

        private void AlignBottom()
        {
            PackRow(false);
        }

        private void AlignLeft()
        {
            PackColumn(true);
        }

        private void AlignRight()
        {
            PackColumn(false);
        }

        private void PackRow(bool alignTop)
        {
            if (_pieces.Count < 2) return;
            List<Piece> ordered = new List<Piece>(_pieces);
            ordered.Sort(delegate(Piece a, Piece b) { return a.Rect.Left.CompareTo(b.Rect.Left); });
            float x = ordered[0].Rect.Left;
            float top = ordered[0].Rect.Top;
            float bottom = ordered[0].Rect.Bottom;
            foreach (Piece p in ordered)
            {
                if (alignTop) { if (p.Rect.Top < top) top = p.Rect.Top; }
                else { if (p.Rect.Bottom > bottom) bottom = p.Rect.Bottom; }
            }
            foreach (Piece p in ordered)
            {
                float y = alignTop ? top : bottom - p.Rect.Height;
                p.Rect = new RectangleF(x, y, p.Rect.Width, p.Rect.Height);
                x += p.Rect.Width;
            }
            _pieces.Clear();
            _pieces.AddRange(ordered);
            UpdateInfo();
            Invalidate();
        }

        private void PackColumn(bool alignLeft)
        {
            if (_pieces.Count < 2) return;
            List<Piece> ordered = new List<Piece>(_pieces);
            ordered.Sort(delegate(Piece a, Piece b) { return a.Rect.Top.CompareTo(b.Rect.Top); });
            float y = ordered[0].Rect.Top;
            float left = ordered[0].Rect.Left;
            float right = ordered[0].Rect.Right;
            foreach (Piece p in ordered)
            {
                if (alignLeft) { if (p.Rect.Left < left) left = p.Rect.Left; }
                else { if (p.Rect.Right > right) right = p.Rect.Right; }
            }
            foreach (Piece p in ordered)
            {
                float px = alignLeft ? left : right - p.Rect.Width;
                p.Rect = new RectangleF(px, y, p.Rect.Width, p.Rect.Height);
                y += p.Rect.Height;
            }
            _pieces.Clear();
            _pieces.AddRange(ordered);
            UpdateInfo();
            Invalidate();
        }

        private void UpdateInfo()
        {
            RectangleF bbox = BoundingBox();
            string extra = string.Empty;
            if (_active != null)
                extra = string.Format(" · 选中 {0} × {1}", (int)Math.Round(_active.Rect.Width), (int)Math.Round(_active.Rect.Height));
            Text = string.Format("合并图片 · {0} 张 · 输出 {1} × {2} · 背景{3}{4}",
                _pieces.Count, (int)Math.Round(bbox.Width), (int)Math.Round(bbox.Height),
                _blackBackground ? "黑色" : "透明", extra);
            if (_bgButton != null) _bgButton.Text = _blackBackground ? "黑色底" : "透明底";
        }

        private void DoMerge()
        {
            if (_pieces.Count == 0) return;
            RectangleF bbox = BoundingBox();
            int w = (int)Math.Round(bbox.Width);
            int h = (int)Math.Round(bbox.Height);
            if (w < 1 || h < 1) return;
            Bitmap outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(outBmp))
            {
                if (_blackBackground) g.Clear(Color.Black);
                else g.Clear(Color.Transparent);
                g.PixelOffsetMode = PixelOffsetMode.Half;
                foreach (Piece p in _pieces)
                {
                    int pw = (int)Math.Round(p.Rect.Width);
                    int ph = (int)Math.Round(p.Rect.Height);
                    if (pw < 1) pw = 1;
                    if (ph < 1) ph = 1;
                    bool scaled = (pw != p.Image.Width || ph != p.Image.Height);
                    g.InterpolationMode = scaled ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    g.DrawImage(p.Image, new Rectangle(
                        (int)Math.Round(p.Rect.X - bbox.X), (int)Math.Round(p.Rect.Y - bbox.Y), pw, ph));
                }
            }
            if (_export != null && _export(outBmp)) Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_back);
            Rectangle area = Canvas;
            if (area.Width <= 0 || area.Height <= 0) return;
            g.SetClip(area);

            RectangleF bbox = BoundingBox();
            RectangleF bboxClient = ToClient(bbox);
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
                g.FillRectangle(bb, bboxClient);

            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            foreach (Piece p in _pieces)
            {
                RectangleF r = ToClient(p.Rect);
                bool scaled = (Math.Abs(p.Rect.Width - p.Image.Width) > 0.5f || Math.Abs(p.Rect.Height - p.Image.Height) > 0.5f);
                g.InterpolationMode = (_zoom < 0.999f || scaled) ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.DrawImage(p.Image, r);
                bool isActive = (p == _active);
                using (Pen pen = new Pen(isActive ? _accent : Color.FromArgb(90, 255, 255, 255), isActive ? 2f : 1f))
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }

            using (Pen dash = new Pen(Color.FromArgb(160, _accent), 1.2f))
            {
                dash.DashStyle = DashStyle.Dash;
                g.DrawRectangle(dash, bboxClient.X, bboxClient.Y, bboxClient.Width, bboxClient.Height);
            }
            using (Pen guide = new Pen(Color.FromArgb(150, 190, 190, 200), 1f))
            {
                foreach (float x in _guidesV)
                {
                    float cx = _offset.X + x * _zoom;
                    g.DrawLine(guide, cx, area.Top, cx, area.Bottom);
                }
                foreach (float y in _guidesH)
                {
                    float cy = _offset.Y + y * _zoom;
                    g.DrawLine(guide, area.Left, cy, area.Right, cy);
                }
            }
            g.ResetClip();
        }
    }

    internal sealed class SmoothLabel : Label
    {
        public SmoothLabel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            Rectangle r = new Rectangle(Padding.Left, 0, Math.Max(0, Width - Padding.Left - Padding.Right), Height);
            TextRenderer.DrawText(g, Text, Font, r, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class CropForm : Form
    {
        private readonly Image _source;
        private readonly Func<Rectangle, bool> _crop;
        private readonly Label _info;
        private readonly Color _back;
        private readonly Color _text;
        private readonly Color _sub;
        private readonly Color _accent = Color.FromArgb(255, 59, 130, 246);
        private readonly int _bottomHeight = 46;

        private float _zoom = 1f;
        private PointF _offset;
        private RectangleF _sel = RectangleF.Empty;
        private int _mode;
        private int _handle = -1;
        private Point _dragStart;
        private RectangleF _selStart;
        private bool _panning;
        private Point _panStart;
        private PointF _offsetStart;
        private Bitmap _preview;
        private float _previewZoom = -1f;
        private PointF _previewOffset;
        private Size _previewSize;
        private bool _crossOn;
        private PointF _cross;
        private Button _crossButton;

        public Image SourceImage { get { return _source; } }

        public CropForm(Image source, string title, Color back, Color text, Color sub, Font font, Func<Rectangle, bool> crop)
        {
            _source = source;
            _crop = crop;
            _back = back;
            _text = text;
            _sub = sub;
            Text = title;
            Font = font;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(480, 360);
            ClientSize = new Size(920, 700);
            BackColor = back;
            ShowInTaskbar = false;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = _bottomHeight;
            bottom.BackColor = back;

            _info = new SmoothLabel();
            _info.Dock = DockStyle.Fill;
            _info.TextAlign = ContentAlignment.MiddleLeft;
            _info.Padding = new Padding(12, 0, 0, 0);
            _info.ForeColor = sub;
            _info.BackColor = back;
            _info.AutoSize = false;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Right;
            buttons.Width = 430;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 8, 8, 0);

            buttons.Controls.Add(MakeButton("全选", delegate { _sel = new RectangleF(0, 0, _source.Width, _source.Height); UpdateInfo(); Invalidate(); }));
            _crossButton = MakeButton("十字线", delegate { ToggleCross(); });
            buttons.Controls.Add(_crossButton);
            buttons.Controls.Add(MakeButton("裁切并继续", delegate { DoCrop(true); }));
            buttons.Controls.Add(MakeButton("裁切", delegate { DoCrop(false); }));
            buttons.Controls.Add(MakeButton("关闭", delegate { Close(); }));

            bottom.Controls.Add(_info);
            bottom.Controls.Add(buttons);
            Controls.Add(bottom);
        }

        private Button MakeButton(string caption, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = caption;
            b.Size = new Size(78, 30);
            b.Margin = new Padding(4, 0, 0, 0);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(95, _text);
            b.BackColor = _back;
            b.ForeColor = _text;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(255,
                Math.Min(255, _back.R + 22), Math.Min(255, _back.G + 22), Math.Min(255, _back.B + 22));
            b.TabStop = false;
            b.Click += onClick;
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ComputeFit();
            UpdateInfo();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_preview != null)
            {
                _preview.Dispose();
                _preview = null;
            }
            if (_source != null) _source.Dispose();
        }

        private void EnsurePreview()
        {
            Rectangle area = ImageArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            if (_preview != null && _previewZoom == _zoom && _previewOffset == _offset && _previewSize == area.Size) return;
            if (_preview != null)
            {
                _preview.Dispose();
                _preview = null;
            }
            Bitmap bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = _zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                RectangleF dest = new RectangleF(
                    _offset.X - area.Left, _offset.Y - area.Top,
                    _source.Width * _zoom, _source.Height * _zoom);
                g.DrawImage(_source, dest);
            }
            _preview = bmp;
            _previewZoom = _zoom;
            _previewOffset = _offset;
            _previewSize = area.Size;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            if (keyData == Keys.Enter)
            {
                DoCrop(false);
                return true;
            }
            if (keyData == (Keys.Control | Keys.A))
            {
                _sel = new RectangleF(0, 0, _source.Width, _source.Height);
                UpdateInfo();
                Invalidate();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private Rectangle ImageArea
        {
            get
            {
                Rectangle r = ClientRectangle;
                r.Height -= _bottomHeight;
                return r;
            }
        }

        private void ComputeFit()
        {
            Rectangle area = ImageArea;
            if (area.Width <= 20 || area.Height <= 20 || _source.Width <= 0 || _source.Height <= 0) return;
            float fit = Math.Min((float)(area.Width - 24) / _source.Width, (float)(area.Height - 24) / _source.Height);
            if (fit <= 0) fit = 0.01f;
            _zoom = fit;
            _offset = new PointF(
                area.Left + (area.Width - _source.Width * _zoom) / 2f,
                area.Top + (area.Height - _source.Height * _zoom) / 2f);
        }

        private PointF ToImage(Point p)
        {
            if (_zoom <= 0) return PointF.Empty;
            return new PointF((p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom);
        }

        private RectangleF ToClient(RectangleF r)
        {
            return new RectangleF(_offset.X + r.X * _zoom, _offset.Y + r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);
        }

        private RectangleF ClampSelection(RectangleF r)
        {
            float x1 = Math.Max(0, Math.Min(r.Left, _source.Width));
            float y1 = Math.Max(0, Math.Min(r.Top, _source.Height));
            float x2 = Math.Max(0, Math.Min(r.Right, _source.Width));
            float y2 = Math.Max(0, Math.Min(r.Bottom, _source.Height));
            if (x2 < x1) { float t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { float t = y1; y1 = y2; y2 = t; }
            return new RectangleF(x1, y1, x2 - x1, y2 - y1);
        }

        private PointF[] Handles(RectangleF r)
        {
            return new PointF[]
            {
                new PointF(r.Left, r.Top),
                new PointF(r.Right, r.Top),
                new PointF(r.Left, r.Bottom),
                new PointF(r.Right, r.Bottom)
            };
        }

        private int HitHandle(Point p)
        {
            if (_sel.Width < 1 || _sel.Height < 1) return -1;
            RectangleF s = ToClient(_sel);
            PointF[] hs = Handles(s);
            for (int i = 0; i < hs.Length; i++)
            {
                if (Math.Abs(p.X - hs[i].X) <= 7 && Math.Abs(p.Y - hs[i].Y) <= 7) return i;
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int h = HitHandle(e.Location);
                if (HitCross(e.Location))
                {
                    _mode = 4;
                }
                else if (h >= 0)
                {
                    _mode = 3;
                    _handle = h;
                    _selStart = _sel;
                }
                else if (_sel.Width >= 1 && ToClient(_sel).Contains(e.Location))
                {
                    _mode = 2;
                    _selStart = _sel;
                }
                else
                {
                    _mode = 1;
                    _sel = RectangleF.Empty;
                }
                _dragStart = e.Location;
                Capture = true;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _panning = true;
                _panStart = e.Location;
                _offsetStart = _offset;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_mode == 1)
            {
                PointF a = ToImage(_dragStart);
                PointF b = ToImage(e.Location);
                _sel = ClampSelection(new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
                UpdateInfo();
                Invalidate();
            }
            else if (_mode == 2)
            {
                float dx = (e.Location.X - _dragStart.X) / _zoom;
                float dy = (e.Location.Y - _dragStart.Y) / _zoom;
                _sel = ClampSelection(new RectangleF(_selStart.X + dx, _selStart.Y + dy, _selStart.Width, _selStart.Height));
                UpdateInfo();
                Invalidate();
            }
            else if (_mode == 3)
            {
                PointF p = ToImage(e.Location);
                RectangleF r = _selStart;
                float left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
                if (_handle == 0) { left = p.X; top = p.Y; }
                else if (_handle == 1) { right = p.X; top = p.Y; }
                else if (_handle == 2) { left = p.X; bottom = p.Y; }
                else if (_handle == 3) { right = p.X; bottom = p.Y; }
                _sel = ClampSelection(new RectangleF(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top)));
                UpdateInfo();
                Invalidate();
            }
            else if (_panning)
            {
                _offset = new PointF(_offsetStart.X + (e.Location.X - _panStart.X), _offsetStart.Y + (e.Location.Y - _panStart.Y));
                Invalidate();
            }
            else if (_mode == 4)
            {
                PointF next = ClampCross(ToImage(e.Location));
                if (next.X != _cross.X || next.Y != _cross.Y)
                {
                    _cross = next;
                    UpdateInfo();
                    Invalidate(ImageArea);
                }
            }
            else
            {
                if (HitCross(e.Location)) Cursor = Cursors.SizeAll;
                else Cursor = HitHandle(e.Location) >= 0 ? Cursors.SizeNWSE : Cursors.Cross;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _mode = 0;
            _handle = -1;
            _panning = false;
            Capture = false;
            UpdateInfoNow();
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Rectangle area = ImageArea;
            if (area.Contains(e.Location))
            {
                PointF before = ToImage(e.Location);
                float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
                float fit = Math.Min((float)area.Width / _source.Width, (float)area.Height / _source.Height);
                float min = Math.Max(0.05f, fit * 0.25f);
                float next = _zoom * factor;
                if (next < min) next = min;
                if (next > 8f) next = 8f;
                _zoom = next;
                PointF after = ToImage(e.Location);
                _offset = new PointF(_offset.X + (after.X - before.X) * _zoom, _offset.Y + (after.Y - before.Y) * _zoom);
                UpdateInfo();
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        private DateTime _infoLast = DateTime.MinValue;

        private void UpdateInfo()
        {
            UpdateInfo(false);
        }

        private void UpdateInfoNow()
        {
            UpdateInfo(true);
        }

        private void UpdateInfo(bool force)
        {
            if (!force && (DateTime.Now - _infoLast).TotalMilliseconds < 85) return;
            _infoLast = DateTime.Now;
            string cross = _crossOn
                ? "   ·   十字线 (" + (int)_cross.X + ", " + (int)_cross.Y + ")：点「裁切」按十字切成 4 张"
                : string.Empty;
            if (_sel.Width >= 1 && _sel.Height >= 1)
            {
                _info.Text = string.Format("选区 {0} × {1}  @ ({2}, {3})   缩放 {4}%{5}",
                    (int)Math.Round(_sel.Width), (int)Math.Round(_sel.Height),
                    (int)Math.Round(_sel.X), (int)Math.Round(_sel.Y), (int)Math.Round(_zoom * 100), cross);
            }
            else
            {
                _info.Text = string.Format("原图 {0} × {1}   缩放 {2}%{3}",
                    _source.Width, _source.Height, (int)Math.Round(_zoom * 100), cross);
            }
        }

        private void DoCrop(bool keepOpen)
        {
            if (_crossOn)
            {
                SplitByCross(keepOpen);
                return;
            }
            if (_sel.Width < 1 || _sel.Height < 1)
            {
                _info.Text = "请先在图上拖拽选择要裁切的区域";
                return;
            }
            Rectangle r = Rectangle.Round(_sel);
            r = Rectangle.Intersect(r, new Rectangle(0, 0, _source.Width, _source.Height));
            if (r.Width < 1 || r.Height < 1) return;
            if (_crop == null || !_crop(r)) return;
            if (keepOpen)
            {
                _sel = RectangleF.Empty;
                UpdateInfo();
                Invalidate();
            }
            else
            {
                Close();
            }
        }

        private void ToggleCross()
        {
            _crossOn = !_crossOn;
            if (_crossOn)
            {
                bool hasSel = _sel.Width >= 1 && _sel.Height >= 1;
                float fx = hasSel ? _sel.Left + _sel.Width / 2f : _source.Width / 2f;
                float fy = hasSel ? _sel.Top + _sel.Height / 2f : _source.Height / 2f;
                _cross = new PointF((float)Math.Round(fx), (float)Math.Round(fy));
                _cross = ClampCross(_cross);
            }
            UpdateCrossButton();
            UpdateInfoNow();
            Invalidate();
        }

        private void UpdateCrossButton()
        {
            if (_crossButton == null) return;
            _crossButton.BackColor = _crossOn
                ? Color.FromArgb(255, Math.Min(255, _back.R + 46), Math.Min(255, _back.G + 46), Math.Min(255, _back.B + 46))
                : _back;
            _crossButton.ForeColor = _crossOn ? _accent : _text;
            _crossButton.FlatAppearance.BorderColor = _crossOn ? _accent : Color.FromArgb(95, _text);
            _crossButton.Invalidate();
        }

        private PointF ClampCross(PointF p)
        {
            float x = Math.Max(1f, Math.Min(_source.Width - 1f, p.X));
            float y = Math.Max(1f, Math.Min(_source.Height - 1f, p.Y));
            return new PointF((float)Math.Round(x), (float)Math.Round(y));
        }

        private bool HitCross(Point p)
        {
            if (!_crossOn) return false;
            float cx = _offset.X + _cross.X * _zoom;
            float cy = _offset.Y + _cross.Y * _zoom;
            RectangleF img = new RectangleF(_offset.X, _offset.Y, _source.Width * _zoom, _source.Height * _zoom);
            if (p.Y >= img.Top - 4 && p.Y <= img.Bottom + 4 && Math.Abs(p.X - cx) <= 6) return true;
            if (p.X >= img.Left - 4 && p.X <= img.Right + 4 && Math.Abs(p.Y - cy) <= 6) return true;
            return false;
        }

        private void SplitByCross(bool keepOpen)
        {
            if (_crop == null || _source == null) return;
            int x = (int)Math.Round(_cross.X);
            int y = (int)Math.Round(_cross.Y);
            x = Math.Max(1, Math.Min(_source.Width - 1, x));
            y = Math.Max(1, Math.Min(_source.Height - 1, y));
            int w = _source.Width;
            int h = _source.Height;
            Rectangle[] rects = new Rectangle[]
            {
                new Rectangle(0, 0, x, y),
                new Rectangle(x, 0, w - x, y),
                new Rectangle(0, y, x, h - y),
                new Rectangle(x, y, w - x, h - y)
            };
            int done = 0;
            foreach (Rectangle rr in rects)
            {
                Rectangle clip = Rectangle.Intersect(rr, new Rectangle(0, 0, w, h));
                if (clip.Width < 1 || clip.Height < 1) continue;
                if (_crop(clip)) done++;
            }
            if (done == 0)
            {
                _info.Text = "十字裁切失败：没有生成任何图片。";
                return;
            }
            if (keepOpen)
            {
                _crossOn = false;
                UpdateCrossButton();
                UpdateInfoNow();
                Invalidate();
            }
            else
            {
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_back);
            Rectangle area = ImageArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            g.SetClip(area);
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;

            EnsurePreview();
            RectangleF dest = new RectangleF(_offset.X, _offset.Y, _source.Width * _zoom, _source.Height * _zoom);
            if (_preview != null)
                g.DrawImage(_preview, new Rectangle(area.Left, area.Top, _preview.Width, _preview.Height));
            else
                g.DrawImage(_source, dest);
            using (Pen p = new Pen(Color.FromArgb(80, _text)))
                g.DrawRectangle(p, dest.X, dest.Y, dest.Width, dest.Height);

            if (_sel.Width >= 1 && _sel.Height >= 1)
            {
                RectangleF s = ToClient(_sel);
                using (Region region = new Region(area))
                {
                    region.Exclude(s);
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                        g.FillRegion(dim, region);
                }
                using (Pen p = new Pen(_accent, 2f))
                    g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
                using (SolidBrush b = new SolidBrush(Color.White))
                {
                    foreach (PointF pt in Handles(s))
                        g.FillRectangle(b, pt.X - 3.5f, pt.Y - 3.5f, 7f, 7f);
                }
            }
            if (_crossOn)
            {
                int ccx = (int)Math.Round(_offset.X + _cross.X * _zoom);
                int ccy = (int)Math.Round(_offset.Y + _cross.Y * _zoom);
                int ix = (int)Math.Round(_offset.X);
                int iy = (int)Math.Round(_offset.Y);
                int iw = (int)Math.Round(_source.Width * _zoom);
                int ih = (int)Math.Round(_source.Height * _zoom);
                using (SolidBrush br = new SolidBrush(_accent))
                {
                    g.FillRectangle(br, ccx - 1, iy, 2, ih);
                    g.FillRectangle(br, ix, ccy - 1, iw, 2);
                    System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(br, ccx - 3, ccy - 3, 6, 6);
                    g.SmoothingMode = old;
                }
            }
            g.ResetClip();
        }
    }

    internal sealed class BallForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        private readonly Color _accent;
        private readonly Font _menuFont;
        private readonly Image _logo;
        private readonly int _size;
        private Bitmap _surface;
        private bool _hover;
        private bool _dragging;
        private bool _moved;
        private Point _dragStart;
        private Point _origin;

        public event EventHandler OpenRequested;
        public event EventHandler ExitRequested;
        public event EventHandler Moved;

        public BallForm(int size, Color back, Color accent, Font menuFont, Image logo)
        {
            _accent = accent;
            _menuFont = menuFont;
            _logo = logo;
            _size = size;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = back;
            MinimumSize = new Size(size, size);
            MaximumSize = new Size(size, size);
            Size = new Size(size, size);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Width != _size || Height != _size) Size = new Size(_size, _size);
            Render();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Render();
        }

        private void Render()
        {
            if (Width <= 0 || Height <= 0) return;
            if (_surface == null || _surface.Width != Width || _surface.Height != Height)
            {
                if (_surface != null) _surface.Dispose();
                _surface = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            }
            using (Graphics g = Graphics.FromImage(_surface))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float k = Width / 48f;
                for (int i = 5; i >= 1; i--)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(11, 0, 0, 0)))
                        g.FillEllipse(sb, i * k, i * k + 1.5f * k, Width - i * 2 * k, Height - i * 2 * k);
                }

                if (_logo != null)
                {
                    g.DrawImage(_logo, new Rectangle(0, 0, Width, Height));
                }
                else
                {
                    Color fill = _hover ? Lighten(_accent, 22) : _accent;
                    using (SolidBrush b = new SolidBrush(fill))
                        g.FillEllipse(b, 1, 1, Width - 2, Height - 2);
                    float cx = Width / 2f;
                    using (Pen p = new Pen(Color.White, 2.6f * k))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawLine(p, cx, 12 * k, cx, 27 * k);
                        g.DrawLine(p, cx - 7 * k, 20 * k, cx, 28 * k);
                        g.DrawLine(p, cx + 7 * k, 20 * k, cx, 28 * k);
                        g.DrawLine(p, cx - 9 * k, 34 * k, cx + 9 * k, 34 * k);
                    }
                }

                if (_hover)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                        g.FillEllipse(b, 1, 1, Width - 2, Height - 2);
                }
            }
            PushLayered();
        }

        private void PushLayered()
        {
            if (_surface == null || !IsHandleCreated) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = _surface.Width;
                bmi.bmiHeader.biHeight = -_surface.Height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;
                IntPtr bits;
                hBitmap = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero) return;

                BitmapData data = _surface.LockBits(new Rectangle(0, 0, _surface.Width, _surface.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = data.Stride * data.Height;
                    byte[] buffer = new byte[bytes];
                    Marshal.Copy(data.Scan0, buffer, 0, bytes);
                    for (int i = 0; i < buffer.Length; i += 4)
                    {
                        int a = buffer[i + 3];
                        if (a == 255) continue;
                        buffer[i] = (byte)(buffer[i] * a / 255);
                        buffer[i + 1] = (byte)(buffer[i + 1] * a / 255);
                        buffer[i + 2] = (byte)(buffer[i + 2] * a / 255);
                    }
                    Marshal.Copy(buffer, 0, bits, bytes);
                }
                finally
                {
                    _surface.UnlockBits(data);
                }

                oldBitmap = SelectObject(memDc, hBitmap);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;
                SIZE size = new SIZE();
                size.cx = _surface.Width;
                size.cy = _surface.Height;
                POINT src = new POINT();
                POINT dst = new POINT();
                dst.x = Left;
                dst.y = Top;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == 1 && _surface != null)
                {
                    int lp = m.LParam.ToInt32();
                    Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    if (p.X >= 0 && p.Y >= 0 && p.X < _surface.Width && p.Y < _surface.Height)
                    {
                        if (_surface.GetPixel(p.X, p.Y).A < 20) m.Result = (IntPtr)(-1);
                    }
                    else
                    {
                        m.Result = (IntPtr)(-1);
                    }
                }
                return;
            }
            base.WndProc(ref m);
        }

        private static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(255,
                Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Render();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Render();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _moved = false;
                _dragStart = Cursor.Position;
                _origin = Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowBallMenu();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                Point now = Cursor.Position;
                if (Math.Abs(now.X - _dragStart.X) > 6 || Math.Abs(now.Y - _dragStart.Y) > 6) _moved = true;
                if (_moved)
                    Location = new Point(_origin.X + (now.X - _dragStart.X), _origin.Y + (now.Y - _dragStart.Y));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                if (_moved)
                {
                    if (Moved != null) Moved(this, EventArgs.Empty);
                }
                else if (OpenRequested != null)
                {
                    OpenRequested(this, EventArgs.Empty);
                }
            }
            base.OnMouseUp(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_logo != null) _logo.Dispose();
                if (_surface != null) _surface.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ShowBallMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.Font = _menuFont;
            menu.Items.Add("打开面板", null, delegate
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
            });
            menu.Items.Add("退出", null, delegate
            {
                if (ExitRequested != null) ExitRequested(this, EventArgs.Empty);
            });
            menu.Show(this, new Point(0, Height));
        }
    }

    internal sealed class SliderBar : Control
    {
        private int _value = 100;
        private bool _dragging;
        private readonly Color _accent;
        private readonly Color _track;
        private const int Min = 10;
        private const int Max = 100;

        public event EventHandler ValueChanged;

        public SliderBar(Color accent, Color track)
        {
            _accent = accent;
            _track = track;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Height = 26;
            Cursor = Cursors.Hand;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < Min) v = Min;
                if (v > Max) v = Max;
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _dragging = true;
            SetFromX(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        private void SetFromX(int x)
        {
            const int pad = 9;
            int width = Width - pad * 2;
            if (width <= 0) return;
            double t = (double)(x - pad) / width;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            Value = Min + (int)Math.Round(t * (Max - Min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int pad = 9;
            int width = Width - pad * 2;
            int trackY = Height / 2 - 3;
            using (SolidBrush tb = new SolidBrush(_track))
                g.FillRectangle(tb, pad, trackY, width, 6);
            double t = (double)(_value - Min) / (Max - Min);
            int filled = (int)Math.Round(width * t);
            using (SolidBrush ab = new SolidBrush(_accent))
                g.FillRectangle(ab, pad, trackY, filled, 6);
            int hx = pad + filled;
            using (SolidBrush hb = new SolidBrush(Color.White))
                g.FillEllipse(hb, hx - 7, Height / 2 - 7, 14, 14);
            using (Pen hp = new Pen(_accent, 2f))
                g.DrawEllipse(hp, hx - 7, Height / 2 - 7, 14, 14);
        }
    }

    internal sealed class SliderForm : Form
    {
        private readonly SliderBar _bar;
        private readonly Label _label;

        public event EventHandler ValueChanged;

        public SliderForm(int initial, Color back, Color text, Color sub, Color accent, Font font)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Text = "面板透明度";
            ClientSize = new Size(380, 130);
            BackColor = back;

            _label = new Label();
            _label.SetBounds(14, 14, 352, 20);
            _label.ForeColor = sub;
            _label.BackColor = back;
            _label.Text = "面板透明度：" + initial + "%";

            _bar = new SliderBar(accent, Color.FromArgb(255, 74, 74, 84));
            _bar.SetBounds(14, 42, 352, 26);
            _bar.Value = initial;
            _bar.ValueChanged += delegate
            {
                _label.Text = "面板透明度：" + _bar.Value + "%";
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            };

            Button ok = MakeButton("确定", DialogResult.OK, back, text);
            ok.SetBounds(194, 84, 82, 30);
            Button cancel = MakeButton("取消", DialogResult.Cancel, back, text);
            cancel.SetBounds(284, 84, 82, 30);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(_label);
            Controls.Add(_bar);
            Controls.Add(ok);
            Controls.Add(cancel);
        }

        public int Value { get { return _bar.Value; } }

        private static Button MakeButton(string caption, DialogResult result, Color back, Color textColor)
        {
            Button b = new Button();
            b.Text = caption;
            b.DialogResult = result;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, textColor);
            b.BackColor = Color.FromArgb(255,
                Math.Min(255, back.R + 16), Math.Min(255, back.G + 16), Math.Min(255, back.B + 16));
            b.ForeColor = textColor;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(255,
                Math.Min(255, back.R + 28), Math.Min(255, back.G + 28), Math.Min(255, back.B + 28));
            b.TabStop = false;
            return b;
        }
    }

    internal sealed class CaptureForm : Form
    {
        private readonly Bitmap _screen;
        private readonly Func<Bitmap, bool> _save;
        private Point _start;
        private Rectangle _sel;
        private bool _dragging;
        private bool _saved;

        public CaptureForm(Bitmap screen, Func<Bitmap, bool> save)
        {
            _screen = screen;
            _save = save;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Cursor = Cursors.Cross;
            Cursor.Current = Cursors.Cross;
            Activate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (!_saved) _screen.Dispose();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _start = e.Location;
                _sel = Rectangle.Empty;
                Invalidate();
            }
            else
            {
                Close();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                _sel = new Rectangle(
                    Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                    Math.Abs(e.X - _start.X), Math.Abs(e.Y - _start.Y));
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !_dragging) return;
            _dragging = false;
            if (_sel.Width < 4 || _sel.Height < 4)
            {
                Close();
                return;
            }
            Rectangle src = Rectangle.Intersect(_sel, new Rectangle(0, 0, _screen.Width, _screen.Height));
            if (src.Width < 1 || src.Height < 1)
            {
                Close();
                return;
            }
            Bitmap crop = null;
            try
            {
                crop = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(crop))
                {
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(_screen, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
                }
                _saved = _save != null && _save(crop);
            }
            catch
            {
                if (crop != null && !_saved) crop.Dispose();
            }
            Close();
            base.OnMouseUp(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImage(_screen, 0, 0);
            if (_sel.Width <= 0 || _sel.Height <= 0)
            {
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(dim, ClientRectangle);
                string hint = "拖动鼠标选择截图区域   ·   Esc / 右键 取消   ·   松开后自动保存并复制到剪贴板";
                using (SolidBrush b = new SolidBrush(Color.White))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                {
                    Size sz = TextRenderer.MeasureText(hint, Font);
                    int hx = Math.Max(10, (ClientSize.Width - sz.Width) / 2);
                    int hy = Math.Max(10, ClientSize.Height / 2 - sz.Height - 10);
                    g.FillRectangle(bg, hx - 10, hy - 6, sz.Width + 20, sz.Height + 12);
                    g.DrawString(hint, Font, b, hx, hy);
                }
                return;
            }

            using (Region region = new Region(ClientRectangle))
            {
                region.Exclude(_sel);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                    g.FillRegion(dim, region);
            }
            using (Pen p = new Pen(Color.FromArgb(255, 59, 130, 246), 2f))
                g.DrawRectangle(p, _sel);

            string info = _sel.Width + " × " + _sel.Height;
            using (SolidBrush b = new SolidBrush(Color.White))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
            {
                Size sz = TextRenderer.MeasureText(info, Font);
                int tx = _sel.Left;
                int ty = _sel.Bottom + 4;
                if (ty + sz.Height + 4 > ClientSize.Height) ty = _sel.Top - sz.Height - 8;
                if (ty < 0) ty = _sel.Bottom + 4;
                g.FillRectangle(bg, tx, ty, sz.Width + 10, sz.Height + 6);
                g.DrawString(info, Font, b, tx + 5, ty + 3);
            }
        }
    }

    internal sealed class KeyCaptureForm : Form
    {
        private Keys _result = Keys.None;

        public Keys Result { get { return _result; } }

        public KeyCaptureForm(Color back, Color text, Color sub, Font font)
        {
            Text = "设置快捷键";
            Font = font;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(320, 120);
            BackColor = back;
            KeyPreview = true;

            Label hint = new Label();
            hint.Text = "请直接按下要设置的按键\r\n（Esc 取消，仅支持字母 / 数字）";
            hint.SetBounds(16, 18, 290, 50);
            hint.ForeColor = sub;
            hint.BackColor = back;
            Controls.Add(hint);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(80, 28);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderSize = 1;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(95, text);
            cancel.BackColor = back;
            cancel.ForeColor = text;
            cancel.TabStop = false;
            cancel.SetBounds(224, 78, 80, 28);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            Keys key = keyData & ~Keys.Modifiers;
            if (key >= Keys.A && key <= Keys.Z)
            {
                _result = key;
                DialogResult = DialogResult.OK;
                Close();
                return true;
            }
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                _result = key;
                DialogResult = DialogResult.OK;
                Close();
                return true;
            }
            return true;
        }
    }

    internal sealed class ShortcutsForm : Form
    {
        private readonly Config _cfg;
        private readonly Color _back;
        private readonly Color _text;
        private readonly Color _sub;
        private readonly Color _accent = Color.FromArgb(255, 59, 130, 246);
        private readonly Label[] _keyLabels = new Label[5];
        private readonly Label[] _descLabels = new Label[5];
        private readonly Action _hotkeyChanged;

        public ShortcutsForm(Config cfg, Color back, Color text, Color sub, Font font, Action hotkeyChanged)
        {
            _cfg = cfg;
            _hotkeyChanged = hotkeyChanged;
            _back = back;
            _text = text;
            _sub = sub;
            Text = "快捷键";
            Font = font;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 230);
            BackColor = back;

            string[] descs = new string[]
            {
                "删除选中的图片",
                "加入 / 取消收藏",
                "合并选中的图片",
                "裁切选中的图片",
                "截图"
            };

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.ColumnCount = 3;
            table.RowCount = 5;
            table.Padding = new Padding(16, 14, 16, 8);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44f));

            int row = 0;
            for (int i = 0; i < 5; i++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
                Label desc = new Label();
                desc.Text = descs[i];
                desc.Dock = DockStyle.Fill;
                desc.TextAlign = ContentAlignment.MiddleLeft;
                desc.ForeColor = text;
                desc.BackColor = back;
                desc.Margin = new Padding(0);
                _descLabels[i] = desc;

                Label key = new Label();
                key.Dock = DockStyle.Fill;
                key.TextAlign = ContentAlignment.MiddleRight;
                key.ForeColor = _accent;
                key.BackColor = back;
                key.Margin = new Padding(0, 0, 8, 0);
                _keyLabels[i] = key;

                Label edit = new Label();
                edit.Text = "改键";
                edit.Dock = DockStyle.Fill;
                edit.TextAlign = ContentAlignment.MiddleCenter;
                edit.ForeColor = sub;
                edit.BackColor = back;
                edit.Cursor = Cursors.Hand;
                edit.Margin = new Padding(0);
                int index = i;
                edit.Click += delegate { Rebind(index); };
                edit.MouseEnter += delegate { edit.ForeColor = _accent; };
                edit.MouseLeave += delegate { edit.ForeColor = _sub; };

                table.Controls.Add(desc, 0, row);
                table.Controls.Add(key, 1, row);
                table.Controls.Add(edit, 2, row);
                row++;
            }

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;
            bottom.BackColor = back;

            Button reset = MakeButton("恢复默认", 86);
            reset.SetBounds(16, 8, 86, 30);
            reset.Click += delegate
            {
                _cfg.KeyDelete = "X";
                _cfg.KeyFavorite = "F";
                _cfg.KeyMerge = "E";
                _cfg.KeyCrop = "T";
                _cfg.Save();
                UpdateLabels();
            };
            bottom.Controls.Add(reset);

            Button close = MakeButton("关闭", 86);
            close.SetBounds(328, 8, 86, 30);
            close.Click += delegate { Close(); };
            bottom.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            Controls.Add(table);
            Controls.Add(bottom);
            UpdateLabels();
        }

        private Button MakeButton(string caption, int width)
        {
            Button b = new Button();
            b.Text = caption;
            b.Size = new Size(width, 30);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(95, _text);
            b.BackColor = _back;
            b.ForeColor = _text;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(255,
                Math.Min(255, _back.R + 22), Math.Min(255, _back.G + 22), Math.Min(255, _back.B + 22));
            b.TabStop = false;
            return b;
        }

        private void UpdateLabels()
        {
            if (_keyLabels[0] != null) _keyLabels[0].Text = _cfg.KeyDelete;
            if (_keyLabels[1] != null) _keyLabels[1].Text = _cfg.KeyFavorite;
            if (_keyLabels[2] != null) _keyLabels[2].Text = _cfg.KeyMerge;
            if (_keyLabels[3] != null) _keyLabels[3].Text = _cfg.KeyCrop;
            if (_keyLabels[4] != null) _keyLabels[4].Text = Config.ModName(_cfg.ShotMod) + " + " + _cfg.KeyShot;
        }

        private void Rebind(int index)
        {
            using (KeyCaptureForm form = new KeyCaptureForm(_back, _text, _sub, Font))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                string key = ((char)(int)form.Result).ToString();
                string[] others = new string[]
                {
                    _cfg.KeyFavorite, _cfg.KeyMerge, _cfg.KeyCrop, _cfg.KeyDelete
                };
                for (int i = 0; i < others.Length; i++)
                {
                    if (i == index) continue;
                    if (string.Equals(others[i], key, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this, "按键 " + key + " 已经被占用了，换一个吧。", "快捷键",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
                if (index == 0) _cfg.KeyDelete = key;
                else if (index == 1) _cfg.KeyFavorite = key;
                else if (index == 2) _cfg.KeyMerge = key;
                else if (index == 3) _cfg.KeyCrop = key;
                else _cfg.KeyShot = key;
                _cfg.Save();
                UpdateLabels();
                if (_hotkeyChanged != null) _hotkeyChanged();
            }
        }
    }

    internal sealed class PromptForm : Form
    {
        private TextBox _box;

        private PromptForm(string title, string prompt, string initial, string okCaption, Color back, Color text, Color sub)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Text = title;
            ClientSize = new Size(380, 124);
            BackColor = back;
            Padding = new Padding(0);

            Label lbl = new Label();
            lbl.Text = prompt;
            lbl.SetBounds(14, 12, 350, 20);
            lbl.ForeColor = sub;
            lbl.BackColor = back;

            _box = new TextBox();
            _box.SetBounds(14, 38, 352, 24);
            _box.Text = initial;
            _box.BackColor = Lighten(back, 14);
            _box.ForeColor = text;
            _box.BorderStyle = BorderStyle.FixedSingle;

            Button ok = MakeButton(okCaption, DialogResult.OK, back, text);
            ok.SetBounds(194, 78, 82, 30);
            Button cancel = MakeButton("取消", DialogResult.Cancel, back, text);
            cancel.SetBounds(284, 78, 82, 30);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(lbl);
            Controls.Add(_box);
            Controls.Add(ok);
            Controls.Add(cancel);

            Shown += delegate { _box.SelectAll(); _box.Focus(); };
        }

        private static Button MakeButton(string caption, DialogResult result, Color back, Color textColor)
        {
            Button b = new Button();
            b.Text = caption;
            b.DialogResult = result;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, textColor);
            b.BackColor = Lighten(back, 16);
            b.ForeColor = textColor;
            b.FlatAppearance.MouseOverBackColor = Lighten(back, 28);
            b.TabStop = false;
            return b;
        }

        private static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(255,
                Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));
        }

        public static string Ask(IWin32Window owner, string title, string prompt, string initial, string okCaption,
            Color back, Color text, Color sub, Font font)
        {
            using (PromptForm f = new PromptForm(title, prompt, initial, okCaption, back, text, sub))
            {
                f.Font = font;
                if (f.ShowDialog(owner) != DialogResult.OK) return null;
                return f._box.Text.Trim();
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HOTKEY_ID = 0xA001;
        private const int SHOT_HOTKEY_ID = 0xA002;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_V = 0x56;
        private const int MaxLoaded = 5000;
        private const int MaxItems = 5000;

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool LockWindowUpdate(IntPtr hWndLock);

        private readonly Config _cfg;
        private readonly Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ThumbTile> _selectedTiles = new List<ThumbTile>();
        private readonly string _dirOverride;
        private readonly string _cropPath;
        private readonly string _mergePaths;
        private readonly bool _showShortcuts;
        private string _dir;
        private bool _boardUsed;
        private bool _boardCreatedHere;

        private TableLayoutPanel _root;
        private Panel _topBar;
        private Label _title;
        private TilePanel _panel;
        private Panel _pagerBar;
        private Font _pagerFont;
        private TextBox _pageBox;
        private PagerLabel _pageCurrent;
        private PagerButton _prevButton;
        private PagerButton _nextButton;
        private Button _pauseButton;
        private Button _starButton;
        private Button _ballButton;
        private ToolTip _tip;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _autoItem;
        private ToolStripMenuItem _topItem;
        private ToolStripMenuItem _autoStartItem;
        private ToolStripMenuItem _selectAllItem;
        private ToolStripMenuItem _clearItem;
        private ToolStripMenuItem _exportFavItem;

        private System.Windows.Forms.Timer _clipTimer;
        private NotifyIcon _tray;
        private Icon _icon;

        private Color _textColor = Color.FromArgb(255, 235, 235, 242);
        private Color _subColor = Color.FromArgb(255, 150, 150, 162);
        private Color _accent = Color.FromArgb(255, 59, 130, 246);
        private Color _starColor = Color.FromArgb(255, 250, 190, 40);
        private bool _favoritesView;
        private bool _downOnStar;
        private ThumbTile _anchorTile;
        private ThumbTile _clickCollapseTile;

        private DateTime _suppressUntil = DateTime.MinValue;
        private bool _draggingOut;
        private bool _reallyExit;
        private bool _hideTipShown;
        private bool _paused;
        private int _tileSize = 92;

        private ThumbTile _dragTile;
        private Point _dragStart;
        private bool _dragStarted;
        private bool _downOnBadge;
        private bool _rmbCandidate;
        private bool _rmbDragging;
        private bool _suppressMenu;
        private Point _rmbStartScreen;
        private Point _rmbOrigin;

        private readonly Dictionary<string, Image> _thumbCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _thumbOrder = new List<string>();
        private readonly List<KeyValuePair<string, string>> _thumbQueue = new List<KeyValuePair<string, string>>();
        private readonly HashSet<string> _thumbQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer _thumbTimer;
        private readonly Dictionary<string, Image> _placeholderCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private BallForm _ball;
        private CropForm _cropForm;
        private MergeForm _mergeForm;
        private ContextMenuStrip _tileMenu;
        private double _dpiScale = 1.0;

        public MainForm(string dirOverride, string cropPath, string mergePaths, bool showShortcuts)
        {
            _dirOverride = dirOverride;
            _cropPath = cropPath;
            _mergePaths = mergePaths;
            _showShortcuts = showShortcuts;
            _cfg = Config.Load();
            _dir = string.IsNullOrEmpty(dirOverride) ? _cfg.RootDir : dirOverride;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = Program.AppTitle;
            Font = Program.UiFont();
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(442, 600);
            MinimumSize = new Size(240, 300);
            _icon = Program.LoadAppIcon(32);
            if (_icon == null) _icon = Program.CreateAppIcon();
            Icon = _icon;
            ShowInTaskbar = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(wa.Left + 20, wa.Right - Width - 60), Math.Max(wa.Top + 20, wa.Top + 90));

            _menu = BuildMenu();
            _tileMenu = BuildTileMenu();

            _root = new RootTable();
            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 1;
            _root.RowCount = 3;
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

            _topBar = new ChromePanel();
            _topBar.Dock = DockStyle.Fill;
            _topBar.Margin = new Padding(0);
            _topBar.Cursor = Cursors.SizeAll;
            _topBar.MouseDown += OnChromeMouseDown;
            _topBar.MouseMove += OnChromeMouseMove;
            _topBar.MouseUp += OnChromeMouseUp;

            _title = new ChromeLabel();
            _title.Dock = DockStyle.Left;
            _title.Width = 260;
            _title.Text = "ClipDrop";
            _title.TextAlign = ContentAlignment.MiddleLeft;
            _title.Padding = new Padding(10, 0, 0, 0);
            _title.AutoSize = false;
            _title.Cursor = Cursors.SizeAll;
            _title.MouseDown += OnChromeMouseDown;
            _title.MouseMove += OnChromeMouseMove;
            _title.MouseUp += OnChromeMouseUp;

            FlowLayoutPanel windowButtons = new ChromeFlow();
            windowButtons.Dock = DockStyle.Right;
            windowButtons.Width = 124;
            windowButtons.FlowDirection = FlowDirection.LeftToRight;
            windowButtons.WrapContents = false;
            windowButtons.Margin = new Padding(0);
            windowButtons.Padding = new Padding(0);

            _pauseButton = MakeChromeButton(ChromeGlyph.Pause);
            _pauseButton.Click += delegate { TogglePaused(); };

            _starButton = MakeChromeButton(ChromeGlyph.Star);
            _starButton.Click += delegate { ToggleFavoritesView(); };

            _ballButton = MakeChromeButton(ChromeGlyph.Ball);
            _ballButton.Click += delegate { CollapseToBall(); };

            Button closeButton = MakeChromeButton(ChromeGlyph.Close);
            closeButton.Click += delegate { Close(); };
            windowButtons.Controls.Add(_pauseButton);
            windowButtons.Controls.Add(_starButton);
            windowButtons.Controls.Add(_ballButton);
            windowButtons.Controls.Add(closeButton);

            _topBar.Controls.Add(windowButtons);
            _topBar.Controls.Add(_title);

            _panel = new TilePanel();
            _panel.Dock = DockStyle.Fill;
            _panel.Margin = new Padding(0);
            _panel.AllowDrop = true;
            _panel.ContextMenuStrip = _menu;
            _panel.DragEnter += OnFileDragEnter;
            _panel.DragOver += OnFileDragEnter;
            _panel.DragDrop += OnFileDragDrop;
            _panel.MouseDown += OnPanelMouseDown;
            _panel.MouseMove += OnChromeMouseMove;
            _panel.MouseUp += OnPanelMouseUp;
            _panel.PageChanged += delegate { UpdatePager(); };

            _root.Controls.Add(_topBar, 0, 0);
            _root.Controls.Add(_panel, 0, 1);
            _root.Controls.Add(BuildPagerBar(), 0, 2);
            Controls.Add(_root);

            AllowDrop = true;
            DragEnter += OnFileDragEnter;
            DragOver += OnFileDragEnter;
            DragDrop += OnFileDragDrop;


            _clipTimer = new System.Windows.Forms.Timer();
            _clipTimer.Interval = 130;
            _clipTimer.Tick += delegate
            {
                _clipTimer.Stop();
                if (_paused) return;
                if (_cfg.AutoCapture) CaptureClipboard(false);
                else SetStatus("剪贴板有新图片：自动保存已关，按 Ctrl+Alt+V 手动保存。");
            };

            _thumbTimer = new System.Windows.Forms.Timer();
            _thumbTimer.Interval = 25;
            _thumbTimer.Tick += delegate
            {
                _thumbTimer.Stop();
                if (OwnedDialogOpen())
                {
                    if (_thumbQueue.Count > 0) _thumbTimer.Start();
                    return;
                }
                DecodePendingThumb();
                if (_thumbQueue.Count > 0) _thumbTimer.Start();
            };

            _tip = new ToolTip();
            _tip.SetToolTip(_pauseButton, "暂停同步");
            _tip.SetToolTip(_ballButton, "收起为小球");

            _tray = new NotifyIcon();
            _tray.Icon = _icon != null ? _icon : SystemIcons.Application;
            _tray.Text = "ClipDrop";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowFromTray(); };

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Renderer = new DarkMenuRenderer();
            trayMenu.Font = Font;
            trayMenu.Items.Add("打开面板", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("保存当前剪贴板图片", null, delegate { CaptureClipboard(true); });
            trayMenu.Items.Add("截图", null, delegate { CaptureRegion(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem trayAutoStart = new ToolStripMenuItem("开机自动启动");
            trayAutoStart.Click += delegate { SetAutoStart(!_cfg.AutoStart); };
            trayMenu.Items.Add(trayAutoStart);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { _reallyExit = true; Close(); });
            trayMenu.Opening += delegate { trayAutoStart.Checked = _cfg.AutoStart; };
            _tray.ContextMenuStrip = trayMenu;

            ApplyColors();
        }

        private enum ChromeGlyph { Pause, Play, Star, Ball, Close }

        private Button MakeChromeButton(ChromeGlyph glyph)
        {
            Button b = new ChromeButton();
            b.Tag = glyph;
            b.Size = new Size(30, 26);
            b.Margin = new Padding(1, 2, 1, 2);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.TabStop = false;
            b.Cursor = Cursors.Default;
            b.Paint += OnChromeButtonPaint;
            return b;
        }

        private void OnChromeButtonPaint(object sender, PaintEventArgs e)
        {
            Button b = (Button)sender;
            ChromeGlyph glyph = (ChromeGlyph)b.Tag;
            if (glyph == ChromeGlyph.Pause && _paused) glyph = ChromeGlyph.Play;
            Color color = _subColor;
            if (b == _pauseButton && _paused) color = _accent;
            if (b == _starButton && _favoritesView) color = _starColor;
            DrawGlyph(e.Graphics, new Rectangle(0, 0, b.Width, b.Height), glyph, color);
        }

        private static void DrawGlyph(Graphics g, Rectangle r, ChromeGlyph glyph, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.Width / 2f;
            float cy = r.Height / 2f;
            using (Pen p = new Pen(color, 1.7f))
            using (SolidBrush brush = new SolidBrush(color))
            {
                switch (glyph)
                {
                    case ChromeGlyph.Pause:
                        g.FillRectangle(brush, cx - 5.5f, cy - 6f, 4f, 12f);
                        g.FillRectangle(brush, cx + 1.5f, cy - 6f, 4f, 12f);
                        break;
                    case ChromeGlyph.Play:
                        g.FillPolygon(brush, new PointF[]
                        {
                            new PointF(cx - 4.5f, cy - 6.5f),
                            new PointF(cx - 4.5f, cy + 6.5f),
                            new PointF(cx + 6.5f, cy)
                        });
                        break;
                    case ChromeGlyph.Star:
                        g.FillPolygon(brush, StarPoints(cx, cy, 7.5f, 3.2f));
                        break;
                    case ChromeGlyph.Ball:
                        g.FillEllipse(brush, cx - 5.5f, cy - 5.5f, 11f, 11f);
                        break;
                    case ChromeGlyph.Close:
                        g.DrawLine(p, cx - 5f, cy - 5f, cx + 5f, cy + 5f);
                        g.DrawLine(p, cx + 5f, cy - 5f, cx - 5f, cy + 5f);
                        break;
                }
            }
        }

        internal static PointF[] StarPoints(float cx, float cy, float outer, float inner)
        {
            PointF[] pts = new PointF[10];
            double start = -Math.PI / 2;
            for (int i = 0; i < 10; i++)
            {
                double angle = start + i * Math.PI / 5;
                float radius = (i % 2 == 0) ? outer : inner;
                pts[i] = new PointF(
                    cx + (float)(Math.Cos(angle) * radius),
                    cy + (float)(Math.Sin(angle) * radius));
            }
            return pts;
        }

        private Control BuildPagerBar()
        {
            Panel bar = new DarkPanel();
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0);

            _prevButton = MakePagerButton(true);
            _prevButton.Click += delegate { _panel.PrevPage(); };
            _nextButton = MakePagerButton(false);
            _nextButton.Click += delegate { _panel.NextPage(); };

            _pageBox = new TextBox();
            _pageBox.Text = "1";
            _pageBox.Font = Font;
            _pageBox.AutoSize = false;
            _pageBox.Width = 34;
            _pageBox.Height = 20;
            _pageBox.TextAlign = HorizontalAlignment.Center;
            _pageBox.BorderStyle = BorderStyle.None;
            _pageBox.BackColor = Color.FromArgb(24, 24, 28);
            _pageBox.ForeColor = Color.FromArgb(235, 235, 242);
            _pageBox.Margin = new Padding(0);
            _pageBox.Visible = false;
            _pageBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    EndPageEdit(false);
                    return;
                }
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                EndPageEdit(true);
            };
            _pageBox.LostFocus += delegate { EndPageEdit(false); };

            _pageCurrent = new PagerLabel();
            _pageCurrent.Font = Font;
            _pageCurrent.Size = new Size(110, 20);
            _pageCurrent.Margin = new Padding(0);
            _pageCurrent.Cursor = Cursors.Hand;
            _pageCurrent.Click += delegate { BeginPageEdit(); };

            bar.Controls.Add(_prevButton);
            bar.Controls.Add(_pageBox);
            bar.Controls.Add(_pageCurrent);
            bar.Controls.Add(_nextButton);
            bar.Resize += delegate { LayoutPager(); };
            bar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(42, _textColor)))
                    e.Graphics.DrawLine(p, 0, 0, bar.ClientSize.Width, 0);
            };
            _pagerBar = bar;
            return bar;
        }

        private class PagerButton : Control
        {
            public bool IsLeft;
            public Color ArrowColor = Color.Gray;
            public Color BorderColor = Color.Gray;
            public Color HoverBack = Color.Black;
            private bool _hover;

            public PagerButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush bg = new SolidBrush(_hover && Enabled ? HoverBack : BackColor))
                    g.FillRectangle(bg, ClientRectangle);
                using (Pen p = new Pen(Enabled ? BorderColor : Color.FromArgb(60, BorderColor), 1f))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Width / 2f;
                float cy = Height / 2f;
                PointF[] tri = IsLeft
                    ? new PointF[]
                    {
                        new PointF(cx + 3f, cy - 4.5f),
                        new PointF(cx + 3f, cy + 4.5f),
                        new PointF(cx - 4f, cy)
                    }
                    : new PointF[]
                    {
                        new PointF(cx - 3f, cy - 4.5f),
                        new PointF(cx - 3f, cy + 4.5f),
                        new PointF(cx + 4f, cy)
                    };
                using (SolidBrush brush = new SolidBrush(Enabled ? ArrowColor : Color.FromArgb(90, ArrowColor)))
                    g.FillPolygon(brush, tri);
            }
        }

        private class PagerLabel : Control
        {
            public string CurrentText = "1";
            public string TotalText = "1";
            public Color CurrentColor = Color.White;
            public Color TotalColor = Color.Gray;
            public Color BarColor = Color.Gray;
            public int Gap = 7;
            public int BarWidth = 1;
            private bool _hover;

            public PagerLabel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
            {
                System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
                int d = radius * 2;
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            private static readonly StringFormat Typo = StringFormat.GenericTypographic;
            private const System.Drawing.Text.TextRenderingHint TextHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            private readonly Dictionary<string, Rectangle> _inkCache = new Dictionary<string, Rectangle>();

            private Rectangle InkOf(Graphics g, string text)
            {
                string key = text + "|" + Font.Name + "|" + Font.SizeInPoints;
                Rectangle hit;
                if (_inkCache.TryGetValue(key, out hit)) return hit;
                SizeF size = g.MeasureString(text, Font, new PointF(0, 0), Typo);
                int w = Math.Max(2, (int)Math.Ceiling(size.Width)) + 8;
                int h = Math.Max(2, (int)Math.Ceiling(size.Height)) + 8;
                Rectangle ink = new Rectangle(0, 0, Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)));
                using (Bitmap bmp = new Bitmap(w, h))
                {
                    using (Graphics bg = Graphics.FromImage(bmp))
                    {
                        bg.Clear(Color.Black);
                        bg.TextRenderingHint = TextHint;
                        using (SolidBrush wb = new SolidBrush(Color.White)) bg.DrawString(text, Font, wb, 2, 2, Typo);
                    }
                    int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            if (bmp.GetPixel(x, y).R <= 40) continue;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                    if (maxX >= 0) ink = new Rectangle(minX - 2, minY - 2, maxX - minX + 1, maxY - minY + 1);
                }
                _inkCache[key] = ink;
                return ink;
            }

            private void LayoutRects(Graphics g, out Rectangle curInk, out Rectangle bar, out Rectangle totInk, out Point curDraw, out Point totDraw)
            {
                Rectangle ci = InkOf(g, CurrentText);
                Rectangle ti = InkOf(g, TotalText);
                int groupW = ci.Width + Gap + BarWidth + Gap + ti.Width;
                int x = Math.Max(0, (ClientSize.Width - groupW) / 2);
                int y = Math.Max(0, (ClientSize.Height - ci.Height + 1) / 2);
                curInk = new Rectangle(x, y, ci.Width, ci.Height);
                totInk = new Rectangle(curInk.Right + Gap + BarWidth + Gap, y, ti.Width, ti.Height);
                bar = new Rectangle(curInk.Right + Gap, curInk.Y, BarWidth, ci.Height);
                curDraw = new Point(curInk.X - ci.X, curInk.Y - ci.Y);
                totDraw = new Point(totInk.X - ti.X, totInk.Y - ti.Y);
            }

            public void FitWidth()
            {
                using (Graphics g = CreateGraphics())
                {
                    Rectangle ci, bar, ti;
                    Point cd, td;
                    LayoutRects(g, out ci, out bar, out ti, out cd, out td);
                    int w = ti.Right;
                    if (w > 0 && Width != w) Width = w;
                }
            }

            public Rectangle CurrentRectangle()
            {
                using (Graphics g = CreateGraphics())
                {
                    Rectangle ci, bar, ti;
                    Point cd, td;
                    LayoutRects(g, out ci, out bar, out ti, out cd, out td);
                    return ci;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
                Rectangle ci, bar, ti;
                Point cd, td;
                LayoutRects(g, out ci, out bar, out ti, out cd, out td);
                if (_hover)
                {
                    Rectangle hl = new Rectangle(ci.X - 8, ci.Y - 3, (ti.Right - ci.X) + 16, ci.Height + 6);
                    using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRect(hl, 4))
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
                        g.FillPath(hb, path);
                }
                g.TextRenderingHint = TextHint;
                using (SolidBrush cb = new SolidBrush(CurrentColor)) g.DrawString(CurrentText, Font, cb, cd.X, cd.Y, Typo);
                using (SolidBrush tb = new SolidBrush(TotalColor)) g.DrawString(TotalText, Font, tb, td.X, td.Y, Typo);
                using (SolidBrush b = new SolidBrush(BarColor)) g.FillRectangle(b, bar);
            }
        }

        private void LayoutPager()
        {
            if (_pagerBar == null || _prevButton == null || _pageBox == null || _pageCurrent == null || _nextButton == null) return;
            const int height = 20;
            const int gap = 10;
            int pageWidth = _pageCurrent.Width;
            int total = _prevButton.Width + pageWidth + _nextButton.Width + gap * 2;
            int x = Math.Max(0, (_pagerBar.ClientSize.Width - total) / 2);
            int y = Math.Max(0, (_pagerBar.ClientSize.Height - height) / 2);
            int pageX = x + _prevButton.Width + gap;
            _prevButton.SetBounds(x, y, _prevButton.Width, height);
            _pageCurrent.SetBounds(pageX, y, pageWidth, height);
            _nextButton.SetBounds(x + total - _nextButton.Width, y, _nextButton.Width, height);
            if (_pageBox.Visible) PositionPageBox();
        }

        private void PositionPageBox()
        {
            if (_pageBox == null || _pageCurrent == null) return;
            Rectangle cur = _pageCurrent.CurrentRectangle();
            int boxW = Math.Max(22, cur.Width + 10);
            if (_pageBox.Width != boxW) _pageBox.Width = boxW;
            int bx = _pageCurrent.Left + cur.X + (cur.Width - _pageBox.Width) / 2;
            int by = _pageCurrent.Top + (_pageCurrent.Height - _pageBox.Height) / 2;
            _pageBox.SetBounds(bx, by, _pageBox.Width, _pageBox.Height);
        }

        private void BeginPageEdit()
        {
            if (_pageBox == null || _pageCurrent == null) return;
            _pageBox.Visible = true;
            _pageBox.Text = (_panel.PageIndex + 1).ToString(CultureInfo.InvariantCulture);
            PositionPageBox();
            _pageBox.BringToFront();
            _pageBox.Focus();
            _pageBox.SelectAll();
        }

        private void EndPageEdit(bool commit)
        {
            if (_pageBox == null || _pageCurrent == null) return;
            if (commit)
            {
                int n;
                if (int.TryParse(_pageBox.Text.Trim(), out n)) _panel.GoToPage(n - 1);
            }
            _pageBox.Visible = false;
            UpdatePager();
        }

        private PagerButton MakePagerButton(bool left)
        {
            PagerButton b = new PagerButton();
            b.IsLeft = left;
            b.Size = new Size(26, 20);
            b.Margin = new Padding(0);
            b.BackColor = _cfg.BgColor;
            b.ArrowColor = _subColor;
            b.BorderColor = Color.FromArgb(95, _textColor);
            b.HoverBack = Lighten(_cfg.BgColor, 22);
            return b;
        }

        private void AdjustPagerHeight()
        {
            if (_root == null || _panel == null || _root.RowStyles.Count < 3) return;
            int tileH = _panel.TileHeight;
            if (tileH <= 0) return;
            int topH = (int)_root.RowStyles[0].Height;
            int minBar = 34;
            int pad = _panel.Pad;
            int gap = _panel.Gap;
            int step = tileH + gap;
            int clientH = ClientSize.Height;
            int avail = clientH - topH - minBar;
            if (avail < step) return;
            int rows = Math.Max(1, (avail - pad * 2 + gap) / step);
            int panelH = pad * 2 + rows * tileH + (rows - 1) * gap;
            int barH = Math.Max(minBar, clientH - topH - panelH);
            if ((int)_root.RowStyles[2].Height != barH) _root.RowStyles[2].Height = barH;
        }

        private void UpdatePager()
        {
            if (_pageBox == null || _pageCurrent == null) return;
            _pageCurrent.CurrentText = (_panel.PageIndex + 1).ToString(CultureInfo.InvariantCulture);
            _pageCurrent.TotalText = _panel.PageCount.ToString(CultureInfo.InvariantCulture);
            _pageCurrent.FitWidth();
            _pageCurrent.Invalidate();
            if (!_pageBox.Visible)
                _pageBox.Text = (_panel.PageIndex + 1).ToString(CultureInfo.InvariantCulture);
            LayoutPager();
            bool many = _panel.PageCount > 1;
            _prevButton.Enabled = many && _panel.PageIndex > 0;
            _nextButton.Enabled = many && _panel.PageIndex < _panel.PageCount - 1;
        }

        private void OnChromeMouseDown(object sender, MouseEventArgs e)
        {
            CancelPageEdit();
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                return;
            }
            if (e.Button == MouseButtons.Right) BeginRightDrag((Control)sender);
        }

        private void OnChromeMouseMove(object sender, MouseEventArgs e)
        {
            ContinueRightDrag();
        }

        private void OnChromeMouseUp(object sender, MouseEventArgs e)
        {
            EndRightDrag((Control)sender);
        }

        private void BeginRightDrag(Control c)
        {
            _rmbCandidate = true;
            _rmbDragging = false;
            _suppressMenu = false;
            _rmbStartScreen = Cursor.Position;
            _rmbOrigin = Location;
            try { c.Capture = true; }
            catch { }
        }

        private void ContinueRightDrag()
        {
            if (!_rmbCandidate) return;
            Point now = Cursor.Position;
            if (!_rmbDragging)
            {
                if (Math.Abs(now.X - _rmbStartScreen.X) < 4 && Math.Abs(now.Y - _rmbStartScreen.Y) < 4) return;
                _rmbDragging = true;
                _suppressMenu = true;
            }
            Location = new Point(_rmbOrigin.X + (now.X - _rmbStartScreen.X),
                _rmbOrigin.Y + (now.Y - _rmbStartScreen.Y));
        }

        private void EndRightDrag(Control c)
        {
            if (!_rmbCandidate) return;
            _rmbCandidate = false;
            _rmbDragging = false;
            try { c.Capture = false; }
            catch { }
        }

        private void ToggleFavoritesView()
        {
            _favoritesView = !_favoritesView;
            _starButton.Invalidate();
            _tip.SetToolTip(_starButton, _favoritesView ? "显示全部图片" : "收藏夹");

            try
            {
                bool truncated = _panel.Controls.Count >= MaxLoaded;
                if (truncated)
                {
                    ReloadFromDisk(MaxLoaded);
                }
                else
                {
                    int shown = 0;
                    _panel.Freeze(true);
                    try
                    {
                        foreach (Control c in _panel.Controls)
                        {
                            ThumbTile t = c as ThumbTile;
                            if (t == null) continue;
                            bool fav = _cfg.IsFavorite(_dir, Path.GetFileName(t.FilePath));
                            t.HiddenByFilter = _favoritesView && !fav;
                            if (!t.HiddenByFilter) shown++;
                        }
                    }
                    finally
                    {
                        _panel.Freeze(false);
                    }
                    _selectedTiles.Clear();
                    _panel.ResetScroll();
                    _panel.Invalidate();
                    UpdatePager();
                    SetStatus(_favoritesView ? "收藏夹：" + shown + " 张" : "已显示全部图片。");
                    return;
                }
                UpdatePager();
                SetStatus(_favoritesView
                    ? "收藏夹：" + _cfg.GetFavorites(_dir).Count + " 张"
                    : "已显示全部图片。");
            }
            catch (Exception ex)
            {
                Config.LogError("ToggleFavoritesView", ex);
                try { _panel.Freeze(false); }
                catch { }
                SetStatus("切换收藏夹失败：" + ex.Message);
            }
        }

        private void ToggleFavoriteSelected()
        {
            List<ThumbTile> tiles = new List<ThumbTile>(_selectedTiles);
            if (tiles.Count == 0) return;

            bool allFav = true;
            foreach (ThumbTile t in tiles)
            {
                if (!_cfg.IsFavorite(_dir, Path.GetFileName(t.FilePath)))
                {
                    allFav = false;
                    break;
                }
            }

            int changed = 0;
            foreach (ThumbTile t in tiles)
            {
                string name = Path.GetFileName(t.FilePath);
                bool isFav = _cfg.IsFavorite(_dir, name);
                if (allFav)
                {
                    if (isFav)
                    {
                        _cfg.ToggleFavorite(_dir, name);
                        changed++;
                    }
                    t.Favorited = false;
                }
                else
                {
                    if (!isFav)
                    {
                        _cfg.ToggleFavorite(_dir, name);
                        changed++;
                    }
                    t.Favorited = true;
                }
                if (_favoritesView && !t.Favorited) t.Visible = false;
            }
            _cfg.Save();
            _panel.Arrange();
            _panel.Invalidate();
            SetStatus(allFav
                ? ("已取消收藏 " + changed + " 张")
                : ("已加入收藏 " + changed + " 张"));
        }

        private void ToggleFavorite(ThumbTile tile)
        {
            if (tile == null) return;
            string name = Path.GetFileName(tile.FilePath);
            bool nowFav = _cfg.ToggleFavorite(_dir, name);
            _cfg.Save();
            tile.Favorited = nowFav;
            if (_favoritesView && !nowFav)
            {
                tile.Visible = false;
                _selectedTiles.Remove(tile);
                _panel.Arrange();
                _panel.Invalidate();
            }
            SetStatus(nowFav ? "已加入收藏：" + name : "已取消收藏：" + name);
        }

        private void TogglePaused()
        {
            _paused = !_paused;
            _tip.SetToolTip(_pauseButton, _paused ? "继续同步" : "暂停同步");
            _pauseButton.Invalidate();
            SetStatus(_paused ? "已暂停同步。" : "已继续同步。");
        }

        private static Image _checkOnImage;
        private static Image _checkOffImage;

        private static Image GetCheckImage(bool on)
        {
            if (on)
            {
                if (_checkOnImage == null) _checkOnImage = MakeCheckImage(true);
                return _checkOnImage;
            }
            if (_checkOffImage == null) _checkOffImage = MakeCheckImage(false);
            return _checkOffImage;
        }

        private static Image MakeCheckImage(bool on)
        {
            Bitmap bmp = new Bitmap(15, 15, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                if (on)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 59, 130, 246)))
                        g.FillRectangle(b, 0, 0, 14, 14);
                    using (Pen p = new Pen(Color.White, 2f))
                    {
                        g.DrawLine(p, 3f, 7.5f, 6f, 10.5f);
                        g.DrawLine(p, 6f, 10.5f, 11.5f, 4f);
                    }
                }
                else
                {
                    using (Pen p = new Pen(Color.FromArgb(150, 200, 200, 210), 1.4f))
                        g.DrawRectangle(p, 0, 0, 13, 13);
                }
            }
            return bmp;
        }

        private void SetupRightCheck(ToolStripMenuItem item)
        {
            item.Padding = new Padding(0, 0, 28, 0);
            item.Tag = "rightcheck";
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.Font = Font;
            menu.ShowCheckMargin = false;
            menu.ShowImageMargin = false;

            ToolStripMenuItem newBoard = new ToolStripMenuItem("新建文件夹");
            newBoard.Click += delegate { NewBoardPrompted(); };

            ToolStripMenuItem readBoard = new ToolStripMenuItem("读取文件夹");
            readBoard.Click += delegate { ReadBoard(); };

            ToolStripMenuItem currentFolder = new ToolStripMenuItem("打开当前文件夹");
            currentFolder.Click += delegate { OpenFolder(_dir); };

            ToolStripMenuItem openRoot = new ToolStripMenuItem("打开主目录");
            openRoot.Click += delegate { OpenFolder(_cfg.RootDir); };

            ToolStripMenuItem pickRoot = new ToolStripMenuItem("设置主目录");
            pickRoot.Click += delegate { PickWorkspace(); };

            _autoItem = new ToolStripMenuItem("自动保存图片");
            SetupRightCheck(_autoItem);
            _autoItem.Click += delegate
            {
                _cfg.AutoCapture = !_cfg.AutoCapture;
                _cfg.Save();
                SetStatus(_cfg.AutoCapture ? "自动保存：开" : "自动保存：关（可用 Ctrl+Alt+V 手动保存）");
            };

            _topItem = new ToolStripMenuItem("窗口置顶");
            _topItem.Click += delegate { ToggleTopMost(); };
            SetupRightCheck(_topItem);

            ToolStripMenuItem bgItem = new ToolStripMenuItem("背景颜色");
            bgItem.Click += delegate { PickColor(); };

            ToolStripMenuItem opacityItem = new ToolStripMenuItem("面板透明度");
            opacityItem.Click += delegate { PickOpacity(); };

            _selectAllItem = new ToolStripMenuItem("全选");
            _selectAllItem.Click += delegate { SelectAllTiles(); };

            _exportFavItem = new ToolStripMenuItem("导出已收藏");
            _exportFavItem.Click += delegate { ExportFavorites(); };

            _clearItem = new ToolStripMenuItem("清理未收藏");
            _clearItem.Click += delegate { CleanList(); };

            _autoStartItem = new ToolStripMenuItem("开机自动启动");
            _autoStartItem.Click += delegate { SetAutoStart(!_cfg.AutoStart); };
            SetupRightCheck(_autoStartItem);

            ToolStripMenuItem shortcuts = new ToolStripMenuItem("快捷键");
            shortcuts.Click += delegate { ShowShortcuts(); };

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { _reallyExit = true; Close(); };

            menu.Items.Add(newBoard);
            menu.Items.Add(readBoard);
            menu.Items.Add(currentFolder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(openRoot);
            menu.Items.Add(pickRoot);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_topItem);
            menu.Items.Add(bgItem);
            menu.Items.Add(opacityItem);
            menu.Items.Add(_autoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_selectAllItem);
            menu.Items.Add(_exportFavItem);
            menu.Items.Add(_clearItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(shortcuts);
            menu.Items.Add(exit);

            menu.Opening += delegate(object sender, CancelEventArgs ce)
            {
                if (_suppressMenu)
                {
                    _suppressMenu = false;
                    ce.Cancel = true;
                    return;
                }
                _autoItem.Checked = _cfg.AutoCapture;
                _topItem.Checked = _cfg.TopMost;
                _autoStartItem.Checked = _cfg.AutoStart;
                bool has = _panel != null && _panel.Controls.Count > 0;
                _selectAllItem.Enabled = has;
            };
            return menu;
        }

        private ContextMenuStrip BuildTileMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.Font = Font;
            menu.ShowCheckMargin = false;
            menu.ShowImageMargin = false;

            ToolStripMenuItem crop = new ToolStripMenuItem("裁切图片");
            crop.Click += delegate { CropSelected(); };
            ToolStripMenuItem copyFiles = new ToolStripMenuItem("复制文件");
            copyFiles.Click += delegate { CopySelectedFiles(); };
            ToolStripMenuItem openFile = new ToolStripMenuItem("打开文件");
            openFile.Click += delegate { OpenSelected(); };
            ToolStripMenuItem favItem = new ToolStripMenuItem("加入收藏");
            favItem.Click += delegate { ToggleFavoriteSelected(); };
            ToolStripMenuItem merge = new ToolStripMenuItem("合并图片");
            merge.Click += delegate { MergeSelected(); };
            ToolStripMenuItem delete = new ToolStripMenuItem("删除文件");
            delete.Click += delegate { DeleteTiles(new List<ThumbTile>(_selectedTiles)); };

            menu.Items.Add(crop);
            menu.Items.Add(merge);
            menu.Items.Add(favItem);
            menu.Items.Add(copyFiles);
            menu.Items.Add(openFile);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(delete);
            menu.Opening += delegate(object sender, CancelEventArgs ce)
            {
                if (_suppressMenu)
                {
                    _suppressMenu = false;
                    ce.Cancel = true;
                    return;
                }
                int selCount = _selectedTiles.Count;
                bool allFav = selCount > 0;
                foreach (ThumbTile t in _selectedTiles)
                {
                    if (!_cfg.IsFavorite(_dir, Path.GetFileName(t.FilePath)))
                    {
                        allFav = false;
                        break;
                    }
                }
                favItem.Text = allFav ? "取消收藏" : "加入收藏";
                favItem.Enabled = selCount > 0;
                merge.Enabled = selCount >= 2;
                crop.Enabled = selCount <= 1;
            };
            return menu;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    _dpiScale = g.DpiX / 96.0;
                    _tileSize = (int)Math.Round(92.0 * _dpiScale);
                }
            }
            catch
            {
                _dpiScale = 1.0;
                _tileSize = 92;
            }
            if (_tileSize < 72) _tileSize = 72;
            if (_tileSize > 168) _tileSize = 168;

            AddClipboardFormatListener(Handle);
            RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_V);
            RegisterShotHotkey();
            TopMost = _cfg.TopMost;
            if (_cfg.Opacity < 100) Opacity = _cfg.Opacity / 100.0;
            ApplyColors();

            RestoreWindowBounds();

            if (_dirOverride != null)
            {
                SwitchBoard(_dirOverride);
                SetStatus("已载入目录：" + _dir);
            }
            else if (!_cfg.RootSet)
            {
                SetStatus("第一次使用：请先选择主目录。");
            }
            else if (_cfg.LastDir.Length > 0 && Directory.Exists(_cfg.LastDir))
            {
                SwitchBoard(_cfg.LastDir);
                SetStatus("已恢复上次的文件夹：" + Path.GetFileName(_cfg.LastDir.TrimEnd(Path.DirectorySeparatorChar)));
            }
            else
            {
                NewBoard();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_cropPath != null && File.Exists(_cropPath)) OpenCropDialog(_cropPath);
            if (_showShortcuts) ShowShortcuts();
            if (!string.IsNullOrEmpty(_mergePaths))
            {
                List<Image> images = new List<Image>();
                foreach (string p in _mergePaths.Split('|'))
                {
                    if (!File.Exists(p)) continue;
                    Image img = null;
                    try { img = LoadImage(p); }
                    catch { }
                    if (img != null) images.Add(img);
                }
                if (images.Count >= 2)
                {
                    MergeForm form = new MergeForm(images, _cfg.BgColor, _textColor, _subColor, Font, MergeExport);
                    _mergeForm = form;
                    form.FormClosed += delegate { if (_mergeForm == form) _mergeForm = null; };
                    ShowOwnedForm(form);
                }
                else
                {
                    foreach (Image i in images) i.Dispose();
                }
            }
            if (_dirOverride != null || _cfg.RootSet) return;
            if (!SetupWorkspace())
            {
                _reallyExit = true;
                Close();
                return;
            }
            NewBoard();
            SetStatus("主目录已设置：" + _cfg.RootDir);
        }

        private bool SetupWorkspace()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "第一次使用：请选择主目录（所有图片文件夹都会建在这里）";
                dlg.SelectedPath = _cfg.RootDir;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                _cfg.RootDir = dlg.SelectedPath;
                _cfg.RootSet = true;
                _cfg.Save();
                return true;
            }
        }

        internal bool IsOnWindowBorder(IntPtr lParam)
        {
            int lp = lParam.ToInt32();
            Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            Point p = PointToClient(screen);
            const int grip = 6;
            return p.X <= grip || p.Y <= grip
                || p.X >= ClientSize.Width - grip || p.Y >= ClientSize.Height - grip;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                _clipTimer.Stop();
                _clipTimer.Start();
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                CaptureClipboard(true);
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == SHOT_HOTKEY_ID)
            {
                CaptureRegion();
            }
            else if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == HTCLIENT)
                {
                    int lp = m.LParam.ToInt32();
                    Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    Point p = PointToClient(screen);
                    const int grip = 6;
                    bool left = p.X <= grip;
                    bool right = p.X >= ClientSize.Width - grip;
                    bool top = p.Y <= grip;
                    bool bottom = p.Y >= ClientSize.Height - grip;
                    if (top && left) m.Result = (IntPtr)HTTOPLEFT;
                    else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)HTLEFT;
                    else if (right) m.Result = (IntPtr)HTRIGHT;
                    else if (top) m.Result = (IntPtr)HTTOP;
                    else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V))
            {
                CaptureClipboard(true);
                return true;
            }
            if (keyData == Keys.Delete)
            {
                DeleteTiles(new List<ThumbTile>(_selectedTiles));
                return true;
            }
            if (keyData == (Keys.Control | Keys.A))
            {
                SelectAllTiles();
                SetStatus("已选中 " + _selectedTiles.Count + " 张。");
                return true;
            }
            if (keyData == Config.ParseKey(_cfg.KeyFavorite, Keys.F))
            {
                ToggleFavoriteSelected();
                return true;
            }
            if (keyData == Config.ParseKey(_cfg.KeyDelete, Keys.X))
            {
                DeleteTiles(new List<ThumbTile>(_selectedTiles));
                return true;
            }
            if (keyData == Config.ParseKey(_cfg.KeyMerge, Keys.E))
            {
                MergeSelected();
                return true;
            }
            if (keyData == Config.ParseKey(_cfg.KeyCrop, Keys.T))
            {
                if (_selectedTiles.Count == 1) CropSelected();
                else SetStatus("裁切需要只选中 1 张图片。");
                return true;
            }
            if (keyData == Keys.Escape)
            {
                SelectOnly(null);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_hideTipShown)
                {
                    _hideTipShown = true;
                        }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                RemoveClipboardFormatListener(Handle);
                UnregisterHotKey(Handle, HOTKEY_ID);
                UnregisterHotKey(Handle, SHOT_HOTKEY_ID);
            }
            catch { }
            SaveWindowState();
            try
            {
                if (_dirOverride == null && _boardCreatedHere && !_boardUsed && Directory.Exists(_dir)
                    && Directory.GetFileSystemEntries(_dir).Length == 0)
                    Directory.Delete(_dir);
            }
            catch { }
            if (_ball != null)
            {
                _ball.Close();
                _ball.Dispose();
                _ball = null;
            }
            if (_pagerFont != null)
            {
                _pagerFont.Dispose();
                _pagerFont = null;
            }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            base.OnFormClosed(e);
        }

        private void ApplyColors()
        {
            Color back = _cfg.BgColor;
            int lum = (back.R * 299 + back.G * 587 + back.B * 114) / 1000;
            _textColor = lum < 128 ? Color.FromArgb(255, 233, 233, 240) : Color.FromArgb(255, 32, 32, 38);
            _subColor = lum < 128 ? Color.FromArgb(255, 148, 148, 160) : Color.FromArgb(255, 104, 104, 116);
            _accent = Color.FromArgb(255, 59, 130, 246);

            BackColor = back;
            _root.BackColor = back;
            _topBar.BackColor = back;
            _panel.BackColor = back;
            if (_pagerBar != null)
            {
                _pagerBar.BackColor = back;
                _pagerBar.Invalidate();
            }
            if (_pageBox != null)
            {
                _pageBox.BackColor = back;
                _pageBox.ForeColor = _textColor;
            }
            if (_pagerFont == null) _pagerFont = new Font(Font.FontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point);
            if (_pageCurrent != null)
            {
                _pageCurrent.BackColor = back;
                _pageCurrent.CurrentColor = _textColor;
                _pageCurrent.TotalColor = _textColor;
                _pageCurrent.BarColor = _subColor;
                _pageCurrent.Font = _pagerFont;
                _pageCurrent.Invalidate();
            }
            if (_pageBox != null) _pageBox.Font = _pagerFont;
            if (_prevButton != null)
            {
                _prevButton.BackColor = back;
                _prevButton.ArrowColor = _subColor;
                _prevButton.BorderColor = Color.FromArgb(95, _textColor);
                _prevButton.HoverBack = Lighten(back, 22);
                _prevButton.Invalidate();
            }
            if (_nextButton != null)
            {
                _nextButton.BackColor = back;
                _nextButton.ArrowColor = _subColor;
                _nextButton.BorderColor = Color.FromArgb(95, _textColor);
                _nextButton.HoverBack = Lighten(back, 22);
                _nextButton.Invalidate();
            }
            _topBar.Invalidate();
            _panel.Invalidate();

            foreach (Control c in _topBar.Controls)
            {
                c.BackColor = back;
                foreach (Control cc in c.Controls)
                {
                    cc.BackColor = back;
                    cc.ForeColor = _subColor;
                    Button b = cc as Button;
                    if (b != null)
                    {
                        b.FlatAppearance.MouseOverBackColor = Lighten(back, 22);
                        b.FlatAppearance.MouseDownBackColor = Lighten(back, 34);
                    }
                }
                Label l = c as Label;
                if (l != null) l.ForeColor = _subColor;
            }

            foreach (Control c in _panel.Controls)
            {
                ThumbTile t = c as ThumbTile;
                if (t != null) t.ApplyColors(back, _textColor, _accent);
            }
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            _title.Text = "ClipDrop";
            _title.ForeColor = _subColor;
            if (_pauseButton != null) _pauseButton.Invalidate();
            try
            {
                string board = Path.GetFileName(_dir.TrimEnd(Path.DirectorySeparatorChar));
                Text = string.IsNullOrEmpty(board) ? Program.AppTitle : Program.AppTitle + " — " + board;
            }
            catch { }
        }

        private static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(255,
                Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));
        }

        private void ToggleTopMost()
        {
            _cfg.TopMost = !_cfg.TopMost;
            TopMost = _cfg.TopMost;
            _cfg.Save();
            UpdateChrome();
            SetStatus(_cfg.TopMost ? "窗口已置顶。" : "窗口已取消置顶。");
        }

        private void PickOpacity()
        {
            double original = _cfg.Opacity / 100.0;
            using (SliderForm dlg = new SliderForm(_cfg.Opacity, _cfg.BgColor, _textColor, _subColor, _accent, Font))
            {
                dlg.ValueChanged += delegate { Opacity = dlg.Value / 100.0; };
                DialogResult result = dlg.ShowDialog(this);
                if (result == DialogResult.OK)
                {
                    _cfg.Opacity = dlg.Value;
                    _cfg.Save();
                }
                else
                {
                    _cfg.Opacity = (int)Math.Round(original * 100.0);
                }
                if (_cfg.Opacity < 100) Opacity = _cfg.Opacity / 100.0;
            }
        }

        private void PickColor()
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = _cfg.BgColor;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _cfg.BgColor = Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _cfg.Save();
                ApplyColors();
                SetStatus("背景颜色已更新。");
            }
        }

        private void CancelPageEdit()
        {
            if (_pageBox != null && _pageBox.Visible) EndPageEdit(false);
        }

        private void OnPanelMouseDown(object sender, MouseEventArgs e)
        {
            CancelPageEdit();
            if (e.Button == MouseButtons.Left) SelectOnly(null);
            else if (e.Button == MouseButtons.Right) BeginRightDrag(_panel);
        }

        private void OnPanelMouseUp(object sender, MouseEventArgs e)
        {
            EndRightDrag(_panel);
        }

        private void SwitchBoard(string dir)
        {
            if (!string.Equals(_dir, dir, StringComparison.OrdinalIgnoreCase)) ClearThumbCache();
            _dir = dir;
            _seen.Clear();
            _boardUsed = false;
            _boardCreatedHere = false;
            if (_dirOverride == null)
            {
                _cfg.PushRecent(dir);
                _cfg.Save();
            }
            ReloadFromDisk(MaxLoaded);
        }

        private void RestoreWindowBounds()
        {
            if (_cfg.WinW > 0 && _cfg.WinH > 0)
            {
                Size = new Size(Math.Max(MinimumSize.Width, _cfg.WinW), Math.Max(MinimumSize.Height, _cfg.WinH));
            }
            if (_cfg.WinX > -10000 && _cfg.WinY > -10000 && (_cfg.WinX != -1 || _cfg.WinY != -1))
            {
                try
                {
                    Rectangle wa = Screen.FromPoint(new Point(_cfg.WinX, _cfg.WinY)).WorkingArea;
                    int x = Math.Max(wa.Left, Math.Min(_cfg.WinX, wa.Right - Width));
                    int y = Math.Max(wa.Top, Math.Min(_cfg.WinY, wa.Bottom - Height));
                    Location = new Point(x, y);
                }
                catch { }
            }
        }

        private void SaveWindowState()
        {
            try
            {
                _cfg.LastDir = _dir;
                _cfg.WinX = Left;
                _cfg.WinY = Top;
                _cfg.WinW = Width;
                _cfg.WinH = Height;
                _cfg.Save();
            }
            catch { }
        }

        private void NewBoard()
        {
            CreateBoard(DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        }

        private void NewBoardPrompted()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name = PromptForm.Ask(this, "新建文件夹", "输入文件夹名称：", stamp, "创建",
                _cfg.BgColor, _textColor, _subColor, Font);
            if (string.IsNullOrEmpty(name)) return;
            CreateBoard(name);
        }

        private void CreateBoard(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            if (name.Length == 0) name = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir;
            try
            {
                if (!Directory.Exists(_cfg.RootDir)) Directory.CreateDirectory(_cfg.RootDir);
                dir = Path.Combine(_cfg.RootDir, name);
                int i = 2;
                while (Directory.Exists(dir))
                {
                    dir = Path.Combine(_cfg.RootDir, name + "_" + i);
                    i++;
                }
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                SetStatus("新建文件夹失败：" + ex.Message);
                return;
            }
            SwitchBoard(dir);
            _boardCreatedHere = true;
            SetStatus("已新建文件夹：" + Path.GetFileName(dir));
        }

        private void ReadBoard()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择要读取的文件夹";
                dlg.SelectedPath = Directory.Exists(_cfg.RootDir) ? _cfg.RootDir : _dir;
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SwitchBoard(dlg.SelectedPath);
                SetStatus("已读取文件夹：" + Path.GetFileName(_dir));
            }
        }

        private void PickWorkspace()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择主目录（新建的文件夹都会建在这里）";
                dlg.SelectedPath = _cfg.RootDir;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _cfg.RootDir = dlg.SelectedPath;
                _cfg.Save();
                NewBoard();
            }
        }

        private void CaptureClipboard(bool manual)
        {
            if (!manual && DateTime.Now < _suppressUntil) return;

            IDataObject dobj = null;
            for (int i = 0; i < 8 && dobj == null; i++)
            {
                try { dobj = Clipboard.GetDataObject(); }
                catch (ExternalException) { dobj = null; }
                if (dobj == null) Thread.Sleep(70);
            }
            if (dobj == null)
            {
                if (manual) SetStatus("剪贴板被其他程序占用，读取失败，请稍后重试。");
                return;
            }

            bool hasFiles = false;
            try { hasFiles = dobj.GetDataPresent(DataFormats.FileDrop); }
            catch { }
            if (hasFiles)
            {
                if (manual) SetStatus("剪贴板里是文件（不是位图）：文件本身就在本地，可直接拖拽使用。");
                return;
            }

            byte[] bytes = null;
            string[] pngFormats = new string[] { "PNG", "image/png", "image/x-png" };
            foreach (string fmt in pngFormats)
            {
                try
                {
                    if (!dobj.GetDataPresent(fmt)) continue;
                    object data = dobj.GetData(fmt);
                    MemoryStream ms = data as MemoryStream;
                    if (ms != null) bytes = ReadAll(ms);
                    else if (data is byte[]) bytes = (byte[])data;
                    if (bytes != null && bytes.Length > 16) break;
                    bytes = null;
                }
                catch { bytes = null; }
            }

            if (bytes == null)
            {
                bool hasBitmap = false;
                try { hasBitmap = dobj.GetDataPresent(DataFormats.Bitmap) || dobj.GetDataPresent(DataFormats.Dib); }
                catch { }
                if (!hasBitmap)
                {
                    if (manual) SetStatus("剪贴板里没有图片。");
                    return;
                }
                try
                {
                    using (Image img = Clipboard.GetImage())
                    {
                        if (img == null)
                        {
                            if (manual) SetStatus("剪贴板里没有图片。");
                            return;
                        }
                        using (MemoryStream ms = new MemoryStream())
                        {
                            img.Save(ms, ImageFormat.Png);
                            bytes = ms.ToArray();
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus("读取图片失败：" + ex.Message);
                    return;
                }
            }

            string hash = Hash(bytes);
            string known;
            if (_seen.TryGetValue(hash, out known))
            {
                ThumbTile tile = FindTile(known);
                if (tile != null) SelectOnly(tile);
                SetStatus("当前文件夹已有这张图，已为你选中。");
                return;
            }

            try
            {
                if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                string dest = MakeDestPath(".png");
                File.WriteAllBytes(dest, bytes);
                _seen[hash] = dest;
                _boardUsed = true;
                if (_favoritesView)
                {
                    SetStatus("已保存：" + Path.GetFileName(dest) + "（收藏夹视图，切回全部可见）");
                }
                else
                {
                    AddTile(dest, true);
                    SetStatus("已保存：" + Path.GetFileName(dest));
                }
            }
            catch (Exception ex)
            {
                SetStatus("保存失败：" + ex.Message);
            }
        }

        private void OnTileMouseDown(object sender, MouseEventArgs e)
        {
            CancelPageEdit();
            ThumbTile tile = (ThumbTile)sender;
            if (e.Button == MouseButtons.Right)
            {
                if (!tile.Selected) SelectOnly(tile);
                BeginRightDrag(tile);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            _dragTile = tile;
            _dragStart = e.Location;
            _dragStarted = false;
            _downOnBadge = tile.BadgeRect.Contains(e.Location);
            _downOnStar = tile.StarRect.Contains(e.Location);

            if (_downOnBadge || _downOnStar) return;

            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;

            _clickCollapseTile = null;
            if (shift && _anchorTile != null && _panel.Controls.Contains(_anchorTile))
            {
                SelectRange(_anchorTile, tile);
            }
            else if (ctrl)
            {
                ToggleSelect(tile);
                _anchorTile = tile;
            }
            else if (!tile.Selected)
            {
                SelectOnly(tile);
                _anchorTile = tile;
            }
            else
            {
                _anchorTile = tile;
                if (_selectedTiles.Count > 1) _clickCollapseTile = tile;
            }
        }

        private void SelectRange(ThumbTile from, ThumbTile to)
        {
            int i1 = _panel.Controls.IndexOf(from);
            int i2 = _panel.Controls.IndexOf(to);
            if (i1 < 0 || i2 < 0) return;
            if (i1 > i2)
            {
                int swap = i1;
                i1 = i2;
                i2 = swap;
            }
            foreach (Control c in _panel.Controls)
            {
                ThumbTile t = c as ThumbTile;
                if (t != null) t.Selected = false;
            }
            _selectedTiles.Clear();
            for (int i = i1; i <= i2; i++)
            {
                ThumbTile t = _panel.Controls[i] as ThumbTile;
                if (t == null) continue;
                t.Selected = true;
                _selectedTiles.Add(t);
            }
            _panel.Invalidate();
            SetStatus("已选中 " + _selectedTiles.Count + " 张。");
        }

        private void OnTileMouseMove(object sender, MouseEventArgs e)
        {
            ThumbTile tile = (ThumbTile)sender;
            if (_rmbCandidate)
            {
                ContinueRightDrag();
                return;
            }
            bool overBadge = tile.BadgeRect.Contains(e.Location);
            bool overStar = tile.StarRect.Contains(e.Location);
            tile.HoverBadge = overBadge;
            tile.HoverStar = overStar;
            tile.Cursor = (overBadge || overStar) ? Cursors.Hand : Cursors.SizeAll;

            if (e.Button != MouseButtons.Left || _downOnBadge || _downOnStar || _draggingOut) return;
            if (!_dragStarted && (Math.Abs(e.X - _dragStart.X) > 4 || Math.Abs(e.Y - _dragStart.Y) > 4))
            {
                _dragStarted = true;
                StartDrag(tile);
            }
        }

        private void OnTileMouseUp(object sender, MouseEventArgs e)
        {
            ThumbTile tile = (ThumbTile)sender;
            if (_rmbCandidate)
            {
                EndRightDrag(tile);
                return;
            }
            if (e.Button == MouseButtons.Left && _downOnBadge && tile.BadgeRect.Contains(e.Location))
            {
                List<ThumbTile> one = new List<ThumbTile>();
                one.Add(tile);
                DeleteTiles(one);
            }
            else if (e.Button == MouseButtons.Left && _downOnStar && tile.StarRect.Contains(e.Location))
            {
                ToggleFavorite(tile);
            }
            else if (e.Button == MouseButtons.Left && !_dragStarted && _clickCollapseTile != null && _clickCollapseTile == tile)
            {
                SelectOnly(tile);
                _anchorTile = tile;
                SetStatus("已选中 1 张。");
            }
            _clickCollapseTile = null;
            _downOnBadge = false;
            _downOnStar = false;
            _dragStarted = false;
        }

        private void OnTileMouseEnter(object sender, EventArgs e)
        {
            ((ThumbTile)sender).HoverTile = true;
        }

        private void OnTileMouseLeave(object sender, EventArgs e)
        {
            ThumbTile tile = (ThumbTile)sender;
            tile.HoverTile = false;
            tile.HoverBadge = false;
            tile.HoverStar = false;
        }

        private void StartDrag(ThumbTile tile)
        {
            List<string> paths = new List<string>();
            if (tile.Selected)
            {
                foreach (ThumbTile t in _selectedTiles)
                    if (File.Exists(t.FilePath)) paths.Add(t.FilePath);
            }
            if (paths.Count == 0) paths.Add(tile.FilePath);

            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            Image dragImage = null;
            try
            {
                dragImage = LoadImage(paths[0]);
                if (dragImage != null) data.SetImage(dragImage);
            }
            catch { }

            _draggingOut = true;
            try
            {
                tile.DoDragDrop(data, DragDropEffects.Copy);
            }
            finally
            {
                _draggingOut = false;
                if (dragImage != null) dragImage.Dispose();
            }
        }

        private void OnFileDragEnter(object sender, DragEventArgs e)
        {
            if (_draggingOut)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (files) SetStatus("松开即可把图片导入到当前文件夹");
        }

        private void OnFileDragDrop(object sender, DragEventArgs e)
        {
            if (_draggingOut || e.Data == null) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;

            int added = 0, skipped = 0;
            string lastAdded = null;
            foreach (string file in files)
            {
                try
                {
                    if (!File.Exists(file) || !IsImageExt(file)) { skipped++; continue; }
                    string full = Path.GetFullPath(file);
                    string srcDir = Path.GetDirectoryName(full);
                    if (srcDir != null && string.Equals(srcDir.TrimEnd(Path.DirectorySeparatorChar),
                            _dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        ThumbTile existingTile = FindTile(full);
                        if (existingTile != null) SelectOnly(existingTile);
                        skipped++;
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(full);
                    string hash = Hash(bytes);
                    string known;
                    if (_seen.TryGetValue(hash, out known))
                    {
                        ThumbTile knownTile = FindTile(known);
                        if (knownTile != null) SelectOnly(knownTile);
                        skipped++;
                        continue;
                    }
                    if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                    string dest = MakeDestPath(Path.GetExtension(full).ToLowerInvariant());
                    File.WriteAllBytes(dest, bytes);
                    _seen[hash] = dest;
                    _boardUsed = true;
                    if (!_favoritesView)
                    {
                        AddTile(dest, false);
                        lastAdded = dest;
                    }
                    added++;
                }
                catch { skipped++; }
            }
            if (lastAdded != null)
            {
                ThumbTile tile = FindTile(lastAdded);
                if (tile != null) SelectOnly(tile);
            }
            if (added > 0) SetStatus("已收录 " + added + " 个图片文件" + (skipped > 0 ? "，跳过 " + skipped + " 个" : "") + "。");
            else SetStatus("没有新的图片文件可收录。");
        }

        private void SelectOnly(ThumbTile tile)
        {
            foreach (Control c in _panel.Controls)
            {
                ThumbTile t = c as ThumbTile;
                if (t != null) t.Selected = (t == tile);
            }
            _selectedTiles.Clear();
            if (tile != null) _selectedTiles.Add(tile);
            if (tile != null) _panel.ScrollTo(tile);
        }

        private void ToggleSelect(ThumbTile tile)
        {
            tile.Selected = !tile.Selected;
            if (tile.Selected)
            {
                if (!_selectedTiles.Contains(tile)) _selectedTiles.Add(tile);
            }
            else _selectedTiles.Remove(tile);
        }

        private void SelectAllTiles()
        {
            _selectedTiles.Clear();
            foreach (Control c in _panel.Controls)
            {
                ThumbTile t = c as ThumbTile;
                if (t == null) continue;
                t.Selected = true;
                _selectedTiles.Add(t);
            }
        }

        private void DeleteTiles(List<ThumbTile> tiles)
        {
            if (tiles == null || tiles.Count == 0) return;

            List<ThumbTile> favs = new List<ThumbTile>();
            foreach (ThumbTile t in tiles)
                if (_cfg.IsFavorite(_dir, Path.GetFileName(t.FilePath))) favs.Add(t);

            List<ThumbTile> targets = new List<ThumbTile>(tiles);
            if (favs.Count > 0)
            {
                if (tiles.Count == 1)
                {
                    DialogResult single = MessageBox.Show(this,
                        "这张图片已收藏。\n\n取消收藏并删除？",
                        "ClipDrop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (single != DialogResult.Yes) return;
                }
                else
                {
                    DialogResult multi = MessageBox.Show(this,
                        "选中的图片里有 " + favs.Count + " 张已收藏。\n\n" +
                        "是：取消收藏并一起删除\n" +
                        "否：只删除未收藏的 " + (tiles.Count - favs.Count) + " 张\n" +
                        "取消：什么都不做",
                        "ClipDrop", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (multi == DialogResult.Cancel) return;
                    if (multi == DialogResult.No)
                    {
                        targets.Clear();
                        foreach (ThumbTile t in tiles)
                            if (!favs.Contains(t)) targets.Add(t);
                        if (targets.Count == 0)
                        {
                            SetStatus("没有可删除的图片（收藏的需先取消收藏）。");
                            return;
                        }
                    }
                }
            }

            List<string> paths = new List<string>();
            foreach (ThumbTile t in targets)
                if (File.Exists(t.FilePath)) paths.Add(t.FilePath);

            bool recycled = RecycleFiles(paths);
            int removed = 0;
            foreach (ThumbTile t in targets)
            {
                if (!recycled && File.Exists(t.FilePath)) continue;
                _panel.Controls.Remove(t);
                _selectedTiles.Remove(t);
                RemoveSeen(t.FilePath);
                DropThumb(t.FilePath);
                _cfg.RemoveFavorite(_dir, Path.GetFileName(t.FilePath));
                t.Dispose();
                removed++;
            }
            _cfg.Save();
            ResetScrollIfEmpty();
            _panel.Arrange();
            SetStatus(recycled
                ? "已删除 " + removed + " 个文件（已放入回收站，可从回收站还原）。"
                : "删除失败，文件仍保留。");
        }

        private void RemoveSeen(string path)
        {
            List<string> keys = new List<string>();
            foreach (KeyValuePair<string, string> kv in _seen)
                if (string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase)) keys.Add(kv.Key);
            foreach (string key in keys) _seen.Remove(key);
        }

        private static bool RecycleFiles(List<string> paths)
        {
            try
            {
                StringBuilder from = new StringBuilder();
                foreach (string p in paths) from.Append(p).Append('\0');
                from.Append('\0');
                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = FO_DELETE;
                op.pFrom = from.ToString();
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
                int result = SHFileOperation(ref op);
                return result == 0;
            }
            catch { }
            return false;
        }

        private void ExportFavorites()
        {
            List<string> srcFiles = new List<string>();
            foreach (string n in _cfg.GetFavorites(_dir))
            {
                if (string.IsNullOrEmpty(n)) continue;
                string src = Path.Combine(_dir, n);
                if (File.Exists(src)) srcFiles.Add(src);
            }
            if (srcFiles.Count == 0)
            {
                SetStatus("当前文件夹没有收藏的图片（选中图片按 F 即可收藏）。");
                return;
            }

            string target = null;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择导出收藏图片的目标文件夹（当前收藏 " + srcFiles.Count + " 张）";
                dlg.ShowNewFolderButton = true;
                dlg.SelectedPath = _dir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                target = dlg.SelectedPath;
            }
            if (string.IsNullOrEmpty(target)) return;
            if (string.Equals(target.TrimEnd('\\'), _dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("目标文件夹就是当前文件夹，无需导出。");
                return;
            }

            try
            {
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                SetStatus("无法创建目标文件夹：" + ex.Message);
                return;
            }

            int copied = 0;
            int skipped = 0;
            int failed = 0;
            foreach (string src in srcFiles)
            {
                try
                {
                    string dst = Path.Combine(target, Path.GetFileName(src));
                    if (File.Exists(dst))
                    {
                        if (new FileInfo(src).Length == new FileInfo(dst).Length)
                        {
                            skipped++;
                            continue;
                        }
                    }
                    File.Copy(src, dst, true);
                    copied++;
                }
                catch { failed++; }
            }

            string msg = "已导出收藏 " + copied + " 张 → " + target;
            if (skipped > 0) msg += "（跳过 " + skipped + " 张同名同大小）";
            if (failed > 0) msg += "，失败 " + failed + " 张";
            SetStatus(msg);
        }

        private void CleanList()
        {
            List<string> paths = new List<string>();
            int kept = 0;
            if (Directory.Exists(_dir))
            {
                foreach (string pattern in new string[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" })
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(_dir, pattern))
                        {
                            if (_cfg.IsFavorite(_dir, Path.GetFileName(f))) kept++;
                            else paths.Add(f);
                        }
                    }
                    catch { }
                }
            }

            if (paths.Count == 0)
            {
                ReloadFromDisk(MaxLoaded);
                SetStatus("没有可清理的图片（收藏的 " + kept + " 张已保留）。");
                return;
            }

            DialogResult r = MessageBox.Show(this,
                "清理未收藏的 " + paths.Count + " 张图片？\n\n文件会放进回收站，可以从回收站还原。\n\n收藏的 " + kept + " 张会保留。\n\n" + _dir,
                "ClipDrop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            bool ok = RecycleFiles(paths);
            if (ok)
            {
                foreach (string p in paths)
                {
                    RemoveSeen(p);
                    DropThumb(p);
                }
                _cfg.Save();
            }
            ReloadFromDisk(MaxLoaded);
            SetStatus(ok
                ? "已清理 " + paths.Count + " 张图片（已放入回收站，可还原），收藏的 " + kept + " 张已保留。"
                : "清理失败，文件仍保留。");
        }

        private void ClearTiles()
        {
            _selectedTiles.Clear();
            List<Control> old = new List<Control>();
            foreach (Control c in _panel.Controls) old.Add(c);
            _panel.Controls.Clear();
            foreach (Control c in old) c.Dispose();
            _panel.ResetScroll();
            _panel.Invalidate();
        }

        private void ResetScrollIfEmpty()
        {
            if (_panel.Controls.Count == 0)
                _panel.ResetScroll();
        }

        private void CollapseToBall()
        {
            if (_ball != null && _ball.Visible) return;
            int size = (int)Math.Round(48 * _dpiScale);
            if (size < 36) size = 36;
            if (size > 96) size = 96;
            if (_ball == null || _ball.Width != size)
            {
                if (_ball != null)
                {
                    _ball.Close();
                    _ball.Dispose();
                }
                _ball = new BallForm(size, _cfg.BgColor, _accent, Font, Program.LoadLogoImage(64));
                _ball.OpenRequested += delegate { RestoreFromBall(); };
                _ball.ExitRequested += delegate { _reallyExit = true; Close(); };
            }

            Point pos = new Point(Right - size - 16, Top + 10);
            try
            {
                if (_ballButton != null && _ballButton.IsHandleCreated)
                {
                    Rectangle br = _ballButton.RectangleToScreen(_ballButton.ClientRectangle);
                    pos = new Point(br.Left + br.Width / 2 - size / 2, br.Top + br.Height / 2 - size / 2);
                }
            }
            catch { }
            _ball.Location = ClampToScreen(pos, size);

            _ball.Show();
            Hide();
        }

        private void RestoreFromBall()
        {
            ShowFromTray();
        }

        private static Point ClampToScreen(Point pos, int size)
        {
            try
            {
                Rectangle wa = Screen.FromPoint(pos).WorkingArea;
                int x = Math.Max(wa.Left, Math.Min(pos.X, wa.Right - size));
                int y = Math.Max(wa.Top, Math.Min(pos.Y, wa.Bottom - size));
                return new Point(x, y);
            }
            catch
            {
                return pos;
            }
        }

        private void OpenFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                SetStatus("打开目录失败：" + ex.Message);
            }
        }

        private void OpenPath(string path)
        {
            try { Process.Start(path); }
            catch (Exception ex) { SetStatus("打开失败：" + ex.Message); }
        }

        private List<string> SelectedPaths()
        {
            List<string> result = new List<string>();
            foreach (ThumbTile t in _selectedTiles)
                if (File.Exists(t.FilePath)) result.Add(t.FilePath);
            return result;
        }

        private void CopySelectedFiles()
        {
            List<string> paths = SelectedPaths();
            if (paths.Count == 0) return;
            try
            {
                StringCollection sc = new StringCollection();
                sc.AddRange(paths.ToArray());
                _suppressUntil = DateTime.Now.AddMilliseconds(800);
                Clipboard.SetFileDropList(sc);
                SetStatus("已复制 " + paths.Count + " 个文件到剪贴板。");
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message);
            }
        }

        private void OpenSelected()
        {
            List<string> paths = SelectedPaths();
            if (paths.Count == 0) return;
            OpenPath(paths[0]);
        }

        private void ShowShortcuts()
        {
            using (ShortcutsForm form = new ShortcutsForm(_cfg, _cfg.BgColor, _textColor, _subColor, Font,
                delegate { RegisterShotHotkey(); }))
                form.ShowDialog(this);
        }

        private void RegisterShotHotkey()
        {
            try
            {
                UnregisterHotKey(Handle, SHOT_HOTKEY_ID);
                uint key = (uint)Config.ParseKey(_cfg.KeyShot, Keys.A);
                uint[] mods = new uint[] { MOD_CONTROL | MOD_ALT, MOD_CONTROL | MOD_SHIFT, MOD_ALT | MOD_SHIFT, MOD_ALT };
                int start = _cfg.ShotMod;
                if (start < 0 || start >= mods.Length) start = 0;
                for (int i = 0; i < mods.Length; i++)
                {
                    int index = (start + i) % mods.Length;
                    if (RegisterHotKey(Handle, SHOT_HOTKEY_ID, mods[index], key))
                    {
                        if (_cfg.ShotMod != index)
                        {
                            _cfg.ShotMod = index;
                            _cfg.Save();
                            if (_tray != null)
                            {
                                try { _tray.ShowBalloonTip(3000, "截图快捷键已调整", "原组合被其他程序占用，已改用 " + Config.ModName(index) + " + " + _cfg.KeyShot + "。", ToolTipIcon.Info); }
                                catch { }
                            }
                        }
                        return;
                    }
                }
                if (_tray != null)
                {
                    try { _tray.ShowBalloonTip(3000, "截图快捷键注册失败", "常用组合都被占用了，请在右键菜单 → 快捷键 里改一个。", ToolTipIcon.Warning); }
                    catch { }
                }
            }
            catch { }
        }

        private void CaptureRegion()
        {
            try
            {
                Application.DoEvents();
                Thread.Sleep(80);

                Rectangle vs = SystemInformation.VirtualScreen;
                Bitmap full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(full))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);

                using (CaptureForm form = new CaptureForm(full, SaveCapturedBitmap))
                    form.ShowDialog();
            }
            catch (Exception ex)
            {
                SetStatus("截图失败：" + ex.Message);
            }
        }

        private bool SaveCapturedBitmap(Bitmap bmp)
        {
            try
            {
                if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                string dest = MakeDestPath(".png");
                bmp.Save(dest, ImageFormat.Png);
                _seen[Hash(File.ReadAllBytes(dest))] = dest;
                _boardUsed = true;
                if (_favoritesView) _cfg.ToggleFavorite(_dir, Path.GetFileName(dest));
                _cfg.Save();
                try
                {
                    _suppressUntil = DateTime.Now.AddMilliseconds(1500);
                    Clipboard.SetImage(bmp);
                }
                catch { }
                AddTile(dest, true);
                SetStatus("已截图并加入画布（也已复制到剪贴板）：" + Path.GetFileName(dest) + "（" + bmp.Width + "×" + bmp.Height + "）");
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("截图保存失败：" + ex.Message);
                return false;
            }
            finally
            {
                bmp.Dispose();
            }
        }

        private void MergeSelected()
        {
            List<string> paths = SelectedPaths();
            if (paths.Count < 2)
            {
                MessageBox.Show(this,
                    "请先选中至少两张图片（Ctrl 点选 / Shift 连选），再按 E 或右键合并。",
                    "合并图片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<Image> images = new List<Image>();
            foreach (string p in paths)
            {
                Image img = null;
                try { img = LoadImage(p); }
                catch { }
                if (img != null) images.Add(img);
            }
            if (images.Count < 2)
            {
                foreach (Image i in images) i.Dispose();
                SetStatus("图片读取失败，无法合并。");
                return;
            }
            if (_mergeForm != null && !_mergeForm.IsDisposed)
            {
                ShowOwnedForm(_mergeForm);
                foreach (Image i in images) i.Dispose();
                return;
            }
            MergeForm form = new MergeForm(images, _cfg.BgColor, _textColor, _subColor, Font, MergeExport);
            _mergeForm = form;
            form.FormClosed += delegate { if (_mergeForm == form) _mergeForm = null; };
            ShowOwnedForm(form);
        }

        private bool MergeExport(Bitmap bmp)
        {
            try
            {
                if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                string dest = MakeDestPath(".png");
                bmp.Save(dest, ImageFormat.Png);
                _seen[Hash(File.ReadAllBytes(dest))] = dest;
                _boardUsed = true;
                if (_favoritesView) _cfg.ToggleFavorite(_dir, Path.GetFileName(dest));
                _cfg.Save();
                AddTile(dest, true);
                SetStatus("已合并并加入画布：" + Path.GetFileName(dest) + "（" + bmp.Width + "×" + bmp.Height + "）");
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("合并保存失败：" + ex.Message);
                return false;
            }
            finally
            {
                bmp.Dispose();
            }
        }

        private void CropSelected()
        {
            List<string> paths = SelectedPaths();
            if (paths.Count == 0)
            {
                MessageBox.Show(this,
                    _selectedTiles.Count == 0
                        ? "请先用左键选中一张图片，再按 T 或右键裁切。"
                        : "选中的文件不存在（可能已被清理），请重新载入文件夹。",
                    "裁切图片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (paths.Count > 1)
            {
                MessageBox.Show(this,
                    "裁切一次只能处理 1 张图片（当前选中 " + paths.Count + " 张）。",
                    "裁切图片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenCropDialog(paths[0]);
        }

        private void ShowOwnedForm(Form form)
        {
            try { form.TopMost = TopMost; }
            catch { }
            if (form.Visible)
            {
                form.BringToFront();
                form.Activate();
                return;
            }
            form.Show(this);
            form.BringToFront();
            form.Activate();
        }

        private void OpenCropDialog(string path)
        {
            if (_cropForm != null && !_cropForm.IsDisposed)
            {
                ShowOwnedForm(_cropForm);
                return;
            }
            Image source = null;
            try
            {
                source = LoadImage(path);
            }
            catch (Exception ex)
            {
                SetStatus("打开图片失败：" + ex.Message);
                return;
            }
            if (source == null)
            {
                SetStatus("打开图片失败：文件可能已被删除。");
                return;
            }

            CropForm form = new CropForm(source, "裁切 — " + Path.GetFileName(path),
                _cfg.BgColor, _textColor, _subColor, Font, CropAndAdd);
            _cropForm = form;
            form.FormClosed += delegate { if (_cropForm == form) _cropForm = null; };
            ShowOwnedForm(form);
        }

        private bool CropAndAdd(Rectangle rect)
        {
            try
            {
                Image source = null;
                CropForm form = _cropForm;
                if (form == null) return false;
                source = form.SourceImage;
                if (source == null) return false;
                rect = Rectangle.Intersect(rect, new Rectangle(0, 0, source.Width, source.Height));
                if (rect.Width < 1 || rect.Height < 1) return false;

                using (Bitmap cropped = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(cropped))
                    {
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
                    }
                    if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                    string dest = MakeDestPath(".png");
                    cropped.Save(dest, ImageFormat.Png);
                    _seen[Hash(File.ReadAllBytes(dest))] = dest;
                    _boardUsed = true;
                    if (_favoritesView) _cfg.ToggleFavorite(_dir, Path.GetFileName(dest));
                    _cfg.Save();
                    AddTile(dest, true);
                    SetStatus("已裁切并加入画布：" + Path.GetFileName(dest) + "（" + rect.Width + "×" + rect.Height + "）");
                }
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("裁切失败：" + ex.Message);
                return false;
            }
        }

        private void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) throw new InvalidOperationException("无法打开启动项注册表");
                    if (on) key.SetValue("ClipDrop", "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue("ClipDrop") != null) key.DeleteValue("ClipDrop", false);
                }
                _cfg.AutoStart = on;
                _cfg.Save();
                SetStatus(on ? "已开启开机自动启动。" : "已关闭开机自动启动。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "设置开机自启失败：" + ex.Message, "ClipDrop",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowFromTray()
        {
            RepositionToBall();
            HideBall();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void RepositionToBall()
        {
            if (_ball == null) return;
            Point ballCenter = new Point(_ball.Left + _ball.Width / 2, _ball.Top + _ball.Height / 2);
            try
            {
                Point offset = Point.Empty;
                if (_ballButton != null && _ballButton.IsHandleCreated)
                {
                    Rectangle br = _ballButton.RectangleToScreen(_ballButton.ClientRectangle);
                    offset = new Point(br.Left + br.Width / 2 - Left, br.Top + br.Height / 2 - Top);
                }
                Point loc = new Point(ballCenter.X - offset.X, ballCenter.Y - offset.Y);
                Rectangle wa = Screen.FromPoint(ballCenter).WorkingArea;
                loc.X = Math.Max(wa.Left, Math.Min(loc.X, wa.Right - Width));
                loc.Y = Math.Max(wa.Top, Math.Min(loc.Y, wa.Bottom - Height));
                Location = loc;
            }
            catch { }
        }

        private void HideBall()
        {
            if (_ball != null && _ball.Visible) _ball.Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _ball != null && _ball.Visible)
            {
                RepositionToBall();
                HideBall();
            }
        }

        private void SetStatus(string text)
        {
            try
            {
                if (_tray == null || string.IsNullOrEmpty(text)) return;
                _tray.Text = text.Length > 62 ? text.Substring(0, 62) : text;
            }
            catch { }
        }

        private void ReloadFromDisk(int max)
        {
            _panel.SuspendArrange();
            try
            {
                ClearTiles();
                if (Directory.Exists(_dir))
                {
                    List<FileInfo> files = new List<FileInfo>();
                    HashSet<string> seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string pattern in new string[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" })
                    {
                        try
                        {
                            foreach (string f in Directory.GetFiles(_dir, pattern))
                            {
                                string full = Path.GetFullPath(f);
                                if (seenPaths.Add(full)) files.Add(new FileInfo(full));
                            }
                        }
                        catch { }
                    }
                    files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                    int n = Math.Min(max, files.Count);
                    for (int i = n - 1; i >= 0; i--)
                    {
                        if (_favoritesView && !_cfg.IsFavorite(_dir, files[i].Name)) continue;
                        AddTile(files[i].FullName, false);
                    }
                }
            }
            finally
            {
                _panel.ResumeArrange();
            }
            ResetScrollIfEmpty();
            _panel.Arrange();
            _panel.Invalidate();
            UpdateChrome();
            SetStatus(_panel.Controls.Count + " 张 · 文件夹：" + Path.GetFileName(_dir.TrimEnd(Path.DirectorySeparatorChar)));
        }

        private Image GetThumb(string path)
        {
            string key = ThumbKey(path);
            Image cached;
            if (_thumbCache.TryGetValue(key, out cached)) return cached;
            Image thumb = null;
            try
            {
                using (Image src = LoadImage(path))
                {
                    if (src == null) return null;
                    thumb = MakeThumb(src, _tileSize);
                }
            }
            catch { return null; }
            _thumbCache[key] = thumb;
            _thumbOrder.Add(key);
            TrimThumbCache();
            return thumb;
        }

        private void TrimThumbCache()
        {
            while (_thumbOrder.Count > 2500)
            {
                string old = _thumbOrder[0];
                _thumbOrder.RemoveAt(0);
                Image img;
                if (_thumbCache.TryGetValue(old, out img))
                {
                    _thumbCache.Remove(old);
                    img.Dispose();
                }
            }
        }

        private bool OwnedDialogOpen()
        {
            Form active = Form.ActiveForm;
            if (active == null) return false;
            if (_cropForm != null && !_cropForm.IsDisposed && active == _cropForm) return true;
            if (_mergeForm != null && !_mergeForm.IsDisposed && active == _mergeForm) return true;
            return false;
        }

        private Image GetPlaceholder(string ext)
        {
            string key = string.IsNullOrEmpty(ext) ? "?" : ext.ToLowerInvariant();
            Image img;
            if (_placeholderCache.TryGetValue(key, out img)) return img;
            img = MakePlaceholder(_tileSize, ext);
            _placeholderCache[key] = img;
            return img;
        }

        private void EnqueueThumb(string path, string key)
        {
            if (_thumbQueued.Contains(key)) return;
            _thumbQueued.Add(key);
            _thumbQueue.Insert(0, new KeyValuePair<string, string>(path, key));
            if (_thumbTimer != null && !_thumbTimer.Enabled) _thumbTimer.Start();
        }

        private void DecodePendingThumb()
        {
            while (_thumbQueue.Count > 0)
            {
                KeyValuePair<string, string> job = _thumbQueue[0];
                _thumbQueue.RemoveAt(0);
                _thumbQueued.Remove(job.Value);
                if (_thumbCache.ContainsKey(job.Value)) continue;
                Image thumb = null;
                Exception lastError = null;
                for (int attempt = 0; attempt < 2 && thumb == null; attempt++)
                {
                    try
                    {
                        using (Image src = LoadImage(job.Key))
                            if (src != null) thumb = MakeThumb(src, _tileSize);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        thumb = null;
                    }
                }
                if (thumb == null)
                {
                    thumb = GetPlaceholder(Path.GetExtension(job.Key));
                    if (lastError != null) Config.LogError("thumb:" + job.Key, lastError);
                }
                _thumbCache[job.Value] = thumb;
                _thumbOrder.Add(job.Value);
                TrimThumbCache();
                ThumbTile tile = FindTile(job.Key);
                if (tile != null) tile.SetThumb(thumb);
                return;
            }
        }

        private static string ThumbKey(string path)
        {
            try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture); }
            catch { return path; }
        }

        private void DropThumb(string path)
        {
            string key = ThumbKey(path);
            Image img;
            if (_thumbCache.TryGetValue(key, out img))
            {
                _thumbCache.Remove(key);
                _thumbOrder.Remove(key);
                img.Dispose();
            }
        }

        private void ClearThumbCache()
        {
            foreach (KeyValuePair<string, Image> kv in _thumbCache) kv.Value.Dispose();
            _thumbCache.Clear();
            _thumbOrder.Clear();
        }

        private void AddTile(string path, bool select)
        {
            string key = ThumbKey(path);
            Image thumb;
            _thumbCache.TryGetValue(key, out thumb);
            if (thumb == null)
            {
                EnqueueThumb(path, key);
                thumb = GetPlaceholder(Path.GetExtension(path));
            }

            ThumbTile tile = new ThumbTile(path, thumb, _tileSize, false);
            tile.ApplyColors(_cfg.BgColor, _textColor, _accent);
            tile.Favorited = _cfg.IsFavorite(_dir, Path.GetFileName(path));
            tile.MouseDown += OnTileMouseDown;
            tile.MouseMove += OnTileMouseMove;
            tile.MouseUp += OnTileMouseUp;
            tile.MouseEnter += OnTileMouseEnter;
            tile.MouseLeave += OnTileMouseLeave;
            tile.MouseDoubleClick += delegate { OpenPath(path); };
            tile.DragEnter += OnFileDragEnter;
            tile.DragOver += OnFileDragEnter;
            tile.DragDrop += OnFileDragDrop;
            tile.ContextMenuStrip = _tileMenu;

            _panel.Controls.Add(tile);
            _panel.Controls.SetChildIndex(tile, 0);
            _panel.Arrange();
            _panel.Invalidate();
            if (select) SelectOnly(tile);
            if (_panel.Controls.Count > MaxItems) ReloadFromDisk(MaxLoaded);
        }

        private ThumbTile FindTile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (Control c in _panel.Controls)
            {
                ThumbTile t = c as ThumbTile;
                if (t != null && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        private string MakeDestPath(string ext)
        {
            string prefix = _cfg.Prefix;
            if (string.IsNullOrEmpty(prefix)) prefix = "Paste";
            foreach (char c in Path.GetInvalidFileNameChars()) prefix = prefix.Replace(c, '_');
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string full = Path.Combine(_dir, prefix + "_" + stamp + ext);
            int i = 1;
            while (File.Exists(full))
            {
                full = Path.Combine(_dir, prefix + "_" + stamp + "_" + i.ToString() + ext);
                i++;
            }
            return full;
        }

        private static Image LoadImage(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return null;
                using (Image img = Image.FromStream(fs, true, true))
                {
                    return new Bitmap(img);
                }
            }
        }

        private static Image MakePlaceholder(int size, string ext)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 46, 46, 52)))
                    g.FillRectangle(b, 0, 0, size, size);
                string text = string.IsNullOrEmpty(ext) ? "?" : ext.TrimStart('.').ToUpperInvariant();
                using (Font f = new Font(FontFamily.GenericSansSerif, Math.Max(8f, size / 5.5f), FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 140, 140, 150)))
                {
                    SizeF sz = g.MeasureString(text, f);
                    g.DrawString(text, f, b, (size - sz.Width) / 2f, (size - sz.Height) / 2f);
                }
            }
            return bmp;
        }

        private static Image MakeThumb(Image src, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (SolidBrush back = new SolidBrush(Color.Black))
                    g.FillRectangle(back, 0, 0, size, size);
                float scale = Math.Min((float)(size - 10) / src.Width, (float)(size - 10) / src.Height);
                int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                g.DrawImage(src, new Rectangle((size - w) / 2, (size - h) / 2, w, h));
            }
            return bmp;
        }

        private static bool IsImageExt(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
        }

        private static string Hash(byte[] data)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static byte[] ReadAll(Stream stream)
        {
            try
            {
                if (stream.CanSeek) stream.Position = 0;
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[81920];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                        buffer.Write(chunk, 0, read);
                    return buffer.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
