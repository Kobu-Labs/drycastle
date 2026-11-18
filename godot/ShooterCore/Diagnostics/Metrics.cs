using System.Globalization;
using System.Net.Http;
using System.Text;

namespace Drycastle.ShooterCore;

/// <summary>
/// The numbers of one report period as Influx line protocol (<c>measurement,tag=value field=number,field=number</c>, one line each), the text a
/// metrics store takes over HTTP (VictoriaMetrics' <c>/write</c>: a field becomes the series <c>measurement_field</c>, a tag its label). Built
/// from the same <c>key=value</c> text the "server: prof" log lines print, so the log and the dashboard cannot disagree. No timestamps: the
/// store stamps the lines when they arrive. Names are kept to letters, digits and underscores.
/// </summary>
public sealed class MetricsBatch
{
    readonly StringBuilder _sb = new();
    bool _fields;

    public bool Empty => _sb.Length == 0;

    /// <summary>Starts a line of <paramref name="measurement"/>, with one tag when <paramref name="tag"/> is given. Fields follow (<see cref="Field"/>); a line that gets none is dropped by <see cref="End"/>.</summary>
    public MetricsBatch Begin(string measurement, string? tag = null, string? tagValue = null)
    {
        _lineAt = _sb.Length;
        _sb.Append(Clean(measurement));
        if (tag != null && !string.IsNullOrEmpty(tagValue)) _sb.Append(',').Append(Clean(tag)).Append('=').Append(Clean(tagValue));
        _sb.Append(' ');
        _fields = false;
        return this;
    }
    int _lineAt;

    /// <summary>A field of the line begun. A value that is not a finite number is left out (the store refuses the whole line otherwise).</summary>
    public MetricsBatch Field(string name, double value)
    {
        if (!double.IsFinite(value)) return this;
        if (_fields) _sb.Append(',');
        _sb.Append(Clean(name)).Append('=').Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        _fields = true;
        return this;
    }

    /// <summary>Ends the line begun.</summary>
    public MetricsBatch End()
    {
        if (_fields) _sb.Append('\n'); else _sb.Length = _lineAt;
        return this;
    }

    /// <summary>
    /// One line of <paramref name="measurement"/> from <c>key=value</c> text (a "server: prof" line's body): every pair with a number is a field. A value
    /// <c>a&gt;b</c> (before and after) is the fields <c>key_before</c> and <c>key_after</c>; a pair without a number is left out.
    /// </summary>
    public MetricsBatch Fields(string measurement, string pairs)
    {
        Begin(measurement);
        foreach (var (key, value) in Pairs(pairs))
        {
            var gt = value.IndexOf('>');
            if (gt < 0) { if (Number(value, out var v)) Field(key, v); continue; }
            if (Number(value[..gt], out var a)) Field(key + "_before", a);
            if (Number(value[(gt + 1)..], out var b)) Field(key + "_after", b);
        }
        return End();
    }

    /// <summary>
    /// A line per pair of <c>key=value</c> text, the key as the tag <paramref name="tag"/>: <c>main=35.1 bot_nav=17.7</c> under the tag <c>thread</c> and the field
    /// <c>cpu_pct</c>. A value of several numbers split by <c>|</c> (a scope's <c>ms|calls|kb</c>) fills <paramref name="fields"/> in order.
    /// </summary>
    public MetricsBatch Tagged(string measurement, string tag, string pairs, params string[] fields)
    {
        foreach (var (key, value) in Pairs(pairs))
        {
            Begin(measurement, tag, key);
            var parts = value.Split('|');
            for (var i = 0; i < parts.Length && i < fields.Length; i++)
                if (Number(parts[i], out var v)) Field(fields[i], v);
            End();
        }
        return this;
    }

    /// <summary>The lines so far, and an empty batch again.</summary>
    public string Take()
    {
        var s = _sb.ToString();
        _sb.Clear();
        return s;
    }

    static IEnumerable<(string Key, string Value)> Pairs(string text)
    {
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq > 0 && eq < token.Length - 1) yield return (token[..eq], token[(eq + 1)..]);
        }
    }

    static bool Number(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    /// <summary>A name the line protocol takes as it is: anything but a letter, a digit or an underscore becomes an underscore.</summary>
    static string Clean(string name)
    {
        var ok = true;
        foreach (var ch in name) if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) { ok = false; break; }
        if (ok) return name;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }
}

/// <summary>
/// Sends a <see cref="MetricsBatch"/>'s text to a metrics store, off the caller's thread: the game tick hands the text over and goes on. One send at
/// a time; a period that comes while the last one is still on its way is dropped (the store is slow or gone, and the next period has newer numbers).
/// A failure is reported once when it starts and once when it is over, not every period.
/// </summary>
public sealed class MetricsSender : IDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    readonly string _url;
    readonly Action<string> _log;
    int _busy;
    bool _failing;

    public long Sent, Dropped, Failed;

    public MetricsSender(string url, Action<string> log) { _url = url; _log = log; }

    /// <summary>Hands <paramref name="lines"/> over. Returns at once.</summary>
    public void Send(string lines)
    {
        if (lines.Length == 0) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref Dropped); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var body = new StringContent(lines, Encoding.UTF8, "text/plain");
                using var answer = await _http.PostAsync(_url, body).ConfigureAwait(false);
                if (!answer.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)answer.StatusCode}");
                Interlocked.Increment(ref Sent);
                if (_failing) { _failing = false; _log("metrics: the store takes them again"); }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                Interlocked.Increment(ref Failed);
                if (!_failing) { _failing = true; _log($"metrics: not sent to {_url}: {e.Message} (said once, until it works again)"); }
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    public void Dispose() => _http.Dispose();
}
