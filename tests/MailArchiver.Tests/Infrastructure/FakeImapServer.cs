using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailArchiver.Tests.Infrastructure;

/// <summary>
/// A small in-process IMAP server on 127.0.0.1 (random port) for tests that drive the REAL folder sync loop with the real MailKit client.
/// It speaks just what the sync uses: CAPABILITY, AUTHENTICATE PLAIN, LIST/LSUB, SELECT/EXAMINE, (UID) SEARCH with ALL, SINCE, SENTSINCE and
/// X-GM-RAW (only [-]label:Name terms), (UID) FETCH with UID, RFC822.SIZE, ENVELOPE, INTERNALDATE, FLAGS, X-GM-LABELS, X-GM-MSGID,
/// X-GM-THRID and BODY[]/BODY.PEEK[], NOOP, CLOSE, LOGOUT. It never talks to anything but the test, and nothing in it is real mail.
/// Faults (a refused FETCH, a dropped connection) are injected with <see cref="OnBodyFetch"/>.
/// </summary>
internal sealed class FakeImapServer : IDisposable
{
    public enum FetchAction { Normal, Drop, No }

    public sealed class Msg
    {
        public uint Uid;
        public byte[] Bytes = Array.Empty<byte>();
        public string MessageId = "";
        public string Subject = "";
        public DateTimeOffset InternalDate;
        public string[] Labels = Array.Empty<string>();
        public ulong GmId;
    }

    public sealed class Folder
    {
        public string Name = "";
        public string Attributes = "\\HasNoChildren";
        public uint UidValidity = 1;
        public List<Msg> Messages = new();
        public uint NextUid = 1;
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<Socket> _sockets = new();
    public int Port { get; }
    public bool Gmail { get; }
    public const string AllMail = "[Gmail]/All Mail";

    public List<Folder> Folders { get; } = new();
    /// <summary>Every command line received (tag stripped), for assertions on what the sync asked.</summary>
    public List<string> Commands { get; } = new();
    /// <summary>Called when the body of a message is fetched (folder, uid). Decides what the server does.</summary>
    public Func<string, uint, (FetchAction Action, string Text)> OnBodyFetch { get; set; } = (_, _) => (FetchAction.Normal, "");
    /// <summary>Called after a body was sent (folder, uid).</summary>
    public Action<string, uint>? AfterBodySent { get; set; }
    public int BodyFetches;

    public FakeImapServer(bool gmail = true)
    {
        Gmail = gmail;
        Folders.Add(new Folder { Name = "INBOX", Attributes = "\\HasNoChildren" });
        if (gmail)
        {
            Folders.Add(new Folder { Name = "[Gmail]", Attributes = "\\Noselect \\HasChildren" });
            Folders.Add(new Folder { Name = AllMail, Attributes = "\\HasNoChildren \\All" });
        }
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public Folder Main => Folders.First(f => f.Name == (Gmail ? AllMail : "INBOX"));

    /// <summary>Adds a message to a folder; the UID is the next one of that folder (UIDs are never reused).</summary>
    public Msg Add(Folder folder, byte[] bytes, string messageId, string subject, DateTimeOffset internalDate, params string[] labels)
    {
        lock (_gate)
        {
            var m = new Msg { Uid = folder.NextUid++, Bytes = bytes, MessageId = messageId, Subject = subject, InternalDate = internalDate, Labels = labels, GmId = 1000000UL + (ulong)Math.Abs(messageId.GetHashCode()) };
            folder.Messages.Add(m);
            return m;
        }
    }

    public void Remove(Folder folder, uint uid)
    {
        lock (_gate) folder.Messages.RemoveAll(m => m.Uid == uid);
    }

    public List<string> CommandsSince(int index) { lock (_gate) return Commands.Skip(index).ToList(); }

    public int CountCommands(string startsWith)
    {
        lock (_gate) return Commands.Count(c => c.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase) || c.Contains(startsWith, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_gate) foreach (var s in _sockets) { try { s.Close(); } catch { } }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var sock = await _listener.AcceptSocketAsync(_cts.Token);
                lock (_gate) _sockets.Add(sock);
                _ = Task.Run(() => Serve(sock));
            }
        }
        catch { }
    }

    private async Task Serve(Socket sock)
    {
        using var stream = new NetworkStream(sock, ownsSocket: true);
        try
        {
            await Write(stream, "* OK IMAP4rev1 fake ready\r\n");
            Folder? selected = null;
            while (true)
            {
                var line = await ReadLine(stream);
                if (line == null) return;
                // A literal announced as {n} at the end of the line: ask for the bytes and append them as a quoted string.
                while (line.EndsWith("}") && line.LastIndexOf('{') > 0 && int.TryParse(line[(line.LastIndexOf('{') + 1)..^1].TrimEnd('+'), out var n))
                {
                    var lit = new byte[n];
                    await Write(stream, "+ go ahead\r\n");
                    var read = 0;
                    while (read < n) { var r = await stream.ReadAsync(lit.AsMemory(read, n - read)); if (r == 0) return; read += r; }
                    var rest = await ReadLine(stream);
                    line = line[..line.LastIndexOf('{')] + "\"" + Encoding.UTF8.GetString(lit).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" + (rest ?? "");
                }
                var sp = line.IndexOf(' ');
                if (sp < 0) { await Write(stream, "* BAD no tag\r\n"); continue; }
                var tag = line[..sp];
                var cmd = line[(sp + 1)..];
                lock (_gate) Commands.Add(cmd);
                var t = Tokenize(cmd);
                var verb = t[0].ToUpperInvariant();
                var uidMode = false;
                if (verb == "UID") { uidMode = true; t.RemoveAt(0); verb = t[0].ToUpperInvariant(); }
                switch (verb)
                {
                    case "CAPABILITY":
                        await Write(stream, $"* CAPABILITY {Caps()}\r\n{tag} OK done\r\n");
                        break;
                    case "AUTHENTICATE":
                        if (t.Count < 3) { await Write(stream, "+ \r\n"); await ReadLine(stream); }
                        await Write(stream, $"{tag} OK [CAPABILITY {Caps()}] authenticated\r\n");
                        break;
                    case "LOGIN":
                        await Write(stream, $"{tag} OK [CAPABILITY {Caps()}] logged in\r\n");
                        break;
                    case "NOOP":
                    case "CHECK":
                        await Write(stream, $"{tag} OK done\r\n");
                        break;
                    case "LOGOUT":
                        await Write(stream, $"* BYE bye\r\n{tag} OK logged out\r\n");
                        return;
                    case "LIST":
                    case "LSUB":
                        await Write(stream, DoList(verb, t, tag));
                        break;
                    case "SELECT":
                    case "EXAMINE":
                        {
                            var name = t[1];
                            Folder? f; lock (_gate) f = Folders.FirstOrDefault(x => x.Name == name && !x.Attributes.Contains("Noselect"));
                            if (f == null) { await Write(stream, $"{tag} NO [NONEXISTENT] no such folder\r\n"); break; }
                            selected = f;
                            int exists; uint next; lock (_gate) { exists = f.Messages.Count; next = f.NextUid; }
                            await Write(stream, $"* FLAGS (\\Seen)\r\n* {exists} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {f.UidValidity}] ok\r\n* OK [UIDNEXT {next}] ok\r\n{tag} OK [READ-ONLY] {verb} done\r\n");
                            break;
                        }
                    case "CLOSE":
                    case "UNSELECT":
                        selected = null;
                        await Write(stream, $"{tag} OK closed\r\n");
                        break;
                    case "SEARCH":
                        {
                            if (selected == null) { await Write(stream, $"{tag} BAD no folder\r\n"); break; }
                            List<Msg> msgs; lock (_gate) msgs = selected.Messages.ToList();
                            var ids = Search(msgs, t.Skip(1).ToList(), out var err);
                            if (err != null) { await Write(stream, $"{tag} NO {err}\r\n"); break; }
                            // Sequence numbers are used when not in UID mode.
                            var outIds = uidMode ? ids.Select(m => m.Uid) : ids.Select(m => (uint)(msgs.IndexOf(m) + 1));
                            await Write(stream, $"* SEARCH{string.Concat(outIds.Select(i => " " + i))}\r\n{tag} OK search done\r\n");
                            break;
                        }
                    case "FETCH":
                        {
                            if (selected == null) { await Write(stream, $"{tag} BAD no folder\r\n"); break; }
                            var ok = await DoFetch(stream, selected, uidMode, t[1], t.Skip(2).ToList(), tag);
                            if (!ok) return;
                            break;
                        }
                    default:
                        await Write(stream, $"{tag} BAD unsupported {verb}\r\n");
                        break;
                }
            }
        }
        catch { }
    }

    private string Caps() => "IMAP4rev1 AUTH=PLAIN UIDPLUS" + (Gmail ? " X-GM-EXT-1" : "");

    private string DoList(string verb, List<string> t, string tag)
    {
        // LIST [(options)] "ref" "pattern" [RETURN (...)]; only the pattern is used.
        var args = t.Skip(1).ToList();
        if (args.Count > 0 && args[0] == "(") { while (args.Count > 0 && args[0] != ")") args.RemoveAt(0); if (args.Count > 0) args.RemoveAt(0); }
        var pattern = args.Count > 1 ? args[1] : "*";
        var sb = new StringBuilder();
        if (pattern == "")
        {
            sb.Append($"* {verb} (\\Noselect) \"/\" \"\"\r\n");
        }
        else
        {
            lock (_gate)
                foreach (var f in Folders)
                    if (Matches(pattern, f.Name))
                        sb.Append($"* {verb} ({f.Attributes}) \"/\" \"{f.Name}\"\r\n");
        }
        sb.Append($"{tag} OK {verb} done\r\n");
        return sb.ToString();
    }

    private static bool Matches(string pattern, string name)
    {
        var re = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("%", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, re) || (name == "INBOX" && string.Equals(pattern, "inbox", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Msg> Search(List<Msg> msgs, List<string> crit, out string? error)
    {
        error = null;
        IEnumerable<Msg> q = msgs;
        for (var i = 0; i < crit.Count; i++)
        {
            var c = crit[i].ToUpperInvariant();
            if (c is "(" or ")" or "ALL") continue;
            if (c is "SINCE" or "SENTSINCE")
            {
                var d = DateTime.ParseExact(crit[++i], new[] { "d-MMM-yyyy", "dd-MMM-yyyy" }, System.Globalization.CultureInfo.InvariantCulture);
                q = q.Where(m => m.InternalDate.UtcDateTime.Date >= d.Date);
            }
            else if (c == "X-GM-RAW")
            {
                foreach (var term in crit[++i].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var neg = term.StartsWith("-");
                    var tt = neg ? term[1..] : term;
                    if (!tt.StartsWith("label:", StringComparison.OrdinalIgnoreCase)) { error = "unsupported X-GM-RAW term " + term; return Array.Empty<Msg>(); }
                    var label = tt[6..];
                    bool Has(Msg m) => m.Labels.Any(l => l.Equals(label, StringComparison.OrdinalIgnoreCase) || l.StartsWith(label + "/", StringComparison.OrdinalIgnoreCase));
                    q = q.Where(m => Has(m) != neg);
                }
            }
            else { error = "unsupported search key " + crit[i]; return Array.Empty<Msg>(); }
        }
        return q.ToList();
    }

    private async Task<bool> DoFetch(NetworkStream stream, Folder folder, bool uidMode, string set, List<string> items, string tag)
    {
        List<Msg> all; lock (_gate) all = folder.Messages.ToList();
        var wanted = new List<(Msg M, int Seq)>();
        foreach (var part in set.Split(','))
        {
            var ab = part.Split(':');
            long lo = ab[0] == "*" ? long.MaxValue : long.Parse(ab[0]);
            long hi = ab.Length > 1 ? (ab[1] == "*" ? long.MaxValue : long.Parse(ab[1])) : lo;
            if (lo > hi) (lo, hi) = (hi, lo);
            for (var i = 0; i < all.Count; i++)
            {
                long key = uidMode ? all[i].Uid : i + 1;
                long last = uidMode ? all[^1].Uid : all.Count;
                var l = lo == long.MaxValue ? last : lo; var h = hi == long.MaxValue ? last : hi;
                if (key >= l && key <= h && !wanted.Any(w => w.M == all[i])) wanted.Add((all[i], i + 1));
            }
        }
        var joined = string.Join(" ", items).ToUpperInvariant();
        var wantBody = joined.Contains("BODY.PEEK[]") || joined.Contains("BODY[]") || joined.Contains("RFC822 ") || joined.EndsWith("RFC822");
        foreach (var (m, seq) in wanted.OrderBy(w => w.M.Uid))
        {
            if (wantBody)
            {
                Interlocked.Increment(ref BodyFetches);
                var (action, text) = OnBodyFetch(folder.Name, m.Uid);
                if (action == FetchAction.Drop) return false;
                if (action == FetchAction.No) { await Write(stream, $"{tag} NO {text}\r\n"); return true; }
            }
            var sb = new StringBuilder();
            sb.Append($"* {seq} FETCH (UID {m.Uid}");
            if (joined.Contains("RFC822.SIZE")) sb.Append($" RFC822.SIZE {m.Bytes.Length}");
            if (joined.Contains("FLAGS")) sb.Append(" FLAGS (\\Seen)");
            if (joined.Contains("INTERNALDATE")) sb.Append($" INTERNALDATE \"{m.InternalDate.ToString("dd-MMM-yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} +0000\"");
            if (joined.Contains("ENVELOPE")) sb.Append(" ENVELOPE (" + Q(m.InternalDate.ToString("ddd, dd MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " +0000") + " " + Q(m.Subject)
                + " ((\"S\" NIL \"from\" \"example.test\")) ((\"S\" NIL \"from\" \"example.test\")) ((\"S\" NIL \"from\" \"example.test\")) ((\"T\" NIL \"to\" \"example.test\")) NIL NIL NIL " + Q("<" + m.MessageId + ">") + ")");
            if (Gmail && joined.Contains("X-GM-LABELS")) sb.Append(" X-GM-LABELS (" + string.Join(" ", m.Labels.Select(Q)) + ")");
            if (Gmail && joined.Contains("X-GM-MSGID")) sb.Append($" X-GM-MSGID {m.GmId}");
            if (Gmail && joined.Contains("X-GM-THRID")) sb.Append($" X-GM-THRID {m.GmId + 7}");
            if (wantBody)
            {
                await WriteRaw(stream, Encoding.ASCII.GetBytes(sb.ToString() + $" BODY[] {{{m.Bytes.Length}}}\r\n"));
                await WriteRaw(stream, m.Bytes);
                await WriteRaw(stream, Encoding.ASCII.GetBytes(")\r\n"));
                AfterBodySent?.Invoke(folder.Name, m.Uid);
            }
            else
            {
                await Write(stream, sb.ToString() + ")\r\n");
            }
        }
        await Write(stream, $"{tag} OK fetch done\r\n");
        return true;
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static List<string> Tokenize(string s)
    {
        var res = new List<string>();
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            if (c == ' ') { i++; continue; }
            if (c is '(' or ')') { res.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != '"') { if (s[i] == '\\' && i + 1 < s.Length) i++; sb.Append(s[i++]); }
                i++; res.Add(sb.ToString()); continue;
            }
            var st = i;
            while (i < s.Length && s[i] is not (' ' or '(' or ')')) i++;
            res.Add(s[st..i]);
        }
        return res;
    }

    private static async Task<string?> ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder(); var buf = new byte[1];
        while (true)
        {
            var r = await stream.ReadAsync(buf.AsMemory(0, 1));
            if (r == 0) return sb.Length == 0 ? null : sb.ToString();
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)buf[0]);
        }
    }

    private static Task Write(NetworkStream s, string text) => WriteRaw(s, Encoding.UTF8.GetBytes(text));
    private static async Task WriteRaw(NetworkStream s, byte[] b) { await s.WriteAsync(b); await s.FlushAsync(); }
}
