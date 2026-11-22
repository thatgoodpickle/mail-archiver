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

        /// <summary>Why a change is risky, shown next to the field.</summary>
        public string? RiskyNote { get; init; }

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
                Key = "Integrity:VerifyAllIntervalHours", Label = "Verify-all schedule (hours)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 0, Max = 8760, Target = typeof(IntegrityOptions), Read = b => I(b.Integrity.VerifyAllIntervalHours),
                Apply = (o, v) => ((IntegrityOptions)o).VerifyAllIntervalHours = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "Re-checks every email against its original this often. 0 = only when you start it.",
                Risky = (old, nw) => nw == "0" && old != "0", RiskyNote = "Switching the schedule off removes a periodic check."
            },
            new()
            {
                Key = "Integrity:ManifestIntervalHours", Label = "Signed manifest schedule (hours)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 0, Max = 8760, Target = typeof(IntegrityOptions), Read = b => I(b.Integrity.ManifestIntervalHours),
                Apply = (o, v) => ((IntegrityOptions)o).ManifestIntervalHours = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "Writes a signed list of every original this often. 0 = only when you create one.",
                Risky = (old, nw) => nw == "0" && old != "0", RiskyNote = "Switching the schedule off removes a periodic record."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:Enabled", Label = "Original bin on", Group = SettingGroup.Editable, Kind = SettingKind.Bool,
                Target = typeof(RawMessageOptions), Read = b => B(b.Raw.OriginalCleanup.Enabled),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.Enabled = v == "true",
                Help = "Originals nobody refers to any more go to a bin and are deleted for good after the retention period. Off = they stay where they are.",
                Risky = (old, nw) => old == "true" && nw == "false", RiskyNote = "Switching the bin off stops deleted emails' originals from ever being cleaned up."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:BinRetentionDays", Label = "Bin retention (days)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 7, Max = 3650, Target = typeof(RawMessageOptions), Read = b => I(b.Raw.OriginalCleanup.BinRetentionDays),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.BinRetentionDays = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "How long a binned original can still be restored. Minimum 7 days here (the environment variable allows less).",
                Risky = (old, nw) => long.Parse(nw, CultureInfo.InvariantCulture) < long.Parse(old, CultureInfo.InvariantCulture),
                RiskyNote = "A shorter retention erases deleted emails' originals sooner, and files already in the bin become due earlier."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:IntervalHours", Label = "Bin run interval (hours)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 168, Target = typeof(RawMessageOptions), Read = b => I(b.Raw.OriginalCleanup.IntervalHours),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.IntervalHours = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "How often the bin looks for originals to move or purge."
            },
            new()
            {
                Key = "RawMessages:OriginalCleanup:MaxFilesPerRun", Label = "Bin files per run (cap)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 100000, Target = typeof(RawMessageOptions), Read = b => I(b.Raw.OriginalCleanup.MaxFilesPerRun),
                Apply = (o, v) => ((RawMessageOptions)o).OriginalCleanup.MaxFilesPerRun = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "Upper bound of files moved, and of files purged, in one run, so a bug cannot clear everything at once."
            },
            new()
            {
                Key = "Ocr:Mode", Label = "Attachment text / OCR mode", Group = SettingGroup.Editable, Kind = SettingKind.Choice,
                Choices = new[] { "off", "cpu", "gpu" }, Target = typeof(AttachmentTextOptions), Read = b => b.Ocr.Mode.ToLowerInvariant(),
                Apply = (o, v) => ((AttachmentTextOptions)o).Mode = v,
                Help = "off = nothing is read; cpu = text layers and OCR on the CPU; gpu = same for now (the GPU build is not available yet).",
                Risky = (old, nw) => old != nw, RiskyNote = "Switching the mode changes what is indexed and, for OCR, how much CPU the server uses."
            },
            new()
            {
                Key = "Ocr:Threads", Label = "OCR threads", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 16, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.Threads),
                Apply = (o, v) => ((AttachmentTextOptions)o).Threads = int.Parse(v, CultureInfo.InvariantCulture),
                AppliesLive = false, RestartNote = "Applies after the application restarts (the OCR engine reads it once, when it starts).",
                Help = "CPU threads the OCR engine may use for one image."
            },
            new()
            {
                Key = "Ocr:MaxPages", Label = "OCR pages per scanned PDF", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 200, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.MaxPages),
                Apply = (o, v) => ((AttachmentTextOptions)o).MaxPages = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "Pages read from one scanned PDF; further pages are ignored."
            },
            new()
            {
                Key = "Ocr:MaxImagePixels", Label = "OCR largest image (pixels)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 100_000, Max = 200_000_000, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.MaxImagePixels),
                Apply = (o, v) => ((AttachmentTextOptions)o).MaxImagePixels = long.Parse(v, CultureInfo.InvariantCulture),
                Help = "Images with more pixels are not read (checked before decoding)."
            },
            new()
            {
                Key = "Ocr:TimeoutSecondsPerImage", Label = "OCR time per image (seconds)", Group = SettingGroup.Editable, Kind = SettingKind.Int,
                Min = 1, Max = 600, Target = typeof(AttachmentTextOptions), Read = b => I(b.Ocr.TimeoutSecondsPerImage),
                Apply = (o, v) => ((AttachmentTextOptions)o).TimeoutSecondsPerImage = int.Parse(v, CultureInfo.InvariantCulture),
                Help = "An image (or PDF page) that takes longer is marked failed."
            },

            // ----------------------------------------------------------------- LOCKED (read-only, set by environment variable)
            Locked("Storage:Mode", "Storage mode", b => b.Storage.Mode, "Standard (upstream) or Lean (original stored once, database keeps an index)."),
            Locked("RawMessages:Enabled", "Originals stored", b => B(b.Raw.Enabled), "Whether the original of each email is kept."),
            Locked("RawMessages:Storage", "Originals storage", b => b.Raw.Storage, "postgres or filesystem."),
            Locked("RawMessages:FileSystem:BasePath", "Originals folder", b => b.Raw.FileSystem.BasePath, "Where the original files live."),
            Locked("RawMessages:FileSystem:HashNamed", "Hash-named originals", b => B(b.Raw.FileSystem.HashNamed), "Files are named by their SHA-256 and written once."),
            Locked("RawMessages:Required", "Originals required", b => B(b.Raw.Required), "Refuse an email when no verified original can be kept."),
            Locked("RawMessages:MaxMessageSizeMb", "Largest original (MB)", b => I(b.Raw.MaxMessageSizeMb), "0 = no limit (required in lean mode)."),
            Locked("RawMessages:IncludeGraph", "Originals for Microsoft 365", b => B(b.Raw.IncludeGraph), "Whether Graph accounts store the original too."),
            Locked("Integrity:AuditChainEnabled", "Audit chain on", b => B(b.Integrity.AuditChainEnabled), "The hash-chained audit trail."),
            Locked("Integrity:ManifestPath", "Manifest folder", b => b.Integrity.ManifestPath, "Where signed manifests are written."),
            Locked("Integrity:ManifestKeyPath", "Signing key path", b => string.IsNullOrWhiteSpace(b.Integrity.ManifestKeyPath) ? "(default: next to the DataProtection keys)" : b.Integrity.ManifestKeyPath, "Where the private signing key lives."),
            Locked("Integrity:AllowNewKey", "Allow a new signing key", b => B(b.Integrity.AllowNewKey), "Normally false: a lost key is never silently replaced."),
            Locked("Storage:AllowSerializedImports", "Allow mbox (re-serialized) imports", b => B(b.Storage.AllowSerializedImports), "mbox copies are not byte-exact, so they are refused in lean/required mode unless this is true."),
            Locked("Storage:AllowHashNamedMigration", "Allow moving hash-named originals", b => B(b.Storage.AllowHashNamedMigration), "Guards the filesystem-to-database migration."),
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
        string? ChangedBy, DateTime? ChangedAt);

    public sealed record SettingActor(int? UserId, string Username);

    public sealed record SettingChangeResult(bool Ok, string Message, bool NeedsPassword = false);

    public interface ISettingsService
    {
        Task<IReadOnlyList<SettingView>> GetAllAsync();

        /// <summary>
        /// Changes (or, with <paramref name="reset"/>, resets to the default) ONE editable setting. Refuses locked and pinned
        /// settings, validates the value, asks for the admin's password when the change lowers a protection, and writes the
        /// chain entry and the access-log row before the change is saved. Nothing is changed when any of that fails.
        /// </summary>
        Task<SettingChangeResult> ChangeAsync(string key, string? newValue, SettingActor actor, string? password, bool reset = false);
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
                    source == "changed in this page" ? stored?.UpdatedBy : null, source == "changed in this page" ? stored?.UpdatedAt : null));
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

        private async Task<SettingChangeResult?> CheckPasswordAsync(SettingActor actor, string? password, SettingDefinition def)
        {
            var user = actor.UserId.HasValue ? await _users.GetUserByIdAsync(actor.UserId.Value) : null;
            if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !string.IsNullOrEmpty(user.OAuthRemoteUserId))
                return new(false, $"This change lowers a protection and needs your password, but your account has no local password (single sign-on). Set the environment variable {def.EnvVar} on the host instead.");

            var now = DateTime.UtcNow;
            if (Failures.TryGetValue(user.Id, out var f) && now - f.Since < PasswordWindow && f.Failures >= MaxPasswordFailures)
                return new(false, "Too many wrong passwords. Try again in a few minutes.", true);

            if (string.IsNullOrEmpty(password))
                return new(false, $"This change lowers a protection ({def.RiskyNote}). Enter your password to confirm.", true);

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
