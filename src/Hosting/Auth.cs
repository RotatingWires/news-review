using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewsReview;

sealed class SetupConflictException() : Exception("A password is already configured.");
sealed class SetupTokenException() : Exception("Invalid setup token.");

/// <summary>
/// The saved password verifier (data/auth.json) and the console-only first-run setup token.
/// The file keeps its original lowercase JSON format so existing installations keep their password.
/// </summary>
sealed class PasswordStore
{
    public const int Iterations = 600_000;
    readonly string path;
    readonly Lock gate = new();
    JsonObject? record;

    public string? SetupToken { get; private set; }
    public bool Configured => record is not null;

    public PasswordStore(string path)
    {
        this.path = path;
        if (!File.Exists(path))
        {
            var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            SetupToken = $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..16]}";
            return;
        }
        try
        {
            var value = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException();
            if (!Json.IsInteger(value["version"], out var version) || version != 1 ||
                !Json.IsInteger(value["iterations"], out var iterations) || iterations is < 1 or > int.MaxValue ||
                Convert.FromBase64String((string)value["salt"]!).Length != 16 || Convert.FromBase64String((string)value["hash"]!).Length != 32)
                throw new FormatException();
            record = value;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or ArgumentNullException or IOException)
        {
            throw new InvalidOperationException($"Invalid credentials file: {path}. Restore a valid auth.json before starting.");
        }
    }

    static JsonObject Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new JsonObject { ["version"] = 1, ["iterations"] = Iterations, ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash) };
    }

    static FileStreamOptions OwnerOnly(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    static void WriteFlushed(FileStream stream, JsonObject value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value.ToJsonString(Json.Compact)));
        stream.Flush(flushToDisk: true);
    }

    public void Setup(string? token, string? password, string? confirmation)
    {
        lock (gate)
        {
            if (Configured || SetupToken is null) throw new SetupConflictException();
            static string Normalize(string? value) => (value ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Normalize(token)), Encoding.UTF8.GetBytes(Normalize(SetupToken))))
                throw new SetupTokenException();
            if (password is null || password.Length is < 8 or > 1024) throw new ValidationException("Password must contain 8–1024 characters.");
            if (password != confirmation) throw new ValidationException("Passwords do not match.");
            var value = Hash(password);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // Exclusive creation prevents competing setup requests from replacing credentials and
            // needs no hard-link support, so it also works on Windows pooled drives.
            FileStream stream;
            try { stream = new FileStream(path, OwnerOnly(FileMode.CreateNew)); }
            catch (IOException) when (File.Exists(path)) { throw new SetupConflictException(); }
            try
            {
                using (stream) WriteFlushed(stream, value);
            }
            catch
            {
                // Remove only the incomplete file this request created. An abrupt power loss
                // instead leaves invalid credentials, which fail closed at startup.
                File.Delete(path);
                throw;
            }
            record = value;
            SetupToken = null;
        }
    }

    /// <summary>Checks the password; on success, a verifier with fewer than 600,000 iterations is replaced once.</summary>
    public bool Verify(string? password, out bool upgraded)
    {
        upgraded = false;
        JsonObject? current;
        lock (gate) current = record;
        if (current is null || password is null || password.Length > 1024) return false;
        var iterations = (int)current["iterations"]!.AsLong();
        var expected = Convert.FromBase64String((string)current["hash"]!);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String((string)current["salt"]!), iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
        if (iterations < Iterations)
        {
            lock (gate)
            {
                if (!ReferenceEquals(record, current)) return true;
                var value = Hash(password);
                // Write beside the target and rename, so an interrupted upgrade cannot truncate auth.json.
                var temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, OwnerOnly(FileMode.Create))) WriteFlushed(stream, value);
                File.Move(temporary, path, overwrite: true);
                record = value;
                upgraded = true;
            }
        }
        return true;
    }
}
