using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MediaDevices;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

class PhoneFile
{
    public string Path;      // full path on the phone
    public string Folder;    // folder name on the phone (e.g. 202301__)
    public string Name;
    public long Size;
    public int Kind;         // 0 = picture, 1 = video, 2 = other (sidecars etc.)
    public string SortKey;
}

class MainForm : Form
{
    static readonly HashSet<string> PictureExt = new(StringComparer.OrdinalIgnoreCase)
    { ".heic", ".heif", ".hif", ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".tif", ".tiff", ".bmp", ".webp",
      ".dng", ".raw", ".cr2", ".nef", ".arw", ".avif", ".jxl" };
    static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    { ".mov", ".mp4", ".m4v", ".hevc", ".3gp", ".3g2", ".avi", ".mkv", ".mts", ".m2ts", ".wmv", ".webm" };

    const int StallSeconds = 60;          // no bytes for this long = timed out
    const int ChunkSize = 1024 * 1024;

    TextBox destBox;
    Button browseBtn, startBtn, stopBtn;
    CheckBox skipAnywhere;
    Label statusLbl, overallLbl, fileLbl, fileNameLbl;
    ProgressBar overallBar, fileBar;
    TextBox logBox;

    CancellationTokenSource cts;

    public MainForm()
    {
        Text = "PhotoPorter - iPhone to PC photo copier";
        Size = new Size(760, 560);
        MinimumSize = new Size(640, 480);
        Font = new Font("Segoe UI", 10);

        var destLbl = new Label { Text = "Copy to:", Left = 12, Top = 16, AutoSize = true };
        destBox = new TextBox { Left = 85, Top = 12, Width = 520, Text = @"D:\iPhone Photos", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        browseBtn = new Button { Text = "Browse...", Left = 615, Top = 10, Width = 115, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        browseBtn.Click += (s, e) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = destBox.Text };
            if (d.ShowDialog() == DialogResult.OK) destBox.Text = d.SelectedPath;
        };

        skipAnywhere = new CheckBox
        {
            Text = "Skip files already anywhere in the destination (same name and size), e.g. the ones copied before",
            Left = 12, Top = 46, Width = 720, Checked = true
        };

        startBtn = new Button { Text = "Start / Resume", Left = 12, Top = 78, Width = 150, Height = 34 };
        stopBtn = new Button { Text = "Stop", Left = 172, Top = 78, Width = 100, Height = 34, Enabled = false };
        startBtn.Click += (s, e) => StartCopy();
        stopBtn.Click += (s, e) => { cts?.Cancel(); Log("Stopping..."); };

        statusLbl = new Label { Left = 285, Top = 86, Width = 450, Text = "Plug in the iPhone, unlock it and tap Trust.", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };

        overallLbl = new Label { Left = 12, Top = 124, Width = 720, Text = "Overall:", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        overallBar = new ProgressBar { Left = 12, Top = 148, Width = 718, Height = 22, Maximum = 1000, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        fileNameLbl = new Label { Left = 12, Top = 180, Width = 720, Text = "Current file:", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        fileLbl = new Label { Left = 12, Top = 202, Width = 720, Text = "", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        fileBar = new ProgressBar { Left = 12, Top = 226, Width = 718, Height = 22, Maximum = 1000, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };

        logBox = new TextBox
        {
            Left = 12, Top = 260, Width = 718, Height = 245, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
        };

        Controls.AddRange(new Control[] { destLbl, destBox, browseBtn, skipAnywhere, startBtn, stopBtn, statusLbl,
            overallLbl, overallBar, fileNameLbl, fileLbl, fileBar, logBox });

        FormClosing += (s, e) => cts?.Cancel();
    }

    // ---------- UI helpers ----------
    void Ui(Action a) { if (IsDisposed) return; try { BeginInvoke(a); } catch { } }
    void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {msg}";
        Ui(() => { logBox.AppendText(line + Environment.NewLine); });
        try { File.AppendAllText(Path.Combine(destRoot ?? Path.GetTempPath(), "copy-log.txt"), line + Environment.NewLine); } catch { }
    }
    void Status(string msg) => Ui(() => statusLbl.Text = msg);

    static string Fmt(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.0} {u[i]}";
    }
    static string FmtTime(double sec)
    {
        if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0) return "--";
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }

    // ---------- keep the PC awake ----------
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;

    string destRoot;

    void StartCopy()
    {
        destRoot = destBox.Text.Trim();
        try { Directory.CreateDirectory(destRoot); }
        catch (Exception ex) { MessageBox.Show("Cannot use that folder: " + ex.Message); return; }

        startBtn.Enabled = false; browseBtn.Enabled = false; destBox.Enabled = false; skipAnywhere.Enabled = false;
        stopBtn.Enabled = true;
        cts = new CancellationTokenSource();
        bool skipAny = skipAnywhere.Checked;
        var token = cts.Token;

        var th = new Thread(() =>
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            try { Run(skipAny, token); }
            catch (Exception ex) { Log("Stopped with error: " + ex.Message); }
            finally
            {
                SetThreadExecutionState(ES_CONTINUOUS);
                Ui(() =>
                {
                    startBtn.Enabled = true; browseBtn.Enabled = true; destBox.Enabled = true; skipAnywhere.Enabled = true;
                    stopBtn.Enabled = false;
                });
            }
        });
        th.SetApartmentState(ApartmentState.MTA);
        th.IsBackground = true;
        th.Start();
    }

    // ---------- device handling ----------
    MediaDevice ConnectPhone(CancellationToken token)
    {
        bool told = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var devs = MediaDevice.GetDevices().ToList();
                var dev = devs.FirstOrDefault(d => (d.FriendlyName + d.Description + d.Manufacturer).IndexOf("apple", StringComparison.OrdinalIgnoreCase) >= 0
                                                || (d.FriendlyName + d.Description).IndexOf("iphone", StringComparison.OrdinalIgnoreCase) >= 0)
                          ?? (devs.Count == 1 ? devs[0] : null);
                if (dev != null)
                {
                    dev.Connect();
                    // the phone only shows files when unlocked and trusted
                    var roots = dev.GetDirectories(@"\");
                    if (roots.Length > 0)
                    {
                        Status("Connected: " + dev.FriendlyName);
                        return dev;
                    }
                    try { dev.Disconnect(); } catch { }
                }
            }
            catch { }
            if (!told) { Log("Waiting for the iPhone (plug it in, unlock it, tap Trust)..."); told = true; }
            Status("Waiting for iPhone... unlock it and tap Trust");
            token.WaitHandle.WaitOne(3000);
        }
        return null;
    }

    static void SafeDisconnect(MediaDevice d)
    {
        if (d == null) return;
        try { d.Disconnect(); } catch { }
        try { d.Dispose(); } catch { }
    }

    List<PhoneFile> Scan(MediaDevice dev, CancellationToken token)
    {
        var list = new List<PhoneFile>();
        var stack = new Stack<string>();
        foreach (var r in dev.GetDirectories(@"\")) stack.Push(r);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            Status("Scanning " + dir);
            string[] subs = Array.Empty<string>(), files = Array.Empty<string>();
            for (int attempt = 0; ; attempt++)
            {
                try { subs = dev.GetDirectories(dir); files = dev.GetFiles(dir); break; }
                catch when (attempt < 3) { Thread.Sleep(1000); }
            }
            foreach (var s in subs) stack.Push(s);
            foreach (var f in files)
            {
                long size = -1;
                try { size = (long)dev.GetFileInfo(f).Length; } catch { }
                string ext = Path.GetExtension(f);
                string folder = Path.GetFileName(dir.TrimEnd('\\'));
                int kind = PictureExt.Contains(ext) ? 0 : VideoExt.Contains(ext) ? 1 : 2;
                list.Add(new PhoneFile
                {
                    Path = f, Folder = folder, Name = Path.GetFileName(f), Size = size, Kind = kind,
                    SortKey = FolderSortKey(folder) + "|" + Path.GetFileName(f)
                });
            }
        }
        // pictures first, then videos, then everything else; each in date order (folder 202301__ etc.)
        return list.OrderBy(p => p.Kind).ThenBy(p => p.SortKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string FolderSortKey(string folder)
    {
        var m = Regex.Match(folder, @"(19|20)\d{2}(\d{2})?");
        return m.Success ? m.Value.PadRight(6, '0') + folder : "999999" + folder;
    }

    // ---------- main job ----------
    void Run(bool skipAny, CancellationToken token)
    {
        MediaDevice dev = ConnectPhone(token);
        if (dev == null) return;

        List<PhoneFile> files = null;
        while (files == null)
        {
            try { files = Scan(dev, token); }
            catch (OperationCanceledException) { SafeDisconnect(dev); return; }
            catch (Exception ex)
            {
                Log("Scan interrupted (" + ex.Message + "), reconnecting...");
                SafeDisconnect(dev);
                dev = ConnectPhone(token);
                if (dev == null) return;
            }
        }
        int pics = files.Count(f => f.Kind == 0), vids = files.Count(f => f.Kind == 1), other = files.Count(f => f.Kind == 2);
        Log($"Found {files.Count} files on the phone: {pics} pictures, {vids} videos, {other} other (sidecar/edit files).");

        // index what is already in the destination
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (skipAny)
        {
            Status("Checking what is already copied...");
            foreach (var p in Directory.EnumerateFiles(destRoot, "*", SearchOption.AllDirectories))
            {
                try { existing.Add(Path.GetFileName(p) + "|" + new FileInfo(p).Length); } catch { }
            }
        }

        var todo = new List<PhoneFile>();
        foreach (var f in files)
        {
            string target = TargetPath(f);
            bool done = File.Exists(target) && (f.Size < 0 || new FileInfo(target).Length == f.Size);
            if (!done && skipAny && f.Size >= 0 && existing.Contains(f.Name + "|" + f.Size)) done = true;
            if (!done) todo.Add(f);
        }
        long totalBytes = todo.Sum(f => Math.Max(0, f.Size));
        Log($"{files.Count - todo.Count} already copied, {todo.Count} to copy ({Fmt(totalBytes)}).");

        long doneBytes = 0;
        int doneCount = 0, failed = 0;
        var overallWatch = Stopwatch.StartNew();
        // rolling speed for estimates
        var speedSamples = new Queue<(double t, long b)>();

        for (int i = 0; i < todo.Count; i++)
        {
            if (token.IsCancellationRequested) break;
            var f = todo[i];
            int tries = 0;
            while (true)
            {
                if (token.IsCancellationRequested) break;
                tries++;
                try
                {
                    long fileDone = 0;
                    var fileWatch = Stopwatch.StartNew();
                    Ui(() => fileNameLbl.Text = $"Current file ({i + 1} of {todo.Count}): {f.Folder}\\{f.Name}  [{(f.Kind == 0 ? "picture" : f.Kind == 1 ? "video" : "other")}]");

                    CopyOne(dev, f, token, n =>
                    {
                        fileDone += n;
                        long all = doneBytes + fileDone;
                        double now = overallWatch.Elapsed.TotalSeconds;
                        speedSamples.Enqueue((now, all));
                        while (speedSamples.Count > 2 && now - speedSamples.Peek().t > 30) speedSamples.Dequeue();
                        var first = speedSamples.Peek();
                        double speed = now - first.t > 0.5 ? (all - first.b) / (now - first.t) : 0;
                        if (speed <= 0 && now > 0) speed = all / now;
                        double fileEta = speed > 0 ? (f.Size - fileDone) / speed : double.NaN;
                        double allEta = speed > 0 ? (totalBytes - all) / speed : double.NaN;
                        int fp = f.Size > 0 ? (int)Math.Min(1000, fileDone * 1000 / f.Size) : 0;
                        int op = totalBytes > 0 ? (int)Math.Min(1000, all * 1000 / totalBytes) : 0;
                        int dc = doneCount;
                        Ui(() =>
                        {
                            fileBar.Value = fp; overallBar.Value = op;
                            fileLbl.Text = $"{Fmt(fileDone)} of {Fmt(f.Size)}   -   {FmtTime(fileEta)} left for this file";
                            overallLbl.Text = $"Overall: {dc} of {todo.Count} files, {Fmt(all)} of {Fmt(totalBytes)}   -   {Fmt((long)speed)}/s   -   about {FmtTime(allEta)} left";
                        });
                    });
                    doneBytes += Math.Max(0, f.Size);
                    doneCount++;
                    break;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"Problem with {f.Folder}\\{f.Name}: {ex.Message}. Reconnecting and retrying (try {tries})...");
                    SafeDisconnect(dev);
                    dev = ConnectPhone(token);
                    if (dev == null) break;
                    if (tries >= 5)
                    {
                        Log($"Giving up on {f.Folder}\\{f.Name} after 5 tries; will be retried next time you press Start.");
                        failed++;
                        doneBytes += Math.Max(0, f.Size);
                        break;
                    }
                }
            }
        }

        SafeDisconnect(dev);
        if (token.IsCancellationRequested)
        {
            Log($"Stopped. {doneCount} copied this run. Press Start / Resume to continue where it left off.");
            Status("Stopped");
        }
        else
        {
            Log($"Finished. {doneCount} copied, {failed} failed. Took {FmtTime(overallWatch.Elapsed.TotalSeconds)}.");
            Status(failed == 0 ? "All done!" : $"Done with {failed} failures - press Start again to retry them");
            Ui(() => { overallBar.Value = 1000; });
        }
    }

    string TargetPath(PhoneFile f)
    {
        string sub = f.Kind == 0 ? "Pictures" : f.Kind == 1 ? "Videos" : "Other";
        return Path.Combine(destRoot, sub, f.Folder, f.Name);
    }

    // Copies one file through a .part file with a stall watchdog; throws on timeout or device errors.
    void CopyOne(MediaDevice dev, PhoneFile f, CancellationToken token, Action<long> progress)
    {
        string target = TargetPath(f);
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        string part = target + ".part";

        var info = dev.GetFileInfo(f.Path);
        using (var src = info.OpenRead())
        using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize))
        {
            var buf = new byte[ChunkSize];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var readTask = Task.Run(() => src.Read(buf, 0, buf.Length));
                if (!readTask.Wait(TimeSpan.FromSeconds(StallSeconds)))
                    throw new TimeoutException($"no data for {StallSeconds} seconds (phone timed out)");
                int n = readTask.Result;
                if (n <= 0) break;
                dst.Write(buf, 0, n);
                progress(n);
            }
        }
        long got = new FileInfo(part).Length;
        if (f.Size >= 0 && got != f.Size)
            throw new IOException($"incomplete copy ({Fmt(got)} of {Fmt(f.Size)})");
        if (File.Exists(target)) File.Delete(target);
        File.Move(part, target);
        try
        {
            if (info.LastWriteTime.HasValue) File.SetLastWriteTime(target, info.LastWriteTime.Value);
            else if (info.CreationTime.HasValue) File.SetLastWriteTime(target, info.CreationTime.Value);
        }
        catch { }
    }
}
