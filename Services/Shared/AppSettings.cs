using System.Collections.Concurrent;
using System.Globalization;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    // ------------------------------------------------------------------------------------------------------------
    // Settings of the "Integrity & Storage" page (fork).
    //
    //  * LOCKED settings define the guarantee (storage layout, Required, audit chain, key locations). They are shown
    //    read-only with the environment variable that sets them; no code path of the page can change them.
    //  * EDITABLE settings (schedules, bin, OCR limits) are stored in the AppSettings table and applied on top of the
    //    configuration. Precedence: environment variable (PINNED, shown locked) > database > built-in default.
    //  * Every change is validated against floors/ceilings, risky ones need the admin's password, and the change is
    //    written to the audit chain and the access log BEFORE it takes effect.
    // ------------------------------------------------------------------------------------------------------------

    public enum SettingGroup { Locked, Editable }

    public enum SettingKind { Int, Bool, Choice, Text }

    /// <summary>The effective option objects a definition reads from (and an editable one writes to).</summary>
    public sealed record OptionsBundle(IntegrityOptions Integrity, RawMessageOptions Raw, AttachmentTextOptions Ocr, StorageModeOptions Storage);

    public sealed class SettingDefinition
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public string Help { get; init; } = string.Empty;
        public required SettingGroup Group { get; init; }
        public SettingKind Kind { get; init; } = SettingKind.Text;
        public long Min { get; init; } = long.MinValue;
        public long Max { get; init; } = long.MaxValue;
        public string[] Choices { get; init; } = Array.Empty<string>();

        /// <summary>False when the value is read once at start-up; <see cref="RestartNote"/> then says so on the page.</summary>
        public bool AppliesLive { get; init; } = true;
        public string? RestartNote { get; init; }

        /// <summary>The options type an editable setting is applied to.</summary>
        public Type? Target { get; init; }

        /// <summary>The value as it is in the effective options right now, as text.</summary>
        public required Func<OptionsBundle, string> Read { get; init; }

        /// <summary>Writes a validated stored value into an options object of type <see cref="Target"/>. Editable settings only.</summary>
        public Action<object, string>? Apply { get; init; }

        /// <summary>(old, new) -> true when the change lowers a protection and needs the admin's password.</summary>
        public Func<string, string, bool>? Risky { get; init; }

        /// <summary>Why a change is risky, in plain words, shown next to the field.</summary>
        public string? RiskyNote { get; init; }

        /// <summary>
        /// When the page should show the password box, as a coarse client-side rule that mirrors <see cref="Risky"/>:
        /// "off" (true to false), "lower" (a smaller number), "zero" (a number set to 0) or "change" (any change). Presentation only:
        /// the server still decides with <see cref="Risky"/>.
        /// </summary>
        public string? PasswordRule { get; init; }

        /// <summary>The environment variable that sets this key (and pins it).</summary>
        public string EnvVar => Key.Replace(":", "__");
    }

    public static class SettingsCatalog
    {
        private static string B(bool v) => v ? "true" : "false";
        private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

        public static readonly IReadOnlyList<SettingDefinition> All = Build();

        public static IEnumerable<SettingDefinition> Editable => All.Where(d => d.Group == SettingGroup.Editable);

        public static SettingDefinition? Find(string? key)
            => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>The built-in default of a setting (from a fresh options object), as text.</summary>
        public static string Default(SettingDefinition def)
            => def.Read(new OptionsBundle(new IntegrityOptions(), new RawMessageOptions(), new AttachmentTextOptions(), new StorageModeOptions()));

        /// <summary>Checks a value against the setting's kind and floors/ceilings. <paramref name="normalized"/> is the canonical text.</summary>
        public static bool TryValidate(SettingDefinition def, string? raw, out string normalized, out string error)
        {
            normalized = string.Empty; error = string.Empty;
            raw = raw?.Trim() ?? string.Empty;
            switch (def.Kind)
            {
                case SettingKind.Int:
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    { error = "Enter a whole number."; return false; }
                    if (n < def.Min || n > def.Max)
                    { error = $"The value must be between {def.Min} and {def.Max}."; return false; }
                    normalized = I(n); return true;
                case SettingKind.Bool:
                    if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) { normalized = "true"; return true; }
                    if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) { normalized = "false"; return true; }
                    error = "The value must be true or false."; return false;
                case SettingKind.Choice:
                    var c = def.Choices.FirstOrDefault(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
                    if (c == null) { error = "The value must be one of: " + string.Join(", ", def.Choices) + "."; return false; }
                    normalized = c; return true;
                default:
                    normalized = raw; return true;
            }
        }

        private static IReadOnlyList<SettingDefinition> Build() => new List<SettingDefinition>
        {
            // ----------------------------------------------------------------- EDITABLE
            new()
            {
                Key = "Integrity:VerifyAllIntervalHours", Label = "How often to check every email (hours)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 0, Max = 8760, Target = typeof(IntegrityOptions), Read = b => I(b.Integrity.VerifyAllIntervalHours),
                Apply = (o, v) => ((IntegrityOptions)o).VerifyAllIntervalHours = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "Every email is checked against its stored original this often. 0 means only when you start a check yourself.",
                Risky = (old, nw) => nw == "0" && old != "0", PasswordRule = "zero", RiskyNote = "Without it, nothing checks the archive unless you do."
            },
            new()
            {
                Key = "Integrity:ManifestIntervalHours", Label = "How often to write a signed record (hours)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 0, Max = 8760, Target = typeof(IntegrityOptions), Read = b => I(b.Integrity.ManifestIntervalHours),
                Apply = (o, v) => ((IntegrityOptions)o).ManifestIntervalHours = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "A signed record lists every stored email and anyone can check it later. 0 means only when you create one yourself.",
                Risky = (old, nw) => nw == "0" && old != "0", PasswordRule = "zero", RiskyNote = "Without it, there are no regular signed records to compare against."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:Enabled", Label = "Keep deleted emails' originals in the Trash first", Group = SettingGroup.Editable, Kind = SettingKind.Bool,
                Target = typeof(RawMessageOptions), Read = b => B(b.Raw.OriginalCleanup.Enabled),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.Enabled = v == "true",
                Help = "When an email is deleted, its original file waits in the Trash before it is erased for good. Off means the original stays where it is and is never cleaned up.",
                Risky = (old, nw) => old == "true" && nw == "false", PasswordRule = "off", RiskyNote = "Without the Trash, originals of deleted emails are never cleaned up."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:BinRetentionDays", Label = "Days to keep deleted originals in the Trash", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 7, Max = 3650, Target = typeof(RawMessageOptions), Read = b => I(b.Raw.OriginalCleanup.BinRetentionDays),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.BinRetentionDays = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "How long a deleted email's original can still be restored from the Trash. After that it is erased for good.",
                Risky = (old, nw) => long.Parse(nw, CultureInfo.InvariantCulture) < long.Parse(old, CultureInfo.InvariantCulture), PasswordRule = "lower",
                RiskyNote = "A shorter time erases deleted emails' originals sooner, including ones already in the Trash."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:IntervalHours", Label = "How often the Trash is checked (hours)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 168, Target = typeof(RawMessageOptions), Read = b => I(b.Raw.OriginalCleanup.IntervalHours),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.IntervalHours = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "How often to look for originals to move into the Trash or erase for good."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:MaxFilesPerRun", Label = "Most files handled per Trash run", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 100000, Target = typeof(RawMessageOptions), Read = b => I(b.Raw.OriginalCleanup.MaxFilesPerRun),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.MaxFilesPerRun = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "A safety limit, so a mistake cannot clear everything at once."
            },
            new()
            {
                Key = "Ocr:Mode", Label = "Reading attachments and pictures", Group = SettingGroup.Editable, Kind = SettingKind.Choice,
                Choices = new[] { "off", "cpu", "gpu" }, Target = typeof(AttachmentTextOptions), Read = b => b.Ocr.Mode.ToLowerInvariant(),
                Apply = (o, v) => ((AttachmentTextOptions)o).Mode = v,
                Help = "Off: attachments are not read. On: text is taken from the files, and pictures and scans are read with OCR on the processor. The graphics-card option works like On for now.",
                Risky = (old, nw) => old != nw, PasswordRule = "change", RiskyNote = "This changes what can be found by searching, and how busy the server is."
            },
            new()
            {
                Key = "Ocr:Threads", Label = "Processor threads for reading pictures", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 16, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.Threads),
                Apply = (o, v) => ((AttachmentTextOptions)o).Threads = int.Parse(v, CultureInfo.InvariantCulture),
                AppliesLive = false, RestartNote = "Takes effect after the application restarts.",
                Help = "More threads read faster but use more of the server."
            },
            new()
            {
                Key = "Ocr:MaxPages", Label = "Pages read from a scanned PDF", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 200, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.MaxPages),
                Apply = (o, v) => ((AttachmentTextOptions)o).MaxPages = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "Pages after this number are not read."
            },
            new()
            {
                Key = "Ocr:MaxImagePixels", Label = "Largest picture to read (pixels)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 100_000, Max = 200_000_000, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.MaxImagePixels),
                Apply = (o, v) => ((AttachmentTextOptions)o).MaxImagePixels = long.Parse(v, CultureInfo.InvariantCulture),
                Help = "Bigger pictures are skipped, to protect the server."
            },
            new()
            {
                Key = "Ocr:TimeoutSecondsPerImage", Label = "Seconds allowed per picture or page", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 600, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.TimeoutSecondsPerImage),
                Apply = (o, v) => ((AttachmentTextOptions)o).TimeoutSecondsPerImage = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "A picture that takes longer is given up on and marked as not readable."
            },

            // ----------------------------------------------------------------- LOCKED (read-only, set by environment variable)
            Locked("Storage:Mode", "Storage mode", b => b.Storage.Mode, "Standard keeps the full email in the database. Lean keeps the original once and only an index in the database."),
            Locked("RawMessages:Enabled", "Originals stored", b => B(b.Raw.Enabled), "Whether the original of every email is kept."),
            Locked("RawMessages:Storage", "Where originals are kept", b => b.Raw.Storage, "In the database (postgres) or as files (filesystem)."),
            Locked("RawMessages:FileSystem:BasePath", "Originals folder", b => b.Raw.FileSystem.BasePath, "The folder that holds the original files."),
            Locked("RawMessages:FileSystem:HashNamed", "Originals named by their fingerprint", b => B(b.Raw.FileSystem.HashNamed), "Each file is named after its own fingerprint (SHA-256) and written only once."),
            Locked("RawMessages:Required", "Refuse emails without a verified original", b => B(b.Raw.Required), "An email is not archived unless its original can be stored and checked."),
            Locked("RawMessages:MaxMessageSizeMb", "Largest original (MB)", b => I(b.Raw.MaxMessageSizeMb), "0 means no limit (needed in lean mode)."),
            Locked("RawMessages:IncludeGraph", "Keep originals for Microsoft 365 accounts", b => B(b.Raw.IncludeGraph), "Whether Microsoft 365 (Graph) accounts store the original too."),
            Locked("Integrity:AuditChainEnabled", "History log on", b => B(b.Integrity.AuditChainEnabled), "The tamper-evident history of what happened in the archive (the audit chain)."),
            Locked("Integrity:ManifestPath", "Manifest folder", b => b.Integrity.ManifestPath, "Where signed records are written."),
            Locked("Integrity:ManifestKeyPath", "Signing key path", b => string.IsNullOrWhiteSpace(b.Integrity.ManifestKeyPath) ? "(default: next to the DataProtection keys)" : b.Integrity.ManifestKeyPath, "Where the private signing key is kept."),
            Locked("Integrity:AllowNewKey", "Allow a new signing key", b => B(b.Integrity.AllowNewKey), "Normally off, so a lost key is never replaced without anyone noticing."),
            Locked("Storage:AllowSerializedImports", "Allow mbox imports", b => B(b.Storage.AllowSerializedImports), "mbox files are not exact copies of the original emails, so they are refused in lean mode unless this is on."),
            Locked("Storage:AllowHashNamedMigration", "Allow moving originals into the database", b => B(b.Storage.AllowHashNamedMigration), "Protects against moving fingerprint-named files into the database by mistake."),
        };

        private static SettingDefinition Locked(string key, string label, Func<OptionsBundle, string> read, string help)
            => new() { Key = key, Label = label, Group = SettingGroup.Locked, Read = read, Help = help };
    }

    /// <summary>The settings stored in the database, loaded into memory so options can be built without a database call.</summary>
    public interface ISettingsSnapshot
    {
        string? Get(string key);
        IReadOnlyDictionary<string, AppSetting> All { get; }
        void Replace(IEnumerable<AppSetting> values);
    }

    public sealed class SettingsSnapshot : ISettingsSnapshot
    {
        private volatile IReadOnlyDictionary<string, AppSetting> _values = new Dictionary<string, AppSetting>(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v.Value : null;
        public IReadOnlyDictionary<string, AppSetting> All => _values;

        public void Replace(IEnumerable<AppSetting> values)
            => _values = values.ToDictionary(v => v.Key, v => v, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Knows which keys an environment variable (or a command-line argument) sets. Such a key is PINNED: the value from the
    /// host wins and the page shows it as locked. Values from appsettings.json do not pin, so they stay editable defaults.
    /// </summary>
    public sealed class SettingsPins
    {
        private readonly IConfigurationRoot? _root;

        public SettingsPins(IConfiguration configuration) => _root = configuration as IConfigurationRoot;

        public bool IsPinned(string key)
        {
            if (_root == null) return false;
            foreach (var p in _root.Providers)
                if ((p is EnvironmentVariablesConfigurationProvider || p is CommandLineConfigurationProvider)
                    && p.TryGet(key, out var v) && !string.IsNullOrEmpty(v))
                    return true;
            return false;
        }
    }

    /// <summary>Applies the stored values on top of the bound configuration. Pinned keys and invalid stored values are skipped (and logged), never fatal.</summary>
    public sealed class SettingsOverlay<T> : IPostConfigureOptions<T> where T : class
    {
        private readonly ISettingsSnapshot _snapshot;
        private readonly SettingsPins _pins;
        private readonly ILogger<SettingsOverlay<T>> _logger;

        public SettingsOverlay(ISettingsSnapshot snapshot, SettingsPins pins, ILogger<SettingsOverlay<T>> logger)
        { _snapshot = snapshot; _pins = pins; _logger = logger; }

        public void PostConfigure(string? name, T options)
        {
            if (name != null && name != Options.DefaultName) return;
            foreach (var def in SettingsCatalog.Editable.Where(d => d.Target == typeof(T)))
            {
                if (_pins.IsPinned(def.Key)) continue;
                var raw = _snapshot.Get(def.Key);
                if (raw == null) continue;
                if (!SettingsCatalog.TryValidate(def, raw, out var ok, out var why))
                {
                    _logger.LogWarning("Stored setting {Key}='{Value}' is not valid ({Why}); the default is used", def.Key, raw, why);
                    continue;
                }
                try { def.Apply!(options, ok); }
                catch (Exception ex) { _logger.LogWarning(ex, "Stored setting {Key} could not be applied; the default is used", def.Key); }
            }
        }
    }

    public sealed record SettingView(
        string Key, string Label, string Help, SettingGroup Group, SettingKind Kind, string EnvVar,
        string Value, string Default, string Source, bool Pinned, bool CanEdit,
        long Min, long Max, string[] Choices, bool AppliesLive, string? RestartNote, string? RiskyNote,
        string? ChangedBy, DateTime? ChangedAt, string? PasswordRule = null);

    public sealed record SettingActor(int? UserId, string Username);

    public sealed record SettingChangeResult(bool Ok, string Message, bool NeedsPassword = false);

    /// <summary>One requested change of the single "Save" of the settings form: a new value, or a reset to the standard value.</summary>
    public sealed record SettingChange(string Key, string? Value, bool Reset = false);

    /// <summary>
    /// The outcome of saving several settings at once. <see cref="Applied"/> is true when the save went through (changes were applied, or
    /// there was nothing to change) and false when nothing was applied because of an error, a missing password or a failed record.
    /// <see cref="RowErrors"/> holds a plain-language error per setting key: when it is not empty NOTHING was applied.
    /// <see cref="NotApplied"/> lists risky changes that could not be made because the account has no local password
    /// (single sign-on); the safe changes of the same save are still applied.
    /// </summary>
    public sealed record SettingBatchResult(
        bool Applied, string Message, IReadOnlyDictionary<string, string> RowErrors,
        IReadOnlyList<string> AppliedLines, IReadOnlyList<string> NotApplied,
        bool NeedsPassword = false, bool NoLocalPassword = false);

    public interface ISettingsService
    {
        Task<IReadOnlyList<SettingView>> GetAllAsync();

        /// <summary>
        /// Changes (or, with <paramref name="reset"/>, resets to the default) ONE editable setting. Refuses locked and pinned
        /// settings, validates the value, asks for the admin's password when the change lowers a protection, and writes the
        /// chain entry and the access-log row before the change is saved. Nothing is changed when any of that fails.
        /// </summary>
        Task<SettingChangeResult> ChangeAsync(string key, string? newValue, SettingActor actor, string? password, bool reset = false);

        /// <summary>
        /// Saves several editable settings at once, all or nothing: every changed value is validated first (any bad one stops the whole
        /// save, with an error per setting), risky changes need the admin's password (an account without a local password cannot make
        /// them here; the safe ones of the same save still apply), the audit chain gets one 'setting.changed' entry per change BEFORE
        /// anything is applied (no record, no change), and the values are then written in one database transaction.
        /// </summary>
        Task<SettingBatchResult> ChangeManyAsync(IReadOnlyList<SettingChange> changes, SettingActor actor, string? password);

        /// <summary>Whether this admin has a local password (false for single sign-on accounts), so the page can say what it will do.</summary>
        Task<bool> HasLocalPasswordAsync(SettingActor actor);
    }

    public sealed class SettingsService : ISettingsService
    {
        private const int MaxPasswordFailures = 5;
        private static readonly TimeSpan PasswordWindow = TimeSpan.FromMinutes(15);
        private static readonly ConcurrentDictionary<int, (int Failures, DateTime Since)> Failures = new();

        /// <summary>Forgets the password-failure counters (tests).</summary>
        internal static void ResetThrottle() => Failures.Clear();

        private readonly MailArchiverDbContext _db;
        private readonly ISettingsSnapshot _snapshot;
        private readonly SettingsPins _pins;
        private readonly IOptionsMonitor<IntegrityOptions> _integrity;
        private readonly IOptionsMonitor<RawMessageOptions> _raw;
        private readonly IOptionsMonitor<AttachmentTextOptions> _ocr;
        private readonly IOptionsMonitor<StorageModeOptions> _storage;
        private readonly IOptionsMonitorCache<IntegrityOptions> _integrityCache;
        private readonly IOptionsMonitorCache<RawMessageOptions> _rawCache;
        private readonly IOptionsMonitorCache<AttachmentTextOptions> _ocrCache;
        private readonly IAuditChain _chain;
        private readonly IAccessLogService _accessLog;
        private readonly IUserService _users;
        private readonly ILogger<SettingsService> _logger;

        public SettingsService(MailArchiverDbContext db, ISettingsSnapshot snapshot, SettingsPins pins,
            IOptionsMonitor<IntegrityOptions> integrity, IOptionsMonitor<RawMessageOptions> raw,
            IOptionsMonitor<AttachmentTextOptions> ocr, IOptionsMonitor<StorageModeOptions> storage,
            IOptionsMonitorCache<IntegrityOptions> integrityCache, IOptionsMonitorCache<RawMessageOptions> rawCache,
            IOptionsMonitorCache<AttachmentTextOptions> ocrCache,
            IAuditChain chain, IAccessLogService accessLog, IUserService users, ILogger<SettingsService> logger)
        {
            _db = db; _snapshot = snapshot; _pins = pins; _integrity = integrity; _raw = raw; _ocr = ocr; _storage = storage;
            _integrityCache = integrityCache; _rawCache = rawCache; _ocrCache = ocrCache;
            _chain = chain; _accessLog = accessLog; _users = users; _logger = logger;
        }

        private OptionsBundle Bundle() => new(_integrity.CurrentValue, _raw.CurrentValue, _ocr.CurrentValue, _storage.CurrentValue);

        public Task<IReadOnlyList<SettingView>> GetAllAsync()
        {
            var bundle = Bundle();
            var list = new List<SettingView>();
            foreach (var d in SettingsCatalog.All)
            {
                var value = d.Read(bundle);
                var def = SettingsCatalog.Default(d);
                var pinned = _pins.IsPinned(d.Key);
                _snapshot.All.TryGetValue(d.Key, out var stored);
                string source;
                if (d.Group == SettingGroup.Locked) source = pinned ? "environment variable" : "configuration / default";
                else if (pinned) source = "environment variable";
                else if (stored != null && SettingsCatalog.TryValidate(d, stored.Value, out _, out _)) source = "changed in this page";
                else source = "default";
                list.Add(new SettingView(d.Key, d.Label, d.Help, d.Group, d.Kind, d.EnvVar, value, def, source, pinned,
                    d.Group == SettingGroup.Editable && !pinned, d.Min, d.Max, d.Choices, d.AppliesLive, d.RestartNote, d.RiskyNote,
                    source == "changed in this page" ? stored?.UpdatedBy : null, source == "changed in this page" ? stored?.UpdatedAt : null,
                    d.PasswordRule));
            }
            return Task.FromResult<IReadOnlyList<SettingView>>(list);
        }

        public async Task<SettingChangeResult> ChangeAsync(string key, string? newValue, SettingActor actor, string? password, bool reset = false)
        {
            var def = SettingsCatalog.Find(key);
            if (def == null) return new(false, "Unknown setting.");
            if (def.Group != SettingGroup.Editable)
                return new(false, $"{def.Label} is locked: it defines the storage guarantee and can only be changed with the environment variable {def.EnvVar} on the host.");
            if (_pins.IsPinned(def.Key))
                return new(false, $"{def.Label} is set by the environment variable {def.EnvVar}, which wins over this page. Change or remove the variable on the host.");

            var bundle = Bundle();
            var old = def.Read(bundle);
            string target;
            if (reset) target = SettingsCatalog.Default(def);
            else if (!SettingsCatalog.TryValidate(def, newValue, out target, out var why)) return new(false, $"{def.Label}: {why}");

            if (target == old && (reset ? !_snapshot.All.ContainsKey(def.Key) : true))
                return new(true, $"{def.Label} already has this value; nothing was changed.");

            // Risky changes (they lower a protection) need the admin's own password.
            var risky = def.Risky?.Invoke(old, target) ?? false;
            if (risky)
            {
                var denied = await CheckPasswordAsync(actor, password, def);
                if (denied != null) return denied;
            }

            // The record comes FIRST. Without it the change is not applied.
            var payload = new { key = def.Key, old, @new = target, reset, risky, actor = actor.Username };
            if (_integrity.CurrentValue.AuditChainEnabled)
            {
                var entry = await _chain.AppendAsync("setting.changed", null, null, payload);
                if (entry == null)
                    return new(false, "The change could not be recorded in the audit chain, so it was not applied. Nothing was changed.");
            }
            try
            {
                await _accessLog.LogAccessAsync(actor.Username, AccessLogType.Integrity,
                    searchParameters: $"setting {def.Key}: {old} -> {target}{(reset ? " (reset to default)" : string.Empty)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Access log write failed for setting {Key}; the change was not applied", def.Key);
                await TryAppendFailureAsync(def.Key, "access log write failed");
                return new(false, "The change could not be recorded in the access log, so it was not applied. Nothing was changed.");
            }

            try
            {
                var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == def.Key);
                if (reset)
                {
                    if (row != null) _db.AppSettings.Remove(row);
                }
                else if (row == null)
                    _db.AppSettings.Add(new AppSetting { Key = def.Key, Value = target, UpdatedAt = DateTime.UtcNow, UpdatedBy = actor.Username });
                else { row.Value = target; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor.Username; }
                await _db.SaveChangesAsync();

                _snapshot.Replace(await _db.AppSettings.AsNoTracking().ToListAsync());
                ClearOptionCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving setting {Key} failed", def.Key);
                await TryAppendFailureAsync(def.Key, "database write failed");
                return new(false, "The setting could not be saved. The audit record says the change was attempted and failed.");
            }

            var note = def.AppliesLive ? "It applies now." : def.RestartNote ?? "It applies after a restart.";
            return new(true, $"{def.Label}: {old} → {target}. {note}");
        }

        public async Task<SettingBatchResult> ChangeManyAsync(IReadOnlyList<SettingChange> changes, SettingActor actor, string? password)
        {
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var none = Array.Empty<string>();
            var bundle = Bundle();
            var plan = new List<(SettingDefinition Def, string Old, string Target, bool Reset, bool Risky)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Validate EVERYTHING first. One bad row stops the whole save.
            foreach (var c in changes)
            {
                var def = SettingsCatalog.Find(c.Key);
                if (def == null) { errors[c.Key ?? string.Empty] = "Unknown setting."; continue; }
                if (!seen.Add(def.Key)) { errors[def.Key] = $"{def.Label} was sent twice."; continue; }
                if (def.Group != SettingGroup.Editable)
                { errors[def.Key] = $"{def.Label} is locked: it can only be changed with the environment variable {def.EnvVar} on the host."; continue; }
                if (_pins.IsPinned(def.Key))
                { errors[def.Key] = $"{def.Label} is set by the environment variable {def.EnvVar}, which wins over this page. Change or remove the variable on the host."; continue; }

                var old = def.Read(bundle);
                string target;
                if (c.Reset) target = SettingsCatalog.Default(def);
                else if (!SettingsCatalog.TryValidate(def, c.Value, out target, out var why)) { errors[def.Key] = why; continue; }

                if (target == old && (c.Reset ? !_snapshot.All.ContainsKey(def.Key) : true)) continue;   // nothing to change
                plan.Add((def, old, target, c.Reset, def.Risky?.Invoke(old, target) ?? false));
            }
            if (errors.Count > 0)
                return new(false, "Nothing was saved: some values need fixing.", errors, none, none);
            if (plan.Count == 0)
                return new(true, "Nothing to change.", errors, none, none);

            // 2. Risky changes need the admin's own password. Without a local password (single sign-on) they cannot be made here.
            var notApplied = new List<string>();
            var noLocal = false;
            var risky = plan.Where(p => p.Risky).ToList();
            if (risky.Count > 0)
            {
                if (!await HasLocalPasswordAsync(actor))
                {
                    noLocal = true;
                    foreach (var r in risky)
                        notApplied.Add($"{r.Def.Label}: not changed. It needs a password, and your account has no local password (single sign-on). Set the environment variable {r.Def.EnvVar} on the host instead.");
                    plan = plan.Where(p => !p.Risky).ToList();
                }
                else
                {
                    var denied = await CheckPasswordCoreAsync(actor, password,
                        string.Empty,
                        "Some of these changes lower a protection. Enter your password to confirm. Nothing was changed.");
                    if (denied != null) return new(false, denied.Message, errors, none, none, denied.NeedsPassword);
                }
            }
            if (plan.Count == 0)
                return new(false, "Nothing was changed. " + string.Join(" ", notApplied), errors, none, notApplied, false, noLocal);

            // 3. The records come FIRST: one chain entry per change. Without them nothing is applied.
            var batch = Guid.NewGuid().ToString("N")[..12];
            if (_integrity.CurrentValue.AuditChainEnabled)
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    var p = plan[i];
                    var entry = await _chain.AppendAsync("setting.changed", null, null,
                        new { key = p.Def.Key, old = p.Old, @new = p.Target, reset = p.Reset, risky = p.Risky, actor = actor.Username, batch, index = i + 1, of = plan.Count });
                    if (entry == null)
                    {
                        await TryAppendFailureAsync(batch, "audit chain write failed; none of the changes of this save were applied");
                        return new(false, "The changes could not be recorded in the audit chain, so none of them were applied. Nothing was changed.", errors, none, notApplied, false, noLocal);
                    }
                }
            }
            try
            {
                foreach (var p in plan)
                    await _accessLog.LogAccessAsync(actor.Username, AccessLogType.Integrity,
                        searchParameters: $"setting {p.Def.Key}: {p.Old} -> {p.Target}{(p.Reset ? " (reset to default)" : string.Empty)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Access log write failed while saving settings; none of the changes were applied");
                await TryAppendFailureAsync(batch, "access log write failed; none of the changes of this save were applied");
                return new(false, "The changes could not be recorded in the access log, so none of them were applied. Nothing was changed.", errors, none, notApplied, false, noLocal);
            }

            // 4. Apply all of them in ONE database transaction.
            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync();
                var keys = plan.Select(p => p.Def.Key).ToList();
                var rows = await _db.AppSettings.Where(s => keys.Contains(s.Key)).ToListAsync();
                foreach (var p in plan)
                {
                    var row = rows.FirstOrDefault(r => string.Equals(r.Key, p.Def.Key, StringComparison.OrdinalIgnoreCase));
                    if (p.Reset) { if (row != null) _db.AppSettings.Remove(row); }
                    else if (row == null) _db.AppSettings.Add(new AppSetting { Key = p.Def.Key, Value = p.Target, UpdatedAt = DateTime.UtcNow, UpdatedBy = actor.Username });
                    else { row.Value = p.Target; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor.Username; }
                }
                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                _snapshot.Replace(await _db.AppSettings.AsNoTracking().ToListAsync());
                ClearOptionCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving settings failed; none of the changes were applied");
                _db.ChangeTracker.Clear();
                await TryAppendFailureAsync(batch, "database write failed; none of the changes of this save were applied");
                return new(false, "The settings could not be saved. The audit record says the change was attempted and failed. Nothing was changed.", errors, none, notApplied, false, noLocal);
            }

            var lines = plan.Select(p => $"{p.Def.Label}: {p.Old} → {p.Target}. {(p.Def.AppliesLive ? "It applies now." : p.Def.RestartNote ?? "It applies after a restart.")}").ToList();
            var message = $"Saved {plan.Count} {(plan.Count == 1 ? "change" : "changes")}." + (notApplied.Count > 0 ? $" {notApplied.Count} could not be made here (see below)." : string.Empty);
            return new(true, message, errors, lines, notApplied, false, noLocal);
        }

        private async Task<SettingChangeResult?> CheckPasswordAsync(SettingActor actor, string? password, SettingDefinition def)
            => await CheckPasswordCoreAsync(actor, password,
                $"This change lowers a protection and needs your password, but your account has no local password (single sign-on). Set the environment variable {def.EnvVar} on the host instead.",
                $"This change lowers a protection ({def.RiskyNote}). Enter your password to confirm.");

        private async Task<User?> LocalUserAsync(SettingActor actor)
        {
            var user = actor.UserId.HasValue ? await _users.GetUserByIdAsync(actor.UserId.Value) : null;
            if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !string.IsNullOrEmpty(user.OAuthRemoteUserId)) return null;
            return user;
        }

        public async Task<bool> HasLocalPasswordAsync(SettingActor actor) => await LocalUserAsync(actor) != null;

        private async Task<SettingChangeResult?> CheckPasswordCoreAsync(SettingActor actor, string? password, string noLocalPasswordMessage, string needPasswordMessage)
        {
            var user = await LocalUserAsync(actor);
            if (user == null) return new(false, noLocalPasswordMessage);

            var now = DateTime.UtcNow;
            if (Failures.TryGetValue(user.Id, out var f) && now - f.Since < PasswordWindow && f.Failures >= MaxPasswordFailures)
                return new(false, "Too many wrong passwords. Try again in a few minutes.", true);

            if (string.IsNullOrEmpty(password))
                return new(false, needPasswordMessage, true);

            if (!_users.VerifyPassword(password, user.PasswordHash))
            {
                Failures.AddOrUpdate(user.Id, (1, now), (_, cur) => now - cur.Since >= PasswordWindow ? (1, now) : (cur.Failures + 1, cur.Since));
                return new(false, "The password is not correct. Nothing was changed.", true);
            }
            Failures.TryRemove(user.Id, out _);
            return null;
        }

        private async Task TryAppendFailureAsync(string key, string reason)
        {
            try { await _chain.AppendAsync("setting.change.failed", null, null, new { key, reason }); }
            catch { /* AppendAsync never throws; belt and braces */ }
        }

        private void ClearOptionCaches()
        {
            _integrityCache.Clear();
            _rawCache.Clear();
            _ocrCache.Clear();
        }
    }

    /// <summary>Loads the stored settings at start-up. A failure never prevents the application from starting.</summary>
    public static class SettingsLoader
    {
        public static async Task LoadAsync(IServiceProvider root, ILogger logger)
        {
            try
            {
                using var scope = root.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                var snapshot = root.GetRequiredService<ISettingsSnapshot>();
                snapshot.Replace(await db.AppSettings.AsNoTracking().ToListAsync());
                root.GetRequiredService<IOptionsMonitorCache<IntegrityOptions>>().Clear();
                root.GetRequiredService<IOptionsMonitorCache<RawMessageOptions>>().Clear();
                root.GetRequiredService<IOptionsMonitorCache<AttachmentTextOptions>>().Clear();
                logger.LogInformation("Loaded {Count} stored setting(s)", snapshot.All.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Stored settings could not be loaded; the defaults and the environment are used");
            }
        }
    }
}
