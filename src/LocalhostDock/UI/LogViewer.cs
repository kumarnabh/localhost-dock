using System.Diagnostics;
using System.Text;
using LocalhostDock.Config;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>Live tail of an app's log file (captured output, or the app's own logFile).</summary>
internal sealed class LogViewer : Form
{
    private const int TailBytes = 256 * 1024;
    private readonly string _path;
    private readonly TextBox _text;
    private readonly CheckBox _follow;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private long _lastLength = -1;
    private DateTime _lastWrite;

    private static readonly Dictionary<string, LogViewer> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shows (or brings forward) the log window for an app.</summary>
    public static void ShowFor(AppEntry app)
    {
        string? path = app.EffectiveLogFile;
        if (path is null)
        {
            MessageBox.Show(
                $"{app.Name} has no log yet.\n\nRight-click it → Edit… and either tick “Capture output to a log” " +
                "(Localhost Dock records everything the start command prints) or point “Log file” at a log your app already writes.",
                "View logs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (Open.TryGetValue(path, out var existing) && !existing.IsDisposed)
        {
            existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }
        var viewer = new LogViewer(app.Name, path);
        Open[path] = viewer;
        viewer.FormClosed += (_, _) => Open.Remove(path);
        viewer.Show();
    }

    private LogViewer(string appName, string path)
    {
        _path = path;
        Text = $"{appName} — logs";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(S(900), S(560));
        ShowInTaskbar = true;

        _text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = Mono, BorderStyle = BorderStyle.None,
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(8), S(6), S(8), S(6)), WrapContents = false };
        _follow = new CheckBox { Text = "Follow", Checked = true, AutoSize = true, Margin = new Padding(0, S(5), S(10), 0) };
        bar.Controls.Add(_follow);
        bar.Controls.Add(Theme.Button("Open file", (_, _) => Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true })));
        bar.Controls.Add(Theme.Button("Open folder", (_, _) => Process.Start("explorer.exe", $"/select,\"{_path}\"")));
        bar.Controls.Add(Theme.Button("Clear", (_, _) => Clear()));
        _status = new Label { AutoSize = true, ForeColor = Muted, Font = Small, Margin = new Padding(S(10), S(6), 0, 0) };
        bar.Controls.Add(_status);

        Controls.Add(_text);
        Controls.Add(bar);
        Theme.Dark(this);
        _text.BackColor = Input;
        _text.BorderStyle = BorderStyle.None;
        _text.HandleCreated += (_, _) => SetWindowTheme(_text.Handle, "DarkMode_Explorer", null);

        _timer.Tick += (_, _) => ReloadLog(false);
        Shown += (_, _) => { ReloadLog(true); _timer.Start(); };
    }

    private void ReloadLog(bool force)
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                _status.Text = $"Waiting for {_path} …";
                return;
            }
            if (!force && info.Length == _lastLength && info.LastWriteTimeUtc == _lastWrite) return;
            _lastLength = info.Length;
            _lastWrite = info.LastWriteTimeUtc;

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string content = reader.ReadToEnd();
            if (start > 0 && content.IndexOf('\n') is int nl and >= 0) content = content[(nl + 1)..];
            content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");

            int selStart = _text.SelectionStart;
            _text.Text = content;
            if (_follow.Checked)
            {
                _text.SelectionStart = _text.TextLength;
                _text.ScrollToCaret();
            }
            else _text.SelectionStart = Math.Min(selStart, _text.TextLength);

            _status.Text = $"{Bytes(info.Length)} · updated {info.LastWriteTime:HH:mm:ss}" + (start > 0 ? " · showing the last 256 KB" : "");
        }
        catch (IOException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void Clear()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.SetLength(0);
            ReloadLog(true);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Couldn't clear the log (the app may hold it open):\n\n{ex.Message}", Text);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
