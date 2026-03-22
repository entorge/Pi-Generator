using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace PiGenerator;

public partial class MainWindow : Window
{
    // ── Adaptive trickle controller ───────────────────────────────────────────
    // Target: keep ~TARGET_BUFFER_MS worth of digits buffered ahead.
    // The controller measures actual compute rate and adjusts TricklePerTick
    // every TUNE_INTERVAL_MS to track it, while capping the UI tick work so
    // frames stay under FRAME_BUDGET_MS.
    private const double TARGET_BUFFER_MS = 1500.0; // aim to stay ~1.5s ahead
    private const double FRAME_BUDGET_MS  = 8.0;    // never spend >8ms per tick
    private const int    TUNE_INTERVAL_MS = 600;     // re-tune every 600ms
    private const int    TICK_MS          = 50;      // UI timer interval (20 fps)

    private int  _tricklePerTick   = 50;    // digits shown per 50ms tick; auto-tuned
    private long _lastTuneMs       = 0;
    private long _lastTuneComputed = 0;

    // ── Compute schedule ──────────────────────────────────────────────────────
    private int _nextChunkSize = 5_000;
    private const int ChunkMax = 2_000_000;

    // ── State ─────────────────────────────────────────────────────────────────
    private CancellationTokenSource _cts = new();
    private bool _paused = false;

    private readonly StringBuilder _computedBuf = new();
    private readonly object        _bufLock     = new();

    private long _displayedDigits = 0;
    private long _computedDigits  = 0;   // total digits computed (may lead display)

    private long _lastRateSample = 0;
    private long _lastRateMs     = 0;

    private readonly Stopwatch       _stopwatch = new();
    private readonly DispatcherTimer _uiTimer   = new();
    private readonly Stopwatch       _tickTimer = new(); // measures actual tick cost

    private readonly StringBuilder _tickSb = new();

    // ── Init ──────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();

        _uiTimer.Interval = TimeSpan.FromMilliseconds(TICK_MS);
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        Start();
    }

    // ── Start / restart ───────────────────────────────────────────────────────

    private void Start()
    {
        _cts    = new CancellationTokenSource();
        _paused = false;
        _stopwatch.Restart();
        _lastRateMs       = 0;
        _lastRateSample   = 0;
        _lastTuneMs       = 0;
        _lastTuneComputed = 0;
        _tricklePerTick   = 50;
        BtnToggle.Content = "⏸  Pause";

        _ = ComputeLoopAsync(_cts.Token);
    }

    // ── Compute loop ──────────────────────────────────────────────────────────

    private async Task ComputeLoopAsync(CancellationToken ct)
    {
        int totalTarget  = _nextChunkSize;
        string allSoFar  = "";

        while (!ct.IsCancellationRequested)
        {
            if (_paused)
            {
                await Task.Delay(80, ct).ConfigureAwait(false);
                continue;
            }

            // Back-pressure: don't compute more than ~10s of trickle ahead
            while (!ct.IsCancellationRequested)
            {
                long buffered;
                long rate;
                lock (_bufLock)
                {
                    buffered = _computedBuf.Length;
                    rate     = _tricklePerTick * (1000 / TICK_MS); // digits/sec trickle rate
                }
                double secondsAhead = rate > 0 ? buffered / (double)rate : 0;
                if (secondsAhead < 10.0) break;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested) break;

            try
            {
                string decimals = await PiEngine.ComputeDecimalDigitsAsync(totalTarget, ct)
                                               .ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (decimals.Length > allSoFar.Length)
                {
                    string slice = decimals[allSoFar.Length..];
                    allSoFar = decimals;
                    lock (_bufLock)
                    {
                        _computedBuf.Append(slice);
                        _computedDigits = allSoFar.Length;
                    }
                }

                _nextChunkSize = Math.Min(ChunkMax, _nextChunkSize * 2);
                totalTarget    = allSoFar.Length + _nextChunkSize;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => TbStatus.Text = $"Error: {ex.Message}");
                break;
            }
        }
    }

    // ── UI timer ──────────────────────────────────────────────────────────────

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_paused)
        {
            _tickTimer.Restart();

            // Drain up to _tricklePerTick chars — but stop early if we exceed frame budget
            _tickSb.Clear();
            int take;
            lock (_bufLock)
            {
                take = Math.Min(_tricklePerTick, _computedBuf.Length);
                if (take > 0)
                {
                    _tickSb.Append(_computedBuf, 0, take);
                    _computedBuf.Remove(0, take);
                }
            }

            if (_tickSb.Length > 0)
            {
                TbDigits.AppendText(_tickSb.ToString());

                // If appending took too long, shrink trickle immediately
                if (_tickTimer.Elapsed.TotalMilliseconds > FRAME_BUDGET_MS)
                {
                    _tricklePerTick = Math.Max(10, (int)(_tricklePerTick * 0.75));
                }

                TbDigits.ScrollToEnd();
                _displayedDigits += _tickSb.Length;
            }

            _tickTimer.Stop();
        }

        // ── Adaptive tuning (every TUNE_INTERVAL_MS) ─────────────────────────
        long ms = _stopwatch.ElapsedMilliseconds;
        long msSinceTune = ms - _lastTuneMs;

        if (msSinceTune >= TUNE_INTERVAL_MS && !_paused)
        {
            long computed;
            long bufLen;
            lock (_bufLock) { computed = _computedDigits; bufLen = _computedBuf.Length; }

            // Compute rate: digits/sec produced by Chudnovsky
            long newDigits   = computed - _lastTuneComputed;
            double computeRate = newDigits / (msSinceTune / 1000.0);   // digits/sec

            // Target trickle = compute rate so buffer stays stable
            // Also factor in current buffer depth vs target
            double targetBufferDigits = computeRate * (TARGET_BUFFER_MS / 1000.0);
            double bufferError        = bufLen - targetBufferDigits;

            // P-controller: nudge trickle rate proportionally to error
            double targetTrickle = computeRate / (1000.0 / TICK_MS);   // digits/tick at compute rate
            double correction    = bufferError / (1000.0 / TICK_MS) * 0.1; // damped
            int newTrickle = (int)Math.Clamp(targetTrickle + correction, 10, 50_000);

            // Only override if frame budget hasn't already reduced it
            if (_tickTimer.Elapsed.TotalMilliseconds <= FRAME_BUDGET_MS)
                _tricklePerTick = newTrickle;

            _lastTuneMs       = ms;
            _lastTuneComputed = computed;
        }

        // ── Stats ─────────────────────────────────────────────────────────────
        long count = _displayedDigits;
        TbDigitCount.Text = count.ToString("N0");
        TbElapsed.Text    = FormatElapsed(ms);

        long bufAhead;
        lock (_bufLock) bufAhead = _computedBuf.Length;

        TbStatus.Text = _paused
            ? "● paused"
            : bufAhead > 500
                ? $"● {bufAhead:N0} digits buffered  •  {_tricklePerTick}/tick"
                : $"● computing...  •  {_tricklePerTick}/tick";

        long msDelta = ms - _lastRateMs;
        if (msDelta >= 800)
        {
            long delta  = count - _lastRateSample;
            TbRate.Text     = FormatRate(delta / (msDelta / 1000.0));
            _lastRateMs     = ms;
            _lastRateSample = count;
        }
    }

    // ── Controls ─────────────────────────────────────────────────────────────

    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        if (_paused)
        {
            BtnToggle.Content = "▶  Resume";
            _stopwatch.Stop();
        }
        else
        {
            BtnToggle.Content = "⏸  Pause";
            _stopwatch.Start();
            _lastRateMs     = _stopwatch.ElapsedMilliseconds;
            _lastRateSample = _displayedDigits;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedDigits > 0)
        {
            Clipboard.SetText(TbDigits.Text);
            TbStatus.Text = $"✓ copied {_displayedDigits:N0} digits to clipboard";
        }
    }

    private void BtnRestart_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        _uiTimer.Stop();

        _paused           = false;
        _displayedDigits  = 0;
        _computedDigits   = 0;
        _nextChunkSize    = 5_000;
        _tricklePerTick   = 50;
        lock (_bufLock) { _computedBuf.Clear(); }
        _stopwatch.Reset();

        TbDigits.Text     = "3.";
        TbDigitCount.Text = "0";
        TbElapsed.Text    = "0s";
        TbRate.Text       = "—";

        _uiTimer.Start();
        Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _uiTimer.Stop();
        base.OnClosed(e);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatElapsed(long ms)
    {
        if (ms < 60_000) return $"{ms / 1000.0:F1}s";
        long s = ms / 1000;
        return $"{s / 60}m {s % 60}s";
    }

    private static string FormatRate(double r)
    {
        if (r >= 1_000_000) return $"{r / 1_000_000:F1}M";
        if (r >= 1_000)     return $"{r / 1_000:F1}k";
        return ((int)r).ToString();
    }
}
