using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MC = Windows.Media.Control;
using WF = Windows.Foundation;
using WS = Windows.Storage.Streams;

namespace MacLook
{
    /// <summary>
    /// What is playing right now, from Windows' System Media Transport Controls (the same source as the
    /// volume flyout): Spotify, browsers, Apple Music, Media Player... All state lives on the UI thread;
    /// WinRT calls run on pool threads and post their results back. If SMTC is missing (Windows 10 before
    /// 1809) Available stays false and nothing else happens.
    /// </summary>
    internal static class NowPlaying
    {
        /// <summary>Raised on the UI thread whenever anything below changes.</summary>
        public static event Action Changed;

        public static bool Available { get; private set; }
        public static bool HasSession { get; private set; }
        public static bool IsPlaying { get; private set; }
        public static string Title = "", Artist = "", Album = "", AppName = "";
        public static int TrackNumber, TrackCount;
        public static double DurationSec;
        public static bool? Shuffle;                 // null: the app doesn't say
        public static int Repeat = -1;               // -1 unknown, 0 off, 1 one, 2 all
        public static bool CanPlayPause, CanNext, CanPrevious, CanSeek, CanShuffle, CanRepeat;

        /// <summary>Album art (frozen), a soft blurred copy for backdrops, and its most vivid colour.</summary>
        public static BitmapSource Art, Ambient;
        public static Color Tint = Color.FromRgb(0xFA, 0x2D, 0x48);
        public static bool HasTint;

        /// <summary>Goes up by one each time the track changes (for "new track" animations).</summary>
        public static int TrackVersion { get; private set; }

        private static Dispatcher ui;
        private static MC.GlobalSystemMediaTransportControlsSessionManager manager;
        private static MC.GlobalSystemMediaTransportControlsSession session;
        private static readonly object gate = new object();
        private static Timer poll;
        private static double posSec, startSec;
        private static DateTime posStamp = DateTime.UtcNow, seekAt = DateTime.MinValue;
        private static string trackKey = "", artKey = "";
        private static int artSeq;
        private static bool stopped, started;

        private const int PollMs = 2000;                 // safety net for apps that forget to raise events
        private const uint MaxArtBytes = 8 * 1024 * 1024;
        private const double SeekTrustSec = 6;           // how long our own seek beats older timeline reports

        /// <summary>Playback position in seconds, moving on by itself while playing.</summary>
        public static double PositionSec
        {
            get
            {
                double p = posSec;
                if (IsPlaying) p += (DateTime.UtcNow - posStamp).TotalSeconds;
                if (DurationSec > 0 && p > DurationSec) p = DurationSec;
                return Math.Max(0, p);
            }
        }

        /// <summary>Starts listening (on the UI thread; later calls do nothing). Never throws.</summary>
        public static void Start()
        {
            if (started) return;
            started = true;
            ui = Dispatcher.CurrentDispatcher;
            stopped = false;
            try { StartCore(); }
            catch (Exception ex) { Available = false; Program.Log("Now Playing unavailable: " + ex.Message); }
        }

        // Separate and not inlined: on a Windows without SMTC the type load fails when this method is
        // compiled, which Start can then catch.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartCore()
        {
            var op = MC.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            op.Completed = (o, status) =>
            {
                try
                {
                    if (status != WF.AsyncStatus.Completed || stopped) return;
                    var m = o.GetResults();
                    if (m == null) return;
                    lock (gate) manager = m;
                    m.CurrentSessionChanged += OnCurrentSessionChanged;
                    m.SessionsChanged += OnSessionsChanged;
                    Post(() => { Available = true; Raise(); });
                    PickSession();
                    poll = new Timer(s => SafePoll(), null, PollMs, PollMs);
                }
                catch (Exception ex) { Program.Log(ex); }
            };
        }

        public static void Stop()
        {
            stopped = true;
            started = false;
            try
            {
                if (poll != null) { poll.Dispose(); poll = null; }
                lock (gate)
                {
                    if (session != null) Detach(session);
                    session = null;
                    if (manager != null)
                    {
                        manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                        manager.SessionsChanged -= OnSessionsChanged;
                    }
                    manager = null;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ------------------------------------------------------------------ controls (fire and forget)
        public static void PlayPause()
        {
            var s = Current();
            if (s == null) return;
            Rebase(IsPlaying);
            IsPlaying = !IsPlaying;   // answer the click at once; the app's own event confirms it
            Raise();
            Fire(() => s.TryTogglePlayPauseAsync());
        }

        public static void Next() { var s = Current(); if (s != null) Fire(() => s.TrySkipNextAsync()); }
        public static void Previous() { var s = Current(); if (s != null) Fire(() => s.TrySkipPreviousAsync()); }

        public static void Seek(double seconds)
        {
            var s = Current();
            if (s == null) return;
            seconds = Math.Max(0, DurationSec > 0 ? Math.Min(DurationSec, seconds) : seconds);
            posSec = seconds;
            posStamp = DateTime.UtcNow;
            seekAt = posStamp;
            Raise();
            long ticks = TimeSpan.FromSeconds(seconds + startSec).Ticks;
            Fire(() => s.TryChangePlaybackPositionAsync(ticks));
        }

        public static void ToggleShuffle()
        {
            var s = Current();
            if (s == null) return;
            bool on = !(Shuffle ?? false);
            Shuffle = on;
            Raise();
            Fire(() => s.TryChangeShuffleActiveAsync(on));
        }

        /// <summary>Off, then all, then one (the order Music uses).</summary>
        public static void CycleRepeat()
        {
            var s = Current();
            if (s == null) return;
            int next = Repeat == 2 ? 1 : Repeat == 1 ? 0 : 2;
            Repeat = next;
            Raise();
            var mode = next == 2 ? global::Windows.Media.MediaPlaybackAutoRepeatMode.List
                     : next == 1 ? global::Windows.Media.MediaPlaybackAutoRepeatMode.Track
                     : global::Windows.Media.MediaPlaybackAutoRepeatMode.None;
            Fire(() => s.TryChangeAutoRepeatModeAsync(mode));
        }

        private static MC.GlobalSystemMediaTransportControlsSession Current()
        {
            lock (gate) return session;
        }

        /// <summary>Starts a WinRT call and swallows whatever it reports: the app's events tell us the outcome.</summary>
        private static void Fire(Func<WF.IAsyncOperation<bool>> start)
        {
            ThreadPool.QueueUserWorkItem(x =>
            {
                try
                {
                    var op = start();
                    op.Completed = (o, st) => { try { if (st == WF.AsyncStatus.Completed) o.GetResults(); } catch { } };
                }
                catch (Exception ex) { Program.Log(ex); }
            });
        }

        // ------------------------------------------------------------------ session tracking
        private static void OnCurrentSessionChanged(MC.GlobalSystemMediaTransportControlsSessionManager m, MC.CurrentSessionChangedEventArgs a) { PickSession(); }
        private static void OnSessionsChanged(MC.GlobalSystemMediaTransportControlsSessionManager m, MC.SessionsChangedEventArgs a) { PickSession(); }
        private static void OnMedia(MC.GlobalSystemMediaTransportControlsSession s, MC.MediaPropertiesChangedEventArgs a) { ReadMedia(s); }
        private static void OnPlayback(MC.GlobalSystemMediaTransportControlsSession s, MC.PlaybackInfoChangedEventArgs a) { ReadPlayback(s); }
        private static void OnTimeline(MC.GlobalSystemMediaTransportControlsSession s, MC.TimelinePropertiesChangedEventArgs a) { ReadTimeline(s); }

        private static void SafePoll()
        {
            try
            {
                if (stopped) return;
                PickSession();
                var s = Current();
                if (s != null) { ReadPlayback(s); ReadTimeline(s); }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Windows' "current" session, unless another app is the one actually playing.</summary>
        private static void PickSession()
        {
            try
            {
                MC.GlobalSystemMediaTransportControlsSessionManager m;
                lock (gate) m = manager;
                if (m == null) return;
                var pick = m.GetCurrentSession();
                if (pick == null || !Playing(pick))
                {
                    foreach (var s in m.GetSessions())
                        if (Playing(s)) { pick = s; break; }
                    if (pick == null)
                        foreach (var s in m.GetSessions()) { pick = s; break; }
                }
                bool changed;
                lock (gate)
                {
                    changed = !SameSession(pick, session);
                    if (changed)
                    {
                        if (session != null) Detach(session);
                        session = pick;
                        if (pick != null) Attach(pick);
                    }
                }
                if (!changed) return;
                if (pick == null)
                {
                    Post(() =>
                    {
                        HasSession = false; IsPlaying = false;
                        Title = Artist = Album = AppName = "";
                        Art = Ambient = null; HasTint = false; trackKey = artKey = "";
                        DurationSec = 0; posSec = 0;
                        Raise();
                    });
                    return;
                }
                string app = Friendly(pick.SourceAppUserModelId);
                Post(() => { HasSession = true; AppName = app; });
                ReadPlayback(pick);
                ReadTimeline(pick);
                ReadMedia(pick);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static bool SameSession(MC.GlobalSystemMediaTransportControlsSession a, MC.GlobalSystemMediaTransportControlsSession b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            try { return a.SourceAppUserModelId == b.SourceAppUserModelId; } catch { return false; }
        }

        private static bool Playing(MC.GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                var pi = s.GetPlaybackInfo();
                return pi != null && pi.PlaybackStatus == MC.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { return false; }
        }

        private static void Attach(MC.GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                s.MediaPropertiesChanged += OnMedia;
                s.PlaybackInfoChanged += OnPlayback;
                s.TimelinePropertiesChanged += OnTimeline;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void Detach(MC.GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                s.MediaPropertiesChanged -= OnMedia;
                s.PlaybackInfoChanged -= OnPlayback;
                s.TimelinePropertiesChanged -= OnTimeline;
            }
            catch { }   // the app may already be gone
        }

        private static bool IsCurrent(MC.GlobalSystemMediaTransportControlsSession s)
        {
            lock (gate) return ReferenceEquals(s, session);
        }

        // ------------------------------------------------------------------ reading
        private static void ReadPlayback(MC.GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                if (!IsCurrent(s)) return;
                var pi = s.GetPlaybackInfo();
                if (pi == null) return;
                bool playing = pi.PlaybackStatus == MC.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                bool? shuffle = pi.IsShuffleActive;
                var rm = pi.AutoRepeatMode;
                int repeat = rm == null ? -1 : rm.Value == global::Windows.Media.MediaPlaybackAutoRepeatMode.Track ? 1
                           : rm.Value == global::Windows.Media.MediaPlaybackAutoRepeatMode.List ? 2 : 0;
                var c = pi.Controls;
                bool cpp = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
                bool cn = c.IsNextEnabled, cp = c.IsPreviousEnabled, cs = c.IsPlaybackPositionEnabled;
                bool csh = c.IsShuffleEnabled, cr = c.IsRepeatEnabled;
                Post(() =>
                {
                    bool diff = playing != IsPlaying || shuffle != Shuffle || repeat != Repeat || cpp != CanPlayPause || cn != CanNext
                             || cp != CanPrevious || cs != CanSeek || csh != CanShuffle || cr != CanRepeat;
                    if (playing != IsPlaying) Rebase(IsPlaying);
                    IsPlaying = playing; Shuffle = shuffle; Repeat = repeat;
                    CanPlayPause = cpp; CanNext = cn; CanPrevious = cp; CanSeek = cs; CanShuffle = csh; CanRepeat = cr;
                    if (diff) Raise();
                });
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void ReadTimeline(MC.GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                if (!IsCurrent(s)) return;
                var t = s.GetTimelineProperties();
                if (t == null) return;
                double start = t.StartTime.TotalSeconds;
                double dur = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
                double pos = (t.Position - t.StartTime).TotalSeconds;
                DateTime stamp = t.LastUpdatedTime.UtcDateTime;
                DateTime now = DateTime.UtcNow;
                bool stale = stamp < seekAt;
                // Some apps report no or nonsense update times; then assume "just now".
                if (stamp > now || (now - stamp).TotalMinutes > 30) stamp = now;
                Post(() =>
                {
                    // Many apps only refresh their timeline lazily: don't let an old report undo our own seek.
                    bool keepOurs = stale && (DateTime.UtcNow - seekAt).TotalSeconds < SeekTrustSec;
                    double before = PositionSec;
                    bool durChanged = Math.Abs(dur - DurationSec) > 0.5;
                    startSec = start;
                    DurationSec = dur;
                    if (!keepOurs) { posSec = pos; posStamp = stamp; }
                    if (durChanged || Math.Abs(PositionSec - before) > 1.5) Raise();
                });
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        private static void ReadMedia(MC.GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                if (!IsCurrent(s)) return;
                var op = s.TryGetMediaPropertiesAsync();
                op.Completed = (o, st) =>
                {
                    try
                    {
                        if (st != WF.AsyncStatus.Completed || !IsCurrent(s)) return;
                        var p = o.GetResults();
                        if (p == null) return;
                        string title = p.Title ?? "", artist = p.Artist ?? "", album = p.AlbumTitle ?? "";
                        if (artist.Length == 0) artist = p.AlbumArtist ?? "";
                        int num = p.TrackNumber, count = p.AlbumTrackCount;
                        string key = title + "\n" + artist + "\n" + album;
                        var thumb = p.Thumbnail;
                        Post(() =>
                        {
                            bool newTrack = key != trackKey;
                            Title = title; Artist = artist; Album = album; TrackNumber = num; TrackCount = count;
                            if (newTrack)
                            {
                                trackKey = key;
                                TrackVersion++;
                                // Keep the old art until the new picture is decoded (no grey flash between songs),
                                // unless the new track has none at all.
                                if (thumb == null) { Art = Ambient = null; HasTint = false; artKey = ""; }
                            }
                            Raise();
                            if (thumb != null && (newTrack || artKey != key || Art == null)) LoadArt(thumb, key);
                        });
                    }
                    catch (Exception ex) { Program.Log(ex); }
                };
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Reads the thumbnail's bytes off the UI thread, then decodes them on it.</summary>
        private static void LoadArt(WS.IRandomAccessStreamReference thumb, string key)
        {
            int seq = ++artSeq;
            ThreadPool.QueueUserWorkItem(x =>
            {
                try
                {
                    var open = thumb.OpenReadAsync();
                    open.Completed = (o, st) =>
                    {
                        try
                        {
                            if (st != WF.AsyncStatus.Completed) return;
                            var stream = o.GetResults();
                            ulong size = stream.Size;
                            if (size == 0 || size > MaxArtBytes) { stream.Dispose(); return; }
                            var reader = new WS.DataReader(stream.GetInputStreamAt(0));
                            var load = reader.LoadAsync((uint)size);
                            load.Completed = (lo, lst) =>
                            {
                                try
                                {
                                    if (lst != WF.AsyncStatus.Completed) return;
                                    uint got = lo.GetResults();
                                    var bytes = new byte[got];
                                    reader.ReadBytes(bytes);
                                    reader.Dispose();
                                    stream.Dispose();
                                    Post(() => { if (seq == artSeq && key == trackKey) SetArt(bytes, key); });
                                }
                                catch (Exception ex) { Program.Log(ex); }
                            };
                        }
                        catch (Exception ex) { Program.Log(ex); }
                    };
                }
                catch (Exception ex) { Program.Log(ex); }
            });
        }

        private static void SetArt(byte[] bytes, string key)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 400;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                Art = bmp;
                artKey = key;
                MakeAmbient(bytes);
                Raise();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>
        /// Shrinks the art to 8x8 (the decoder's high-quality downscale does the averaging), finds its most
        /// vivid colour, and smoothly re-enlarges it to 48x48: a cheap, very soft "blurred art" backdrop.
        /// </summary>
        private static void MakeAmbient(byte[] bytes)
        {
            const int S = 8, Big = 48;
            var small = new BitmapImage();
            small.BeginInit();
            small.CacheOption = BitmapCacheOption.OnLoad;
            small.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            small.DecodePixelWidth = S;
            small.DecodePixelHeight = S;
            small.StreamSource = new MemoryStream(bytes);
            small.EndInit();
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new int[w * h];
            conv.CopyPixels(px, w * 4, 0);

            // Most vivid colour: average weighted by saturation, ignoring near-black and near-white.
            double tr = 0, tg = 0, tb = 0, tw = 0;
            foreach (int c in px)
            {
                double r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                double sat = mx <= 0 ? 0 : (mx - mn) / mx, lum = mx / 255.0;
                double wgt = 0.02 + sat * sat * (lum > 0.15 && lum < 0.97 ? 1 : 0.1);
                tr += r * wgt; tg += g * wgt; tb += b * wgt; tw += wgt;
            }
            Tint = Vivid(tr / tw, tg / tw, tb / tw);
            HasTint = true;

            // Bilinear re-enlarge, slightly darkened and saturated so white text stays readable.
            var outPx = new int[Big * Big];
            for (int j = 0; j < Big; j++)
            {
                double fy = (j + 0.5) * h / Big - 0.5;
                int y0 = Clamp((int)Math.Floor(fy), 0, h - 1), y1 = Clamp(y0 + 1, 0, h - 1);
                double vy = Math.Max(0, Math.Min(1, fy - Math.Floor(fy)));
                for (int i = 0; i < Big; i++)
                {
                    double fx = (i + 0.5) * w / Big - 0.5;
                    int x0 = Clamp((int)Math.Floor(fx), 0, w - 1), x1 = Clamp(x0 + 1, 0, w - 1);
                    double vx = Math.Max(0, Math.Min(1, fx - Math.Floor(fx)));
                    double r = 0, g = 0, b = 0;
                    Mix(px[y0 * w + x0], (1 - vx) * (1 - vy), ref r, ref g, ref b);
                    Mix(px[y0 * w + x1], vx * (1 - vy), ref r, ref g, ref b);
                    Mix(px[y1 * w + x0], (1 - vx) * vy, ref r, ref g, ref b);
                    Mix(px[y1 * w + x1], vx * vy, ref r, ref g, ref b);
                    double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    r = (l + (r - l) * 1.3) * 0.62; g = (l + (g - l) * 1.3) * 0.62; b = (l + (b - l) * 1.3) * 0.62;
                    outPx[j * Big + i] = unchecked((int)0xFF000000) | (C8(r) << 16) | (C8(g) << 8) | C8(b);
                }
            }
            // Two light box-blur passes take the bilinear "diamond" pattern out of the enlarged 8x8.
            var tmp = new int[outPx.Length];
            for (int pass = 0; pass < 2; pass++) { Box(outPx, tmp, Big, 1, 0); Box(tmp, outPx, Big, 0, 1); }
            var amb = BitmapSource.Create(Big, Big, 96, 96, PixelFormats.Bgra32, null, outPx, Big * 4);
            amb.Freeze();
            Ambient = amb;
        }

        /// <summary>Pushes a colour to a lively, readable brightness for buttons and waveforms.</summary>
        private static Color Vivid(double r, double g, double b)
        {
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx < 1) return Color.FromRgb(0xE8, 0xE8, 0xED);
            if ((mx - mn) / mx < 0.12) return Color.FromRgb(0xE8, 0xE8, 0xED);   // greyscale art: plain light grey
            double k = 235 / mx;
            r *= k; g *= k; b *= k;
            double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (l < 120) { double lift = (120 - l) * 0.8; r += lift; g += lift; b += lift; }
            return Color.FromRgb((byte)C8(r), (byte)C8(g), (byte)C8(b));
        }

        /// <summary>Radius-3 box blur along one axis of a square, opaque image.</summary>
        private static void Box(int[] src, int[] dst, int n, int dx, int dy)
        {
            const int R = 3;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int k = -R; k <= R; k++)
                    {
                        int x = Clamp(i + k * dx, 0, n - 1), y = Clamp(j + k * dy, 0, n - 1);
                        int c = src[y * n + x];
                        r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF;
                    }
                    const int d = 2 * R + 1;
                    dst[j * n + i] = unchecked((int)0xFF000000) | (r / d << 16) | (g / d << 8) | (b / d);
                }
        }

        private static void Mix(int c, double wgt, ref double r, ref double g, ref double b)
        {
            r += ((c >> 16) & 0xFF) * wgt; g += ((c >> 8) & 0xFF) * wgt; b += (c & 0xFF) * wgt;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        private static int C8(double v) { return v <= 0 ? 0 : v >= 255 ? 255 : (int)v; }

        // ------------------------------------------------------------------ helpers
        /// <summary>Freezes the extrapolated position, e.g. when play/pause flips.</summary>
        private static void Rebase(bool wasPlaying)
        {
            double p = posSec;
            if (wasPlaying) p += (DateTime.UtcNow - posStamp).TotalSeconds;
            if (DurationSec > 0) p = Math.Min(p, DurationSec);
            posSec = Math.Max(0, p);
            posStamp = DateTime.UtcNow;
        }

        private static void Post(Action a)
        {
            var d = ui;
            if (d == null || stopped) return;
            d.BeginInvoke(new Action(() => { try { a(); } catch (Exception ex) { Program.Log(ex); } }));
        }

        private static void Raise()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); } catch (Exception ex) { Program.Log(ex); }
        }

        private static readonly Dictionary<string, string> knownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "spotify", "Spotify" }, { "zunemusic", "Media Player" }, { "zunevideo", "Movies & TV" },
            { "applemusic", "Apple Music" }, { "appleinc.itunes", "iTunes" }, { "msedge", "Microsoft Edge" },
            { "chrome", "Google Chrome" }, { "308046b0af4a39cb", "Firefox" }, { "firefox", "Firefox" },
            { "brave", "Brave" }, { "opera", "Opera" }, { "vivaldi", "Vivaldi" }, { "vlc", "VLC" },
            { "foobar2000", "foobar2000" }, { "musicbee", "MusicBee" }, { "tidal", "TIDAL" }, { "deezer", "Deezer" },
            { "amazonmusic", "Amazon Music" }, { "youtube", "YouTube Music" }, { "discord", "Discord" },
        };

        /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" -> "Spotify".</summary>
        private static string Friendly(string id)
        {
            if (string.IsNullOrEmpty(id)) return "an app";
            string low = id.ToLowerInvariant();
            foreach (var kv in knownApps) if (low.Contains(kv.Key)) return kv.Value;
            string s = id;
            int bang = s.LastIndexOf('!');
            if (bang >= 0 && bang < s.Length - 1) s = s.Substring(bang + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            return s.Length == 0 ? "an app" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    /// <summary>Live output level of the default speakers (Core Audio IAudioMeterInformation).</summary>
    internal static class AudioMeter
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }

        private const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;
        private const int RefreshEveryMs = 8000;   // re-open now and then so a new default output is followed

        private static IAudioMeterInformation meter;
        private static DateTime opened, nextTry;
        private static bool broken;

        /// <summary>0..1 peak of what the speakers are playing right now (0 if unknown).</summary>
        public static float Peak()
        {
            if (broken) return 0;
            try
            {
                if (meter != null && (DateTime.UtcNow - opened).TotalMilliseconds > RefreshEveryMs) Close();
                if (meter == null && DateTime.UtcNow >= nextTry) Open();
                if (meter == null) { nextTry = DateTime.UtcNow.AddSeconds(5); return 0; }   // no speakers: don't retry every frame
                float p;
                if (meter.GetPeakValue(out p) != 0) { Close(); return 0; }
                return p < 0 ? 0 : p > 1 ? 1 : p;
            }
            catch (Exception ex) { Program.Log(ex); broken = true; Close(); return 0; }
        }

        public static void Close()
        {
            if (meter != null && Marshal.IsComObject(meter)) Marshal.ReleaseComObject(meter);
            meter = null;
        }

        private static void Open()
        {
            object en = null;
            IMMDevice dev = null;
            try
            {
                en = new MMDeviceEnumeratorCom();
                if (((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(eRender, eMultimedia, out dev) != 0 || dev == null) return;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                object o;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return;
                meter = o as IAudioMeterInformation;
                if (meter == null && o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
                opened = DateTime.UtcNow;
            }
            finally
            {
                if (dev != null && Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
                if (en != null && Marshal.IsComObject(en)) Marshal.ReleaseComObject(en);
            }
        }
    }
}
